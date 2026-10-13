using PenForLang.Core.Geometry;

namespace PenForLang.Core.Tests.Geometry;

public class HungarianTests
{
    /// <summary>Полный перебор: при rows ≤ cols — инъекции строк в столбцы; иначе — инъекции столбцов в строки.</summary>
    private static double BruteForce(double[] costs, int rows, int cols)
    {
        double best = double.PositiveInfinity;
        if (rows <= cols)
        {
            var usedCols = new bool[cols];

            void Rec(int r, double acc)
            {
                if (r == rows)
                {
                    best = Math.Min(best, acc);
                    return;
                }

                for (int c = 0; c < cols; c++)
                {
                    if (usedCols[c])
                    {
                        continue;
                    }

                    usedCols[c] = true;
                    Rec(r + 1, acc + costs[(r * cols) + c]);
                    usedCols[c] = false;
                }
            }

            Rec(0, 0.0);
        }
        else
        {
            var usedRows = new bool[rows];

            void Rec(int c, double acc)
            {
                if (c == cols)
                {
                    best = Math.Min(best, acc);
                    return;
                }

                for (int r = 0; r < rows; r++)
                {
                    if (usedRows[r])
                    {
                        continue;
                    }

                    usedRows[r] = true;
                    Rec(c + 1, acc + costs[(r * cols) + c]);
                    usedRows[r] = false;
                }
            }

            Rec(0, 0.0);
        }

        return best;
    }

    [Fact]
    public void Known_three_by_three_example()
    {
        double[] costs = [4, 1, 3, 2, 0, 5, 3, 2, 2];
        var assignment = new int[3];
        double total = Hungarian.Solve(costs, 3, 3, assignment);
        Assert.Equal(5.0, total);
        Assert.Equal([1, 0, 2], assignment);
    }

    [Fact]
    public void Matches_brute_force_for_random_matrices_up_to_seven()
    {
        var rng = new Pcg32(41);
        for (int c = 0; c < 1500; c++)
        {
            int rows = 1 + rng.NextInt32(7);
            int cols = 1 + rng.NextInt32(7);
            bool integers = (c % 2) == 0;
            var costs = new double[rows * cols];
            for (int k = 0; k < costs.Length; k++)
            {
                costs[k] = integers ? rng.NextInt32(5) : (rng.NextDouble() * 20.0) - 5.0; // с отрицательными тоже
            }

            var assignment = new int[rows];
            double total = Hungarian.Solve(costs, rows, cols, assignment);
            double expected = BruteForce(costs, rows, cols);
            Assert.True(Math.Abs(expected - total) < 1e-9, $"case {c} ({rows}x{cols}): {total} vs {expected}");

            // Назначение корректно: столбцы различны, число пар = min(rows, cols), сумма совпадает.
            var seen = new HashSet<int>();
            double sum = 0.0;
            int pairs = 0;
            for (int r = 0; r < rows; r++)
            {
                if (assignment[r] < 0)
                {
                    continue;
                }

                Assert.InRange(assignment[r], 0, cols - 1);
                Assert.True(seen.Add(assignment[r]), $"case {c}: column reused");
                sum += costs[(r * cols) + assignment[r]];
                pairs++;
            }

            Assert.Equal(Math.Min(rows, cols), pairs);
            Assert.True(Math.Abs(sum - total) < 1e-9);
        }
    }

    [Fact]
    public void Square_instances_larger_than_brute_force_remain_optimal_against_dp_over_subsets()
    {
        // Независимая точная проверка для n = 10 динамикой по подмножествам.
        var rng = new Pcg32(42);
        for (int c = 0; c < 30; c++)
        {
            const int n = 10;
            var costs = new double[n * n];
            for (int k = 0; k < costs.Length; k++)
            {
                costs[k] = rng.NextDouble() * 100.0;
            }

            var dp = new double[1 << n];
            Array.Fill(dp, double.PositiveInfinity);
            dp[0] = 0.0;
            for (int mask = 0; mask < (1 << n) - 1; mask++)
            {
                int row = System.Numerics.BitOperations.PopCount((uint)mask);
                for (int col = 0; col < n; col++)
                {
                    if ((mask & (1 << col)) == 0)
                    {
                        dp[mask | (1 << col)] = Math.Min(dp[mask | (1 << col)], dp[mask] + costs[(row * n) + col]);
                    }
                }
            }

            double total = Hungarian.Solve(costs, n, n, new int[n]);
            Assert.Equal(dp[(1 << n) - 1], total, 1e-9);
        }
    }

    [Fact]
    public void Rows_exceeding_columns_leave_the_costliest_rows_unassigned()
    {
        // 3 строки, 2 столбца: строка 1 очень дорогая везде.
        double[] costs = [1, 9, 100, 100, 9, 1];
        var assignment = new int[3];
        double total = Hungarian.Solve(costs, 3, 2, assignment);
        Assert.Equal(2.0, total);
        Assert.Equal([0, -1, 1], assignment);
    }

    [Fact]
    public void Empty_and_invalid_inputs()
    {
        var assignment = new int[2];
        Assert.Equal(0.0, Hungarian.Solve([], 0, 5, assignment));
        Assert.Equal(0.0, Hungarian.Solve([], 2, 0, assignment));
        Assert.Equal([-1, -1], assignment);

        Assert.Throws<ArgumentException>(() => Hungarian.Solve([1.0, 2.0], 2, 2, assignment));
        Assert.Throws<ArgumentException>(() => Hungarian.Solve([1.0, double.PositiveInfinity], 1, 2, assignment));
        Assert.Throws<ArgumentException>(() => Hungarian.Solve([1.0, 2.0, 3.0, 4.0], 2, 2, new int[1]));
    }

    [Fact]
    public void Result_is_deterministic_for_tied_costs()
    {
        double[] costs = Enumerable.Repeat(1.0, 25).ToArray();
        var a = new int[5];
        var b = new int[5];
        Hungarian.Solve(costs, 5, 5, a);
        Hungarian.Solve(costs, 5, 5, b);
        Assert.Equal(a, b);
        Assert.Equal(5, a.Distinct().Count());

        // Сверка с известным назначением: при полном равенстве получается тождественная перестановка.
        Assert.Equal([0, 1, 2, 3, 4], a);

        double[] tie = [1, 1, 1, 1];
        var t = new int[2];
        Hungarian.Solve(tie, 2, 2, t);
        Assert.Equal([0, 1], t);
    }

    [Fact]
    public void Unassigned_cost_keeps_an_extra_far_stroke_away_from_a_missing_template_slot()
    {
        // Рукопись: три штриха (x = 0, 1, 9), эталон: два слота (x = 0, 1). Стоимость пары - квадрат расстояния.
        double[] childX = [0.0, 1.0, 9.0];
        double[] slotX = [0.0, 1.0];
        var costs = new double[6];
        for (int r = 0; r < 3; r++)
        {
            for (int c = 0; c < 2; c++)
            {
                costs[(r * 2) + c] = (childX[r] - slotX[c]) * (childX[r] - slotX[c]);
            }
        }

        var assignment = new int[3];
        double total = Hungarian.SolveWithUnassignedCost(costs, 3, 2, 4.0, 4.0, assignment);
        Assert.Equal([0, 1, -1], assignment);
        Assert.Equal(4.0, total); // лишний штрих: штраф 4

        // Слот (x = 5) далёк от обоих штрихов: пропущенный слот остаётся пустым, а не притягивает чужой штрих.
        double[] farSlot = [0.0, 1.0, 5.0];
        double[] child2 = [0.0, 1.0];
        var costs2 = new double[2 * 3];
        for (int r = 0; r < 2; r++)
        {
            for (int c = 0; c < 3; c++)
            {
                costs2[(r * 3) + c] = (child2[r] - farSlot[c]) * (child2[r] - farSlot[c]);
            }
        }

        var a2 = new int[2];
        double t2 = Hungarian.SolveWithUnassignedCost(costs2, 2, 3, 4.0, 4.0, a2);
        Assert.Equal([0, 1], a2);
        Assert.Equal(4.0, t2);

        // Дальняя пара дороже суммы штрафов: оба остаются без пары.
        var a3 = new int[1];
        double t3 = Hungarian.SolveWithUnassignedCost([100.0], 1, 1, 4.0, 4.0, a3);
        Assert.Equal([-1], a3);
        Assert.Equal(8.0, t3);

        // +inf запрещает пару.
        var a4 = new int[1];
        double t4 = Hungarian.SolveWithUnassignedCost([double.PositiveInfinity], 1, 1, 4.0, 4.0, a4);
        Assert.Equal([-1], a4);
        Assert.Equal(8.0, t4);
    }

    [Fact]
    public void Unassigned_cost_matches_brute_force_over_partial_matchings()
    {
        var rng = new Pcg32(43);
        for (int c = 0; c < 400; c++)
        {
            int rows = 1 + rng.NextInt32(4);
            int cols = 1 + rng.NextInt32(4);
            double ur = rng.NextDouble() * 6.0;
            double uc = rng.NextDouble() * 6.0;
            var costs = new double[rows * cols];
            for (int k = 0; k < costs.Length; k++)
            {
                costs[k] = rng.NextInt32(9) == 0 ? double.PositiveInfinity : rng.NextDouble() * 10.0;
            }

            double best = double.PositiveInfinity;
            var usedCols = new bool[cols];

            void Rec(int r, double acc)
            {
                if (r == rows)
                {
                    best = Math.Min(best, acc + (usedCols.Count(u => !u) * uc));
                    return;
                }

                Rec(r + 1, acc + ur); // строка без пары
                for (int col = 0; col < cols; col++)
                {
                    double v = costs[(r * cols) + col];
                    if (usedCols[col] || double.IsPositiveInfinity(v))
                    {
                        continue;
                    }

                    usedCols[col] = true;
                    Rec(r + 1, acc + v);
                    usedCols[col] = false;
                }
            }

            Rec(0, 0.0);
            var assignment = new int[rows];
            double total = Hungarian.SolveWithUnassignedCost(costs, rows, cols, ur, uc, assignment);
            Assert.True(Math.Abs(best - total) < 1e-9, $"case {c} ({rows}x{cols}): {total} vs {best}");
        }
    }
}
