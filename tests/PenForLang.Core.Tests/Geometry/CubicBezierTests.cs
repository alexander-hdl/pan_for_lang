using PenForLang.Core.Geometry;

namespace PenForLang.Core.Tests.Geometry;

public class CubicBezierTests
{
    internal static CubicBezier RandomCurve(Pcg32 rng, double size = 3.0)
    {
        Vec2 P() => new(rng.NextDouble() * size, rng.NextDouble() * size);
        return new CubicBezier(P(), P(), P(), P());
    }

    [Fact]
    public void Endpoints_and_known_midpoint()
    {
        var c = new CubicBezier(new Vec2(0, 0), new Vec2(0, 1), new Vec2(1, 1), new Vec2(1, 0));
        Assert.Equal(c.P0, c.PointAt(0.0));
        Assert.Equal(c.P3, c.PointAt(1.0));
        Vec2 mid = c.PointAt(0.5);
        Assert.Equal(0.5, mid.X, 1e-15);
        Assert.Equal(0.75, mid.Y, 1e-15);
    }

    [Fact]
    public void Derivative_matches_central_differences_and_endpoint_formulas()
    {
        var rng = new Pcg32(11);
        const double h = 1e-6;
        for (int k = 0; k < 200; k++)
        {
            CubicBezier c = RandomCurve(rng);
            double t = 0.05 + (rng.NextDouble() * 0.9);
            Vec2 numeric = (c.PointAt(t + h) - c.PointAt(t - h)) / (2.0 * h);
            Vec2 analytic = c.DerivativeAt(t);
            Assert.True(Vec2.Distance(numeric, analytic) < 1e-6, $"derivative mismatch at {k}");

            Vec2 numeric2 = (c.DerivativeAt(t + h) - c.DerivativeAt(t - h)) / (2.0 * h);
            Assert.True(Vec2.Distance(numeric2, c.SecondDerivativeAt(t)) < 1e-5, $"second derivative mismatch at {k}");
        }

        var q = new CubicBezier(new Vec2(1, 2), new Vec2(3, 5), new Vec2(4, 1), new Vec2(8, 8));
        Assert.Equal(3.0 * (q.P1 - q.P0), q.DerivativeAt(0.0));
        Assert.Equal(3.0 * (q.P3 - q.P2), q.DerivativeAt(1.0));
        Assert.Equal(6.0 * (q.P2 - (2.0 * q.P1) + q.P0), q.SecondDerivativeAt(0.0));
        Assert.Equal(6.0 * (q.P3 - (2.0 * q.P2) + q.P1), q.SecondDerivativeAt(1.0));
    }

    [Fact]
    public void Curvature_of_quarter_circle_is_close_to_inverse_radius_and_has_sign()
    {
        const double k = 0.5522847498307936;
        var arc = new CubicBezier(new Vec2(1, 0), new Vec2(1, k), new Vec2(k, 1), new Vec2(0, 1));
        Assert.InRange(arc.CurvatureAt(0.5), 0.98, 1.01); // аппроксимация окружности: ~0,6% ошибки
        var mirrored = new CubicBezier(new Vec2(1, 0), new Vec2(1, -k), new Vec2(k, -1), new Vec2(0, -1));
        Assert.InRange(mirrored.CurvatureAt(0.5), -1.01, -0.98);
        var degenerate = new CubicBezier(new Vec2(1, 1), new Vec2(1, 1), new Vec2(1, 1), new Vec2(1, 1));
        Assert.Equal(0.0, degenerate.CurvatureAt(0.3));
    }

    [Fact]
    public void Split_reproduces_the_curve()
    {
        var rng = new Pcg32(12);
        for (int i = 0; i < 100; i++)
        {
            CubicBezier c = RandomCurve(rng);
            double t = 0.1 + (rng.NextDouble() * 0.8);
            c.Split(t, out CubicBezier left, out CubicBezier right);
            Assert.True(Vec2.Distance(left.P3, c.PointAt(t)) < 1e-12);
            Assert.Equal(left.P3, right.P0);
            double u = rng.NextDouble();
            Assert.True(Vec2.Distance(left.PointAt(u), c.PointAt(t * u)) < 1e-12);
            Assert.True(Vec2.Distance(right.PointAt(u), c.PointAt(t + ((1.0 - t) * u))) < 1e-12);
            Assert.Equal(c.Length(), left.Length() + right.Length(), 1e-8);
        }
    }

    [Fact]
    public void Arc_length_of_straight_and_circle_like_curves_has_known_values()
    {
        var line = new CubicBezier(new Vec2(0, 0), new Vec2(1, 1), new Vec2(2, 2), new Vec2(3, 3));
        Assert.Equal(3.0 * Math.Sqrt(2.0), line.Length(), 1e-9);

        // Равномерная параметризация отрезка: длина до t равна t * L.
        Assert.Equal(0.25 * 3.0 * Math.Sqrt(2.0), line.LengthTo(0.25), 1e-9);

        // Четверть окружности по стандартной аппроксимации: ошибка радиуса ~2.7e-4.
        const double k = 0.5522847498307936;
        var arc = new CubicBezier(new Vec2(1, 0), new Vec2(1, k), new Vec2(k, 1), new Vec2(0, 1));
        Assert.Equal(Math.PI / 2.0, arc.Length(), 3);

        var point = new CubicBezier(new Vec2(2, 2), new Vec2(2, 2), new Vec2(2, 2), new Vec2(2, 2));
        Assert.Equal(0.0, point.Length());
    }

    [Fact]
    public void Arc_length_matches_a_dense_polyline()
    {
        var rng = new Pcg32(13);
        for (int i = 0; i < 100; i++)
        {
            CubicBezier c = RandomCurve(rng);
            var dense = new List<Vec2>();
            c.Flatten(1e-7, dense);
            double polyLength = PolylineMath.Length([.. dense]);
            double length = c.Length();
            // Полилиния всегда короче дуги, но отличается не более чем на ~tolerance·число сегментов·малый коэффициент.
            Assert.True(polyLength <= length + 1e-9, $"polyline longer than arc at {i}");
            Assert.True(length - polyLength < 1e-5, $"arc {length} vs polyline {polyLength} at {i}");
            Assert.True(length <= c.ControlPolygonLength() + 1e-12);
            Assert.True(length >= Vec2.Distance(c.P0, c.P3) - 1e-12);
        }
    }

    [Fact]
    public void ParameterAtLength_inverts_LengthTo()
    {
        var rng = new Pcg32(14);
        for (int i = 0; i < 50; i++)
        {
            CubicBezier c = RandomCurve(rng);
            double total = c.Length();
            double s = rng.NextDouble() * total;
            double t = c.ParameterAtLength(s);
            Assert.InRange(t, 0.0, 1.0);
            Assert.Equal(s, c.LengthTo(t), 1e-7);
        }

        var q = RandomCurve(new Pcg32(1));
        Assert.Equal(0.0, q.ParameterAtLength(-1.0));
        Assert.Equal(1.0, q.ParameterAtLength(1e9));
    }

    [Fact]
    public void Flatten_stays_within_tolerance_of_the_curve_and_keeps_exact_endpoints()
    {
        var rng = new Pcg32(15);
        foreach (double tol in new[] { 0.1, 0.01, 0.001 })
        {
            for (int i = 0; i < 50; i++)
            {
                CubicBezier c = RandomCurve(rng);
                var coarse = new List<Vec2>();
                c.Flatten(tol, coarse);
                Assert.Equal(c.P0, coarse[0]);
                Assert.Equal(c.P3, coarse[^1]);
                Assert.Equal(c.FlattenSegmentCount(tol) + 1, coarse.Count);

                // Плотные точки кривой не дальше tol от полилинии.
                Vec2[] poly = [.. coarse];
                double worst = 0.0;
                for (int k = 0; k <= 2000; k++)
                {
                    worst = Math.Max(worst, PolylineMath.DistanceToPolyline(c.PointAt(k / 2000.0), poly));
                }

                Assert.True(worst <= tol, $"deviation {worst} > tol {tol} (curve {i})");
            }
        }
    }

    [Fact]
    public void Flatten_without_start_appends_after_existing_points()
    {
        var c = new CubicBezier(new Vec2(0, 0), new Vec2(0, 1), new Vec2(1, 1), new Vec2(1, 0));
        var list = new List<Vec2> { c.P0 };
        c.Flatten(0.01, list, includeStart: false);
        var full = new List<Vec2>();
        c.Flatten(0.01, full);
        Assert.Equal(full, list);
    }

    [Fact]
    public void Straight_line_flattens_to_one_segment_and_bad_arguments_throw()
    {
        var line = new CubicBezier(new Vec2(0, 0), new Vec2(1, 0), new Vec2(2, 0), new Vec2(3, 0));
        Assert.Equal(1, line.FlattenSegmentCount(0.001));
        Assert.Throws<ArgumentOutOfRangeException>(() => line.FlattenSegmentCount(0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => line.Length(0.0));
        Assert.Throws<ArgumentNullException>(() => line.Flatten(0.1, null!));
    }
}
