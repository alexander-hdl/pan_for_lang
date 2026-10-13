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
}
