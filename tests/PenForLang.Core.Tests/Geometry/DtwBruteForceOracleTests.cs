using PenForLang.Core.Geometry;

namespace PenForLang.Core.Tests.Geometry;

/// <summary>
/// Независимый оракул G13: полный перебор всех монотонных путей (шаги вправо/вниз/по диагонали) при n, m ≤ 6 с явными
/// множествами начала и конца — без общего с боевой реализацией кода открытых концов — и тождества
/// «open-begin = min по j0 закрытого DTW на b[j0..]», «open-end = min по j1 закрытого DTW на b[..j1]».
/// Матрицы стоимостей случайные, с нулями, повторами и +∞.
/// </summary>
public class DtwBruteForceOracleTests
{
    private const int Cases = 1000;

    private static double[,] RandomMatrix(Pcg32 rng, int n, int m, int flavour)
    {
        var c = new double[n, m];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < m; j++)
            {
                double v = flavour switch
                {
                    0 => rng.NextDouble() * 10.0,
                    1 => rng.NextInt32(3), // много нулей и равенств
                    2 => rng.NextInt32(2) == 0 ? 0.0 : rng.NextDouble(),
                    _ => rng.NextInt32(7) == 0 ? double.PositiveInfinity : rng.NextInt32(4),
                };
                c[i, j] = v;
            }
        }

        return c;
    }

    private static double BruteForce(double[,] c, int n, int m, bool openBegin, bool openEnd)
    {
        double best = double.PositiveInfinity;
        var starts = openBegin ? Enumerable.Range(0, m).ToArray() : [0];

        void Walk(int i, int j, double acc)
        {
            double cell = c[i, j];
            if (double.IsPositiveInfinity(cell))
            {
                return;
            }

            acc += cell;
            if (i == n - 1 && (openEnd || j == m - 1))
            {
                best = Math.Min(best, acc);
            }

            if (i + 1 < n)
            {
                Walk(i + 1, j, acc);
            }

            if (j + 1 < m)
            {
                Walk(i, j + 1, acc);
            }

            if (i + 1 < n && j + 1 < m)
            {
                Walk(i + 1, j + 1, acc);
            }
        }

        foreach (int j0 in starts)
        {
            Walk(0, j0, 0.0);
        }

        return best;
    }

    private static DtwResult Run(double[,] c, int n, int m, bool openBegin, bool openEnd, List<DtwCell>? path = null) =>
        Dtw.Align(n, m, new DelegateCost((i, j) => c[i, j]), new DtwOptions { OpenBegin = openBegin, OpenEnd = openEnd }, null, path);

    private static void AssertSame(double expected, DtwResult actual, string label)
    {
        if (double.IsPositiveInfinity(expected))
        {
            Assert.False(actual.IsFeasible, $"{label}: expected infeasible");
        }
        else
        {
            Assert.True(actual.IsFeasible, $"{label}: expected feasible, cost {expected}");
            Assert.True(Math.Abs(expected - actual.Cost) <= 1e-9, $"{label}: {actual.Cost} vs brute force {expected}");
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Matches_exhaustive_path_enumeration_on_1000_random_cost_matrices(bool openBegin, bool openEnd)
    {
        var rng = new Pcg32(0xB07E + (ulong)((openBegin ? 1 : 0) + (openEnd ? 2 : 0)));
        for (int k = 0; k < Cases; k++)
        {
            int n = 1 + rng.NextInt32(6);
            int m = 1 + rng.NextInt32(6);
            double[,] c = RandomMatrix(rng, n, m, k % 4);
            double expected = BruteForce(c, n, m, openBegin, openEnd);
            var path = new List<DtwCell>();
            DtwResult r = Run(c, n, m, openBegin, openEnd, path);
            AssertSame(expected, r, $"#{k} ({n}x{m}, begin={openBegin}, end={openEnd}, flavour {k % 4})");

            if (r.IsFeasible)
            {
                double sum = path.Sum(p => c[p.I, p.J]);
                Assert.True(Math.Abs(sum - r.Cost) <= 1e-9, $"#{k}: path sum");
            }
        }
    }

    [Fact]
    public void Open_begin_and_open_end_equal_minima_of_closed_dtw_on_sub_references()
    {
        var rng = new Pcg32(0x1DE7);
        for (int k = 0; k < Cases; k++)
        {
            int n = 1 + rng.NextInt32(6);
            int m = 1 + rng.NextInt32(6);
            double[,] c = RandomMatrix(rng, n, m, k % 4);

            DtwResult Closed(int j0, int j1) => Dtw.Align(
                n, j1 - j0 + 1, new DelegateCost((i, j) => c[i, j0 + j]));

            double openBeginExpected = double.PositiveInfinity;
            double openEndExpected = double.PositiveInfinity;
            double bothExpected = double.PositiveInfinity;
            for (int j0 = 0; j0 < m; j0++)
            {
                DtwResult suffix = Closed(j0, m - 1);
                if (suffix.IsFeasible)
                {
                    openBeginExpected = Math.Min(openBeginExpected, suffix.Cost);
                }

                for (int j1 = j0; j1 < m; j1++)
                {
                    DtwResult window = Closed(j0, j1);
                    if (window.IsFeasible)
                    {
                        bothExpected = Math.Min(bothExpected, window.Cost);
                        if (j0 == 0)
                        {
                            openEndExpected = Math.Min(openEndExpected, window.Cost);
                        }
                    }
                }
            }

            AssertSame(openBeginExpected, Run(c, n, m, true, false), $"#{k} open-begin identity");
            AssertSame(openEndExpected, Run(c, n, m, false, true), $"#{k} open-end identity");
            AssertSame(bothExpected, Run(c, n, m, true, true), $"#{k} both identity");
        }
    }

    [Fact]
    public void Negative_costs_are_rejected_instead_of_giving_a_wrong_open_begin_minimum()
    {
        // Проба ревью G1: при отрицательных стоимостях открытое начало давало −5 вместо −6.
        double[,] probe = { { -1, -5, 2 }, { 3, 1, 0 } };
        var options = new DtwOptions { OpenBegin = true, OpenEnd = true };
        Assert.Throws<InvalidOperationException>(() => Dtw.Align(2, 3, new DelegateCost((i, j) => probe[i, j]), options));
        Assert.Throws<InvalidOperationException>(() => Dtw.Align(2, 3, new DelegateCost((i, j) => probe[i, j])));
        Assert.Throws<InvalidOperationException>(() => Dtw.Align(1, 1, new DelegateCost((_, _) => -1e-300)));
        Assert.Throws<InvalidOperationException>(() => Dtw.Align(1, 1, new DelegateCost((_, _) => double.NaN)));
        Assert.Throws<InvalidOperationException>(() => Dtw.Align(1, 1, new DelegateCost((_, _) => double.NegativeInfinity)));
        Assert.True(Dtw.Align(1, 1, new DelegateCost((_, _) => double.PositiveInfinity), options).IsFeasible == false);
        Assert.Equal(0.0, Dtw.Align(1, 1, new DelegateCost((_, _) => 0.0)).Cost);
    }

    [Fact]
    public void Infinite_costs_without_a_band_are_respected_by_every_variant()
    {
        // Единственный путь идёт в обход запрещённой клетки (1,1).
        double[,] c = { { 1, 1, 1 }, { 1, double.PositiveInfinity, 1 }, { 1, 1, 1 } };
        DtwResult r = Run(c, 3, 3, false, false);
        Assert.True(r.IsFeasible);
        Assert.Equal(BruteForce(c, 3, 3, false, false), r.Cost);

        // Полностью запрещённая строка делает путь невозможным.
        double inf = double.PositiveInfinity;
        double[,] wall = { { 1, 1 }, { inf, inf }, { 1, 1 } };
        Assert.False(Run(wall, 3, 2, true, true).IsFeasible);
    }
}
