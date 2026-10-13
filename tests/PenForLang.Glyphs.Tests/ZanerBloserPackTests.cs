using PenForLang.Core.Geometry;
using PenForLang.Glyphs.Tests.Support;

namespace PenForLang.Glyphs.Tests;

/// <summary>Стиль-пакет <c>content/styles/zaner-bloser</c>: загружается, формы правдоподобны, метаданные честные.</summary>
public class ZanerBloserPackTests
{
    [Fact]
    public void Pack_loads_without_errors_or_warnings()
    {
        Assert.True(Shipped.LoadResult.Succeeded, string.Join("\n", Shipped.LoadResult.Issues));
        Assert.Empty(Shipped.LoadResult.Issues);
    }

    [Fact]
    public void Style_has_the_internal_id_and_a_ui_name_without_the_trademark()
    {
        StyleManifest style = Shipped.Style;

        Assert.Equal("zaner-bloser", style.Id.Value);
        Assert.Equal(StyleRole.Teach, style.Role);
        Assert.Equal("Американская пропись", style.DisplayName["ru"]);
        Assert.Equal("American cursive", style.DisplayName["en"]);
        Assert.All(style.DisplayName.Values, name =>
        {
            Assert.DoesNotContain("zaner", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("bloser", name, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal(DeferredTeachTiming.AfterWord, style.DeferredTeachTiming);
        Assert.Equal(1.0, style.Lines.XHeight);
        Assert.Equal(0.0, style.Lines.Baseline);
    }

    [Fact]
    public void Pack_has_exactly_the_default_glyphs_a_i_t_marked_illustrative_and_unreviewed()
    {
        Assert.Equal(
            ["zaner-bloser/a.default", "zaner-bloser/i.default", "zaner-bloser/t.default"],
            Shipped.Pack.Glyphs.Select(g => g.Id.ToString()).ToArray());
        Assert.All(Shipped.Pack.Glyphs, glyph =>
        {
            Assert.Equal("illustrative", glyph.Meta.Source);
            Assert.False(glyph.Meta.Reviewed);
        });
    }

    [Fact]
    public void A_is_a_downcurve_letter_with_a_closed_oval_and_a_retraced_stem()
    {
        Glyph a = Shipped.Glyph("a");

        Assert.Equal(EntryClass.C, a.Entry.Class);
        Assert.Equal(ExitClass.Base, a.Exit.Class);
        Assert.Single(a.Strokes);
        Assert.Empty(a.Deferred);
        Assert.Contains("closedOval", a.Features);
        Assert.Contains("retrace", a.Features);
        Assert.Single(a.Checks.Retrace);
        Assert.True(a.Strokes[0].Path.Segments[a.Checks.Retrace[0].Segment].Retrace);
        Assert.NotNull(a.LeadIn);
        Assert.NotNull(a.LeadOut);

        // Овал против часовой стрелки: знаковая площадь замкнутого участка (формула шнуровки) положительна при оси v вверх.
        StrokeRange closed = Assert.Single(a.Checks.Closed);
        Vec2[] ring = a.Strokes[0].Path.Flatten(0.01);
        int end = (int)(closed.To * (ring.Length - 1));
        double doubledArea = 0;
        for (int k = 0; k <= end; k++)
        {
            doubledArea += Vec2.Cross(ring[k], ring[(k + 1) % (end + 1)]);
        }

        Assert.True(doubledArea > 0, "the oval must be written counter-clockwise");
    }

    [Fact]
    public void I_has_a_deferred_dot_anchored_to_the_stem_above_the_x_height()
    {
        Glyph i = Shipped.Glyph("i");

        Assert.Equal(EntryClass.Up, i.Entry.Class);
        DeferredStroke dot = Assert.Single(i.Deferred);
        Assert.Equal(DeferredKind.Dot, dot.Kind);
        Assert.Equal("s1", dot.Anchor.StrokeId);
        Assert.True(dot.Path.Start.Y > 1.2 && dot.Path.Start.Y < 2.0, "the dot floats between the x-height and the ascender line");
        Assert.Contains("deferred", i.Features);
        Assert.DoesNotContain("retrace", i.Features);
    }

    [Fact]
    public void T_is_taller_than_the_x_height_but_below_the_ascender_with_a_left_to_right_bar()
    {
        Glyph t = Shipped.Glyph("t");

        Assert.Equal(1.5, t.Entry.Height);
        (_, Vec2 max) = t.Strokes[0].Path.GetBounds();
        Assert.InRange(max.Y, 1.45, 1.55);
        Assert.True(max.Y < Shipped.Style.Lines.Ascender);
        DeferredStroke bar = Assert.Single(t.Deferred);
        Assert.Equal(DeferredKind.Bar, bar.Kind);
        Assert.Equal(DeferredDirection.LeftToRight, bar.Direction);
        Assert.True(bar.Path.End.X > bar.Path.Start.X);
        Assert.Equal(1.0, bar.Path.Start.Y, 3);
        Assert.Equal(1.0, bar.Path.End.Y, 3);
        Assert.True(t.Strokes[0].Path.Segments[0].Retrace);
    }

    [Theory]
    [InlineData("i", 0)]
    [InlineData("t", 0)]
    [InlineData("a", 4)]
    public void Straight_stems_lean_forward_by_the_style_slant(string symbol, int segment)
    {
        Glyph glyph = Shipped.Glyph(symbol);
        GlyphSegment stem = glyph.Strokes[0].Path.Segments[segment];
        Vec2 direction = stem.P3 - stem.P0;
        double slant = Math.Atan2(direction.X, direction.Y) * 180.0 / Math.PI;

        // Ствол идёт вниз (v убывает), поэтому отклонение от вертикали берём по модулю.
        double fromVertical = Math.Abs(Math.Abs(slant) - 180.0);
        Assert.Equal(Shipped.Style.SlantDeg!.Value, fromVertical, 1.5);
        Assert.True(direction.X < 0 && direction.Y < 0, "the stem runs down and to the left, so the letter leans to the right");
    }

    [Fact]
    public void Advance_matches_the_exit_point_and_exits_head_up_and_to_the_right()
    {
        foreach (Glyph glyph in Shipped.Pack.Glyphs)
        {
            Assert.Equal(glyph.Exit.Point.X, glyph.Advance, 0.05);
            Assert.True(glyph.Exit.Tangent.X > 0 && glyph.Exit.Tangent.Y > 0, $"{glyph.Id}: undercurve exit rises to the right");
            Assert.Equal(StartDotMode.LeadIn, glyph.Animation.StartDot);
        }
    }

    [Fact]
    public void Every_stroke_has_a_number_and_every_label_key_has_ru_and_en_text()
    {
        foreach (Glyph glyph in Shipped.Pack.Glyphs)
        {
            Assert.Equal(glyph.PenStrokes.Length, glyph.Animation.Numbers.Length);
            Assert.NotEmpty(glyph.Animation.Arrows);
        }

        Assert.Equal(
            Shipped.Pack.Labels["ru"].Keys.Order(StringComparer.Ordinal),
            Shipped.Pack.Labels["en"].Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Verbal_phrase_of_a_matches_the_methodology_wording()
    {
        Assert.Equal(
            "перекат наверх — назад по кругу — вверх — вниз по той же линии — хвостик",
            Shipped.Pack.BuildPhrase(Shipped.Glyph("a"), "ru"));
        Assert.Equal("roll up — back around — up — down the same line — tail", Shipped.Pack.BuildPhrase(Shipped.Glyph("a"), "en"));
        Assert.Equal("вверх — вниз по наклонной — хвостик — точка сверху", Shipped.Pack.BuildPhrase(Shipped.Glyph("i"), "ru"));
        Assert.Equal("up, above the midline — down the same line — tail — bar, left to right", Shipped.Pack.BuildPhrase(Shipped.Glyph("t"), "en"));
    }

    [Fact]
    public void Glyph_lookup_supports_ids_and_variants()
    {
        StylePack pack = Shipped.Pack;

        Assert.True(pack.TryGetGlyph("a", "default", out Glyph? a));
        Assert.True(pack.TryGetGlyph(GlyphId.Parse("zaner-bloser/i.default"), out Glyph? i));
        Assert.Equal("a", a.Symbol);
        Assert.Equal("i", i.Symbol);
        Assert.False(pack.TryGetGlyph("a", "after-high", out _));
        Assert.Throws<KeyNotFoundException>(() => pack.GetGlyph("z"));
    }
}
