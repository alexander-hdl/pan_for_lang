using PenForLang.Core.Geometry;

namespace PenForLang.Evaluation.Pipeline;

/// <summary>Мелкие операции над ломаными, общие для эталона и рукописи.</summary>
internal static class PolylineTools
{
    /// <summary>
    /// Ресэмплинг по длине дуги с шагом, ближайшим к <paramref name="step"/> (но не больше <paramref name="maxPoints"/> точек), и
    /// сглаживание фильтром.
    /// </summary>
    public static Vec2[] ResampleSmooth(ReadOnlySpan<Vec2> source, double step, SavitzkyGolayFilter filter, int maxPoints)
    {
        var resampled = new Vec2[Math.Min(Resampler.CountForStep(PolylineMath.Length(source), step), maxPoints)];
        Resampler.Resample(source, resampled);
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

    /// <summary>
    /// Геометрический зазор замыкания: наименьшее расстояние от участка вокруг <paramref name="toIndex"/> (возврат) до ломаной
    /// участка вокруг <paramref name="fromIndex"/> (старт); полуокно — <paramref name="halfWindow"/> вершин. Не зависит от того,
    /// насколько точно выравнивание указало момент возврата вдоль пути.
    /// </summary>
    public static double ClosureGap(ReadOnlySpan<Vec2> points, int toIndex, int fromIndex, int halfWindow)
    {
        int fromLo = Math.Max(0, fromIndex - halfWindow);
        int fromHi = Math.Min(points.Length - 1, fromIndex + halfWindow);
        int toLo = Math.Max(0, toIndex - halfWindow);
        int toHi = Math.Min(points.Length - 1, toIndex + halfWindow);
        ReadOnlySpan<Vec2> start = points[fromLo..(fromHi + 1)];
        double best = double.PositiveInfinity;
        for (int i = toLo; i <= toHi; i++)
        {
            best = Math.Min(best, PolylineMath.DistanceToPolyline(points[i], start));
        }

        return best;
    }

    /// <summary>Ориентированная площадь замкнутой хордой ломаной (формула шнуровки): положительна при обходе против часовой стрелки (ось v вверх).</summary>
    public static double SignedArea(ReadOnlySpan<Vec2> points)
    {
        double twice = 0.0;
        for (int i = 0; i < points.Length; i++)
        {
            Vec2 a = points[i];
            Vec2 b = points[(i + 1) % points.Length];
            twice += Vec2.Cross(a, b);
        }

        return twice * 0.5;
    }

    /// <summary>Число вершин, соответствующее длине <paramref name="length"/> при шаге <paramref name="step"/> (не меньше 1).</summary>
    public static int PointsFor(double length, double step) => Math.Max(1, (int)Math.Round(length / step, MidpointRounding.AwayFromZero));

    /// <summary>
    /// Скользящее среднее по времени: каждая точка заменяется средним по точкам, отстоящим от неё по времени не больше
    /// <paramref name="halfWindowUs"/>; у концов окно симметрично сужается, поэтому начало и конец штриха не сдвигаются.
    /// </summary>
    public static void SmoothByTime(List<Vec2> points, List<long> timesUs, double halfWindowUs)
    {
        int n = points.Count;
        if (n < 3 || !(halfWindowUs > 0.0))
        {
            return;
        }

        var result = new Vec2[n];
        for (int i = 0; i < n; i++)
        {
            double h = Math.Min(halfWindowUs, Math.Min(timesUs[i] - timesUs[0], timesUs[n - 1] - timesUs[i]));
            int lo = i;
            int hi = i;
            while (lo > 0 && timesUs[i] - timesUs[lo - 1] <= h)
            {
                lo--;
            }

            while (hi < n - 1 && timesUs[hi + 1] - timesUs[i] <= h)
            {
                hi++;
            }

            double x = 0.0;
            double y = 0.0;
            for (int k = lo; k <= hi; k++)
            {
                x += points[k].X;
                y += points[k].Y;
            }

            int count = hi - lo + 1;
            result[i] = new Vec2(x / count, y / count);
        }

        for (int i = 0; i < n; i++)
        {
            points[i] = result[i];
        }
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
