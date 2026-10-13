namespace PenForLang.Core.Geometry;

/// <summary>Показатели двустороннего коридора допуска между рукописью и эталоном (F1: <c>corridorShare</c>, <c>coverage</c>).</summary>
/// <param name="Share">Доля вершин рукописи, лежащих в коридоре эталона (по числу вершин).</param>
/// <param name="Coverage">Доля длины дуги эталона, лежащая в коридоре рукописи.</param>
/// <param name="MeanDistance">Среднее расстояние от вершин рукописи до эталона.</param>
/// <param name="MaxDistance">Наибольшее расстояние от вершины рукописи до эталона.</param>
public readonly record struct CorridorMeasure(double Share, double Coverage, double MeanDistance, double MaxDistance);

/// <summary>
/// Коридор допуска вокруг полилинии: какая доля рукописи лежит ближе <c>tau</c> к эталону и какая доля эталона
/// «покрыта» рукописью (ARCHITECTURE.md §6.4.9: <c>corridorShare</c>, <c>coverage</c>; §6.4.3: доля штриха, проходящая в
/// коридоре 0,1 x-h от уже написанных чернил). Расстояния — до ломаной, а не до вершин. Сложность O(N·M).
/// </summary>
public static class Corridor
{
    /// <summary>
    /// Доля вершин <paramref name="points"/>, расстояние от которых до ломаной <paramref name="reference"/> не больше
    /// <paramref name="tau"/>. Рукопись должна быть ресэмплирована равномерно (тогда доля вершин ≈ доле длины).
    /// </summary>
    /// <param name="points">Проверяемые вершины (не менее одной).</param>
    /// <param name="reference">Ломаная-эталон (не менее одной вершины).</param>
    /// <param name="tau">Полуширина коридора, ≥ 0.</param>
    public static double Share(ReadOnlySpan<Vec2> points, ReadOnlySpan<Vec2> reference, double tau)
    {
        CheckArguments(points, reference, tau);
        int inside = 0;
        for (int i = 0; i < points.Length; i++)
        {
            if (PolylineMath.DistanceToPolyline(points[i], reference) <= tau)
            {
                inside++;
            }
        }

        return (double)inside / points.Length;
    }

    /// <summary>
    /// Доля длины дуги ломаной <paramref name="reference"/>, расстояние от которой до ломаной <paramref name="sample"/> не
    /// больше <paramref name="tau"/>. Эталон делится на интервалы не длиннее <paramref name="step"/>, проверяется середина
    /// каждого, вес — длина интервала. Для вырожденного эталона нулевой длины — 1 или 0 по расстоянию до его точки.
    /// </summary>
    /// <param name="reference">Ломаная-эталон (не менее одной вершины).</param>
    /// <param name="sample">Ломаная-рукопись (не менее одной вершины).</param>
    /// <param name="tau">Полуширина коридора, ≥ 0.</param>
    /// <param name="step">Максимальная длина интервала проверки, &gt; 0 (обычно <c>resampleStep</c>).</param>
    public static double Coverage(ReadOnlySpan<Vec2> reference, ReadOnlySpan<Vec2> sample, double tau, double step)
    {
        CheckArguments(sample, reference, tau);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(step);
        double total = PolylineMath.Length(reference);
        if (!(total > 0.0))
        {
            return PolylineMath.DistanceToPolyline(reference[0], sample) <= tau ? 1.0 : 0.0;
        }

        double covered = 0.0;
        for (int i = 1; i < reference.Length; i++)
        {
            Vec2 a = reference[i - 1];
            Vec2 b = reference[i];
            double len = Vec2.Distance(a, b);
            if (len <= 0.0)
            {
                continue;
            }

            double pieces = Math.Ceiling(len / step);
            int count = pieces >= int.MaxValue ? int.MaxValue : (int)pieces;
            double pieceLength = len / count;
            for (int k = 0; k < count; k++)
            {
                Vec2 mid = Vec2.Lerp(a, b, (k + 0.5) / count);
                if (PolylineMath.DistanceToPolyline(mid, sample) <= tau)
                {
                    covered += pieceLength;
                }
            }
        }

        return Math.Min(1.0, covered / total);
    }

    /// <summary>Все показатели коридора сразу.</summary>
    /// <param name="sample">Рукопись (равномерно ресэмплированная).</param>
    /// <param name="reference">Эталон.</param>
    /// <param name="tau">Полуширина коридора, ≥ 0.</param>
    /// <param name="step">Шаг проверки покрытия эталона, &gt; 0.</param>
    public static CorridorMeasure Measure(ReadOnlySpan<Vec2> sample, ReadOnlySpan<Vec2> reference, double tau, double step)
    {
        CheckArguments(sample, reference, tau);
        int inside = 0;
        double sum = 0.0;
        double max = 0.0;
        for (int i = 0; i < sample.Length; i++)
        {
            double d = PolylineMath.DistanceToPolyline(sample[i], reference);
            sum += d;
            max = Math.Max(max, d);
            if (d <= tau)
            {
                inside++;
            }
        }

        return new CorridorMeasure(
            (double)inside / sample.Length,
            Coverage(reference, sample, tau, step),
            sum / sample.Length,
            max);
    }

    private static void CheckArguments(ReadOnlySpan<Vec2> points, ReadOnlySpan<Vec2> reference, double tau)
    {
        if (points.Length == 0 || reference.Length == 0)
        {
            throw new ArgumentException("Both polylines must be non-empty.");
        }

        if (!(tau >= 0.0))
        {
            throw new ArgumentOutOfRangeException(nameof(tau), "Tolerance must be >= 0.");
        }
    }
}
