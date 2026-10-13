using PenForLang.Core.Geometry;

namespace PenForLang.Core.Tests.Geometry;

public class SavitzkyGolayFilterTests
{
    [Fact]
    public void Center_weights_match_published_tables()
    {
        // Савицкий и Голей (1964), таблицы коэффициентов.
        AssertWeights(new SavitzkyGolayFilter(5, 2), [-3, 12, 17, 12, -3], 35);
        AssertWeights(new SavitzkyGolayFilter(7, 2), [-2, 3, 6, 7, 6, 3, -2], 21);
        AssertWeights(new SavitzkyGolayFilter(5, 4), [0, 0, 1, 0, 0], 1);
        AssertWeights(new SavitzkyGolayFilter(5, 2, derivative: 1), [-2, -1, 0, 1, 2], 10);
        AssertWeights(new SavitzkyGolayFilter(7, 2, derivative: 1), [-3, -2, -1, 0, 1, 2, 3], 28);
        AssertWeights(new SavitzkyGolayFilter(5, 2, derivative: 2), [2, -1, -2, -1, 2], 7);
    }

    private static void AssertWeights(SavitzkyGolayFilter filter, double[] numerators, double denominator)
    {
        ReadOnlySpan<double> actual = filter.CenterWeights;
        Assert.Equal(numerators.Length, actual.Length);
        for (int i = 0; i < numerators.Length; i++)
        {
            Assert.Equal(numerators[i] / denominator, actual[i], 1e-12);
        }
    }

    [Fact]
    public void Polynomials_up_to_the_order_are_reproduced_exactly_including_the_edges()
    {
        const int n = 40;
        var rng = new Pcg32(31);
        for (int order = 0; order <= 4; order++)
        {
            double[] coeffs = Enumerable.Range(0, order + 1).Select(_ => (rng.NextDouble() * 2.0) - 1.0).ToArray();
            double Poly(double x, int d)
            {
                // d-я производная многочлена.
                double sum = 0.0;
                for (int j = d; j <= order; j++)
                {
                    double f = 1.0;
                    for (int q = 0; q < d; q++)
                    {
                        f *= j - q;
                    }

                    sum += coeffs[j] * f * Math.Pow(x, j - d);
                }

                return sum;
            }

            const double spacing = 0.5;
            double[] y = Enumerable.Range(0, n).Select(i => Poly(i * spacing, 0)).ToArray();
            foreach (int window in new[] { 5, 7, 9, 11 })
            {
                if (window <= order)
                {
                    continue;
                }

                for (int d = 0; d <= Math.Min(order, 2); d++)
                {
                    var filter = new SavitzkyGolayFilter(window, order, d, spacing);
                    var result = new double[n];
                    filter.Apply(y, result);
                    for (int i = 0; i < n; i++)
                    {
                        Assert.True(
                            Math.Abs(Poly(i * spacing, d) - result[i]) < 1e-7,
                            $"order {order}, window {window}, derivative {d}, index {i}: {result[i]} vs {Poly(i * spacing, d)}");
                    }
                }
            }
        }
    }

    [Fact]
    public void Smoothing_reduces_noise_and_preserves_a_smooth_signal()
    {
        const int n = 400;
        var rng = new Pcg32(32);
        double[] clean = Enumerable.Range(0, n).Select(i => Math.Sin(i * 0.05)).ToArray();
        double[] noisy = clean.Select(v => v + ((rng.NextDouble() - 0.5) * 0.2)).ToArray();
        var smoothed = new double[n];
        new SavitzkyGolayFilter(7, 2).Apply(noisy, smoothed);

        double Rms(double[] a, double[] b) => Math.Sqrt(a.Zip(b, (u, v) => (u - v) * (u - v)).Average());
        Assert.True(Rms(smoothed, clean) < 0.6 * Rms(noisy, clean));
    }

    [Fact]
    public void Derivative_of_a_sine_is_a_cosine_within_discretisation_error()
    {
        const int n = 200;
        const double dx = 0.05;
        double[] y = Enumerable.Range(0, n).Select(i => Math.Sin(i * dx)).ToArray();
        var dy = new double[n];
        new SavitzkyGolayFilter(7, 3, derivative: 1, spacing: dx).Apply(y, dy);
        for (int i = 0; i < n; i++)
        {
            Assert.Equal(Math.Cos(i * dx), dy[i], 2e-4);
        }
    }

    [Fact]
    public void Vec2_overload_filters_each_coordinate_and_gives_velocity()
    {
        var points = new Vec2[30];
        for (int i = 0; i < points.Length; i++)
        {
            points[i] = new Vec2(2.0 * i, (3.0 * i) + 1.0);
        }

        var velocity = new Vec2[points.Length];
        new SavitzkyGolayFilter(7, 2, derivative: 1).Apply(points, velocity);
        Assert.All(velocity, v =>
        {
            Assert.Equal(2.0, v.X, 1e-9);
            Assert.Equal(3.0, v.Y, 1e-9);
        });

        var smoothed = new Vec2[points.Length];
        new SavitzkyGolayFilter(7, 2).Apply(points, smoothed);
        for (int i = 0; i < points.Length; i++)
        {
            Assert.True(Vec2.Distance(points[i], smoothed[i]) < 1e-9);
        }
    }

    [Fact]
    public void Signals_shorter_than_the_window_use_a_reduced_window()
    {
        var filter = new SavitzkyGolayFilter(7, 2);
        double[] line = [1.0, 3.0, 5.0, 7.0]; // n = 4 < 7 -> окно 3, порядок 2: точная подгонка
        var result = new double[4];
        filter.Apply(line, result);
        Assert.Equal(line, result.Select(v => Math.Round(v, 9)).ToArray());

        var one = new double[1];
        filter.Apply([5.0], one);
        Assert.Equal(5.0, one[0]);

        new SavitzkyGolayFilter(5, 2, derivative: 1).Apply([5.0], one);
        Assert.Equal(0.0, one[0]);
        filter.Apply(ReadOnlySpan<double>.Empty, Span<double>.Empty);
    }

    [Fact]
    public void Apply_is_deterministic_and_allocation_free()
    {
        double[] y = Enumerable.Range(0, 100).Select(i => Math.Sin(i * 0.1)).ToArray();
        var filter = new SavitzkyGolayFilter(7, 2);
        var a = new double[100];
        var b = new double[100];
        filter.Apply(y, a);
        long before = GC.GetAllocatedBytesForCurrentThread();
        filter.Apply(y, b);
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(a, b);
        Assert.Equal(0, after - before);
    }

    [Fact]
    public void Invalid_arguments_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SavitzkyGolayFilter(6, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SavitzkyGolayFilter(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SavitzkyGolayFilter(5, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SavitzkyGolayFilter(5, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SavitzkyGolayFilter(5, 2, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SavitzkyGolayFilter(5, 2, 0, 0.0));

        var filter = new SavitzkyGolayFilter(5, 2);
        double[] buffer = new double[10];
        Assert.Throws<ArgumentException>(() => filter.Apply(buffer, new double[5]));
        Assert.Throws<ArgumentException>(() => filter.Apply(buffer.AsSpan(0, 8), buffer.AsSpan(1, 8)));
    }
}
