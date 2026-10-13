using PenForLang.Core.Geometry;
using PenForLang.Evaluation.Config;
using PenForLang.Ink;

namespace PenForLang.Evaluation.Pipeline;

/// <summary>Штрих рукописи после предобработки (§6.4.1) и нормализации для выравнивания (§6.4.2, проход 1).</summary>
internal sealed class ChildStroke
{
    /// <summary>Номер штриха в попытке (<see cref="RulerStroke.Index"/>).</summary>
    public required int InkIndex { get; init; }

    /// <summary>Хронологический порядок (с 0).</summary>
    public int Chrono { get; set; }

    /// <summary>Ресэмплированные и сглаженные точки в координатах линейки (проход 2: абсолютные единицы).</summary>
    public required Vec2[] Pos { get; init; }

    public required long StartUs { get; init; }

    public required long EndUs { get; init; }

    /// <summary>Длина ресэмплированной и сглаженной ломаной (шум отсчётов её не раздувает), x-height.</summary>
    public required double Length { get; init; }

    /// <summary>Диагональ габаритного прямоугольника точек: робастный размер точки или короткой черты.</summary>
    public required double Extent { get; init; }

    /// <summary>Микроштрих (<c>dotCandidate</c>, §6.4.1).</summary>
    public required bool IsDot { get; init; }

    public required bool Clipped { get; init; }

    /// <summary>Нормализованные точки (проход 1): деслантинг, масштаб, сдвиг по u и baseline.</summary>
    public Vec2[] NPos { get; set; } = [];

    public Vec2[] NDir { get; set; } = [];

    /// <summary>Середина по длине дуги в нормализованных координатах.</summary>
    public Vec2 NCenter => PolylineTools.ArcMidpoint(NPos);
}

/// <summary>Параметры нормализации, применённые к рукописи.</summary>
internal sealed record NormalizationParams(double ShearDeg, double Scale, double ShiftU, double ShiftV, double? InkSlantDeg, double? TemplateSlantDeg);

/// <summary>Предобработка и нормализация рукописи.</summary>
internal static class ChildInk
{
    /// <summary>
    /// Предобработка (§6.4.1): отбрасывание нечисловых точек и дубликатов, выбросов-«шпилек», пометка микроштрихов,
    /// ресэмплинг по длине дуги, сглаживание. Штрихи упорядочиваются хронологически (по времени первой точки).
    /// </summary>
    public static List<ChildStroke> Prepare(RulerInk ink, ScoringConfig config, SavitzkyGolayFilter filter)
    {
        var result = new List<ChildStroke>(ink.Strokes.Length);
        foreach (RulerStroke stroke in ink.Strokes)
        {
            var points = new List<Vec2>(stroke.Points.Length);
            var kept = new List<RulerPoint>(stroke.Points.Length);
            foreach (RulerPoint p in stroke.Points)
            {
                if (!double.IsFinite(p.U) || !double.IsFinite(p.V))
                {
                    continue;
                }

                var v = new Vec2(p.U, p.V);
                if (points.Count > 0 && Vec2.DistanceSquared(points[^1], v) < 1e-18)
                {
                    continue;
                }

                points.Add(v);
                kept.Add(p);
            }

            if (points.Count == 0)
            {
                continue;
            }

            RemoveSpikes(points, kept, config.Preprocess.SpikeJumpMedianSteps);
            long startUs = kept[0].TimeUs;
            long endUs = kept[^1].TimeUs;
            double extent = Extent(points);
            List<Vec2> thinned = Decimate(points, config.Preprocess.MinPointDistance);
            double rawLength = thinned.Count < 2 ? 0.0 : PolylineMath.Length(CollectionsSpan(thinned));
            bool dot = rawLength < config.Preprocess.DotMaxLength && (endUs - startUs) < (long)(config.Preprocess.DotMaxDurationMs * 1000.0);
            Vec2[] pos = thinned.Count < 2 || !(rawLength > 0.0)
                ? [Centroid(points)]
                : PolylineTools.ResampleSmooth(CollectionsSpan(thinned), config.ResampleStep, filter);
            double length = pos.Length < 2 ? 0.0 : PolylineMath.Length(pos);
            result.Add(new ChildStroke
            {
                InkIndex = stroke.Index,
                Pos = pos,
                StartUs = startUs,
                EndUs = endUs,
                Length = length,
                Extent = extent,
                IsDot = dot,
                Clipped = stroke.Clipped,
            });
        }

        result.Sort(static (a, b) =>
        {
            int c = a.StartUs.CompareTo(b.StartUs);
            return c != 0 ? c : a.InkIndex.CompareTo(b.InkIndex);
        });
        for (int i = 0; i < result.Count; i++)
        {
            result[i].Chrono = i;
        }

        return result;
    }

    /// <summary>
    /// Нормализация для выравнивания (§6.4.2, проход 1): деслантинг к наклону эталона (по квазивертикалям), масштаб по
    /// процентилям v (в конфигурации — квартили; не по bounding box: выбросы на краях не влияют), сдвиг по u по центру тяжести ведущих штрихов.
    /// Масштаб и сдвиг затем уточняются по соответствиям выравнивания (<see cref="Transform"/>, <see cref="SimilarityFit"/>).
    /// Метрики достижения линий считаются по <see cref="ChildStroke.Pos"/> (проход 2) и от нормализации не зависят.
    /// </summary>
    public static NormalizationParams Normalize(List<ChildStroke> strokes, LetterReference reference, ScoringConfig config)
    {
        NormalizationConfig n = config.Normalization;
        double maxLength = 0.0;
        foreach (ChildStroke s in strokes)
        {
            if (!s.IsDot)
            {
                maxLength = Math.Max(maxLength, s.Length);
            }
        }

        var dominant = new List<ChildStroke>();
        foreach (ChildStroke s in strokes)
        {
            if (maxLength <= 0.0 || (!s.IsDot && s.Length >= n.DominantStrokeShare * maxLength))
            {
                dominant.Add(s);
            }
        }

        // Наклон рукописи по ведущим штрихам.
        var angles = new List<double>();
        foreach (ChildStroke s in dominant)
        {
            PolylineTools.CollectSlantAngles(s.Pos, n.Deslant.MinRunLength, n.Deslant.MaxDeviationDeg, angles);
        }

        SlantEstimate inkSlant = SlantEstimator.Summarize(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(angles));
        double? inkSlantDeg = inkSlant.Count > 0 && inkSlant.StdDevDegrees <= n.Deslant.MaxSpreadDeg ? inkSlant.MedianDegrees : null;
        double shear = 0.0;
        if (n.Deslant.Enabled && inkSlantDeg is { } child && reference.SlantDeg is { } template)
        {
            double diff = child - template;
            if (Math.Abs(diff) > n.Deslant.DeadbandDeg)
            {
                shear = Math.Clamp(diff, -n.Deslant.MaxDeg, n.Deslant.MaxDeg);
            }
        }

        var sheared = new List<Vec2[]>(strokes.Count);
        foreach (ChildStroke s in strokes)
        {
            var copy = new Vec2[s.Pos.Length];
            Deslant.ApplyDegrees(s.Pos, copy, shear, 0.0);
            sheared.Add(copy);
        }

        var shearedDominant = new List<Vec2[]>(dominant.Count);
        foreach (ChildStroke s in dominant)
        {
            shearedDominant.Add(sheared[strokes.IndexOf(s)]);
        }

        double lo = PolylineTools.PercentileV(shearedDominant, n.Scale.LowPercentile);
        double hi = PolylineTools.PercentileV(shearedDominant, n.Scale.HighPercentile);
        double inkHeight = hi - lo;
        double refHeight = reference.HighV - reference.LowV;
        double scale = 1.0;
        if (n.Scale.Enabled && inkHeight > 1e-6 && refHeight > 1e-6)
        {
            scale = Math.Clamp(inkHeight / refHeight, n.Scale.Min, n.Scale.Max);
        }

        double centroid = PolylineTools.MeanU(shearedDominant);
        for (int i = 0; i < strokes.Count; i++)
        {
            Vec2[] source = sheared[i];
            var normalized = new Vec2[source.Length];
            for (int k = 0; k < source.Length; k++)
            {
                normalized[k] = new Vec2(
                    reference.CentroidU + ((source[k].X - centroid) / scale),
                    source[k].Y / scale);
            }

            strokes[i].NPos = normalized;
            strokes[i].NDir = PolylineTools.Directions(normalized);
        }

        return new NormalizationParams(shear, scale, reference.CentroidU - (centroid / scale), 0.0, inkSlantDeg, reference.SlantDeg);
    }

    /// <summary>Применяет преобразование подобия к нормализованным точкам всех штрихов (направления не меняются).</summary>
    public static void Transform(List<ChildStroke> strokes, Similarity transform)
    {
        foreach (ChildStroke s in strokes)
        {
            for (int k = 0; k < s.NPos.Length; k++)
            {
                s.NPos[k] = transform.Apply(s.NPos[k]);
            }
        }
    }

    /// <summary>Прореживание: оставляет точки не ближе <paramref name="minDistance"/> друг к другу; концы сохраняются.</summary>
    private static List<Vec2> Decimate(List<Vec2> points, double minDistance)
    {
        var result = new List<Vec2>(points.Count) { points[0] };
        for (int i = 1; i < points.Count - 1; i++)
        {
            if (Vec2.Distance(points[i], result[^1]) >= minDistance)
            {
                result.Add(points[i]);
            }
        }

        if (points.Count > 1)
        {
            Vec2 last = points[^1];
            if (result.Count > 1 && Vec2.Distance(last, result[^1]) < 0.5 * minDistance)
            {
                result[^1] = last;
            }
            else if (Vec2.Distance(last, result[^1]) > 1e-9)
            {
                result.Add(last);
            }
        }

        return result;
    }

    private static Vec2 Centroid(List<Vec2> points)
    {
        double x = 0.0;
        double y = 0.0;
        foreach (Vec2 p in points)
        {
            x += p.X;
            y += p.Y;
        }

        return new Vec2(x / points.Count, y / points.Count);
    }

    private static double Extent(List<Vec2> points)
    {
        double minX = double.MaxValue;
        double minY = double.MaxValue;
        double maxX = double.MinValue;
        double maxY = double.MinValue;
        foreach (Vec2 p in points)
        {
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }

        return Math.Sqrt(((maxX - minX) * (maxX - minX)) + ((maxY - minY) * (maxY - minY)));
    }

    private static ReadOnlySpan<Vec2> CollectionsSpan(List<Vec2> list) => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list);

    /// <summary>Убирает точки-«шпильки»: оба шага к точке длиннее порога, а соседи между собой близки.</summary>
    private static void RemoveSpikes(List<Vec2> points, List<RulerPoint> kept, double jumpMedianSteps)
    {
        if (points.Count < 4)
        {
            return;
        }

        var steps = new double[points.Count - 1];
        for (int i = 0; i < steps.Length; i++)
        {
            steps[i] = Vec2.Distance(points[i], points[i + 1]);
        }

        double[] sorted = (double[])steps.Clone();
        Array.Sort(sorted);
        double median = sorted[sorted.Length / 2];
        if (!(median > 0.0))
        {
            return;
        }

        double limit = jumpMedianSteps * median;
        for (int i = points.Count - 2; i >= 1; i--)
        {
            if (Vec2.Distance(points[i - 1], points[i]) > limit
                && Vec2.Distance(points[i], points[i + 1]) > limit
                && Vec2.Distance(points[i - 1], points[i + 1]) <= limit)
            {
                points.RemoveAt(i);
                kept.RemoveAt(i);
            }
        }
    }
}
