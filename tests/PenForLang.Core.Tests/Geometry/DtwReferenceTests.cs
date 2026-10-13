using PenForLang.Core.Geometry;

namespace PenForLang.Core.Tests.Geometry;

/// <summary>
/// DTW всех вариантов против наивной O(NM) реализации на 1000 случайных входах (фиксированный seed, Pcg32).
/// Сверяются стоимость, достижимость, путь целиком, его допустимость и сумма стоимостей вдоль пути.
/// </summary>
public class DtwReferenceTests
{
    private const int Cases = 1000;

    private static readonly DtwWorkspace Shared = new();

    private static Vec2[] RandomPoints(Pcg32 rng, int count, bool integerGrid, bool monotoneX)
    {
        var pts = new Vec2[count];
        double x = 0.0;
        for (int i = 0; i < count; i++)
        {
            double px;
            double py;
            if (integerGrid)
            {
                px = rng.NextInt32(4);
                py = rng.NextInt32(4);
            }
            else
            {
                px = rng.NextDouble() * 5.0;
                py = rng.NextDouble() * 5.0;
            }

            if (monotoneX)
            {
                x += integerGrid ? rng.NextInt32(2) : rng.NextDouble();
                px = x;
            }

            pts[i] = new Vec2(px, py);
        }

        return pts;
    }

    private static void Verify(
        string label,
        int n,
        int m,
        Func<int, int, double> naiveCost,
        Func<int, int, bool> allowed,
        DtwOptions options,
        DtwResult actual,
        List<DtwCell> actualPath)
    {
        NaiveDtw.Result expected = NaiveDtw.Run(n, m, naiveCost, allowed, options.OpenBegin, options.OpenEnd);
        Assert.True(expected.Feasible == actual.IsFeasible, $"{label}: feasibility differs (n={n}, m={m}, naive={expected.Feasible})");
        if (!expected.Feasible)
        {
            Assert.True(double.IsPositiveInfinity(actual.Cost), label);
            Assert.Empty(actualPath);
            Assert.Equal(0, actual.PathLength);
            return;
        }

        Assert.True(Math.Abs(expected.Cost - actual.Cost) <= 1e-9 * Math.Max(1.0, expected.Cost), $"{label}: cost {actual.Cost} != {expected.Cost}");
        Assert.True(expected.Path.SequenceEqual(actualPath), $"{label}: path differs (n={n}, m={m})");
        Assert.Equal(expected.Path.Count, actual.PathLength);
        Assert.Equal(expected.Path[0], actual.Start);
        Assert.Equal(expected.Path[^1], actual.End);

        // Допустимость пути и сумма стоимостей по нему.
        double sum = 0.0;
        for (int k = 0; k < actualPath.Count; k++)
        {
            DtwCell c = actualPath[k];
            Assert.True(allowed(c.I, c.J), $"{label}: path leaves the band at {c}");
            sum += naiveCost(c.I, c.J);
            if (k > 0)
            {
                DtwCell p = actualPath[k - 1];
                int di = c.I - p.I;
                int dj = c.J - p.J;
                Assert.True((di is 0 or 1) && (dj is 0 or 1) && (di + dj) > 0, $"{label}: bad step {p}->{c}");
            }
        }

        Assert.True(Math.Abs(sum - actual.Cost) <= 1e-9 * Math.Max(1.0, sum), $"{label}: path cost sum {sum} != {actual.Cost}");
        Assert.Equal(0, actualPath[0].I);
        if (!options.OpenBegin)
        {
            Assert.Equal(new DtwCell(0, 0), actualPath[0]);
        }

        Assert.Equal(n - 1, actualPath[^1].I);
        if (!options.OpenEnd)
        {
            Assert.Equal(m - 1, actualPath[^1].J);
        }
    }

    [Fact]
    public void Full_matches_naive_on_1000_random_inputs()
    {
        var rng = new Pcg32(20240601);
        for (int c = 0; c < Cases; c++)
        {
            int n = 1 + rng.NextInt32(14);
            int m = 1 + rng.NextInt32(14);
            bool grid = (c % 2) == 0;
            Vec2[] a = RandomPoints(rng, n, grid, false);
            Vec2[] b = RandomPoints(rng, m, grid, false);
            var path = new List<DtwCell>();
            DtwResult r = Dtw.Align(n, m, new SquaredEuclideanCost(a, b), default, Shared, path);
            Func<int, int, double> cost = (i, j) => Vec2.DistanceSquared(a[i], b[j]);
            Verify($"full#{c}", n, m, cost, (_, _) => true, default, r, path);
            Assert.Equal(NaiveDtw.TopDownCost(n, m, cost, (_, _) => true), r.Cost, 1e-9);
        }
    }

    [Fact]
    public void Index_band_matches_naive_on_1000_random_inputs()
    {
        var rng = new Pcg32(20240602);
        for (int c = 0; c < Cases; c++)
        {
            int n = 1 + rng.NextInt32(14);
            int m = 1 + rng.NextInt32(14);
            int radius = rng.NextInt32(6);
            bool grid = (c % 2) == 0;
            Vec2[] a = RandomPoints(rng, n, grid, false);
            Vec2[] b = RandomPoints(rng, m, grid, false);
            var options = new DtwOptions { IndexBandRadius = radius };
            var path = new List<DtwCell>();
            DtwResult r = Dtw.Align(n, m, new SquaredEuclideanCost(a, b), options, Shared, path);
            int eff = Math.Max(radius, Math.Abs(n - m));
            Func<int, int, double> cost = (i, j) => Vec2.DistanceSquared(a[i], b[j]);
            Func<int, int, bool> allowed = (i, j) => Math.Abs(i - j) <= eff;
            Verify($"band#{c}", n, m, cost, allowed, options, r, path);
            Assert.True(r.IsFeasible);
            Assert.Equal(NaiveDtw.TopDownCost(n, m, cost, allowed), r.Cost, 1e-9);
        }
    }

    [Fact]
    public void Index_band_never_beats_full_and_wide_band_equals_full()
    {
        var rng = new Pcg32(7);
        for (int c = 0; c < 200; c++)
        {
            int n = 1 + rng.NextInt32(14);
            int m = 1 + rng.NextInt32(14);
            Vec2[] a = RandomPoints(rng, n, false, false);
            Vec2[] b = RandomPoints(rng, m, false, false);
            double full = Dtw.Align(n, m, new SquaredEuclideanCost(a, b)).Cost;
            double narrow = Dtw.Align(n, m, new SquaredEuclideanCost(a, b), new DtwOptions { IndexBandRadius = 1 }).Cost;
            double wide = Dtw.Align(n, m, new SquaredEuclideanCost(a, b), new DtwOptions { IndexBandRadius = 100 }).Cost;
            Assert.True(narrow >= full - 1e-12);
            Assert.Equal(full, wide);
        }
    }

    [Fact]
    public void X_band_matches_naive_on_1000_random_inputs()
    {
        var rng = new Pcg32(20240603);
        int infeasible = 0;
        for (int c = 0; c < Cases; c++)
        {
            int n = 1 + rng.NextInt32(14);
            int m = 1 + rng.NextInt32(14);
            bool grid = (c % 2) == 0;
            bool monotoneA = (c % 3) != 0;
            bool monotoneB = (c % 4) != 0; // часть эталонов немонотонна по x: проверяет общий путь поиска диапазонов
            Vec2[] a = RandomPoints(rng, n, grid, monotoneA);
            Vec2[] b = RandomPoints(rng, m, grid, monotoneB);
            double[] xa = a.Select(p => p.X).ToArray();
            double[] xb = b.Select(p => p.X).ToArray();
            double band = grid ? rng.NextInt32(4) : rng.NextDouble() * 2.5;
            int? radius = (c % 5) == 0 ? rng.NextInt32(5) : null;
            var options = new DtwOptions { IndexBandRadius = radius, OpenBegin = (c % 7) == 0, OpenEnd = (c % 11) == 0 };
            var path = new List<DtwCell>();
            DtwResult r = Dtw.AlignXBand(xa, xb, band, new SquaredEuclideanCost(a, b), options, Shared, path);

            int eff = radius.HasValue ? Math.Max(radius.Value, Math.Abs(n - m)) : int.MaxValue;
            Func<int, int, double> cost = (i, j) => Vec2.DistanceSquared(a[i], b[j]);
            Func<int, int, bool> allowed = (i, j) => Math.Abs(i - j) <= eff && Math.Abs(xa[i] - xb[j]) <= band;
            Verify($"xband#{c}", n, m, cost, allowed, options, r, path);
            if (!r.IsFeasible)
            {
                infeasible++;
            }
        }

        // Тест осмыслен, только если полоса действительно режет и допускает пути.
        Assert.InRange(infeasible, 20, Cases - 100);
    }

    [Fact]
    public void X_band_forbids_cells_outside_the_band_and_reports_infeasible_when_no_path()
    {
        double[] xa = [0.0, 1.0, 2.0];
        double[] xb = [10.0, 11.0, 12.0];
        var r = Dtw.AlignXBand(xa, xb, 1.0, new DelegateCost((_, _) => 1.0));
        Assert.False(r.IsFeasible);
        Assert.True(double.IsPositiveInfinity(r.Cost));
        Assert.True(double.IsPositiveInfinity(r.NormalizedCost));

        // Широкая полоса эквивалентна полному DTW.
        var wide = Dtw.AlignXBand(xa, xb, 100.0, new DelegateCost((i, j) => Math.Abs(i - j)));
        var full = Dtw.Align(3, 3, new DelegateCost((i, j) => Math.Abs(i - j)));
        Assert.Equal(full.Cost, wide.Cost);
    }

    [Fact]
    public void Open_begin_and_open_end_match_naive_on_1000_random_inputs()
    {
        var rng = new Pcg32(20240604);
        for (int c = 0; c < Cases; c++)
        {
            int n = 1 + rng.NextInt32(10);
            int m = 1 + rng.NextInt32(16);
            bool grid = (c % 2) == 0;
            Vec2[] a = RandomPoints(rng, n, grid, false);
            Vec2[] b = RandomPoints(rng, m, grid, false);
            bool openBegin = (c % 4) is 0 or 1;
            bool openEnd = (c % 4) is 0 or 2;
            int? radius = (c % 3) == 0 ? rng.NextInt32(5) : null;
            var options = new DtwOptions { OpenBegin = openBegin, OpenEnd = openEnd, IndexBandRadius = radius };
            var path = new List<DtwCell>();
            DtwResult r = Dtw.Align(n, m, new SquaredEuclideanCost(a, b), options, Shared, path);
            int eff = radius.HasValue ? Math.Max(radius.Value, Math.Abs(n - m)) : int.MaxValue;
            Func<int, int, double> cost = (i, j) => Vec2.DistanceSquared(a[i], b[j]);
            Verify($"open#{c}", n, m, cost, (i, j) => Math.Abs(i - j) <= eff, options, r, path);
        }
    }

    [Fact]
    public void Open_end_finds_best_prefix_and_open_begin_finds_embedded_subsequence()
    {
        double[] reference = [0, 1, 2, 3, 4, 5, 6, 7];
        double[] prefix = [0, 1, 2, 3];
        var end = Dtw.Align(4, 8, new SquaredScalarCost(prefix, reference), new DtwOptions { OpenEnd = true });
        Assert.Equal(0.0, end.Cost);
        Assert.Equal(new DtwCell(3, 3), end.End);

        double[] inner = [3, 4, 5];
        var begin = Dtw.Align(3, 8, new SquaredScalarCost(inner, reference), new DtwOptions { OpenBegin = true, OpenEnd = true });
        Assert.Equal(0.0, begin.Cost);
        Assert.Equal(new DtwCell(0, 3), begin.Start);
        Assert.Equal(new DtwCell(2, 5), begin.End);
    }

    [Fact]
    public void Multi_feature_matches_naive_on_1000_random_inputs()
    {
        var rng = new Pcg32(20240605);
        for (int c = 0; c < Cases; c++)
        {
            int n = 1 + rng.NextInt32(12);
            int m = 1 + rng.NextInt32(12);
            int dim = 1 + rng.NextInt32(4);
            var a = new double[n * dim];
            var b = new double[m * dim];
            for (int k = 0; k < a.Length; k++)
            {
                a[k] = (rng.NextDouble() * 6.0) - 3.0;
            }

            for (int k = 0; k < b.Length; k++)
            {
                b[k] = (rng.NextDouble() * 6.0) - 3.0;
            }

            var weights = new double[dim];
            var kinds = new FeatureKind[dim];
            for (int k = 0; k < dim; k++)
            {
                weights[k] = (c % 5 == 0 && k == 0) ? 0.0 : rng.NextDouble() * 3.0;
                kinds[k] = (FeatureKind)rng.NextInt32(3);
            }

            bool withKinds = (c % 3) != 0;
            Func<int, int, double> cost = (i, j) =>
            {
                double s = 0.0;
                for (int k = 0; k < dim; k++)
                {
                    double d = a[(i * dim) + k] - b[(j * dim) + k];
                    FeatureKind kind = withKinds ? kinds[k] : FeatureKind.Squared;
                    double f = kind == FeatureKind.Absolute ? Math.Abs(d) : kind == FeatureKind.Angular ? 1.0 - Math.Cos(d) : d * d;
                    s += weights[k] * f;
                }

                return s;
            };

            var options = new DtwOptions { OpenEnd = (c % 4) == 0, IndexBandRadius = (c % 6) == 0 ? 2 : null };
            var path = new List<DtwCell>();
            var featureCost = new WeightedFeatureCost(a, b, dim, weights, withKinds ? kinds : default);
            DtwResult r = Dtw.Align(n, m, featureCost, options, Shared, path);
            int eff = options.IndexBandRadius.HasValue ? Math.Max(options.IndexBandRadius.Value, Math.Abs(n - m)) : int.MaxValue;
            Verify($"features#{c}", n, m, cost, (i, j) => Math.Abs(i - j) <= eff, options, r, path);
        }
    }

    [Fact]
    public void Reversed_reference_alignment_matches_naive_on_reversed_input_for_1000_inputs()
    {
        var rng = new Pcg32(20240606);
        int reversedWins = 0;
        for (int c = 0; c < Cases; c++)
        {
            int n = 1 + rng.NextInt32(12);
            int m = 1 + rng.NextInt32(12);
            Vec2[] a = RandomPoints(rng, n, (c % 2) == 0, false);
            Vec2[] b = RandomPoints(rng, m, (c % 2) == 0, false);
            var options = new DtwOptions { OpenEnd = (c % 5) == 0, IndexBandRadius = (c % 4) == 0 ? 3 : null };
            var fwdPath = new List<DtwCell>();
            var revPath = new List<DtwCell>();
            DtwDirectionComparison cmp = Dtw.CompareWithReversedReference(
                n, m, new SquaredEuclideanCost(a, b), options, Shared, fwdPath, revPath);

            int eff = options.IndexBandRadius.HasValue ? Math.Max(options.IndexBandRadius.Value, Math.Abs(n - m)) : int.MaxValue;
            Func<int, int, bool> allowed = (i, j) => Math.Abs(i - j) <= eff;
            Func<int, int, double> fwdCost = (i, j) => Vec2.DistanceSquared(a[i], b[j]);
            Verify($"fwd#{c}", n, m, fwdCost, allowed, options, cmp.Forward, fwdPath);

            // Эталон наивно обращаем и выравниваем «в лоб», потом приводим индексы к исходному эталону.
            Vec2[] br = b.Reverse().ToArray();
            Func<int, int, double> revCost = (i, j) => Vec2.DistanceSquared(a[i], br[j]);
            NaiveDtw.Result expected = NaiveDtw.Run(n, m, revCost, allowed, false, options.OpenEnd);
            Assert.Equal(expected.Feasible, cmp.Reversed.IsFeasible);
            if (expected.Feasible)
            {
                Assert.Equal(expected.Cost, cmp.Reversed.Cost, 1e-9);
                Assert.True(
                    expected.Path.Select(p => new DtwCell(p.I, m - 1 - p.J)).SequenceEqual(revPath),
                    $"reversed#{c}: mapped path differs");
                Assert.Equal(revPath[^1], cmp.Reversed.End);
                Assert.Equal(revPath[0], cmp.Reversed.Start);
            }

            if (cmp.ReversedIsBetter)
            {
                reversedWins++;
            }
        }

        Assert.InRange(reversedWins, 50, Cases - 50);
    }

    [Fact]
    public void Reversed_reference_detects_stroke_drawn_backwards()
    {
        // Дуга, нарисованная в обратном направлении, ближе к обращённому эталону.
        var reference = new Vec2[40];
        for (int i = 0; i < reference.Length; i++)
        {
            double t = Math.PI * i / (reference.Length - 1);
            reference[i] = new Vec2(Math.Cos(t), Math.Sin(t));
        }

        Vec2[] forward = reference.Select(p => new Vec2(p.X + 0.01, p.Y)).ToArray();
        Vec2[] backward = forward.Reverse().ToArray();

        DtwDirectionComparison good = Dtw.CompareWithReversedReference(forward.Length, reference.Length, new SquaredEuclideanCost(forward, reference));
        DtwDirectionComparison bad = Dtw.CompareWithReversedReference(backward.Length, reference.Length, new SquaredEuclideanCost(backward, reference));
        Assert.False(good.ReversedIsBetter);
        Assert.True(bad.ReversedIsBetter);
        Assert.True(bad.Reversed.Cost < 0.1 * bad.Forward.Cost);
    }

    [Fact]
    public void Compare_directions_with_explicit_reversed_cost_maps_indices_back()
    {
        double[] a = [3, 2, 1];
        double[] b = [1, 2, 3];
        double[] bReversed = [3, 2, 1];
        var path = new List<DtwCell>();
        var cmp = Dtw.CompareDirections(
            3, 3, new SquaredScalarCost(a, b), new SquaredScalarCost(a, bReversed), default, null, null, path);
        Assert.True(cmp.ReversedIsBetter);
        Assert.Equal(0.0, cmp.Reversed.Cost);
        Assert.Equal([new DtwCell(0, 2), new DtwCell(1, 1), new DtwCell(2, 0)], path);
    }

    [Fact]
    public void Workspace_reuse_does_not_change_results_and_does_not_reallocate()
    {
        var ws = new DtwWorkspace();
        var rng = new Pcg32(99);
        Vec2[] big = RandomPoints(rng, 60, false, false);
        Vec2[] big2 = RandomPoints(rng, 60, false, false);
        DtwResult first = Dtw.Align(60, 60, new SquaredEuclideanCost(big, big2), default, ws);
        for (int i = 0; i < 20; i++)
        {
            Vec2[] a = RandomPoints(rng, 1 + rng.NextInt32(40), false, false);
            Vec2[] b = RandomPoints(rng, 1 + rng.NextInt32(40), false, false);
            DtwResult withWs = Dtw.Align(a.Length, b.Length, new SquaredEuclideanCost(a, b), default, ws);
            DtwResult fresh = Dtw.Align(a.Length, b.Length, new SquaredEuclideanCost(a, b));
            Assert.Equal(fresh, withWs);
        }

        // После прогрева повторный вызов того же размера не выделяет память.
        long before = GC.GetAllocatedBytesForCurrentThread();
        DtwResult again = Dtw.Align(60, 60, new SquaredEuclideanCost(big, big2), default, ws);
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(first, again);
        Assert.Equal(0, after - before);
    }

    [Fact]
    public void Identical_sequences_have_zero_cost_along_the_diagonal()
    {
        Vec2[] a = RandomPoints(new Pcg32(5), 25, false, false);
        var path = new List<DtwCell>();
        DtwResult r = Dtw.Align(25, 25, new SquaredEuclideanCost(a, a), default, null, path);
        Assert.Equal(0.0, r.Cost);
        Assert.Equal(25, r.PathLength);
        Assert.True(path.Select((c, k) => c.I == k && c.J == k).All(x => x));
    }

    [Fact]
    public void Invalid_arguments_are_rejected()
    {
        var cost = new DelegateCost((_, _) => 0.0);
        Assert.Throws<ArgumentOutOfRangeException>(() => Dtw.Align(0, 3, cost));
        Assert.Throws<ArgumentOutOfRangeException>(() => Dtw.Align(3, 0, cost));
        Assert.Throws<ArgumentOutOfRangeException>(() => Dtw.Align(3, 3, cost, new DtwOptions { IndexBandRadius = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Dtw.AlignXBand([1.0], [1.0], -0.5, cost));
        Assert.Throws<InvalidOperationException>(() => Dtw.Align(2, 2, new DelegateCost((_, _) => double.NaN)));
        Assert.Throws<ArgumentException>(() => new WeightedFeatureCost(new double[4], new double[4], 2, new double[3]));
    }
}
