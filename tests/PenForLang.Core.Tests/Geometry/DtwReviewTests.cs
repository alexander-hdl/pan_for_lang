using PenForLang.Core.Geometry;

namespace PenForLang.Core.Tests.Geometry;

/// <summary>Правки по ревью V0.2: нормировка, доступ к матрице, транспонирование, мягкая полоса, направления.</summary>
public class DtwReviewTests
{
    [Fact]
    public void Cost_per_first_element_divides_by_the_handwriting_length_not_by_the_path()
    {
        double[] a = [0, 1, 2];
        double[] stretched = [0.5, 0.5, 0.5, 1.5, 1.5, 1.5, 2.5, 2.5, 2.5];
        DtwResult r = Dtw.Align(3, 9, new SquaredScalarCost(a, stretched));

        Assert.Equal(9, r.PathLength); // каждый из 9 элементов эталона сопоставлен с одним из 3 элементов рукописи
        Assert.Equal(3, r.FirstLength);
        Assert.Equal(2.25, r.Cost, 1e-12);
        Assert.Equal(2.25 / 3.0, r.CostPerFirstElement, 1e-12);
        Assert.Equal(2.25 / 9.0, r.CostPerPathCell, 1e-12);
        Assert.NotEqual(r.CostPerFirstElement, r.CostPerPathCell);

        // Идеальное совпадение: ноль при любом делителе.
        Assert.Equal(0.0, Dtw.Align(3, 3, new SquaredScalarCost(a, a)).CostPerFirstElement);
    }

    [Fact]
    public void Open_end_raw_minimum_is_the_minimum_per_first_element()
    {
        double[] reference = [0, 1, 2, 3, 4, 5, 6, 7];
        double[] prefix = [0.1, 1.1, 2.1, 3.1];
        DtwResult r = Dtw.Align(4, 8, new SquaredScalarCost(prefix, reference), new DtwOptions { OpenEnd = true });
        Assert.Equal(r.Cost / 4.0, r.CostPerFirstElement);
        Assert.Equal(new DtwCell(3, 3), r.End);
    }

    [Fact]
    public void Workspace_exposes_the_accumulated_matrix_and_last_row()
    {
        var ws = new DtwWorkspace();
        Assert.True(ws.LastRow.IsEmpty);
        Assert.False(ws.TryGetAccumulated(0, 0, out _));

        double[,] c = { { 1, 2, 3, 4 }, { 4, 3, 2, 1 }, { 1, 1, 1, 1 } };
        var cost = new DelegateCost((i, j) => c[i, j]);
        DtwResult r = Dtw.Align(3, 4, cost, new DtwOptions { OpenEnd = true }, ws);

        // D(0, j) = накопление по строке; D(i, j) считается вручную.
        Assert.True(ws.TryGetAccumulated(0, 0, out double d00));
        Assert.Equal(1.0, d00);
        Assert.True(ws.TryGetAccumulated(0, 3, out double d03));
        Assert.Equal(10.0, d03);
        Assert.True(ws.TryGetAccumulated(1, 1, out double d11));
        Assert.Equal(3.0 + 1.0, d11); // c(1,1) + min(D(0,0)=1, D(0,1)=3, D(1,0)=5)

        ReadOnlySpan<double> last = ws.LastRow;
        Assert.Equal(4, last.Length);
        Assert.Equal(0, ws.LastRowFirstColumn);
        double best = double.PositiveInfinity;
        for (int j = 0; j < last.Length; j++)
        {
            Assert.True(ws.TryGetAccumulated(2, j, out double v));
            Assert.Equal(v, last[j]);
            best = Math.Min(best, last[j]);
        }

        Assert.Equal(r.Cost, best);
        Assert.False(ws.TryGetAccumulated(3, 0, out _));
        Assert.False(ws.TryGetAccumulated(0, 4, out _));
        Assert.False(ws.TryGetAccumulated(-1, 0, out _));

        // Полоса: клетки вне диапазона строки недоступны, last row начинается с LastRowFirstColumn.
        Dtw.Align(3, 4, cost, new DtwOptions { IndexBandRadius = 1 }, ws);
        Assert.Equal(1, ws.LastRowFirstColumn);
        Assert.Equal(3, ws.LastRow.Length);
        Assert.False(ws.TryGetAccumulated(0, 3, out _));
    }

    [Fact]
    public void Workspace_buffers_grow_geometrically()
    {
        var ws = new DtwWorkspace();
        Dtw.Align(10, 10, new DelegateCost((_, _) => 1.0), default, ws);
        int before = ws.CellCapacity;
        Dtw.Align(10, 11, new DelegateCost((_, _) => 1.0), default, ws);
        Assert.True(ws.CellCapacity >= (int)(before * 1.5));
    }

    [Fact]
    public void Transposed_cost_gives_the_same_total_as_swapping_the_sequences()
    {
        var rng = new Pcg32(61);
        for (int k = 0; k < 300; k++)
        {
            int n = 1 + rng.NextInt32(12);
            int m = 1 + rng.NextInt32(12);
            var a = Enumerable.Range(0, n).Select(_ => new Vec2(rng.NextDouble(), rng.NextDouble())).ToArray();
            var b = Enumerable.Range(0, m).Select(_ => new Vec2(rng.NextDouble(), rng.NextDouble())).ToArray();
            var options = new DtwOptions { IndexBandRadius = (k % 3) == 0 ? 2 : null };

            var direct = new SquaredEuclideanCost(a, b);
            var swapped = new SquaredEuclideanCost(b, a);
            DtwResult viaTransposed = Dtw.Align(m, n, new TransposedCost<SquaredEuclideanCost>(in direct), options);
            DtwResult viaSwapped = Dtw.Align(m, n, swapped, options);
            DtwResult original = Dtw.Align(n, m, direct, options);
            Assert.Equal(viaSwapped.Cost, viaTransposed.Cost, 1e-12);
            Assert.Equal(original.Cost, viaTransposed.Cost, 1e-9); // DTW симметричен по стоимости
        }
    }

    [Fact]
    public void Hard_x_band_is_infeasible_across_an_x_gap_and_soft_band_pays_the_penalty()
    {
        // Рукопись: после разрыва по x (точка 2 далеко правее) эталон не успевает.
        double[] xa = [0.0, 0.1, 5.0];
        double[] xb = [0.0, 0.1, 0.2];
        var cost = new DelegateCost((_, _) => 1.0);

        DtwResult hard = Dtw.AlignXBand(xa, xb, 0.5, cost);
        Assert.False(hard.IsFeasible);

        var soft = new DtwOptions { OutOfBandCost = 10.0 };
        DtwResult relaxed = Dtw.AlignXBand(xa, xb, 0.5, cost, soft);
        Assert.True(relaxed.IsFeasible);
        // Три клетки по диагонали: 3 · 1,0 + штраф за (2,2) вне полосы (|5,0 − 0,2| > 0,5).
        Assert.Equal(13.0, relaxed.Cost, 1e-12);

        // Точка вне полосы платит штраф ровно один раз, если пути без неё нет; при достаточно большом штрафе путь не меняется.
        DtwResult hugePenalty = Dtw.AlignXBand(xa, xb, 0.5, cost, new DtwOptions { OutOfBandCost = 1e9 });
        Assert.True(hugePenalty.Cost >= 1e9);
    }

    [Fact]
    public void Soft_band_equals_hard_band_when_a_feasible_path_exists_and_penalty_is_large()
    {
        var rng = new Pcg32(62);
        int compared = 0;
        for (int k = 0; k < 300; k++)
        {
            int n = 2 + rng.NextInt32(8);
            int m = 2 + rng.NextInt32(8);
            var xa = new double[n];
            var xb = new double[m];
            double x = 0;
            for (int i = 0; i < n; i++)
            {
                x += rng.NextDouble();
                xa[i] = x;
            }

            x = 0;
            for (int j = 0; j < m; j++)
            {
                x += rng.NextDouble();
                xb[j] = x;
            }

            var cost = new DelegateCost((i, j) => 1.0 + Math.Abs(i - j));
            DtwResult hard = Dtw.AlignXBand(xa, xb, 1.5, cost);
            DtwResult soft = Dtw.AlignXBand(xa, xb, 1.5, cost, new DtwOptions { OutOfBandCost = 1e6 });
            Assert.True(soft.IsFeasible);
            Assert.True(soft.Cost <= (hard.IsFeasible ? hard.Cost : double.PositiveInfinity) + 1e-9);
            if (hard.IsFeasible)
            {
                compared++;
                Assert.Equal(hard.Cost, soft.Cost, 1e-9);
            }
        }

        Assert.True(compared > 50);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Dtw.AlignXBand([0.0], [0.0], 1.0, new DelegateCost((_, _) => 0.0), new DtwOptions { OutOfBandCost = -1.0 }));
    }

    [Fact]
    public void Direction_cost_matches_one_minus_cosine_without_trig_in_the_cell()
    {
        var rng = new Pcg32(63);
        double[] ta = Enumerable.Range(0, 20).Select(_ => (rng.NextDouble() * 12.0) - 6.0).ToArray();
        double[] tb = Enumerable.Range(0, 17).Select(_ => (rng.NextDouble() * 12.0) - 6.0).ToArray();
        var da = new Vec2[ta.Length];
        var db = new Vec2[tb.Length];
        DirectionFeatures.FromAngles(ta, da);
        DirectionFeatures.FromAngles(tb, db);

        var direction = new DirectionCost(da, db);
        for (int i = 0; i < ta.Length; i++)
        {
            for (int j = 0; j < tb.Length; j++)
            {
                Assert.Equal(1.0 - Math.Cos(ta[i] - tb[j]), direction.Cost(i, j), 1e-12);
            }
        }

        // Эквивалент WeightedFeatureCost.Angular по результату DTW.
        DtwResult viaTable = Dtw.Align(ta.Length, tb.Length, direction);
        DtwResult viaKind = Dtw.Align(
            ta.Length, tb.Length, new WeightedFeatureCost(ta, tb, 1, [1.0], [FeatureKind.Angular]));
        Assert.Equal(viaKind.Cost, viaTable.Cost, 1e-9);

        // Полная стоимость оценщика: позиция + направление.
        Vec2[] pa = Enumerable.Range(0, 20).Select(_ => new Vec2(rng.NextDouble(), rng.NextDouble())).ToArray();
        Vec2[] pb = Enumerable.Range(0, 17).Select(_ => new Vec2(rng.NextDouble(), rng.NextDouble())).ToArray();
        var full = new PositionDirectionCost(pa, pb, da, db, 2.0, 0.5);
        Assert.Equal(
            (2.0 * Vec2.DistanceSquared(pa[3], pb[4])) + (0.5 * (1.0 - Math.Cos(ta[3] - tb[4]))),
            full.Cost(3, 4),
            1e-12);
        Assert.Throws<ArgumentException>(() => new PositionDirectionCost(pa, pb, da.AsSpan(0, 5), db, 1.0, 1.0));
    }

    [Fact]
    public void Direction_features_from_polyline_are_unit_tangents()
    {
        Vec2[] line = [new(0, 0), new(1, 0), new(2, 0), new(3, 0)];
        var dirs = new Vec2[4];
        DirectionFeatures.FromPolyline(line, dirs);
        Assert.All(dirs, d => Assert.Equal(new Vec2(1, 0), d));

        Vec2[] withDuplicates = [new(0, 0), new(0, 0), new(0, 1), new(0, 1)];
        var dirs2 = new Vec2[4];
        DirectionFeatures.FromPolyline(withDuplicates, dirs2);
        Assert.All(dirs2, d => Assert.Equal(1.0, d.Length, 1e-12));
        Assert.Equal(1.0, dirs2[3].Y, 1e-12);

        var dirs3 = new Vec2[2];
        DirectionFeatures.FromPolyline([new Vec2(1, 1), new Vec2(1, 1)], dirs3);
        Assert.All(dirs3, d => Assert.Equal(new Vec2(1, 0), d));
    }

    [Fact]
    public void Reversed_to_forward_ratio_is_one_when_both_costs_vanish()
    {
        // Симметричная кривая: прямой и обращённый эталон совпадают.
        Vec2[] symmetric = [new(0, 0), new(1, 1), new(0, 0)];
        DtwDirectionComparison same = Dtw.CompareWithReversedReference(3, 3, new SquaredEuclideanCost(symmetric, symmetric));
        Assert.Equal(1.0, same.ReversedToForwardRatio);
        Assert.False(same.ReversedIsBetter);

        Vec2[] reference = [new(0, 0), new(1, 0), new(2, 0)];
        Vec2[] backwards = [new(2, 0), new(1, 0), new(0, 0)];
        DtwDirectionComparison bad = Dtw.CompareWithReversedReference(3, 3, new SquaredEuclideanCost(backwards, reference));
        Assert.True(bad.ReversedIsBetter);
        Assert.True(bad.ReversedToForwardRatio < 1e-9);

        // Прямая нулевая, обращённая нет -> бесконечность; нет путей -> бесконечность.
        DtwDirectionComparison good = Dtw.CompareWithReversedReference(3, 3, new SquaredEuclideanCost(reference, reference));
        Assert.True(double.IsPositiveInfinity(good.ReversedToForwardRatio)); // прямая нулевая, обращённая нет
        var noPath = new DtwDirectionComparison(DtwResult.Infeasible, DtwResult.Infeasible);
        Assert.True(double.IsPositiveInfinity(noPath.ReversedToForwardRatio));
    }

    [Fact]
    public void Reversed_reference_with_open_begin_maps_the_start_back()
    {
        // Подпоследовательность [3,4,5] в обращённом эталоне: путь стартует внутри эталона.
        double[] reference = [0, 1, 2, 3, 4, 5, 6, 7];
        double[] a = [5, 4, 3];
        var path = new List<DtwCell>();
        var options = new DtwOptions { OpenBegin = true, OpenEnd = true };
        DtwDirectionComparison cmp = Dtw.CompareWithReversedReference(
            3, 8, new SquaredScalarCost(a, reference), options, null, null, path);
        Assert.True(cmp.ReversedIsBetter);
        Assert.Equal(0.0, cmp.Reversed.Cost);
        Assert.Equal(new DtwCell(0, 5), cmp.Reversed.Start);
        Assert.Equal(new DtwCell(2, 3), cmp.Reversed.End);
        Assert.Equal([new DtwCell(0, 5), new DtwCell(1, 4), new DtwCell(2, 3)], path);
    }

    [Fact]
    public void Plus_infinity_costs_without_a_band_are_handled_by_the_public_api()
    {
        double inf = double.PositiveInfinity;
        double[] a = [0, 1, 2];
        double[] b = [0, 1, 2];
        // Запрет диагонали (1,1) делает идеальный путь невозможным, но путь через соседние клетки существует.
        var cost = new DelegateCost((i, j) => i == 1 && j == 1 ? inf : (a[i] - b[j]) * (a[i] - b[j]));
        DtwResult r = Dtw.Align(3, 3, cost);
        Assert.True(r.IsFeasible);
        Assert.True(r.Cost > 0.0);
        var path = new List<DtwCell>();
        Dtw.Align(3, 3, cost, default, null, path);
        Assert.DoesNotContain(new DtwCell(1, 1), path);
    }
}
