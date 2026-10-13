using PenForLang.Core.Geometry;
using PenForLang.Evaluation.Config;

namespace PenForLang.Evaluation.Pipeline;

/// <summary>Преобразование подобия без поворота: <c>p' = Scale·p + Translation</c>.</summary>
internal readonly record struct Similarity(double Scale, Vec2 Translation)
{
    public static Similarity Identity { get; } = new(1.0, Vec2.Zero);

    public Vec2 Apply(Vec2 p) => (p * Scale) + Translation;
}

/// <summary>
/// Робастная подгонка масштаба и сдвига рукописи к эталону по соответствиям выравнивания (§6.4.2: «робастная оценка
/// масштаба и сдвига»): метод наименьших квадратов с отсечением худших остатков, несколько итераций. Лишний «подход» к
/// неверному старту, ошибочная петля или короткий хвост занимают малую долю соответствий и в подгонку не попадают, поэтому
/// нормализация не «прощает» ошибку старта сдвигом всей буквы.
/// </summary>
internal static class SimilarityFit
{
    /// <summary>Подгоняет <paramref name="ink"/> к <paramref name="template"/> (параллельные массивы соответствий).</summary>
    public static Similarity Fit(IReadOnlyList<Vec2> ink, IReadOnlyList<Vec2> template, RefineConfig refine)
    {
        int n = ink.Count;
        if (n < 3)
        {
            return Similarity.Identity;
        }

        var keep = new bool[n];
        Array.Fill(keep, true);
        double scale = 1.0;
        Vec2 inkCentroid = Vec2.Zero;
        Vec2 templateCentroid = Vec2.Zero;
        var residuals = new double[n];
        var sorted = new double[n];
        for (int iteration = 0; iteration < refine.Iterations; iteration++)
        {
            inkCentroid = Centroid(ink, keep);
            templateCentroid = Centroid(template, keep);
            double numerator = 0.0;
            double denominator = 0.0;
            for (int i = 0; i < n; i++)
            {
                if (!keep[i])
                {
                    continue;
                }

                Vec2 a = ink[i] - inkCentroid;
                Vec2 b = template[i] - templateCentroid;
                numerator += Vec2.Dot(a, b);
                denominator += a.LengthSquared;
            }

            scale = denominator > 1e-12 ? Math.Clamp(numerator / denominator, refine.ScaleMin, refine.ScaleMax) : 1.0;
            for (int i = 0; i < n; i++)
            {
                residuals[i] = Vec2.Distance(template[i], templateCentroid + ((ink[i] - inkCentroid) * scale));
            }

            Array.Copy(residuals, sorted, n);
            Array.Sort(sorted);
            double limit = sorted[Math.Clamp((int)Math.Ceiling(refine.KeepShare * n) - 1, 0, n - 1)];
            for (int i = 0; i < n; i++)
            {
                keep[i] = residuals[i] <= limit;
            }
        }

        // p' = rc + s (p − pc) = s·p + (rc − s·pc)
        Vec2 translation = templateCentroid - (inkCentroid * scale);
        return new Similarity(scale, translation);
    }

    private static Vec2 Centroid(IReadOnlyList<Vec2> points, bool[] keep)
    {
        double x = 0.0;
        double y = 0.0;
        int count = 0;
        for (int i = 0; i < points.Count; i++)
        {
            if (keep[i])
            {
                x += points[i].X;
                y += points[i].Y;
                count++;
            }
        }

        return count == 0 ? Vec2.Zero : new Vec2(x / count, y / count);
    }
}
