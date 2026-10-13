namespace PenForLang.Core.Geometry;

/// <summary>Базовые операции над полилиниями, заданными последовательностью <see cref="Vec2"/>.</summary>
public static class PolylineMath
{
    /// <summary>Суммарная длина ломаной (0 для менее чем двух точек).</summary>
    /// <param name="points">Вершины ломаной.</param>
    public static double Length(ReadOnlySpan<Vec2> points)
    {
        double total = 0.0;
        for (int i = 1; i < points.Length; i++)
        {
            total += Vec2.Distance(points[i - 1], points[i]);
        }

        return total;
    }

    /// <summary>
    /// Накопленная длина дуги в каждой вершине: <c>destination[0] = 0</c>, <c>destination[i]</c> — длина от начала до вершины <c>i</c>.
    /// </summary>
    /// <param name="points">Вершины ломаной.</param>
    /// <param name="destination">Буфер длиной не меньше числа вершин.</param>
    public static void CumulativeLengths(ReadOnlySpan<Vec2> points, Span<double> destination)
    {
        if (destination.Length < points.Length)
        {
            throw new ArgumentException("Destination is shorter than the polyline.", nameof(destination));
        }

        double total = 0.0;
        for (int i = 0; i < points.Length; i++)
        {
            if (i > 0)
            {
                total += Vec2.Distance(points[i - 1], points[i]);
            }

            destination[i] = total;
        }
    }

    /// <summary>Записывает вершины в обратном порядке (направление обхода меняется на противоположное).</summary>
    /// <param name="points">Исходные вершины.</param>
    /// <param name="destination">Буфер длиной не меньше числа вершин; не должен пересекаться с исходным.</param>
    public static void Reverse(ReadOnlySpan<Vec2> points, Span<Vec2> destination)
    {
        if (destination.Length < points.Length)
        {
            throw new ArgumentException("Destination is shorter than the polyline.", nameof(destination));
        }

        if (points.Overlaps(destination))
        {
            throw new ArgumentException("Source and destination must not overlap.", nameof(destination));
        }

        int n = points.Length;
        for (int i = 0; i < n; i++)
        {
            destination[i] = points[n - 1 - i];
        }
    }

    /// <summary>Расстояние от точки до отрезка <c>[a, b]</c>.</summary>
    /// <param name="p">Точка.</param>
    /// <param name="a">Начало отрезка.</param>
    /// <param name="b">Конец отрезка.</param>
    public static double DistanceToSegment(Vec2 p, Vec2 a, Vec2 b)
    {
        Vec2 ab = b - a;
        double len2 = ab.LengthSquared;
        if (len2 <= 0.0)
        {
            return Vec2.Distance(p, a);
        }

        double t = Math.Clamp(Vec2.Dot(p - a, ab) / len2, 0.0, 1.0);
        return Vec2.Distance(p, a + (t * ab));
    }

    /// <summary>Расстояние от точки до ломаной (для одной вершины — до неё; для пустой — исключение).</summary>
    /// <param name="p">Точка.</param>
    /// <param name="polyline">Вершины ломаной.</param>
    public static double DistanceToPolyline(Vec2 p, ReadOnlySpan<Vec2> polyline)
    {
        if (polyline.Length == 0)
        {
            throw new ArgumentException("Polyline is empty.", nameof(polyline));
        }

        if (polyline.Length == 1)
        {
            return Vec2.Distance(p, polyline[0]);
        }

        double best = double.PositiveInfinity;
        for (int i = 1; i < polyline.Length; i++)
        {
            best = Math.Min(best, DistanceToSegment(p, polyline[i - 1], polyline[i]));
        }

        return best;
    }

    /// <summary>
    /// Точка на расстоянии <paramref name="distance"/> по дуге от начала ломаной (значение обрезается до [0, длина]).
    /// Поиск бинарный, O(log n) на вызов.
    /// </summary>
    /// <param name="points">Вершины ломаной (не менее одной).</param>
    /// <param name="cumulative">Накопленные длины из <see cref="CumulativeLengths"/>.</param>
    /// <param name="distance">Расстояние по дуге.</param>
    public static Vec2 PointAtLength(ReadOnlySpan<Vec2> points, ReadOnlySpan<double> cumulative, double distance)
    {
        if (points.Length == 0)
        {
            throw new ArgumentException("Polyline is empty.", nameof(points));
        }

        if (cumulative.Length < points.Length)
        {
            throw new ArgumentException("Cumulative lengths are shorter than the polyline.", nameof(cumulative));
        }

        if (points.Length == 1 || distance <= 0.0)
        {
            return points[0];
        }

        if (distance >= cumulative[points.Length - 1])
        {
            return points[^1];
        }

        int lo = 0;
        int hi = points.Length - 1;
        while (hi - lo > 1)
        {
            int mid = lo + ((hi - lo) / 2);
            if (cumulative[mid] <= distance)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        double span = cumulative[hi] - cumulative[lo];
        return span > 0.0 ? Vec2.Lerp(points[lo], points[hi], (distance - cumulative[lo]) / span) : points[lo];
    }

    /// <summary>
    /// Расстояние между точками ломаной, лежащими на долях длины дуги <paramref name="fromFraction"/> и
    /// <paramref name="toFraction"/> (0 — начало, 1 — конец). Применяется к участку «замкнутость овала» (<c>closedGap</c>, F2:
    /// участок <c>from..to</c> штриха эталона).
    /// </summary>
    /// <param name="points">Вершины ломаной (не менее одной).</param>
    /// <param name="fromFraction">Доля длины дуги первой точки, [0, 1].</param>
    /// <param name="toFraction">Доля длины дуги второй точки, [0, 1].</param>
    public static double ClosureGap(ReadOnlySpan<Vec2> points, double fromFraction, double toFraction)
    {
        if (points.Length == 0)
        {
            throw new ArgumentException("Polyline is empty.", nameof(points));
        }

        var cumulative = new double[points.Length];
        CumulativeLengths(points, cumulative);
        double total = cumulative[^1];
        Vec2 a = PointAtLength(points, cumulative, Math.Clamp(fromFraction, 0.0, 1.0) * total);
        Vec2 b = PointAtLength(points, cumulative, Math.Clamp(toFraction, 0.0, 1.0) * total);
        return Vec2.Distance(a, b);
    }
}
