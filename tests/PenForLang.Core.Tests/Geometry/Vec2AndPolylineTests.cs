using PenForLang.Core.Geometry;

namespace PenForLang.Core.Tests.Geometry;

public class Vec2AndPolylineTests
{
    [Fact]
    public void Vec2_arithmetic_and_products()
    {
        var a = new Vec2(3, 4);
        var b = new Vec2(-1, 2);
        Assert.Equal(new Vec2(2, 6), a + b);
        Assert.Equal(new Vec2(4, 2), a - b);
        Assert.Equal(new Vec2(-3, -4), -a);
        Assert.Equal(new Vec2(6, 8), a * 2.0);
        Assert.Equal(new Vec2(6, 8), 2.0 * a);
        Assert.Equal(new Vec2(1.5, 2), a / 2.0);
        Assert.Equal(5.0, a.Length);
        Assert.Equal(25.0, a.LengthSquared);
        Assert.Equal(5.0, Vec2.Dot(a, b));
        Assert.Equal(10.0, Vec2.Cross(a, b));
        Assert.Equal(Vec2.Zero, Vec2.Zero.Normalized());
        Assert.Equal(1.0, a.Normalized().Length, 1e-15);
        Assert.Equal(new Vec2(-4, 3), a.Perpendicular());
        Assert.Equal(Math.Atan2(4, 3), a.Angle());
        Assert.Equal(new Vec2(1, 1), Vec2.Lerp(new Vec2(0, 0), new Vec2(2, 2), 0.5));
        Assert.Equal(5.0, Vec2.Distance(a, Vec2.Zero));
        Assert.Equal(25.0, Vec2.DistanceSquared(a, Vec2.Zero));
    }

    [Fact]
    public void Polyline_length_cumulative_and_reverse()
    {
        Vec2[] pts = [new(0, 0), new(3, 4), new(3, 8)];
        Assert.Equal(9.0, PolylineMath.Length(pts));
        Assert.Equal(0.0, PolylineMath.Length([new Vec2(1, 1)]));
        Assert.Equal(0.0, PolylineMath.Length([]));

        var cumulative = new double[3];
        PolylineMath.CumulativeLengths(pts, cumulative);
        Assert.Equal([0.0, 5.0, 9.0], cumulative);

        var reversed = new Vec2[3];
        PolylineMath.Reverse(pts, reversed);
        Assert.Equal([new Vec2(3, 8), new Vec2(3, 4), new Vec2(0, 0)], reversed);
        Assert.Throws<ArgumentException>(() => PolylineMath.Reverse(pts, new Vec2[2]));
        Assert.Throws<ArgumentException>(() => PolylineMath.CumulativeLengths(pts, new double[2]));
        Vec2[] buffer = [.. pts];
        Assert.Throws<ArgumentException>(() => PolylineMath.Reverse(buffer, buffer));
    }

    [Fact]
    public void Distance_to_segment_and_polyline()
    {
        Assert.Equal(1.0, PolylineMath.DistanceToSegment(new Vec2(1, 1), new Vec2(0, 0), new Vec2(2, 0)));
        Assert.Equal(Math.Sqrt(2.0), PolylineMath.DistanceToSegment(new Vec2(3, 1), new Vec2(0, 0), new Vec2(2, 0)));
        Assert.Equal(5.0, PolylineMath.DistanceToSegment(new Vec2(3, 4), new Vec2(0, 0), new Vec2(0, 0)));
        Vec2[] poly = [new(0, 0), new(2, 0), new(2, 2)];
        Assert.Equal(0.5, PolylineMath.DistanceToPolyline(new Vec2(1.5, 0.5), poly));
        Assert.Equal(5.0, PolylineMath.DistanceToPolyline(new Vec2(3, 4), [new Vec2(0, 0)]));
        Assert.Throws<ArgumentException>(() => PolylineMath.DistanceToPolyline(Vec2.Zero, []));
    }

    [Fact]
    public void Deslant_straightens_a_forward_slanted_stroke_and_keeps_the_pivot()
    {
        double angle = 12.0 * Math.PI / 180.0;
        double tan = Math.Tan(angle);

        // Штрих с наклоном вправо: x = y * tan(angle), y вверх.
        var stroke = new Vec2[11];
        for (int i = 0; i < stroke.Length; i++)
        {
            double y = i * 0.1;
            stroke[i] = new Vec2(0.3 + (y * tan), y);
        }

        var result = new Vec2[stroke.Length];
        Deslant.Apply(stroke, result, angle);
        Assert.All(result, p => Assert.Equal(0.3, p.X, 1e-12));
        Assert.Equal(stroke.Select(p => p.Y), result.Select(p => p.Y));

        // Опорная ордината не сдвигается.
        var pivot = new Vec2[] { new(5, 0.5), new(7, 1.5) };
        var shifted = new Vec2[2];
        Deslant.ApplyDegrees(pivot, shifted, 30.0, pivotY: 0.5);
        Assert.Equal(5.0, shifted[0].X, 1e-12);
        Assert.Equal(7.0 - Math.Tan(30.0 * Math.PI / 180.0), shifted[1].X, 1e-12);

        // На месте.
        Vec2[] inPlace = [.. stroke];
        Deslant.Apply(inPlace, inPlace, angle);
        Assert.Equal(result, inPlace);

        // Нулевой угол — тождество; недопустимые аргументы.
        var same = new Vec2[stroke.Length];
        Deslant.Apply(stroke, same, 0.0);
        Assert.Equal(stroke, same);
        Assert.Throws<ArgumentOutOfRangeException>(() => Deslant.Apply(stroke, result, Math.PI / 2.0));
        Assert.Throws<ArgumentException>(() => Deslant.Apply(stroke, new Vec2[1], 0.1));
    }
}
