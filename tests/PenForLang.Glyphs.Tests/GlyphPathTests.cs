using PenForLang.Core.Geometry;

namespace PenForLang.Glyphs.Tests;

public class GlyphPathTests
{
    private static GlyphPath LineThenQuarterCircle()
    {
        // Отрезок (0,0)->(1,0), затем четверть окружности радиуса 1 с центром (1,1): (1,0)->(2,1).
        const double K = 0.5522847498307936;
        return new GlyphPath(
        [
            GlyphSegment.Line(new Vec2(0, 0), new Vec2(1, 0)),
            GlyphSegment.Cubic(new Vec2(1, 0), new Vec2(1 + K, 0), new Vec2(2, 1 - K), new Vec2(2, 1)),
        ]);
    }

    [Fact]
    public void Length_is_the_sum_of_segment_lengths()
    {
        GlyphPath path = LineThenQuarterCircle();

        Assert.Equal(1.0, path.SegmentLength(0), 9);
        Assert.Equal(Math.PI / 2, path.SegmentLength(1), 3);
        Assert.Equal(1.0 + (Math.PI / 2), path.Length, 3);
        Assert.Equal(1.0, path.SegmentStart(1), 9);
    }

    [Fact]
    public void A_line_segment_is_traversed_at_constant_speed()
    {
        GlyphSegment line = GlyphSegment.Line(new Vec2(0, 0), new Vec2(3, 4));
        var path = new GlyphPath([line]);

        Vec2 quarter = path.PointAtDistance(1.25);

        Assert.Equal(5.0, path.Length, 9);
        Assert.Equal(0.75, quarter.X, 6);
        Assert.Equal(1.0, quarter.Y, 6);
        Assert.Equal(0.6, path.StartTangent.X, 9);
        Assert.Equal(0.8, path.EndTangent.Y, 9);
    }

    [Fact]
    public void Points_and_tangents_are_found_by_distance_and_fraction()
    {
        GlyphPath path = LineThenQuarterCircle();

        Assert.Equal(new Vec2(0, 0), path.PointAtDistance(-1));
        Assert.Equal(path.End, path.PointAtDistance(100));
        Vec2 joint = path.PointAtFraction(path.FractionAtSegmentEnd(0));
        Assert.Equal(1.0, joint.X, 6);
        Assert.Equal(0.0, joint.Y, 6);

        Vec2 start = path.TangentAtDistance(0.5);
        Vec2 end = path.EndTangent;
        Assert.Equal(1.0, start.X, 9);
        Assert.Equal(0.0, end.X, 6);
        Assert.Equal(1.0, end.Y, 6);

        // Середина дуги окружности: точка (1 + sin 45°, 1 - cos 45°).
        Vec2 middle = path.PointAtDistance(1.0 + (Math.PI / 4));
        Assert.Equal(1.0 + Math.Sin(Math.PI / 4), middle.X, 3);
        Assert.Equal(1.0 - Math.Cos(Math.PI / 4), middle.Y, 3);
    }

    [Fact]
    public void Flatten_stays_within_tolerance_and_keeps_the_end_points()
    {
        GlyphPath path = LineThenQuarterCircle();

        Vec2[] points = path.Flatten(0.001);

        Assert.Equal(path.Start, points[0]);
        Assert.Equal(path.End, points[^1]);
        foreach (Vec2 p in points.Skip(points.Length / 2))
        {
            Assert.Equal(1.0, Vec2.Distance(p, new Vec2(1, 1)), 2);
        }
    }

    [Fact]
    public void Bounds_cover_the_whole_path()
    {
        (Vec2 min, Vec2 max) = LineThenQuarterCircle().GetBounds();

        Assert.Equal(0.0, min.X, 6);
        Assert.Equal(0.0, min.Y, 3);
        Assert.Equal(2.0, max.X, 3);
        Assert.Equal(1.0, max.Y, 3);
    }

    [Fact]
    public void An_empty_path_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new GlyphPath([]));
    }

    [Fact]
    public void Segment_factories_make_lines_as_degenerate_cubics()
    {
        GlyphSegment line = GlyphSegment.Line(new Vec2(0, 0), new Vec2(3, 0), retrace: true, label: "x.y", tempo: 2.0, pauseBeforeMs: 50);

        Assert.Equal(SegmentKind.Line, line.Kind);
        Assert.Equal(new Vec2(1, 0), line.P1);
        Assert.Equal(new Vec2(2, 0), line.P2);
        Assert.True(line.Retrace);
        Assert.Equal("x.y", line.Label);
        Assert.Equal(2.0, line.Tempo);
        Assert.Equal(50, line.PauseBeforeMs);
    }
}
