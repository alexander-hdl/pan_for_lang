using System.Windows;
using PenForLang.Core.Geometry;
using PenForLang.Glyphs;
using PenForLang.Glyphs.Animation;
using PenForLang.Ink;
using PenForLang.Testing;
using PenForLang.Wpf.Controls.Tests.Support;

namespace PenForLang.Wpf.Controls.Tests;

public class GlyphAnimatorLayerTests
{
    private const double PxPerMm = 7.5;

    private static AnimationScript ScriptFor(string symbol) =>
        AnimationScriptBuilder.Build(Shipped.Pack.GetGlyph(symbol), Shipped.Style.Animation);

    private static GuideFrame FrameFor(double dpi, double originMm = 30, double baselineMm = 50, double xHeightMm = 15)
    {
        var mapper = new SurfaceMapper(new PenSurface(0, 0, 1920, 1080), PxPerMm, dpi);
        return GuideFrame.FromMm(mapper, Shipped.Style.Id, originMm, baselineMm, xHeightMm, 3.0);
    }

    private static GlyphAnimatorLayer Create(AnimationScript script, FakeMonotonicClock clock, double dpi = 1.0)
    {
        return new GlyphAnimatorLayer(clock) { Script = script, Guides = FrameFor(dpi), DpiScale = dpi };
    }

    [Fact]
    public void Initial_state_shows_only_the_start_point_of_the_first_stroke()
    {
        Sta.Run(() =>
        {
            AnimationScript script = ScriptFor("a");
            GlyphAnimatorLayer layer = Create(script, new FakeMonotonicClock());

            AnimationState state = layer.State!;
            Assert.Equal(0, layer.PositionMs);
            Assert.False(layer.IsPlaying);
            Assert.False(layer.IsFinished);
            Assert.All(state.StrokeProgress, p => Assert.Equal(0, p));
            Assert.False(state.PenDown);
            Assert.Contains(0, state.VisibleStartDots);
            Assert.Contains(0, state.VisibleNumbers);
            Assert.Empty(state.VisibleArrows);
        });
    }

    [Fact]
    public void Visible_elements_grow_with_time_and_everything_is_shown_at_the_end()
    {
        Sta.Run(() =>
        {
            AnimationScript script = ScriptFor("t");
            GlyphAnimatorLayer layer = Create(script, new FakeMonotonicClock());

            int numbers = 0;
            int arrows = 0;
            int dots = 0;
            double progress = 0;
            for (double t = 0; t <= script.DurationMs; t += script.DurationMs / 40)
            {
                layer.Seek(t);
                AnimationState s = layer.State!;
                Assert.Equal(script.StateAt(t).VisibleNumbers, s.VisibleNumbers);
                Assert.True(s.VisibleNumbers.Length >= numbers && s.VisibleArrows.Length >= arrows && s.VisibleStartDots.Length >= dots);
                Assert.True(s.StrokeProgress.Sum() >= progress - 1e-12);
                numbers = s.VisibleNumbers.Length;
                arrows = s.VisibleArrows.Length;
                dots = s.VisibleStartDots.Length;
                progress = s.StrokeProgress.Sum();
            }

            layer.Seek(script.DurationMs);
            AnimationState end = layer.State!;
            Assert.True(layer.IsFinished);
            Assert.True(end.IsFinished);
            Assert.All(end.StrokeProgress, p => Assert.Equal(1.0, p));
            Assert.Equal(script.Numbers.Length, end.VisibleNumbers.Length);
            Assert.Equal(script.Arrows.Length, end.VisibleArrows.Length);
            Assert.Equal(script.StartDots.Length, end.VisibleStartDots.Length);
        });
    }

    [Fact]
    public void Second_stroke_of_a_two_stroke_letter_appears_only_after_the_first_is_finished()
    {
        Sta.Run(() =>
        {
            AnimationScript script = ScriptFor("t");
            Assert.True(script.Strokes.Length >= 2);
            GlyphAnimatorLayer layer = Create(script, new FakeMonotonicClock());

            layer.Seek(script.Strokes[0].StartMs + ((script.Strokes[0].EndMs - script.Strokes[0].StartMs) / 2));
            Assert.InRange(layer.State!.StrokeProgress[0], 0.01, 0.99);
            Assert.Equal(0, layer.State.StrokeProgress[1]);
            Assert.Equal(0, layer.State.ActiveStroke);
            Assert.True(layer.State.PenDown);

            layer.Seek((script.Strokes[0].EndMs + script.Strokes[1].StartMs) / 2); // перо в воздухе между штрихами
            Assert.Equal(1.0, layer.State!.StrokeProgress[0]);
            Assert.Equal(0, layer.State.StrokeProgress[1]);
            Assert.False(layer.State.PenDown);

            layer.Seek(script.Strokes[1].EndMs);
            Assert.Equal(1.0, layer.State!.StrokeProgress[1]);
        });
    }

    [Fact]
    public void Play_pause_seek_and_speed_follow_the_injected_clock()
    {
        Sta.Run(() =>
        {
            var clock = new FakeMonotonicClock();
            GlyphAnimatorLayer layer = Create(ScriptFor("a"), clock);

            layer.FrameTick();
            clock.Advance(TimeSpan.FromMilliseconds(500));
            layer.FrameTick();
            Assert.Equal(0, layer.PositionMs); // без Play время не идёт

            layer.Play();
            Assert.True(layer.IsPlaying);
            clock.Advance(TimeSpan.FromMilliseconds(100));
            layer.FrameTick();
            Assert.Equal(100, layer.PositionMs, 3);

            layer.Pause();
            Assert.False(layer.IsPlaying);
            clock.Advance(TimeSpan.FromMilliseconds(700));
            layer.FrameTick();
            Assert.Equal(100, layer.PositionMs, 3);

            layer.Play();
            clock.Advance(TimeSpan.FromMilliseconds(50));
            layer.FrameTick();
            Assert.Equal(150, layer.PositionMs, 3); // пауза не «съела» и не добавила время

            layer.Seek(400);
            Assert.Equal(400, layer.PositionMs, 3);
            clock.Advance(TimeSpan.FromMilliseconds(10));
            layer.FrameTick();
            Assert.Equal(410, layer.PositionMs, 3);

            layer.Speed = 2.0;
            clock.Advance(TimeSpan.FromMilliseconds(10));
            layer.FrameTick();
            Assert.Equal(430, layer.PositionMs, 3);

            layer.Stop();
            Assert.False(layer.IsPlaying);
            Assert.Equal(0, layer.PositionMs);
            layer.Seek(-50);
            Assert.Equal(0, layer.PositionMs);
            layer.Seek(1e9);
            Assert.Equal(layer.Script!.DurationMs, layer.PositionMs);
            layer.Seek(double.NaN);
            Assert.Equal(0, layer.PositionMs);
        });
    }

    [Fact]
    public void Playback_finishes_once_and_replay_starts_over()
    {
        Sta.Run(() =>
        {
            var clock = new FakeMonotonicClock();
            AnimationScript script = ScriptFor("i");
            GlyphAnimatorLayer layer = Create(script, clock);
            int finished = 0;
            layer.PlaybackFinished += (_, _) => finished++;

            layer.Play();
            clock.Advance(TimeSpan.FromMilliseconds(script.DurationMs - 10));
            layer.FrameTick();
            Assert.Equal(0, finished);
            Assert.True(layer.IsPlaying);

            clock.Advance(TimeSpan.FromMilliseconds(20));
            layer.FrameTick();
            Assert.Equal(1, finished);
            Assert.False(layer.IsPlaying);
            Assert.True(layer.IsFinished);
            Assert.Equal(script.DurationMs, layer.PositionMs);

            clock.Advance(TimeSpan.FromSeconds(5));
            layer.FrameTick();
            Assert.Equal(1, finished);

            layer.Replay();
            Assert.True(layer.IsPlaying);
            Assert.Equal(0, layer.PositionMs);
            Assert.False(layer.IsFinished);
            clock.Advance(TimeSpan.FromMilliseconds(200));
            layer.FrameTick();
            Assert.Equal(200, layer.PositionMs, 3);

            // Play в конце сценария тоже начинает с начала.
            layer.Seek(script.DurationMs);
            layer.Pause();
            layer.Play();
            Assert.Equal(0, layer.PositionMs);
        });
    }

    [Fact]
    public void Changing_the_script_resets_the_playback()
    {
        Sta.Run(() =>
        {
            var clock = new FakeMonotonicClock();
            GlyphAnimatorLayer layer = Create(ScriptFor("a"), clock);
            layer.Play();
            clock.Advance(TimeSpan.FromMilliseconds(300));
            layer.FrameTick();
            Assert.True(layer.PositionMs > 0);

            layer.Script = ScriptFor("i");

            Assert.False(layer.IsPlaying);
            Assert.Equal(0, layer.PositionMs);
            Assert.Equal("i", layer.Script.Glyph?.Symbol);

            layer.Script = null;
            Assert.Null(layer.State);
            layer.Play(); // без сценария ничего не происходит
            Assert.False(layer.IsPlaying);
        });
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void Pen_position_maps_ruler_coordinates_to_dip_without_double_dpi_scaling(double dpi)
    {
        Sta.Run(() =>
        {
            AnimationScript script = ScriptFor("a");
            GlyphAnimatorLayer layer = Create(script, new FakeMonotonicClock(), dpi);
            GuideFrame frame = layer.Guides!;
            Vec2 start = script.Strokes[0].Path.Start;

            Point pen = layer.PenPositionDip!.Value;

            // Физические px не зависят от масштаба Windows, DIP = px / dpi.
            double xPx = (30 * PxPerMm) + (start.X * 15 * PxPerMm);
            double yPx = (50 * PxPerMm) - (start.Y * 15 * PxPerMm);
            Assert.Equal(xPx, pen.X * dpi, 6);
            Assert.Equal(yPx, pen.Y * dpi, 6);
            Assert.Equal((frame.OriginXPx + (start.X * frame.XHeightPx)) / dpi, pen.X, 6);
        });
    }

    [Fact]
    public void Rendered_layer_shows_the_start_dot_first_and_the_stroke_ink_at_the_end()
    {
        Sta.Run(() =>
        {
            AnimationScript script = ScriptFor("t");
            GlyphAnimatorLayer layer = Create(script, new FakeMonotonicClock());

            (byte[] start, int width, _) = Pixels.Render(layer, 700, 500, 1.0);
            int green = CountColor(start, c => c.G > 130 && c.R < 80 && c.B < 130);
            int ink = CountColor(start, c => c.B > 150 && c.R < 70 && c.G > 60 && c.G < 130);
            Assert.True(green > 20, $"the green start dot must be visible at t = 0 (found {green})");
            Assert.Equal(0, ink);

            layer.Seek(script.DurationMs);
            (byte[] end, _, _) = Pixels.Render(layer, 700, 500, 1.0);
            int finalInk = CountColor(end, c => c.B > 150 && c.R < 70 && c.G > 60 && c.G < 130);
            Assert.True(finalInk > 300, $"the finished stroke must be inked (found {finalInk})");
            Assert.True(width == 700);
        });
    }

    [Fact]
    public void Partial_stroke_is_drawn_only_as_far_as_the_pen_has_gone()
    {
        Sta.Run(() =>
        {
            AnimationScript script = ScriptFor("t");
            GlyphAnimatorLayer layer = Create(script, new FakeMonotonicClock());
            AnimatedStroke first = script.Strokes[0];

            layer.Seek(first.StartMs + ((first.EndMs - first.StartMs) * 0.3));
            (byte[] early, _, _) = Pixels.Render(layer, 700, 500, 1.0);
            layer.Seek(first.EndMs);
            (byte[] done, _, _) = Pixels.Render(layer, 700, 500, 1.0);

            // Штрих = тело (синий) + входящий/выходящий штрихи (серый); перо, номера и стрелки в счёт не входят.
            bool IsStroke((byte R, byte G, byte B) c) =>
                (c.B > 150 && c.R < 70 && c.G > 60 && c.G < 130) || (Math.Abs(c.R - 0x9A) < 12 && Math.Abs(c.G - 0x99) < 12 && Math.Abs(c.B - 0x96) < 12);
            int earlyInk = CountColor(early, IsStroke);
            int doneInk = CountColor(done, IsStroke);
            Assert.True(earlyInk > 0);
            Assert.True(earlyInk < doneInk * 0.8, $"30% of the stroke ({earlyInk}) must be clearly less than the whole ({doneInk})");
        });
    }

    [Theory]
    [InlineData(1.0, 12.0)]
    [InlineData(1.25, 12.0)]
    [InlineData(1.5, 12.0)]
    [InlineData(1.5, 6.0)]
    public void Marks_never_fall_below_their_physical_minimum_in_millimetres(double dpi, double xHeightMm)
    {
        var style = new AnimatorMarkStyle();
        double dipPerMm = PxPerMm / dpi;

        AnimatorMarkSizes sizes = style.Resolve(xHeightMm * dipPerMm, dipPerMm);

        double ToMm(double dip) => dip * dpi / PxPerMm;
        Assert.True(ToMm(sizes.ArrowLength) >= 4.5 - 1e-9, "arrow >= 4.5 mm");
        Assert.True(ToMm(sizes.BadgeRadius * 2) >= 6.0, "number badge >= 6 mm");
        Assert.True(ToMm(sizes.DigitEm * 0.7) >= 4.0 - 1e-9, "digit >= 4 mm");
        Assert.True(ToMm(sizes.StartDotRadius * 2) >= 3.0 - 1e-9, "start dot >= 3 mm");
        Assert.True(ToMm(sizes.PenRadius * 2) >= 4.0 - 1e-9, "pen >= 4 mm");
        Assert.True(sizes.BadgeRadius > sizes.DigitEm * 0.5, "the badge encloses the digit");
        Assert.True(sizes.ArrowSideOffset > 0, "the arrow stands beside the line");
    }

    [Fact]
    public void Finished_strokes_are_cached_and_only_the_active_one_is_rebuilt()
    {
        Sta.Run(() =>
        {
            AnimationScript script = ScriptFor("t");
            GlyphAnimatorLayer layer = Create(script, new FakeMonotonicClock());
            AnimatedStroke first = script.Strokes[0];

            layer.Seek(first.StartMs + ((first.EndMs - first.StartMs) * 0.5));
            _ = Pixels.Render(layer, 700, 500, 1.0);
            Assert.Equal(0, layer.CachedStrokeCount);

            layer.Seek(script.Strokes[1].StartMs + 1);
            _ = Pixels.Render(layer, 700, 500, 1.0);
            Assert.Equal(1, layer.CachedStrokeCount);

            layer.Seek(script.DurationMs);
            _ = Pixels.Render(layer, 700, 500, 1.0);
            Assert.Equal(script.Strokes.Length, layer.CachedStrokeCount);

            // Смена линейки (например, другое окно или DPI) сбрасывает кеш; картинка та же.
            layer.Guides = FrameFor(1.0, originMm: 31);
            _ = Pixels.Render(layer, 700, 500, 1.0);
            Assert.Equal(script.Strokes.Length, layer.CachedStrokeCount);
        });
    }

    private static int CountColor(byte[] bgra, Func<(byte R, byte G, byte B), bool> predicate)
    {
        int count = 0;
        for (int i = 0; i < bgra.Length; i += 4)
        {
            if (bgra[i + 3] > 200 && predicate((bgra[i + 2], bgra[i + 1], bgra[i])))
            {
                count++;
            }
        }

        return count;
    }

    [Fact]
    public void Arrows_can_be_hidden_and_the_rest_of_the_model_stays()
    {
        Sta.Run(() =>
        {
            AnimationScript script = ScriptFor("a");
            GlyphAnimatorLayer layer = Create(script, new FakeMonotonicClock());
            layer.Seek(script.DurationMs);

            (byte[] withArrows, _, _) = Pixels.Render(layer, 1920, 1080, 1.0);
            layer.ShowArrows = false;
            Assert.False(layer.ShowArrows);
            (byte[] without, _, _) = Pixels.Render(layer, 1920, 1080, 1.0);

            Assert.True(Pixels.CountAlpha(without) < Pixels.CountAlpha(withArrows)); // стрелки пропали
            Assert.True(Pixels.CountAlpha(without) > 0.8 * Pixels.CountAlpha(withArrows)); // буква, номер и точка старта на месте
        });
    }
}
