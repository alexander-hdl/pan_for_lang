using PenForLang.Core.Geometry;

namespace PenForLang.Evaluation.Pipeline;

/// <summary>Мелкие операции над ломаными, общие для эталона и рукописи.</summary>
internal static class PolylineTools
{
    /// <summary>Полуокно (в вершинах) для измерения замкнутости: расстояние до короткого участка, а не до одной точки.</summary>
    public const int ClosureHalfWindow = 3;

    /// <summary>Ресэмплинг по длине дуги с шагом, ближайшим к <paramref name="step"/>, и сглаживание фильтром.</summary>
    public static Vec2[] ResampleSmooth(ReadOnlySpan<Vec2> source, double step, SavitzkyGolayFilter filter)
    {
        Vec2[] resampled = Resampler.ResampleByStep(source, step);
        if (resampled.Length < 3)
        {
            return resampled;
        }

        var smoothed = new Vec2[resampled.Length];
        filter.Apply(resampled, smoothed);
        return smoothed;
    }

    /// <summary>
    /// Углы наклона всех квазивертикальных участков штриха независимо от направления обхода: участок, идущий вверх, виден как
    /// нисходящий в обращённой копии. Так оценка наклона не зависит от того, в какую сторону написан штрих (P2 не портит нормализацию).
    /// </summary>
    public static void CollectSlantAngles(Vec2[] points, double minRunLength, double maxDeviationDeg, List<double> angles)
    {
        SlantEstimator.CollectAngles(points, minRunLength, maxDeviationDeg, angles);
        SlantEstimator.CollectAngles(Reversed(points), minRunLength, maxDeviationDeg, angles);
    }

    public static Vec2[] Directions(Vec2[] points)
    {
        var result = new Vec2[points.Length];
        DirectionFeatures.FromPolyline(points, result);
        return result;
    }

    public static Vec2[] Reversed(Vec2[] points)
    {
        var result = new Vec2[points.Length];
        PolylineMath.Reverse(points, result);
        return result;
    }

    public static Vec2[] ReversedDirections(Vec2[] directions)
    {
        var result = new Vec2[directions.Length];
        for (int i = 0; i < directions.Length; i++)
        {
            result[i] = -directions[directions.Length - 1 - i];
        }

        return result;
    }

    /// <summary>Середина ломаной по длине дуги.</summary>
    public static Vec2 ArcMidpoint(ReadOnlySpan<Vec2> points)
    {
        if (points.Length == 1)
        {
            return points[0];
        }

        var cumulative = new double[points.Length];
        PolylineMath.CumulativeLengths(points, cumulative);
        return PolylineMath.PointAtLength(points, cumulative, cumulative[^1] * 0.5);
    }

    /// <summary>Расстояние от точки до участка ломаной <c>[from − w, from + w]</c> — устойчивая к сдвигу вдоль пути «замкнутость».</summary>
    public static double GapToWindow(ReadOnlySpan<Vec2> points, int toIndex, int fromIndex)
    {
        int lo = Math.Max(0, fromIndex - ClosureHalfWindow);
        int hi = Math.Min(points.Length - 1, fromIndex + ClosureHalfWindow);
        return PolylineMath.DistanceToPolyline(points[toIndex], points[lo..(hi + 1)]);
    }

    /// <summary>Доля точек <c>[from, to]</c>, лежащих не дальше <paramref name="tolerance"/> от ломаной, написанной раньше (<c>[0, from − 1]</c>).</summary>
    public static double ShareOnPriorInk(ReadOnlySpan<Vec2> points, int from, int to, double tolerance)
    {
        ReadOnlySpan<Vec2> prior = points[..from];
        int inside = 0;
        int count = 0;
        for (int i = from; i <= to && i < points.Length; i++)
        {
            count++;
            if (PolylineMath.DistanceToPolyline(points[i], prior) <= tolerance)
            {
                inside++;
            }
        }

        return count == 0 ? 1.0 : (double)inside / count;
    }

    /// <summary>Процентиль значений v (метод ближайшего ранга); <paramref name="p"/> в [0, 1].</summary>
    public static double PercentileV(IReadOnlyList<Vec2[]> strokes, double p)
    {
        int total = 0;
        foreach (Vec2[] s in strokes)
        {
            total += s.Length;
        }

        var values = new double[total];
        int k = 0;
        foreach (Vec2[] s in strokes)
        {
            foreach (Vec2 point in s)
            {
                values[k++] = point.Y;
            }
        }

        Array.Sort(values);
        int index = (int)Math.Round(p * (total - 1), MidpointRounding.AwayFromZero);
        return values[Math.Clamp(index, 0, total - 1)];
    }

    public static double MeanU(IReadOnlyList<Vec2[]> strokes)
    {
        double sum = 0.0;
        int count = 0;
        foreach (Vec2[] s in strokes)
        {
            foreach (Vec2 point in s)
            {
                sum += point.X;
                count++;
            }
        }

        return count == 0 ? 0.0 : sum / count;
    }

    /// <summary>Угол между векторами, градусы, [0, 180]; для нулевого вектора — 0.</summary>
    public static double AngleDegrees(Vec2 a, Vec2 b)
    {
        double la = a.Length;
        double lb = b.Length;
        if (!(la > 0.0) || !(lb > 0.0))
        {
            return 0.0;
        }

        double cos = Math.Clamp(Vec2.Dot(a, b) / (la * lb), -1.0, 1.0);
        return Math.Acos(cos) * (180.0 / Math.PI);
    }

    /// <summary>Вершина на расстоянии не менее <paramref name="distance"/> по дуге от <paramref name="start"/> (или последняя).</summary>
    public static int IndexAtArc(ReadOnlySpan<Vec2> points, int start, int direction, double distance)
    {
        double travelled = 0.0;
        int i = start;
        while (true)
        {
            int next = i + direction;
            if (next < 0 || next >= points.Length)
            {
                return i;
            }

            travelled += Vec2.Distance(points[i], points[next]);
            i = next;
            if (travelled >= distance)
            {
                return i;
            }
        }
    }
}
