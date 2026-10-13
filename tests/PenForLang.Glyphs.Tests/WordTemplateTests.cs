using System.Collections.Immutable;
using PenForLang.Core.Geometry;
using PenForLang.Glyphs.Animation;
using PenForLang.Glyphs.Tests.Support;

namespace PenForLang.Glyphs.Tests;

/// <summary>
/// Эталон слова (V0.10): расстановка по <c>advance + letterSpacing</c>, соединение JF1 с G1-стыками, разметка участков,
/// отложенные штрихи в конце, разрыв как ожидаемый отрыв, варианты глифов.
/// </summary>
public class WordTemplateTests
{
    private const double Epsilon = 1e-9;

    private static WordTemplate Word(string text) => WordTemplateBuilder.Build(Shipped.Pack, text);

    private static double AngleDeg(Vec2 a, Vec2 b) =>
        Math.Acos(Math.Clamp(Vec2.Dot(a.Normalized(), b.Normalized()), -1.0, 1.0)) * 180.0 / Math.PI;

    private static StyleManifest WithJoinRules(params JoinRule[] rules) =>
        Shipped.Style with { Joins = new JoinPolicy([.. rules], Shipped.Style.Joins.TallEntryClass) };

    [Fact]
    public void Letters_stand_at_advance_plus_letter_spacing()
    {
        WordTemplate word = Word("at");

        Assert.Equal(0.0, word.Letters[0].OffsetU);
        Assert.Equal(Shipped.Glyph("a").Advance + Shipped.Style.LetterSpacing, word.Letters[1].OffsetU, Epsilon);
        Assert.Equal(word.Letters[1].OffsetU + Shipped.Glyph("t").Advance, word.Advance, Epsilon);
        Assert.Equal(Shipped.Glyph("t").Entry.Point.X + word.Letters[1].OffsetU, word.Letters[1].EntryPoint.X, Epsilon);
        Assert.Equal(Shipped.Glyph("a").Exit.Point, word.Letters[0].ExitPoint);
    }

    [Theory]
    [InlineData("at")]
    [InlineData("it")]
    public void Join_from_base_exit_to_up_entry_is_jf1_with_g1_ends(string text)
    {
        WordTemplate word = Word(text);

        WordJoin join = Assert.Single(word.Joins);

        Assert.Equal("JF1", join.Family);
        Assert.Equal(JoinShape.Hermite, join.Shape);
        Assert.False(join.High);
        Assert.True(join.Tall); // у t вход на высоте 1,5
        Assert.False(join.ExpectedLift);
        Assert.NotNull(join.Path);
        Assert.Equal(0, join.Left);
        Assert.Equal(1, join.Right);
        Assert.True(Vec2.Distance(join.Path.Start, word.Letters[0].ExitPoint) < Epsilon);
        Assert.True(Vec2.Distance(join.Path.End, word.Letters[1].EntryPoint) < Epsilon);
        Assert.True(AngleDeg(join.Path.StartTangent, word.Letters[0].ExitTangent) < 0.5, "the join leaves along the exit tangent");
        Assert.True(AngleDeg(join.Path.EndTangent, word.Letters[1].EntryTangent) < 0.5, "the join arrives along the entry tangent");
    }

    [Fact]
    public void Join_rises_to_the_top_of_t_without_dips_or_loops()
    {
        WordTemplate word = Word("at");
        Vec2[] points = word.Joins[0].Path!.Flatten(0.005);

        Assert.True(points[^1].Y > 1.4, "the join climbs to the top of t (v = 1.5)");
        for (int i = 1; i < points.Length; i++)
        {
            Assert.True(points[i].Y >= points[i - 1].Y - 1e-9, "no dips along the join");
            Assert.True(points[i].X >= points[i - 1].X - 1e-9, "no backward steps along the join");
        }
    }

    [Fact]
    public void The_word_is_one_pen_stroke_with_marked_parts()
    {
        WordTemplate word = Word("at");

        WordPenStroke stroke = Assert.Single(word.PenStrokes);

        Assert.Equal(
            [WordPartKind.LeadIn, WordPartKind.Body, WordPartKind.Join, WordPartKind.Body, WordPartKind.LeadOut],
            stroke.Parts.Select(p => p.Kind).ToArray());
        Assert.Equal([0, 0, 0, 1, 1], stroke.Parts.Select(p => p.Letter).ToArray());
        Assert.Equal(0.0, stroke.Parts[0].FromDistance);
        for (int i = 1; i < stroke.Parts.Length; i++)
        {
            Assert.Equal(stroke.Parts[i - 1].ToDistance, stroke.Parts[i].FromDistance, Epsilon);
        }

        Assert.Equal(stroke.Path.Length, stroke.Parts[^1].ToDistance, 1e-6);
        foreach (WordPart part in stroke.Parts)
        {
            Assert.True(Vec2.Distance(stroke.Path.PointAtDistance(part.FromDistance), part.Path.Start) < 1e-6);
            Assert.True(Vec2.Distance(stroke.Path.PointAtDistance(part.ToDistance), part.Path.End) < 1e-6);
        }

        Assert.NotNull(stroke.FindBody(1, "s1"));
        Assert.Null(stroke.FindBody(1, "s9"));
    }

    [Fact]
    public void Inner_letters_have_no_lead_in_and_the_join_replaces_it()
    {
        WordTemplate word = Word("at");

        Assert.Equal(1, word.PenStrokes[0].Parts.Count(p => p.Kind == WordPartKind.LeadIn));
        Assert.Equal(1, word.PenStrokes[0].Parts.Count(p => p.Kind == WordPartKind.LeadOut));
        Assert.True(Vec2.Distance(word.PenStrokes[0].Parts[0].Path.Start, Shipped.Glyph("a").LeadIn!.Start) < Epsilon);
        WordPart tail = word.PenStrokes[0].Parts[^1];
        Assert.True(Vec2.Distance(tail.Path.Start, word.Letters[1].ExitPoint) < Epsilon);
    }

    [Fact]
    public void Deferred_strokes_of_the_word_follow_in_letter_order_in_word_coordinates()
    {
        WordTemplate it = Word("it");
        WordTemplate at = Word("at");

        Assert.Equal(["dot", "bar"], it.Deferred.Select(d => d.SlotId).ToArray());
        Assert.Equal([0, 1], it.Deferred.Select(d => d.Letter).ToArray());
        WordDeferred bar = Assert.Single(at.Deferred);
        Assert.Equal(1, bar.Letter);
        double shift = at.Letters[1].OffsetU;
        Assert.Equal(Shipped.Glyph("t").Deferred[0].Path.Start.X + shift, bar.Path.Start.X, Epsilon);
        Assert.Equal("s1", bar.AnchorStrokeId);
        Assert.Equal(Shipped.Glyph("t").Strokes[0].Path.PointAtFraction(bar.AnchorT).X + shift, bar.AnchorPoint.X, Epsilon);
        Assert.All(it.PenStrokes, s => Assert.DoesNotContain(s.Parts, p => p.StrokeId is "dot" or "bar"));
    }

    [Fact]
    public void A_break_rule_gives_an_expected_lift_and_two_pen_strokes()
    {
        StyleManifest style = WithJoinRules(new JoinRule(null, ExitClass.Base, [], true, JoinShape.Break, false, new Dictionary<string, double>()));
        Glyph a = Shipped.Glyph("a");
        Glyph t = Shipped.Glyph("t");

        WordTemplate word = WordTemplateBuilder.Build(style, [a, t]);

        WordJoin join = Assert.Single(word.Joins);
        Assert.True(join.ExpectedLift);
        Assert.Null(join.Path);
        Assert.Equal(JoinShape.Break, join.Shape);
        Assert.Equal(2, word.PenStrokes.Length);
        Assert.Equal([WordPartKind.LeadIn, WordPartKind.Body], word.PenStrokes[0].Parts.Select(p => p.Kind).ToArray());
        Assert.Equal([WordPartKind.Body, WordPartKind.LeadOut], word.PenStrokes[1].Parts.Select(p => p.Kind).ToArray());
        Assert.Equal("at", word.Text);
    }

    [Fact]
    public void No_matching_rule_means_a_break_too()
    {
        StyleManifest style = WithJoinRules();

        WordTemplate word = WordTemplateBuilder.Build(style, [Shipped.Glyph("i"), Shipped.Glyph("t")]);

        Assert.True(word.Joins[0].ExpectedLift);
        Assert.Null(word.Joins[0].Rule);
        Assert.Null(word.Joins[0].Family);
    }

    [Fact]
    public void A_single_letter_word_has_no_joins_and_keeps_both_leads()
    {
        WordTemplate word = Word("a");

        Assert.Empty(word.Joins);
        Assert.Equal([WordPartKind.LeadIn, WordPartKind.Body, WordPartKind.LeadOut], word.PenStrokes[0].Parts.Select(p => p.Kind).ToArray());
    }

    [Fact]
    public void Rejects_empty_words_and_unknown_letters()
    {
        Assert.Throws<ArgumentException>(() => WordTemplateBuilder.Build(Shipped.Pack, string.Empty));
        Assert.Throws<KeyNotFoundException>(() => WordTemplateBuilder.Build(Shipped.Pack, "az"));
        Assert.Throws<ArgumentException>(() => WordTemplateBuilder.Build(Shipped.Style, []));
    }

    [Fact]
    public void Variant_rules_pick_the_variant_after_the_matching_exit()
    {
        Glyph t = Shipped.Glyph("t");
        Glyph variant = t with { Id = new GlyphId("zaner-bloser", "t", "after-base") };
        StyleManifest manifest = Shipped.Style with { VariantRules = [new VariantRule("t", ExitClass.Base, "after-base")] };
        var pack = new StylePack(manifest, [.. Shipped.Pack.Glyphs, variant], Shipped.Pack.Labels);

        ImmutableArray<Glyph> glyphs = WordTemplateBuilder.ResolveGlyphs(pack, "tat");

        Assert.Equal("default", glyphs[0].Variant); // перед первой буквой выхода нет
        Assert.Equal("default", glyphs[1].Variant); // a: правила для неё нет
        Assert.Equal("after-base", glyphs[2].Variant); // t после a (выход X-BASE)
    }

    [Fact]
    public void A_variant_rule_without_such_glyph_falls_back_to_the_default()
    {
        StyleManifest manifest = Shipped.Style with { VariantRules = [new VariantRule("t", ExitClass.Base, "no-such-variant")] };
        var pack = new StylePack(manifest, Shipped.Pack.Glyphs, Shipped.Pack.Labels);

        ImmutableArray<Glyph> glyphs = WordTemplateBuilder.ResolveGlyphs(pack, "at");

        Assert.Equal("default", glyphs[1].Variant);
    }

    [Theory]
    [InlineData("at")]
    [InlineData("it")]
    public void The_word_animation_draws_main_strokes_then_deferred_after_the_word(string text)
    {
        WordTemplate word = Word(text);
        AnimationScript script = AnimationScriptBuilder.Build(word, Shipped.Style.Animation);

        Assert.Null(script.Glyph);
        Assert.Same(word, script.Word);
        Assert.Equal(word.PenStrokes.Length + word.Deferred.Length, script.Strokes.Length);
        Assert.All(script.Strokes.Take(word.PenStrokes.Length), s => Assert.False(s.IsDeferred));
        Assert.All(script.Strokes.Skip(word.PenStrokes.Length), s => Assert.True(s.IsDeferred));
        double mainEnd = script.Strokes.Where(s => !s.IsDeferred).Max(s => s.EndMs);
        Assert.All(script.Strokes.Where(s => s.IsDeferred), s => Assert.True(s.StartMs >= mainEnd + Shipped.Style.Animation.DeferredGapMs - 1e-6));
        Assert.Equal(Enumerable.Range(1, script.Strokes.Length), script.Strokes.Select(s => s.Number));
        Assert.Equal(script.Strokes.Length, script.Numbers.Length);
    }

    [Fact]
    public void The_word_animation_state_is_monotone_and_ends_with_everything_drawn()
    {
        AnimationScript script = AnimationScriptBuilder.Build(Word("it"), Shipped.Style.Animation);
        double[] previous = new double[script.Strokes.Length];
        int shownNumbers = 0;
        for (double t = 0; t <= script.DurationMs; t += 25)
        {
            AnimationState state = script.StateAt(t);
            for (int i = 0; i < previous.Length; i++)
            {
                Assert.True(state.StrokeProgress[i] >= previous[i] - 1e-12, $"stroke {i} regressed at {t} ms");
                previous[i] = state.StrokeProgress[i];
            }

            Assert.True(state.VisibleNumbers.Length >= shownNumbers);
            shownNumbers = state.VisibleNumbers.Length;
        }

        AnimationState end = script.StateAt(script.DurationMs);
        Assert.All(end.StrokeProgress, p => Assert.Equal(1.0, p));
        Assert.True(end.IsFinished);
    }

    [Fact]
    public void A_dot_gets_no_start_dot_and_joins_are_slower_than_bodies()
    {
        AnimationScript it = AnimationScriptBuilder.Build(Word("it"), Shipped.Style.Animation);
        int dotIndex = Array.FindIndex([.. it.Strokes], s => s.StrokeId.StartsWith("dot", StringComparison.Ordinal));
        Assert.True(dotIndex >= 0);
        Assert.DoesNotContain(it.StartDots, d => d.StrokeIndex == dotIndex);

        WordTemplate word = Word("at");
        AnimationScript script = AnimationScriptBuilder.Build(word, Shipped.Style.Animation);
        AnimatedStroke main = script.Strokes[0];
        WordPart join = word.PenStrokes[0].Parts.First(p => p.Kind == WordPartKind.Join);
        WordPart body = word.PenStrokes[0].Parts.First(p => p.Kind == WordPartKind.Body && p.Letter == 1);
        double JoinSpeed(WordPart p) => p.Path.Length / (main.TimeAtDistance(p.ToDistance) - main.TimeAtDistance(p.FromDistance));
        Assert.True(JoinSpeed(join) < JoinSpeed(body), "the join is drawn slower (joinSlowdown)");
    }

    [Fact]
    public void A_pause_follows_the_cusp_at_the_top_of_t()
    {
        WordTemplate word = Word("at");
        AnimationScript script = AnimationScriptBuilder.Build(word, Shipped.Style.Animation);
        AnimatedStroke main = script.Strokes[0];
        WordPart join = word.PenStrokes[0].Parts.First(p => p.Kind == WordPartKind.Join);

        double arrive = main.TimeAtDistance(join.ToDistance - 1e-9);
        double leave = main.TimeAtDistance(join.ToDistance + 0.05);

        Assert.True(leave - arrive >= Shipped.Style.Animation.CuspPauseMs, "the pen pauses where the stem turns back down over the join");
    }
}
