using PenForLang.Core.Geometry;

namespace PenForLang.Core.Tests.Geometry;

public class ResamplerTests
{
    /// <summary>Гладкая кривая Безье без острых изломов (кривизна ограничена), типичная по размеру для письма (единицы x-height).</summary>
    private static CubicBezier SmoothCurve(Pcg32 rng)
    {
        while (true)
        {
            CubicBezier c = CubicBezierTests.RandomCurve(rng, 3.0);
            double maxCurvature = 0.0;
            for (int k = 0; k <= 200; k++)
            {
                maxCurvature = Math.Max(maxCurvature, Math.Abs(c.CurvatureAt(k / 200.0)));
            }

            if (c.Length() > 3.0 && maxCurvature < 4.0)
            {
                return c;
            }
        }
    }

    [Fact]
    public void Step_error_is_below_one_percent_on_bezier_curves_for_the_evaluator_step()
    {
        var rng = new Pcg32(21);
        const double step = 0.04; // scoring.resampleStep, ARCHITECTURE.md §6.4.1
        for (int i = 0; i < 200; i++)
        {
            CubicBezier curve = SmoothCurve(rng);
            var dense = new List<Vec2>();
            curve.Flatten(1e-7, dense);
            Vec2[] source = [.. dense];
            double length = PolylineMath.Length(source);

            Vec2[] samples = Resampler.ResampleByStep(source, step);
            double actualStep = length / (samples.Length - 1);
            Assert.True(Math.Abs(actualStep - step) / step < 0.01, $"declared step error at {i}: {actualStep}");

            // Реальный шаг между соседними точками (по хорде) и по дуге исходной ломаной.
            Assert.Equal(source[0], samples[0]);
            Assert.True(Vec2.Distance(source[^1], samples[^1]) < 1e-12);
            var arcPositions = new double[samples.Length];
            for (int k = 0; k < samples.Length; k++)
            {
                arcPositions[k] = PolylineMath.DistanceToPolyline(samples[k], source);
                Assert.True(arcPositions[k] < 1e-9, "resampled point is off the curve");
                if (k > 0)
                {
                    double chord = Vec2.Distance(samples[k - 1], samples[k]);
                    Assert.True(Math.Abs(chord - step) / step < 0.01, $"chord step error {chord} at curve {i}, sample {k}");
                }
            }

            // Суммарная длина ресэмплированной ломаной не отличается от длины кривой более чем на 0,1%.
            Assert.Equal(curve.Length(), PolylineMath.Length(samples), curve.Length() * 1e-3);
        }
    }

    [Theory]
    [InlineData(0.01)]
    [InlineData(0.02)]
    [InlineData(0.1)]
    [InlineData(0.25)]
    public void Resampled_arc_steps_are_equal_to_within_one_percent_for_various_steps(double step)
    {
        var rng = new Pcg32(22);
        for (int i = 0; i < 100; i++)
        {
            CubicBezier curve = SmoothCurve(rng);
            var dense = new List<Vec2>();
            curve.Flatten(1e-7, dense);
            Vec2[] source = [.. dense];
            double length = PolylineMath.Length(source);
            Vec2[] samples = Resampler.ResampleByStep(source, step);

            // Положения на исходной ломаной: сверяем равенство длины дуги между соседними точками.
            var positions = new SamplePosition[samples.Length];
            Resampler.ResamplePositions(source, positions);
            var cumulative = new double[source.Length];
            PolylineMath.CumulativeLengths(source, cumulative);
            double expected = length / (samples.Length - 1);
            for (int k = 0; k < positions.Length; k++)
            {
                double s = cumulative[positions[k].Segment]
                    + (positions[k].T * Vec2.Distance(source[positions[k].Segment], source[positions[k].Segment + 1]));
                Assert.True(Math.Abs(s - (k * expected)) <= 1e-9 * Math.Max(1.0, length), $"arc position {k}");
            }

            Assert.True(Math.Abs(expected - step) / step < 0.01 + (step / (2.0 * length)), $"step {expected} vs {step}");
        }
    }

    [Fact]
    public void Count_for_step_bounds_the_step_error()
    {
        for (double length = 2.0; length < 40.0; length += 0.137)
        {
            int count = Resampler.CountForStep(length, 0.04);
            double actual = length / (count - 1);
            Assert.True(Math.Abs(actual - 0.04) / 0.04 <= (0.04 / ((2.0 * length) - 0.04)) + 1e-12);
        }

        // Случайные длины и шаги: граница step / (2L − step) выполняется, а при L >= 50,5·step ошибка меньше 1%.
        var rng = new Pcg32(23);
        for (int i = 0; i < 20000; i++)
        {
            double step = 0.005 + (rng.NextDouble() * 0.5);
            double length = step * (0.5 + (rng.NextDouble() * 400.0));
            int count = Resampler.CountForStep(length, step);
            double actual = length / (count - 1);
            double error = Math.Abs(actual - step) / step;
            Assert.True(error <= (step / ((2.0 * length) - step)) + 1e-12, $"L={length}, step={step}, error={error}");
            if (length >= 50.5 * step)
            {
                Assert.True(error < 0.01, $"L={length}, step={step}, error={error}");
            }
        }

        Assert.Equal(1, Resampler.CountForStep(0.0, 0.04));
        Assert.Equal(2, Resampler.CountForStep(0.001, 0.04));
    }

    [Fact]
    public void Straight_polyline_with_duplicate_points_is_resampled_evenly()
    {
        Vec2[] source = [new(0, 0), new(0, 0), new(1, 0), new(1, 0), new(1, 0), new(3, 0), new(10, 0)];
        Vec2[] samples = Resampler.ResampleByStep(source, 1.0);
        Assert.Equal(11, samples.Length);
        for (int k = 0; k < samples.Length; k++)
        {
            Assert.Equal(k, samples[k].X, 1e-12);
            Assert.Equal(0.0, samples[k].Y);
        }
    }

    [Fact]
    public void Degenerate_inputs_are_handled()
    {
        Vec2[] single = [new(3, 4)];
        Assert.Equal(single, Resampler.ResampleByStep(single, 0.1));

        Vec2[] allSame = [new(1, 1), new(1, 1), new(1, 1)];
        Assert.Equal([new Vec2(1, 1)], Resampler.ResampleByStep(allSame, 0.1));

        var target = new Vec2[4];
        Resampler.Resample(allSame, target);
        Assert.All(target, p => Assert.Equal(new Vec2(1, 1), p));

        Assert.Throws<ArgumentException>(() => Resampler.ResampleByStep([], 0.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Resampler.ResampleByStep(single, 0.0));
    }

    [Fact]
    public void Extra_channels_are_interpolated_linearly_with_the_same_positions()
    {
        Vec2[] source = [new(0, 0), new(2, 0), new(2, 6)];
        double[] time = [0.0, 100.0, 400.0]; // 50 за единицу на первом участке, 50 на втором
        var positions = new SamplePosition[5];
        Resampler.ResamplePositions(source, positions);

        // Длина 8, шаг 2: точки на расстоянии 0, 2, 4, 6, 8.
        double[] expected = [0.0, 100.0, 200.0, 300.0, 400.0];
        for (int k = 0; k < positions.Length; k++)
        {
            Assert.Equal(expected[k], Resampler.Interpolate(time, positions[k]), 1e-9);
        }

        Assert.Equal(new Vec2(2, 2), Resampler.Interpolate(source, positions[2]));
    }

    [Fact]
    public void Caller_scratch_overload_matches_and_does_not_allocate_for_large_counts()
    {
        Vec2[] source = [new(0, 0), new(5, 5), new(10, 0)];
        var expected = new Vec2[1000];
        Resampler.Resample(source, expected);

        var actual = new Vec2[1000];
        var scratch = new SamplePosition[1000];
        Resampler.Resample(source, actual, scratch);
        Assert.Equal(expected, actual);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Resampler.Resample(source, actual, scratch);
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
        Assert.Throws<ArgumentException>(() => Resampler.Resample(source, actual, new SamplePosition[10]));
    }

    [Fact]
    public void Resampling_without_stack_overflow_for_large_counts_and_no_allocation_for_small()
    {
        Vec2[] source = [new(0, 0), new(5, 5), new(10, 0)];
        var big = new Vec2[1000];
        Resampler.Resample(source, big);
        Assert.Equal(source[0], big[0]);
        Assert.Equal(source[^1], big[^1]);

        var small = new Vec2[64];
        Resampler.Resample(source, small);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Resampler.Resample(source, small);
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
    }
}
