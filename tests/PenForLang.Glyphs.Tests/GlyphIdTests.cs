namespace PenForLang.Glyphs.Tests;

public class GlyphIdTests
{
    [Fact]
    public void Parse_round_trips_canonical_id()
    {
        GlyphId id = GlyphId.Parse("zaner-bloser/a.default");

        Assert.Equal(new GlyphId("zaner-bloser", "a", "default"), id);
        Assert.Equal("zaner-bloser/a.default", id.ToString());
    }

    [Fact]
    public void Parse_supports_variants_with_hyphen()
    {
        Assert.Equal("after-high", GlyphId.Parse("zaner-bloser/e.after-high").Variant);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a.default")]
    [InlineData("zaner-bloser/a")]
    [InlineData("zaner-bloser/.default")]
    [InlineData("zaner-bloser/a.")]
    [InlineData("/a.default")]
    public void TryParse_rejects_malformed_ids(string? text)
    {
        Assert.False(GlyphId.TryParse(text, out _));
    }

    [Fact]
    public void Parse_throws_FormatException_for_malformed_id()
    {
        Assert.Throws<FormatException>(() => GlyphId.Parse("nonsense"));
    }
}
