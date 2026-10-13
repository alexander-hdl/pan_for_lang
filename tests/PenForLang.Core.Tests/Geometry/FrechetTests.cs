using PenForLang.Core.Geometry;

namespace PenForLang.Core.Tests.Geometry;

public class FrechetTests
{
    /// <summary>Рекурсия Эйтера–Маннила с мемоизацией.</summary>
    private static double Naive(Vec2[] p, Vec2[] q)
    {
        var memo = new Dictionary<(int, int), double>();

        double Rec(int i, int j)
        {
            if (memo.TryGetValue((i, j), out double cached))
            {
                return cached;
            }

            double d = Vec2.Distance(p[i], q[j]);
            double value;
            if (i == 0 && j == 0)
            {
                value = d;
            }
            else if (i == 0)
            {
                value = Math.Max(Rec(0, j - 1), d);
            }
            else if (j == 0)
            {
                value = Math.Max(Rec(i - 1, 0), d);
            }
            else
            {
                value = Math.Max(Math.Min(Rec(i - 1, j), Math.Min(Rec(i - 1, j - 1), Rec(i, j - 1))), d);
            }

            memo[(i, j)] = value;
            return value;
        }

        return Rec(p.Length - 1, q.Length - 1);
    }

    [Fact]
    public void Identical_curves_have_zero_distance()
    {
        Vec2[] a = [new(0, 0), new(1, 2), new(3, 1), new(4, 4)];
        Assert.Equal(0.0, Frechet.Distance(a, a));
    }

    [Fact]
    public void Parallel_shifted_lines_have_distance_equal_to_the_shift()
    {
        Vec2[] a = [new(0, 0), new(1, 0), new(2, 0)];
        Vec2[] b = [new(0, 1), new(1, 1), new(2, 1)];
        Assert.Equal(1.0, Frechet.Distance(a, b));
    }

    [Fact]
    public void Direction_matters_unlike_hausdorff()
    {
        Vec2[] a = [new(0, 0), new(1, 0), new(2, 0)];
        Vec2[] reversed = [new(2, 0), new(1, 0), new(0, 0)];
        Assert.Equal(2.0, Frechet.Distance(a, reversed));
    }

    [Fact]
    public void Discrete_distance_depends_on_vertices_only()
    {
        // Два отрезка совпадают, но у второго лишняя вершина: расстояние между вершинами 1, хотя сами кривые совпадают.
        Vec2[] a = [new(0, 0), new(2, 0)];
        Vec2[] b = [new(0, 0), new(1, 0), new(2, 0)];
        Assert.Equal(1.0, Frechet.Distance(a, b));
    }

    [Fact]
    public void Vertex_coupling_picks_the_best_monotone_pairing()
    {
        Vec2[] p = [new(0, 0), new(4, 0), new(4, 3)];
        Vec2[] q = [new(0, 0), new(4, 3)];
        // Сцепление (p0,q0), (p1,q0), (p2,q1): max(0, 4, 0) = 4; (p0,q0),(p1,q1),(p2,q1): max(0, 3, 0) = 3.
        Assert.Equal(3.0, Frechet.Distance(p, q));
    }

    [Fact]
    public void Single_points_and_symmetry()
    {
        Assert.Equal(5.0, Frechet.Distance([new Vec2(0, 0)], [new Vec2(3, 4)]));
        Assert.Equal(5.0, Frechet.Distance([new Vec2(0, 0)], [new Vec2(3, 4), new Vec2(3, 4)]));
        Assert.Throws<ArgumentException>(() => Frechet.Distance([], [new Vec2(0, 0)]));
        Assert.Throws<ArgumentException>(() => Frechet.Distance([new Vec2(0, 0)], []));
    }

    [Fact]
    public void Matches_memoised_recursion_on_random_curves()
    {
        var rng = new Pcg32(51);
        for (int c = 0; c < 1000; c++)
        {
            int n = 1 + rng.NextInt32(16);
            int m = 1 + rng.NextInt32((c % 10) == 0 ? 40 : 16);
            Vec2[] a = Enumerable.Range(0, n).Select(_ => new Vec2(rng.NextInt32(6), rng.NextInt32(6))).ToArray();
            Vec2[] b = Enumerable.Range(0, m).Select(_ => new Vec2(rng.NextDouble() * 6.0, rng.NextDouble() * 6.0)).ToArray();
            double expected = Naive(a, b);
            double actual = Frechet.Distance(a, b);
            Assert.True(Math.Abs(expected - actual) < 1e-12, $"case {c}: {actual} vs {expected}");
            Assert.Equal(actual, Frechet.Distance(b, a), 1e-12);
        }
    }

    [Fact]
    public void Long_curves_beyond_the_stack_buffer_work()
    {
        var a = new Vec2[400];
        var b = new Vec2[400];
        for (int i = 0; i < a.Length; i++)
        {
            a[i] = new Vec2(i * 0.1, Math.Sin(i * 0.1));
            b[i] = new Vec2(i * 0.1, Math.Sin(i * 0.1) + 0.5);
        }

        Assert.Equal(0.5, Frechet.Distance(a, b), 1e-12);
    }

    [Fact]
    public void Triangle_inequality_holds_on_random_curves()
    {
        var rng = new Pcg32(52);
        Vec2[] Curve() => Enumerable.Range(0, 8).Select(_ => new Vec2(rng.NextDouble() * 4.0, rng.NextDouble() * 4.0)).ToArray();
        for (int c = 0; c < 200; c++)
        {
            Vec2[] a = Curve();
            Vec2[] b = Curve();
            Vec2[] d = Curve();
            Assert.True(Frechet.Distance(a, d) <= Frechet.Distance(a, b) + Frechet.Distance(b, d) + 1e-12);
        }
    }
}
