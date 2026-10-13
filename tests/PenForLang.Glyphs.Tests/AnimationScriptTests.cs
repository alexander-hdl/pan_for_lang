using PenForLang.Core.Geometry;
using PenForLang.Glyphs.Animation;
using PenForLang.Glyphs.Tests.Support;

namespace PenForLang.Glyphs.Tests;

/// <summary>Сценарий анимации: порядок, длительность, монотонность, паузы, стрелки и отложенные штрихи в конце.</summary>
public class AnimationScriptTests
{
    private static readonly AnimationSettings Settings = Shipped.Style.Animation;

    public static TheoryData<string> Symbols => ["a", "i", "t"];

    private static AnimationScript Build(string symbol, AnimationOptions? options = null) =>
        AnimationScriptBuilder.Build(Shipped.Glyph(symbol), Settings, options);

    [Theory]
    [MemberData(nameof(Symbols))]
    public void Time_zero_shows_nothing_drawn_with_the_pen_at_the_start(string symbol)
    {
        AnimationScript script = Build(symbol);

        AnimationState state = script.StateAt(0);

        Assert.All(state.StrokeProgress, p => Assert.Equal(0.0, p));
        Assert.Equal(-1, state.ActiveStroke);
        Assert.False(state.PenDown);
        Assert.False(state.IsFinished);
        Assert.Equal(script.Strokes[0].Path.Start, state.PenPosition);
        Assert.Single(state.VisibleStartDots);
        Assert.Contains(0, state.VisibleNumbers.Select(i => script.Numbers[i].StrokeIndex));
        Assert.Empty(state.VisibleArrows);
        Assert.Empty(state.CompletedStrokes);
    }

    [Theory]
    [MemberData(nameof(Symbols))]
    public void The_end_shows_everything_drawn_and_all_markers_visible(string symbol)
    {
        AnimationScript script = Build(symbol);

        AnimationState end = script.StateAt(script.DurationMs);
        AnimationState beyond = script.StateAt(script.DurationMs + 10_000);

        Assert.All(end.StrokeProgress, p => Assert.Equal(1.0, p));
        Assert.True(end.IsFinished);
        Assert.False(end.PenDown);
        Assert.Equal(-1, end.ActiveStroke);
        Assert.Equal(script.Strokes[^1].Path.End, end.PenPosition);
        Assert.Equal(script.Numbers.Length, end.VisibleNumbers.Length);
        Assert.Equal(script.Arrows.Length, end.VisibleArrows.Length);
        Assert.Equal(script.StartDots.Length, end.VisibleStartDots.Length);
        Assert.Equal(script.DurationMs, beyond.TimeMs);
        Assert.Equal(end.StrokeProgress.ToArray(), beyond.StrokeProgress.ToArray());
        Assert.Equal(script.Strokes.Length, end.CompletedStrokes.Count());
        Assert.Equal(0.0, script.StateAt(-5).TimeMs);
        Assert.Equal(0.0, script.StateAt(double.NaN).TimeMs);
    }

    [Fact]
    public void Duration_of_i_without_leads_is_hold_plus_stem_plus_gap_plus_dot_plus_hold()
    {
        Glyph i = Shipped.Glyph("i");
        AnimationScript script = Build("i", new AnimationOptions(IncludeLeads: false));

        double stemMs = i.Strokes[0].Path.Length / Settings.SpeedXhPerSec * 1000.0;
        double expected = Settings.StartHoldMs + stemMs + Settings.DeferredGapMs + Settings.DotMs + Settings.EndHoldMs;

        Assert.Equal(expected, script.DurationMs, 6);
        Assert.Equal(Settings.StartHoldMs, script.Strokes[0].StartMs, 9);
        Assert.Equal(Settings.StartHoldMs + stemMs, script.Strokes[0].EndMs, 6);
        Assert.Equal(Settings.StartHoldMs + stemMs + Settings.DeferredGapMs, script.Strokes[1].StartMs, 6);
        Assert.Equal(Settings.DotMs, script.Strokes[1].EndMs - script.Strokes[1].StartMs, 9);
    }

    [Fact]
    public void Leads_are_drawn_slower_and_a_sharp_turn_costs_a_cusp_pause()
    {
        Glyph i = Shipped.Glyph("i");
        AnimationScript withLeads = Build("i");
        AnimationScript withoutLeads = Build("i", new AnimationOptions(IncludeLeads: false));

        double leadLength = i.LeadIn!.Length + i.LeadOut!.Length;
        double extraMs = (leadLength / (Settings.SpeedXhPerSec * Settings.JoinSlowdown) * 1000.0) + Settings.CuspPauseMs;

        Assert.Equal(withoutLeads.DurationMs + extraMs, withLeads.DurationMs, 6);
        Assert.Equal(i.LeadIn.Length + i.Strokes[0].Path.Length + i.LeadOut.Length, withLeads.Strokes[0].Path.Length, 6);
        Assert.Equal(i.LeadIn.Length, withLeads.Strokes[0].BodyStart, 9);
    }

    [Fact]
    public void The_cusp_pause_holds_the_pen_still_between_lead_in_and_body()
    {
        AnimationScript script = Build("i");
        AnimatedStroke stroke = script.Strokes[0];

        TimeKnot[] flat = [.. stroke.Knots.Zip(stroke.Knots.Skip(1), (a, b) => (a, b)).Where(p => p.a.Distance == p.b.Distance).Select(p => p.b)];

        Assert.Single(flat);
        Assert.Equal(stroke.BodyStart, flat[0].Distance, 9);
        double pauseStart = stroke.StartMs + flat[0].TimeMs - Settings.CuspPauseMs;
        Vec2 before = script.StateAt(pauseStart + 1).PenPosition;
        Vec2 during = script.StateAt(pauseStart + (Settings.CuspPauseMs / 2.0)).PenPosition;
        Vec2 after = script.StateAt(pauseStart + Settings.CuspPauseMs).PenPosition;
        Assert.Equal(before, during);
        Assert.Equal(before, after);
    }

    [Theory]
    [MemberData(nameof(Symbols))]
    public void Progress_markers_and_the_pen_never_go_backwards_or_jump(string symbol)
    {
        AnimationScript script = Build(symbol);
        AnimationState previous = script.StateAt(0);
        const double Step = 2.0;

        for (double t = Step; t <= script.DurationMs + Step; t += Step)
        {
            AnimationState state = script.StateAt(t);
            for (int k = 0; k < state.StrokeProgress.Length; k++)
            {
                Assert.True(state.StrokeProgress[k] >= previous.StrokeProgress[k], $"{symbol}: stroke {k} progress went back at t={t}");
            }

            Assert.True(previous.VisibleNumbers.All(state.VisibleNumbers.Contains), $"{symbol}: a number disappeared at t={t}");
            Assert.True(previous.VisibleArrows.All(state.VisibleArrows.Contains), $"{symbol}: an arrow disappeared at t={t}");
            Assert.True(previous.VisibleStartDots.All(state.VisibleStartDots.Contains), $"{symbol}: a start dot disappeared at t={t}");
            Assert.True(state.TimeMs >= previous.TimeMs);

            // Перо движется непрерывно: не быстрее самой быстрой фазы (перелёт в воздухе) с запасом.
            Assert.True(Vec2.Distance(state.PenPosition, previous.PenPosition) < 0.05, $"{symbol}: the pen jumped at t={t}");
            previous = state;
        }
    }

    [Theory]
    [MemberData(nameof(Symbols))]
    public void Strokes_are_written_in_order_and_deferred_strokes_come_last(string symbol)
    {
        Glyph glyph = Shipped.Glyph(symbol);
        AnimationScript script = Build(symbol);

        Assert.Equal(glyph.PenStrokes.Select(s => s.Id), script.Strokes.Select(s => s.StrokeId));
        for (int k = 1; k < script.Strokes.Length; k++)
        {
            Assert.True(script.Strokes[k].StartMs >= script.Strokes[k - 1].EndMs, $"{symbol}: stroke {k} starts before stroke {k - 1} ends");
            Assert.Equal(k + 1, script.Strokes[k].Number);
        }

        int mainCount = glyph.Strokes.Length;
        Assert.All(script.Strokes.Take(mainCount), s => Assert.False(s.IsDeferred));
        Assert.All(script.Strokes.Skip(mainCount), s => Assert.True(s.IsDeferred));
        double lastMainEnd = script.Strokes[mainCount - 1].EndMs;
        Assert.All(script.Strokes.Skip(mainCount), s => Assert.True(s.StartMs >= lastMainEnd + Settings.DeferredGapMs - 1e-9));

        // Пока рисуется основной штрих, отложенные ещё не начаты; когда начат отложенный, основные закончены.
        foreach (double t in Enumerable.Range(0, 400).Select(n => n * script.DurationMs / 400.0))
        {
            AnimationState state = script.StateAt(t);
            bool anyDeferredStarted = Enumerable.Range(mainCount, script.Strokes.Length - mainCount).Any(k => state.StrokeProgress[k] > 0.0);
            if (anyDeferredStarted)
            {
                Assert.All(state.StrokeProgress.Take(mainCount), p => Assert.Equal(1.0, p));
            }

            for (int k = 1; k < state.StrokeProgress.Length; k++)
            {
                if (state.StrokeProgress[k] > 0.0)
                {
                    Assert.Equal(1.0, state.StrokeProgress[k - 1]);
                }
            }
        }
    }

    [Fact]
    public void The_active_stroke_and_pen_state_follow_the_timeline()
    {
        AnimationScript script = Build("t");
        AnimatedStroke s1 = script.Strokes[0];
        AnimatedStroke bar = script.Strokes[1];

        AnimationState drawing = script.StateAt((s1.StartMs + s1.EndMs) / 2);
        AnimationState gap = script.StateAt((s1.EndMs + bar.StartMs) / 2);
        AnimationState barDrawing = script.StateAt((bar.StartMs + bar.EndMs) / 2);

        Assert.Equal(0, drawing.ActiveStroke);
        Assert.True(drawing.PenDown);
        Assert.InRange(drawing.StrokeProgress[0], 0.01, 0.99);
        Assert.Equal(0.0, drawing.StrokeProgress[1]);

        Assert.Equal(-1, gap.ActiveStroke);
        Assert.False(gap.PenDown);
        Assert.Equal(1.0, gap.StrokeProgress[0]);
        Vec2 expectedMidAir = Vec2.Lerp(s1.Path.End, bar.Path.Start, 0.5);
        Assert.Equal(expectedMidAir.X, gap.PenPosition.X, 9);
        Assert.Equal(expectedMidAir.Y, gap.PenPosition.Y, 9);

        Assert.Equal(1, barDrawing.ActiveStroke);
        Assert.True(barDrawing.PenDown);
        Assert.True(barDrawing.PenPosition.X > bar.Path.Start.X && barDrawing.PenPosition.X < bar.Path.End.X, "the bar is written left to right");
    }

    [Fact]
    public void Arrows_appear_when_the_pen_passes_them_and_numbers_when_the_previous_stroke_ends()
    {
        AnimationScript script = Build("t");

        foreach (ScriptArrow arrow in script.Arrows)
        {
            AnimatedStroke stroke = script.Strokes[arrow.StrokeIndex];
            double at = arrow.AppearMs;
            double distanceAtAppear = stroke.DistanceAt(at);
            double distanceBefore = stroke.DistanceAt(at - 5);

            Assert.InRange(at, stroke.StartMs, stroke.EndMs);
            Assert.Equal(1.0, arrow.Direction.Length, 9);
            if (at > stroke.StartMs + 5)
            {
                Assert.True(distanceBefore < distanceAtAppear + 1e-9);
            }

            Assert.DoesNotContain(script.Arrows.ToList().IndexOf(arrow), script.StateAt(at - 1e-3).VisibleArrows);
            Assert.Contains(script.Arrows.ToList().IndexOf(arrow), script.StateAt(at).VisibleArrows);
        }

        ScriptNumber first = script.Numbers[0];
        ScriptNumber second = script.Numbers[1];
        Assert.Equal(0.0, first.AppearMs);
        Assert.Equal(script.Strokes[0].EndMs, second.AppearMs);
        Assert.Equal(1, first.Number);
        Assert.Equal(2, second.Number);
    }

    [Fact]
    public void Script_is_deterministic()
    {
        AnimationScript one = Build("a");
        AnimationScript two = Build("a");

        Assert.Equal(one.DurationMs, two.DurationMs);
        Assert.Equal(one.Strokes[0].Knots.ToArray(), two.Strokes[0].Knots.ToArray());
        foreach (double t in new[] { 0.0, 333.3, 1500.0, 2999.9, one.DurationMs })
        {
            AnimationState a = one.StateAt(t);
            AnimationState b = two.StateAt(t);
            Assert.Equal(a.PenPosition, b.PenPosition);
            Assert.Equal(a.StrokeProgress.ToArray(), b.StrokeProgress.ToArray());
            Assert.Equal(a.VisibleArrows.ToArray(), b.VisibleArrows.ToArray());
        }
    }

    [Fact]
    public void Explicit_pause_and_tempo_of_segments_change_the_timeline()
    {
        Glyph i = Shipped.Glyph("i");
        GlyphPath body = new(
        [
            GlyphSegment.Line(new Vec2(0, 1), new Vec2(0, 0), tempo: 2.0, pauseBeforeMs: 0),
            GlyphSegment.Line(new Vec2(0, 0), new Vec2(1, 0), tempo: 0.5, pauseBeforeMs: 250),
        ]);
        Glyph glyph = i with
        {
            Strokes = [new GlyphStroke("s1", 1, body)],
            Deferred = [],
            LeadIn = null,
            LeadOut = null,
            KeyPoints = [],
            Animation = new GlyphAnimationMeta(StartDotMode.Body, [], []),
        };

        AnimationScript script = AnimationScriptBuilder.Build(glyph, Settings);

        double expectedMs = (1.0 / (Settings.SpeedXhPerSec * 2.0) * 1000.0) + 250.0 + (1.0 / (Settings.SpeedXhPerSec * 0.5) * 1000.0);
        Assert.Equal(expectedMs, script.Strokes[0].EndMs - script.Strokes[0].StartMs, 6);
        Assert.Equal(Settings.StartHoldMs + expectedMs + Settings.EndHoldMs, script.DurationMs, 6);
    }

    [Fact]
    public void Start_dot_follows_the_glyph_setting()
    {
        Glyph i = Shipped.Glyph("i");

        AnimationScript lead = AnimationScriptBuilder.Build(i, Settings);
        AnimationScript body = AnimationScriptBuilder.Build(
            i with { Animation = i.Animation with { StartDot = StartDotMode.Body } }, Settings);
        AnimationScript none = AnimationScriptBuilder.Build(
            i with { Animation = i.Animation with { StartDot = StartDotMode.None } }, Settings);

        Assert.Equal(i.LeadIn!.Start, lead.StartDots[0].Position);
        Assert.Equal(i.Strokes[0].Path.Start.X, body.StartDots[0].Position.X, 9);
        Assert.Empty(none.StartDots);
        Assert.Single(lead.StartDots); // у точки (отложенный штрих i) своей точки старта нет: она сама метка
        Assert.Equal(2, AnimationScriptBuilder.Build(Shipped.Glyph("t"), Settings).StartDots.Length);
    }
}
