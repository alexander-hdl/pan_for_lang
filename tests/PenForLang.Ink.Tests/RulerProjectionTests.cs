using System.Collections.Immutable;
using static PenForLang.Ink.Tests.TestData;

namespace PenForLang.Ink.Tests;

public class RulerProjectionTests
{
    private const double Eps = 1e-12;

    private static InkAttempt Attempt(GuideFrame guides, params InkStroke[] strokes) =>
        new([.. strokes], guides, Device(), 640_000, InkCompletion.Pause, 0, []);

    [Fact]
    public void Baseline_is_zero_x_height_is_one_and_v_points_up()
    {
        GuideFrame guides = Guides(); // x0=180, baseline=620, xHeight=112.5 px = 15 mm
        var attempt = Attempt(guides, new InkStroke(0, [
            Sample(1, 180, 620, PenPhase.Down),
            Sample(2, 292.5, 507.5),
            Sample(3, 180 + 56.25, 620 + 112.5, PenPhase.Up),
        ]));

        RulerInk ink = RulerProjection.Project(attempt);

        ImmutableArray<RulerPoint> points = ink.Strokes[0].Points;
        Assert.Equal(0, points[0].U, Eps);
        Assert.Equal(0, points[0].V, Eps);
        Assert.Equal(1, points[1].U, Eps);
        Assert.Equal(1, points[1].V, Eps);
        Assert.Equal(0.5, points[2].U, Eps);
        Assert.Equal(-1, points[2].V, Eps); // ниже baseline на x-height
        Assert.Equal(15, ink.MmPerUnit, Eps);
        Assert.Equal(640_000, ink.StimulusEndUs);
        Assert.False(ink.InterpolatedTime);
        Assert.Equal(new long[] { 1, 2, 3 }, points.Select(p => p.TimeUs));
    }

    [Fact]
    public void ToRuler_and_ToSurface_are_inverse()
    {
        GuideFrame guides = new(33.3, 417.9, 80.25, 6.0, 2.5, null, "s");
        foreach ((double x, double y) in new[] { (0.0, 0.0), (1234.5, -77.7), (33.3, 417.9) })
        {
            (double u, double v) = RulerProjection.ToRuler(guides, x, y);
            (double x2, double y2) = RulerProjection.ToSurface(guides, u, v);
            Assert.Equal(x, x2, 9);
            Assert.Equal(y, y2, 9);
        }
    }

    [Fact]
    public void Hover_barrel_and_eraser_points_are_dropped_and_empty_strokes_vanish_keeping_indices()
    {
        GuideFrame guides = Guides();
        var attempt = Attempt(
            guides,
            new InkStroke(0, [Sample(1, 200, 600, PenPhase.Hover), Sample(2, 210, 600, PenPhase.Down), Sample(3, 220, 600, buttons: PenButtons.Barrel), Sample(4, 230, 600, PenPhase.Up)]),
            new InkStroke(1, [Sample(5, 200, 600, PenPhase.Down, buttons: PenButtons.Eraser), Sample(6, 210, 600, PenPhase.Up, buttons: PenButtons.Eraser)]),
            new InkStroke(2, [Sample(7, 240, 600, PenPhase.Down), Sample(8, 250, 600, PenPhase.Up)]));

        RulerInk ink = RulerProjection.Project(attempt);

        Assert.Equal([0, 2], ink.Strokes.Select(s => s.Index));
        Assert.Equal([2L, 4L], ink.Strokes[0].Points.Select(p => p.TimeUs));
        Assert.Equal(2, ink.Strokes[1].Points.Length);
    }

    [Fact]
    public void Clipped_and_interpolated_flags_propagate()
    {
        var attempt = Attempt(
            Guides(),
            new InkStroke(0, [Sample(1, 200, 600, PenPhase.Down), Sample(2, -3.5, 601, flags: SampleFlags.Clipped), Sample(3, 210, 600, PenPhase.Up)]),
            new InkStroke(1, [Sample(4, 200, 600, PenPhase.Down, flags: SampleFlags.InterpolatedTime), Sample(5, 201, 600, PenPhase.Up)]));

        RulerInk ink = RulerProjection.Project(attempt);

        Assert.True(ink.Strokes[0].Clipped);
        Assert.False(ink.Strokes[1].Clipped);
        Assert.True(ink.InterpolatedTime);
    }

    [Fact]
    public void Empty_attempt_projects_to_empty_ink_and_null_stimulus_is_kept()
    {
        var attempt = new InkAttempt([], Guides(), Device(), null, InkCompletion.Cancelled, 0, []);

        RulerInk ink = RulerProjection.Project(attempt);

        Assert.Empty(ink.Strokes);
        Assert.Null(ink.StimulusEndUs);
    }

    [Fact]
    public void Pressure_is_copied()
    {
        var attempt = Attempt(Guides(), new InkStroke(0, [Sample(1, 200, 600, PenPhase.Down, pressure: 0.125f), Sample(2, 201, 600, PenPhase.Up, pressure: 0f)]));

        RulerInk ink = RulerProjection.Project(attempt);

        Assert.Equal([0.125f, 0f], ink.Strokes[0].Points.Select(p => p.Pressure));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void Projection_does_not_depend_on_dpi_scale(double dpi)
    {
        // Один и тот же физический холст и один и тот же штрих в физических px на мониторе с любым DPI-масштабом Windows.
        // Окно в DIP отличается, физические px (PointToScreen) — нет; линейка берётся из физических px, значит u, v совпадают.
        var monitor = new SurfaceMapper(new PenSurface(0, 0, 1200, 800), 7.5, dpi);
        (double x, double y) = monitor.ScreenToSurface(180 + 56.25, 620 - 56.25);
        var attempt = Attempt(Guides(), new InkStroke(0, [Sample(1, x, y, PenPhase.Down), Sample(2, x, y, PenPhase.Up)]));

        RulerInk ink = RulerProjection.Project(attempt);

        Assert.Equal(0.5, ink.Strokes[0].Points[0].U, Eps);
        Assert.Equal(0.5, ink.Strokes[0].Points[0].V, Eps);
    }

    [Fact]
    public void Null_attempt_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => RulerProjection.Project(null!));
    }
}
