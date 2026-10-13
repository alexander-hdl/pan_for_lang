namespace PenForLang.Core.Geometry;

/// <summary>Дискретное расстояние Фреше (Эйтер–Маннила) между двумя ломаными по их вершинам.</summary>
public static class Frechet
{
    private const int StackLimit = 256;

    /// <summary>
    /// Дискретное расстояние Фреше: минимум по всем монотонным «сцеплениям» вершин (шаги <c>(i+1, j)</c>, <c>(i, j+1)</c>,
    /// <c>(i+1, j+1)</c>, начало в <c>(0, 0)</c>, конец в <c>(n−1, m−1)</c>) максимума расстояния между сцепленными вершинами.
    /// Время O(n·m), память O(m). Направление важно: у обращённой кривой расстояние обычно больше.
    /// </summary>
    /// <param name="p">Вершины первой ломаной (не менее одной).</param>
    /// <param name="q">Вершины второй ломаной (не менее одной).</param>
    public static double Distance(ReadOnlySpan<Vec2> p, ReadOnlySpan<Vec2> q)
    {
        if (p.Length == 0 || q.Length == 0)
        {
            throw new ArgumentException("Both polylines must be non-empty.");
        }

        int m = q.Length;
        Span<double> row = m <= StackLimit ? stackalloc double[m] : new double[m];

        // Сравниваем квадраты расстояний (sqrt монотонна), корень берём один раз в конце.
        for (int i = 0; i < p.Length; i++)
        {
            double diagonal = 0.0; // значение ca[i-1][j-1] до перезаписи
            for (int j = 0; j < m; j++)
            {
                double d = Vec2.DistanceSquared(p[i], q[j]);
                double previousRowSame = row[j]; // ca[i-1][j]
                double value;
                if (i == 0 && j == 0)
                {
                    value = d;
                }
                else if (i == 0)
                {
                    value = Math.Max(row[j - 1], d);
                }
                else if (j == 0)
                {
                    value = Math.Max(previousRowSame, d);
                }
                else
                {
                    value = Math.Max(Math.Min(Math.Min(previousRowSame, row[j - 1]), diagonal), d);
                }

                diagonal = previousRowSame;
                row[j] = value;
            }
        }

        return Math.Sqrt(row[m - 1]);
    }
}
