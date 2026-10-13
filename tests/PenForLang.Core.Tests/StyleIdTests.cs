namespace PenForLang.Core.Tests;

public class StyleIdTests
{
    [Theory]
    [InlineData("zaner-bloser")]
    [InlineData("ru-propis")]
    [InlineData("zaner-bloser-print")]
    [InlineData("a")]
    [InlineData("0style_1")]
    public void Valid_ids_are_accepted_and_keep_their_text(string text)
    {
        var id = new StyleId(text);

        Assert.Equal(text, id.Value);
        Assert.Equal(text, id.ToString());
        Assert.False(id.IsDefault);
        Assert.True(StyleId.TryParse(text, out StyleId parsed));
        Assert.Equal(id, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Zaner")]
    [InlineData("-lead")]
    [InlineData("_lead")]
    [InlineData("has space")]
    [InlineData("slash/id")]
    [InlineData("dot.id")]
    [InlineData("кириллица")]
    public void Invalid_ids_are_rejected(string text)
    {
        Assert.Throws<ArgumentException>(() => new StyleId(text));
        Assert.False(StyleId.TryParse(text, out StyleId id));
        Assert.True(id.IsDefault);
    }

    [Fact]
    public void Length_limit_and_null_are_enforced()
    {
        Assert.Equal(64, new StyleId(new string('a', 64)).Value.Length);
        Assert.Throws<ArgumentException>(() => new StyleId(new string('a', 65)));
        Assert.Throws<ArgumentNullException>(() => new StyleId(null!));
        Assert.False(StyleId.TryParse(null, out _));
    }

    [Fact]
    public void Default_value_is_empty_and_distinct_from_any_valid_id()
    {
        StyleId none = default;

        Assert.True(none.IsDefault);
        Assert.Equal(string.Empty, none.Value);
        Assert.NotEqual(new StyleId("a"), none);
        Assert.Equal(new StyleId("a"), new StyleId("a"));
    }
}
