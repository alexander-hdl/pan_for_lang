using PenForLang.Core.Geometry;

namespace PenForLang.Core.Tests.Geometry;

/// <summary>
/// Наивная эталонная реализация DTW для сверки: полная матрица n×m, допустимость клетки — предикатом,
/// без диапазонов строк и без рабочих буферов. Правило выбора при равенстве то же, что у боевой реализации
/// (диагональ, затем шаг по i, затем по j).
/// </summary>
internal static class NaiveDtw
{
    internal readonly record struct Result(bool Feasible, double Cost, List<DtwCell> Path);

    public static Result Run(
        int n,
        int m,
        Func<int, int, double> cost,
        Func<int, int, bool> allowed,
        bool openBegin,
        bool openEnd)
    {
        double inf = double.PositiveInfinity;
        var d = new double[n, m];
        var dir = new int[n, m]; // 0 старт, 1 диагональ, 2 вверх (i-1), 3 влево (j-1), -1 нет
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < m; j++)
            {
                d[i, j] = inf;
                dir[i, j] = -1;
                if (!allowed(i, j))
                {
                    continue;
                }

                double best = inf;
                int code = -1;
                if (i == 0)
                {
                    if (openBegin || j == 0)
                    {
                        best = 0.0;
                        code = 0;
                    }
                    else
                    {
                        best = d[0, j - 1];
                        code = 3;
                    }
                }
                else
                {
                    if (j > 0)
                    {
                        best = d[i - 1, j - 1];
                        code = 1;
                    }

                    if (d[i - 1, j] < best)
                    {
                        best = d[i - 1, j];
                        code = 2;
                    }

                    if (j > 0 && d[i, j - 1] < best)
                    {
                        best = d[i, j - 1];
                        code = 3;
                    }
                }

                d[i, j] = best + cost(i, j);
                dir[i, j] = code;
            }
        }

        int endJ = -1;
        if (openEnd)
        {
            double bestEnd = inf;
            for (int j = 0; j < m; j++)
            {
                if (d[n - 1, j] < bestEnd)
                {
                    bestEnd = d[n - 1, j];
                    endJ = j;
                }
            }
        }
        else if (!double.IsPositiveInfinity(d[n - 1, m - 1]))
        {
            endJ = m - 1;
        }

        if (endJ < 0)
        {
            return new Result(false, inf, []);
        }

        var path = new List<DtwCell>();
        int ci = n - 1;
        int cj = endJ;
        while (true)
        {
            path.Add(new DtwCell(ci, cj));
            int code = dir[ci, cj];
            if (code == 0)
            {
                break;
            }

            if (code == 1)
            {
                ci--;
                cj--;
            }
            else if (code == 2)
            {
                ci--;
            }
            else
            {
                cj--;
            }
        }

        path.Reverse();
        return new Result(true, d[n - 1, endJ], path);
    }

    /// <summary>Независимая проверка стоимости: рекурсия сверху вниз с мемоизацией (без пути и открытого начала).</summary>
    public static double TopDownCost(int n, int m, Func<int, int, double> cost, Func<int, int, bool> allowed)
    {
        var memo = new Dictionary<(int, int), double>();

        double Rec(int i, int j)
        {
            if (i < 0 || j < 0 || !allowed(i, j))
            {
                return double.PositiveInfinity;
            }

            if (memo.TryGetValue((i, j), out double cached))
            {
                return cached;
            }

            double best;
            if (i == 0 && j == 0)
            {
                best = 0.0;
            }
            else
            {
                best = Math.Min(Rec(i - 1, j), Math.Min(Rec(i, j - 1), Rec(i - 1, j - 1)));
            }

            double value = best + cost(i, j);
            memo[(i, j)] = value;
            return value;
        }

        return Rec(n - 1, m - 1);
    }
}
