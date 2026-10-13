using PenForLang.Core.Geometry;

namespace PenForLang.Core.Tests.Geometry;

/// <summary>V0.2b: коридор, ключевые точки, наклон, самопересечения на синтетических кривых.</summary>
public class ShapePrimitivesTests
{
    private static Vec2[] Sample(int count, Func<double, Vec2> curve, double t0, double t1) =>
        Enumerable.Range(0, count).Select(i => curve(t0 + ((t1 - t0) * i / (count - 1)))).ToArray();

    // ---------- Коридор ----------

    [Fact]
    public void Corridor_share_counts_vertices_inside_the_tolerance()
    {
        Vec2[] reference = [new(0, 0), new(10, 0)];
        Vec2[] close = Enumerable.Range(0, 101).Select(i => new Vec2(i * 0.1, 0.05)).ToArray();
        Assert.Equal(1.0, Corridor.Share(close, reference, 0.06));
        Assert.Equal(0.0, Corridor.Share(close, reference, 0.04));

        // Половина вершин далеко (0,5), половина рядом.
        Vec2[] mixed = Enumerable.Range(0, 100).Select(i => new Vec2(i * 0.1, i < 50 ? 0.0 : 0.5)).ToArray();
        Assert.Equal(0.5, Corridor.Share(mixed, reference, 0.1));

        // Расстояние до отрезка, а не до вершин: точка над серединой длинного отрезка.
        Assert.Equal(1.0, Corridor.Share([new Vec2(5, 0.01)], reference, 0.02));
    }

    [Fact]
    public void Corridor_coverage_is_the_arc_length_share_of_the_reference_near_the_sample()
    {
        Vec2[] reference = [new(0, 0), new(10, 0)];
        Vec2[] half = Enumerable.Range(0, 51).Select(i => new Vec2(i * 0.1, 0.02)).ToArray(); // покрывает x в [0, 5]
        Assert.Equal(0.5, Corridor.Coverage(reference, half, 0.05, 0.05), 0.02);

        Vec2[] whole = Enumerable.Range(0, 101).Select(i => new Vec2(i * 0.1, 0.0)).ToArray();
        Assert.Equal(1.0, Corridor.Coverage(reference, whole, 0.01, 0.1), 1e-9);
        Assert.Equal(0.0, Corridor.Coverage(reference, [new Vec2(0, 5), new Vec2(10, 5)], 0.1, 0.1));

        // Вырожденный эталон нулевой длины.
        Assert.Equal(1.0, Corridor.Coverage([new Vec2(1, 1), new Vec2(1, 1)], [new Vec2(1, 1.05)], 0.1, 0.1));
        Assert.Equal(0.0, Corridor.Coverage([new Vec2(1, 1)], [new Vec2(5, 5)], 0.1, 0.1));
    }

    [Fact]
    public void Corridor_measure_separates_a_good_trace_from_a_short_one_and_a_wild_one()
    {
        CubicBezier curve = new(new Vec2(0, 0), new Vec2(1, 2), new Vec2(2, -1), new Vec2(3, 1));
        var dense = new List<Vec2>();
        curve.Flatten(1e-5, dense);
        Vec2[] reference = Resampler.ResampleByStep([.. dense], 0.04);

        // Идеальная копия.
        CorridorMeasure same = Corridor.Measure(reference, reference, 0.05, 0.04);
        Assert.Equal(1.0, same.Share);
        Assert.Equal(1.0, same.Coverage);
        Assert.Equal(0.0, same.MaxDistance, 1e-12);

        // Аккуратный, но недописанный штрих: рукопись целиком в коридоре, эталон покрыт частично (двусторонность).
        Vec2[] shortTrace = reference.Take(reference.Length / 2).ToArray();
        CorridorMeasure partial = Corridor.Measure(shortTrace, reference, 0.05, 0.04);
        Assert.Equal(1.0, partial.Share);
        Assert.InRange(partial.Coverage, 0.4, 0.6);

        // Штрих сдвинут на 0,3: вне коридора и по доле, и по покрытию.
        Vec2[] shifted = reference.Select(p => new Vec2(p.X, p.Y + 0.3)).ToArray();
        CorridorMeasure wild = Corridor.Measure(shifted, reference, 0.05, 0.04);
        Assert.True(wild.Share < 0.3);
        Assert.True(wild.Coverage < 0.3);
        Assert.True(wild.MeanDistance > 0.05);
        Assert.True(wild.MaxDistance <= 0.3 + 1e-9);

        Assert.Throws<ArgumentException>(() => Corridor.Share([], reference, 0.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Corridor.Share(reference, reference, -1.0));
    }

    // ---------- Ключевые точки ----------

    [Fact]
    public void Y_extrema_of_a_sine_wave_alternate_and_ignore_noise_below_the_prominence()
    {
        Vec2[] wave = Sample(401, t => new Vec2(t, Math.Sin(t)), 0.0, 4.0 * Math.PI);
        var found = new List<KeyPoint>();
        KeyPoints.FindYExtrema(wave, 0.3, found);

        Assert.Equal(
            [KeyPointKind.LocalMaxY, KeyPointKind.LocalMinY, KeyPointKind.LocalMaxY, KeyPointKind.LocalMinY],
            found.Select(k => k.Kind));
        double[] expectedX = [Math.PI / 2, 3 * Math.PI / 2, 5 * Math.PI / 2, 7 * Math.PI / 2];
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(expectedX[i], found[i].Position.X, 0.05);
            Assert.Equal(found[i].Kind == KeyPointKind.LocalMaxY ? 1.0 : -1.0, found[i].Position.Y, 1e-3);
        }

        Assert.Equal(1.0, found[0].Strength, 0.01); // от старта (y = 0) до первого максимума
        Assert.Equal(2.0, found[1].Strength, 0.01);

        // Шум амплитудой 0,02 не создаёт лишних экстремумов.
        var rng = new Pcg32(71);
        Vec2[] noisy = wave.Select(p => new Vec2(p.X, p.Y + ((rng.NextDouble() - 0.5) * 0.04))).ToArray();
        var foundNoisy = new List<KeyPoint>();
        KeyPoints.FindYExtrema(noisy, 0.3, foundNoisy);
        Assert.Equal(4, foundNoisy.Count);

        // Слишком большая требуемая амплитуда — ничего не находится.
        var none = new List<KeyPoint>();
        KeyPoints.FindYExtrema(wave, 3.0, none);
        Assert.Empty(none);
    }

    [Fact]
    public void Y_extrema_endpoints_are_reported_only_on_request()
    {
        Vec2[] rising = Sample(50, t => new Vec2(t, t), 0.0, 5.0);
        var interior = new List<KeyPoint>();
        KeyPoints.FindYExtrema(rising, 0.5, interior);
        Assert.Empty(interior);

        var withEnds = new List<KeyPoint>();
        KeyPoints.FindYExtrema(rising, 0.5, withEnds, includeEndpoints: true);
        Assert.Equal([KeyPointKind.LocalMinY, KeyPointKind.LocalMaxY], withEnds.Select(k => k.Kind));
        Assert.Equal([0, 49], withEnds.Select(k => k.Index));

        // «Горб»: вверх-вниз даёт ровно один максимум; число горбов n считается как число максимумов.
        Vec2[] humps = Sample(300, t => new Vec2(t, Math.Abs(Math.Sin(t))), 0.0, 3.0 * Math.PI);
        var peaks = new List<KeyPoint>();
        KeyPoints.FindYExtrema(humps, 0.3, peaks);
        Assert.Equal(3, peaks.Count(k => k.Kind == KeyPointKind.LocalMaxY));
    }

    [Fact]
    public void Corners_and_reversals_are_found_on_polylines_but_not_on_smooth_curves()
    {
        // Г-образный излом 90°.
        var lShape = new List<Vec2>();
        for (int i = 0; i <= 50; i++)
        {
            lShape.Add(new Vec2(i * 0.1, 0));
        }

        for (int i = 1; i <= 50; i++)
        {
            lShape.Add(new Vec2(5, i * 0.1));
        }

        var corners = new List<KeyPoint>();
        KeyPoints.FindCorners([.. lShape], 0.3, 0.5, 2.6, corners);
        KeyPoint corner = Assert.Single(corners);
        Assert.Equal(KeyPointKind.Corner, corner.Kind);
        Assert.Equal(Math.PI / 2, corner.Strength, 0.05);
        Assert.InRange(corner.Index, 49, 51);

        // Шпилька (ретрейс): поворот почти на 180°.
        var hairpin = new List<Vec2>();
        for (int i = 0; i <= 50; i++)
        {
            hairpin.Add(new Vec2(i * 0.1, 0));
        }

        for (int i = 1; i <= 50; i++)
        {
            hairpin.Add(new Vec2(5 - (i * 0.1), 0.02));
        }

        var reversals = new List<KeyPoint>();
        KeyPoints.FindCorners([.. hairpin], 0.3, 0.5, 2.6, reversals);
        KeyPoint reversal = Assert.Single(reversals);
        Assert.Equal(KeyPointKind.Reversal, reversal.Kind);
        Assert.True(reversal.Strength > 2.6);

        // Гладкая окружность и синусоида: изломов нет.
        Vec2[] circle = Sample(400, t => new Vec2(3 * Math.Cos(t), 3 * Math.Sin(t)), 0.0, 2 * Math.PI);
        var none = new List<KeyPoint>();
        KeyPoints.FindCorners(circle, 0.2, 0.5, 2.6, none);
        Assert.Empty(none);

        // Зигзаг: каждый излом находится, число равно числу вершин зигзага.
        Vec2[] zigzag = Sample(5, t => new Vec2(t, (int)t % 2 == 0 ? 0 : 1), 0, 4);
        var zig = new List<Vec2>();
        for (int s = 0; s < 4; s++)
        {
            for (int i = 0; i < 20; i++)
            {
                zig.Add(Vec2.Lerp(zigzag[s], zigzag[s + 1], i / 20.0));
            }
        }

        zig.Add(zigzag[4]);
        var zigCorners = new List<KeyPoint>();
        KeyPoints.FindCorners([.. zig], 0.25, 0.8, 3.0, zigCorners);
        Assert.Equal(3, zigCorners.Count);
    }

    // ---------- Наклон ----------

    private static Vec2[] SlantedStrokes(double slantDegrees, int strokes, Pcg32? noise = null)
    {
        double k = Math.Tan(slantDegrees * Math.PI / 180.0);
        var pts = new List<Vec2>();
        double x0 = 0;
        for (int s = 0; s < strokes; s++)
        {
            // Нисходящий штрих высотой 1: верх сдвинут вправо на tan(slant).
            for (int i = 0; i <= 25; i++)
            {
                double y = 1.0 - (i * 0.04);
                pts.Add(new Vec2(x0 + (y * k), y));
            }

            // Горизонтальный переход к следующему штриху.
            for (int i = 1; i <= 12; i++)
            {
                pts.Add(new Vec2(x0 + (i * 0.04), 0.0));
            }

            x0 += 0.5;
        }

        if (noise is not null)
        {
            for (int i = 0; i < pts.Count; i++)
            {
                pts[i] = new Vec2(pts[i].X + ((noise.NextDouble() - 0.5) * 0.004), pts[i].Y + ((noise.NextDouble() - 0.5) * 0.004));
            }
        }

        return [.. pts];
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(12.0)]
    [InlineData(-8.0)]
    [InlineData(25.0)]
    public void Slant_estimate_recovers_the_shear_angle(double degrees)
    {
        Vec2[] strokes = SlantedStrokes(degrees, 4);
        SlantEstimate clean = SlantEstimator.Estimate(strokes, 0.3, 45.0);
        Assert.Equal(4, clean.Count);
        Assert.Equal(degrees, clean.MedianDegrees, 0.6);
        Assert.True(clean.StdDevDegrees < 1.0);

        SlantEstimate noisy = SlantEstimator.Estimate(SlantedStrokes(degrees, 4, new Pcg32(72)), 0.3, 45.0, halfWindow: 3);
        Assert.Equal(degrees, noisy.MedianDegrees, 2.0);
    }

    [Fact]
    public void Deslanting_by_the_estimated_angle_makes_the_strokes_vertical()
    {
        Vec2[] slanted = SlantedStrokes(14.0, 3);
        double angle = SlantEstimator.Estimate(slanted, 0.3, 45.0).MedianDegrees;
        var straight = new Vec2[slanted.Length];
        Deslant.ApplyDegrees(slanted, straight, angle);

        SlantEstimate after = SlantEstimator.Estimate(straight, 0.3, 45.0);
        Assert.Equal(0.0, after.MedianDegrees, 0.6);
    }

    [Fact]
    public void Slant_ignores_ascending_and_horizontal_motion_and_short_runs()
    {
        // Подъём по наклонной линии (в обратном порядке) — не нисходящая квазивертикаль.
        Vec2[] up = SlantedStrokes(10.0, 1).Take(26).Reverse().ToArray();
        Assert.Equal(0, SlantEstimator.Estimate(up, 0.3, 45.0).Count);

        // Горизонталь и слишком короткий участок.
        Vec2[] flat = Sample(50, t => new Vec2(t, 0), 0, 2);
        Assert.Equal(SlantEstimate.None, SlantEstimator.Estimate(flat, 0.3, 45.0));
        Assert.Equal(0, SlantEstimator.Estimate(SlantedStrokes(10.0, 1), 5.0, 45.0).Count);

        // Слишком наклонный участок (60° от вертикали) отбрасывается при допуске 45°.
        Assert.Equal(0, SlantEstimator.Estimate(SlantedStrokes(60.0, 2), 0.3, 45.0).Count);
    }

    [Fact]
    public void Slant_summary_has_median_mean_and_standard_deviation()
    {
        SlantEstimate s = SlantEstimator.Summarize([10.0, 12.0, 14.0]);
        Assert.Equal(3, s.Count);
        Assert.Equal(12.0, s.MedianDegrees);
        Assert.Equal(12.0, s.MeanDegrees);
        Assert.Equal(Math.Sqrt(8.0 / 3.0), s.StdDevDegrees, 1e-12);
        Assert.Equal(11.0, SlantEstimator.Summarize([10.0, 12.0]).MedianDegrees);
        Assert.Equal(SlantEstimate.None, SlantEstimator.Summarize([]));
    }

    // ---------- Самопересечения ----------

    [Fact]
    public void Segment_intersection_basics()
    {
        Assert.True(SelfIntersections.TryIntersectSegments(new Vec2(0, 0), new Vec2(2, 2), new Vec2(0, 2), new Vec2(2, 0), out double ta, out double tb));
        Assert.Equal(0.5, ta, 1e-12);
        Assert.Equal(0.5, tb, 1e-12);

        Assert.False(SelfIntersections.TryIntersectSegments(new Vec2(0, 0), new Vec2(1, 0), new Vec2(0, 1), new Vec2(1, 1), out _, out _)); // параллельные
        Assert.False(SelfIntersections.TryIntersectSegments(new Vec2(0, 0), new Vec2(1, 0), new Vec2(2, -1), new Vec2(2, 1), out _, out _)); // не достают

        // Пересечение ровно в вершине находится у одного сегмента из двух.
        Vec2[] bent = [new(0, 0), new(1, 1), new(2, 2), new(3, 3), new(1, 3), new(1, 1.0), new(1, 0)];
        int touching = 0;
        for (int i = 0; i < 2; i++)
        {
            if (SelfIntersections.TryIntersectSegments(bent[i], bent[i + 1], new Vec2(1, 3), new Vec2(1, 0), out _, out _))
            {
                touching++;
            }
        }

        Assert.Equal(1, touching);
    }

    [Fact]
    public void Lemniscate_has_one_crossing_and_a_loop_with_the_known_area()
    {
        // Лемниската Жероно: x = sin t, y = sin t cos t. Петля (правая доля) имеет площадь 2/3 и обход по часовой стрелке.
        Vec2[] curve = Sample(2001, t => new Vec2(Math.Sin(t), Math.Sin(t) * Math.Cos(t)), -1.0, Math.PI + 1.0);

        var crossings = new List<SegmentIntersection>();
        SelfIntersections.Find(curve, crossings);
        SegmentIntersection x = Assert.Single(crossings);
        Assert.True(x.SegmentA < x.SegmentB);
        Assert.True(Vec2.Distance(x.Point, Vec2.Zero) < 1e-3);

        var loops = new List<PolylineLoop>();
        SelfIntersections.FindLoops(curve, loops);
        PolylineLoop loop = Assert.Single(loops);
        Assert.Equal(-2.0 / 3.0, loop.SignedArea, 2e-3);
        Assert.Equal(2.0 / 3.0, loop.Area, 2e-3);
        Assert.True(loop.Length > 3.0 && loop.Length < 4.5);
    }

    [Fact]
    public void Prolate_trochoid_has_one_loop_per_period_with_equal_area()
    {
        // x = t − 2 sin t, y = −2 cos t: на каждом периоде 2π — одна петля (знак площади одинаков).
        Vec2[] curve = Sample(3001, t => new Vec2(t - (2.0 * Math.Sin(t)), -2.0 * Math.Cos(t)), Math.PI, 7.0 * Math.PI);
        var loops = new List<PolylineLoop>();
        SelfIntersections.FindLoops(curve, loops);
        Assert.Equal(3, loops.Count);
        Assert.All(loops, l => Assert.True(Math.Sign(l.SignedArea) == Math.Sign(loops[0].SignedArea)));
        Assert.All(loops, l => Assert.Equal(loops[0].Area, l.Area, loops[0].Area * 2e-3));
        Assert.All(loops, l => Assert.Equal(loops[0].Length, l.Length, loops[0].Length * 2e-3));
    }

    [Fact]
    public void Simple_curves_and_a_nearly_closed_oval_have_no_self_intersections()
    {
        var loops = new List<PolylineLoop>();
        Vec2[] oval = Sample(300, t => new Vec2(2 * Math.Cos(t), Math.Sin(t)), 0.0, (2 * Math.PI) - 0.05);
        SelfIntersections.FindLoops(oval, loops);
        Assert.Empty(loops);

        Vec2[] spiral = Sample(500, t => new Vec2(t * Math.Cos(t), t * Math.Sin(t)), 0.1, 12.0);
        SelfIntersections.FindLoops(spiral, loops);
        Assert.Empty(loops);

        // Контур, дорисованный с перехлёстом через начало: хвост пересекает первый сегмент -> петля 3 x 4, обход против часовой.
        loops.Clear();
        Vec2[] overlapped = [new(0.5, 0), new(4, 0), new(4, 4), new(1, 4), new(1, -1)];
        SelfIntersections.FindLoops(overlapped, loops);
        PolylineLoop square = Assert.Single(loops);
        Assert.Equal(12.0, square.SignedArea, 1e-12);
        Assert.Equal(14.0, square.Length, 1e-12);
        Assert.Equal(new Vec2(1, 0), square.Intersection.Point);

        // Прямая и вырожденные входы.
        SelfIntersections.FindLoops([new Vec2(0, 0), new Vec2(1, 0), new Vec2(2, 0)], loops);
        SelfIntersections.FindLoops([], loops);
        SelfIntersections.FindLoops([new Vec2(1, 1)], loops);
    }

    // ---------- Вспомогательные функции полилинии ----------

    [Fact]
    public void Point_at_length_and_closure_gap()
    {
        Vec2[] square = [new(0, 0), new(2, 0), new(2, 2), new(0, 2), new(0, 0.5)];
        var cumulative = new double[square.Length];
        PolylineMath.CumulativeLengths(square, cumulative);
        Assert.Equal(new Vec2(0, 0), PolylineMath.PointAtLength(square, cumulative, -1.0));
        Assert.Equal(new Vec2(1, 0), PolylineMath.PointAtLength(square, cumulative, 1.0));
        Assert.Equal(new Vec2(2, 1), PolylineMath.PointAtLength(square, cumulative, 3.0));
        Assert.Equal(new Vec2(0, 0.5), PolylineMath.PointAtLength(square, cumulative, 99.0));
        Assert.Equal(new Vec2(5, 5), PolylineMath.PointAtLength([new Vec2(5, 5)], [0.0], 1.0));

        // Незамкнутый овал: зазор между началом и концом.
        Vec2[] oval = Sample(500, t => new Vec2(Math.Cos(t), Math.Sin(t)), 0.0, (2 * Math.PI) - 0.2);
        Assert.Equal(2.0 * Math.Sin(0.1), PolylineMath.ClosureGap(oval, 0.0, 1.0), 1e-3);
        Assert.Equal(0.0, PolylineMath.ClosureGap(oval, 0.3, 0.3), 1e-12);
        Assert.Throws<ArgumentException>(() => PolylineMath.ClosureGap([], 0.0, 1.0));
    }
}
