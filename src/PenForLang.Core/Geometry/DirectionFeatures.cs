namespace PenForLang.Core.Geometry;

/// <summary>
/// Признаки направления для угловых стоимостей DTW: единичные векторы <c>(cos θ, sin θ)</c>, посчитанные один раз на
/// последовательность (а не в каждой клетке матрицы).
/// </summary>
public static class DirectionFeatures
{
    /// <summary>Единичные векторы по углам в радианах.</summary>
    /// <param name="angles">Углы θ.</param>
    /// <param name="destination">Буфер длиной не меньше числа углов.</param>
    public static void FromAngles(ReadOnlySpan<double> angles, Span<Vec2> destination)
    {
        if (destination.Length < angles.Length)
        {
            throw new ArgumentException("Destination is shorter than the source.", nameof(destination));
        }

        for (int i = 0; i < angles.Length; i++)
        {
            destination[i] = new Vec2(Math.Cos(angles[i]), Math.Sin(angles[i]));
        }
    }

    /// <summary>
    /// Единичные касательные ломаной в вершинах (центральная разность <c>p[i+1] − p[i−1]</c>, на концах — односторонняя).
    /// Если разность нулевая (совпадающие точки), берётся направление предыдущей вершины, а в начале — следующей ненулевой;
    /// у полностью вырожденной ломаной — <c>(1, 0)</c>.
    /// </summary>
    /// <param name="points">Вершины ломаной.</param>
    /// <param name="destination">Буфер длиной не меньше числа вершин.</param>
    public static void FromPolyline(ReadOnlySpan<Vec2> points, Span<Vec2> destination)
    {
        if (destination.Length < points.Length)
        {
            throw new ArgumentException("Destination is shorter than the source.", nameof(destination));
        }

        int n = points.Length;
        bool anyDirection = false;
        Vec2 last = new(1.0, 0.0);
        for (int i = 0; i < n; i++)
        {
            Vec2 d = points[Math.Min(i + 1, n - 1)] - points[Math.Max(i - 1, 0)];
            double len = d.Length;
            if (len > 0.0)
            {
                last = d / len;
                anyDirection = true;
                destination[i] = last;
            }
            else
            {
                destination[i] = anyDirection ? last : new Vec2(double.NaN, double.NaN);
            }
        }

        // Ведущие нулевые направления получают первое ненулевое.
        Vec2 first = last;
        if (anyDirection)
        {
            for (int i = 0; i < n; i++)
            {
                if (!double.IsNaN(destination[i].X))
                {
                    first = destination[i];
                    break;
                }
            }
        }

        for (int i = 0; i < n && double.IsNaN(destination[i].X); i++)
        {
            destination[i] = first;
        }
    }
}
