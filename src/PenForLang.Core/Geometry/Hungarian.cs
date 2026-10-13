namespace PenForLang.Core.Geometry;

/// <summary>
/// Венгерский алгоритм: назначение минимальной суммарной стоимости для прямоугольной матрицы (метод потенциалов, O(r²·c)
/// при r ≤ c). Используется для сопоставления штрихов рукописи и эталона.
/// </summary>
public static class Hungarian
{
    /// <summary>
    /// Решает задачу о назначениях для матрицы <paramref name="rows"/> × <paramref name="cols"/> (хранится построчно).
    /// Если строк не больше столбцов, каждой строке достаётся свой столбец; если строк больше, свой столбец получают
    /// <paramref name="cols"/> строк, остальные остаются без назначения (−1) — в сумме минимальная стоимость среди всех
    /// максимальных паросочетаний. Все стоимости должны быть конечными (для запрета пары используйте большое конечное число).
    /// При равных стоимостях результат детерминирован.
    /// </summary>
    /// <param name="costs">Матрица стоимостей длиной <c>rows · cols</c>.</param>
    /// <param name="rows">Число строк, ≥ 0.</param>
    /// <param name="cols">Число столбцов, ≥ 0.</param>
    /// <param name="rowToCol">Буфер длиной не меньше <paramref name="rows"/>: для каждой строки номер столбца или −1.</param>
    /// <returns>Суммарная стоимость назначенных пар.</returns>
    public static double Solve(ReadOnlySpan<double> costs, int rows, int cols, Span<int> rowToCol)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rows);
        ArgumentOutOfRangeException.ThrowIfNegative(cols);
        if (costs.Length != checked(rows * cols))
        {
            throw new ArgumentException("Cost matrix length must equal rows * cols.", nameof(costs));
        }

        if (rowToCol.Length < rows)
        {
            throw new ArgumentException("Destination is shorter than the number of rows.", nameof(rowToCol));
        }

        for (int k = 0; k < costs.Length; k++)
        {
            if (!double.IsFinite(costs[k]))
            {
                throw new ArgumentException("All costs must be finite.", nameof(costs));
            }
        }

        rowToCol[..rows].Fill(-1);
        if (rows == 0 || cols == 0)
        {
            return 0.0;
        }

        if (rows <= cols)
        {
            return SolveWide(costs, rows, cols, transposed: false, rowToCol);
        }

        // Строк больше столбцов: решаем транспонированную задачу (строки ↔ столбцы).
        var transposedCosts = new double[costs.Length];
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                transposedCosts[(c * rows) + r] = costs[(r * cols) + c];
            }
        }

        return SolveWide(transposedCosts, cols, rows, transposed: true, rowToCol);
    }

    // rows <= cols. Если transposed, "строки" задачи — исходные столбцы, а результат записывается в исходные строки.
    private static double SolveWide(ReadOnlySpan<double> a, int n, int m, bool transposed, Span<int> rowToCol)
    {
        var u = new double[n + 1];
        var v = new double[m + 1];
        var p = new int[m + 1];
        var way = new int[m + 1];
        var minv = new double[m + 1];
        var used = new bool[m + 1];

        for (int i = 1; i <= n; i++)
        {
            p[0] = i;
            int j0 = 0;
            Array.Fill(minv, double.PositiveInfinity);
            Array.Clear(used);
            do
            {
                used[j0] = true;
                int i0 = p[j0];
                double delta = double.PositiveInfinity;
                int j1 = 0;
                for (int j = 1; j <= m; j++)
                {
                    if (used[j])
                    {
                        continue;
                    }

                    double cur = a[((i0 - 1) * m) + (j - 1)] - u[i0] - v[j];
                    if (cur < minv[j])
                    {
                        minv[j] = cur;
                        way[j] = j0;
                    }

                    if (minv[j] < delta)
                    {
                        delta = minv[j];
                        j1 = j;
                    }
                }

                for (int j = 0; j <= m; j++)
                {
                    if (used[j])
                    {
                        u[p[j]] += delta;
                        v[j] -= delta;
                    }
                    else
                    {
                        minv[j] -= delta;
                    }
                }

                j0 = j1;
            }
            while (p[j0] != 0);

            do
            {
                int j1 = way[j0];
                p[j0] = p[j1];
                j0 = j1;
            }
            while (j0 != 0);
        }

        // p[j] — строка задачи, назначенная столбцу j. Сумма считается по исходным числам (без накопленной погрешности потенциалов).
        double total = 0.0;
        for (int j = 1; j <= m; j++)
        {
            if (p[j] == 0)
            {
                continue;
            }

            int row = p[j] - 1;
            int col = j - 1;
            total += a[(row * m) + col];
            if (transposed)
            {
                rowToCol[col] = row;
            }
            else
            {
                rowToCol[row] = col;
            }
        }

        return total;
    }
}
