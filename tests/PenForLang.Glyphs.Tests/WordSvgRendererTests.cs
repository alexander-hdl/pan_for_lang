using System.Text.RegularExpressions;
using PenForLang.Glyphs.Rendering;
using PenForLang.Glyphs.Tests.Support;

namespace PenForLang.Glyphs.Tests;

/// <summary>
/// SVG-рендер слов: детерминизм, структура, golden-снимки <c>golden/{at,it}.word.svg</c> и копии для ревью
/// <c>docs/spikes/glyphs-v0/{at,it}.svg</c>. Обновление — как у глифов: <c>PFL_UPDATE_GOLDEN=1</c>, затем <c>git diff</c> и коммит.
/// </summary>
public partial class WordSvgRendererTests
{
    private const string UpdateVariable = "PFL_UPDATE_GOLDEN";

    public static TheoryData<string> Words => ["at", "it"];

    [GeneratedRegex("NaN|Infinity|∞", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BadNumber();

    private static string Render(string text, SvgRenderOptions? options = null) =>
        WordSvgRenderer.Render(WordTemplateBuilder.Build(Shipped.Pack, text), options);

    private static bool UpdateRequested
    {
        get
        {
            if (Environment.GetEnvironmentVariable(UpdateVariable) != "1")
            {
                return false;
            }

            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"))
                || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS")))
            {
                throw new InvalidOperationException($"{UpdateVariable}=1 is not allowed in CI: golden snapshots are accepted by a human.");
            }

            return true;
        }
    }

    private static void AssertFile(string path, string actual, string what)
    {
        if (UpdateRequested)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
        }

        Assert.True(File.Exists(path), $"{what} {path} is missing. Create it by running the tests with {UpdateVariable}=1.");
        Assert.True(
            string.Equals(File.ReadAllText(path), actual, StringComparison.Ordinal),
            $"{what} {path} differs from the render. If the change is intended, run the tests with {UpdateVariable}=1, review the diff and commit.");
    }

    [Theory]
    [MemberData(nameof(Words))]
    public void Rendering_twice_gives_the_same_string(string text)
    {
        Assert.Equal(Render(text), Render(text));
    }

    [Theory]
    [MemberData(nameof(Words))]
    public void Svg_matches_the_golden_snapshot(string text) =>
        AssertFile(Repo.Path("tests", "PenForLang.Glyphs.Tests", "golden", $"{text}.word.svg"), Render(text), "Golden file");

    [Theory]
    [MemberData(nameof(Words))]
    public void Review_copies_in_docs_spikes_are_up_to_date(string text) =>
        AssertFile(Repo.Path("docs", "spikes", "glyphs-v0", $"{text}.svg"), Render(text), "Review copy");

    [Theory]
    [MemberData(nameof(Words))]
    public void Svg_has_the_expected_structure(string text)
    {
        string svg = Render(text);

        Assert.StartsWith("<svg ", svg, StringComparison.Ordinal);
        Assert.EndsWith("</svg>\n", svg, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', svg);
        Assert.DoesNotMatch(BadNumber(), svg);
        Assert.Contains("id=\"bodies\"", svg, StringComparison.Ordinal);
        Assert.Contains("id=\"joins\"", svg, StringComparison.Ordinal);
        Assert.Contains("data-family=\"JF1\"", svg, StringComparison.Ordinal);
        Assert.Contains("id=\"deferred\"", svg, StringComparison.Ordinal);
        Assert.Contains("class=\"body-end\"", svg, StringComparison.Ordinal);
        Assert.Contains("class=\"body-start\"", svg, StringComparison.Ordinal);
        Assert.Equal(text.Length, Regex.Count(svg, "<path id=\"body-", RegexOptions.None, TimeSpan.FromSeconds(1)));
        Assert.Contains($"<title>{text}</title>", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_switch_parts_off()
    {
        string bare = Render("at", new SvgRenderOptions { ShowGuides = false, ShowLeads = false, ShowNumbers = false, ShowStartDots = false, ShowCaption = false });

        Assert.DoesNotContain("id=\"guides\"", bare, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"leads\"", bare, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"numbers\"", bare, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"start-dots\"", bare, StringComparison.Ordinal);
        Assert.DoesNotContain("not reviewed", bare, StringComparison.Ordinal);
    }
}
