using System.Text.RegularExpressions;
using PenForLang.Glyphs.Rendering;
using PenForLang.Glyphs.Tests.Support;

namespace PenForLang.Glyphs.Tests;

/// <summary>
/// SVG-рендер: детерминизм, структура и golden-снимки <c>golden/{a,i,t}.default.svg</c>.
/// <para>
/// Обновить снимки после намеренного изменения форм или рендера: запустить тесты с переменной окружения
/// <c>PFL_UPDATE_GOLDEN=1</c> (файлы <c>tests/PenForLang.Glyphs.Tests/golden/*.svg</c> и копии для ревью
/// <c>docs/spikes/glyphs-v0/*.svg</c> перезапишутся), просмотреть <c>git diff</c> и закоммитить.
/// Снимки принимает человек: это то, что видит ревьюер форм.
/// </para>
/// </summary>
public partial class GlyphSvgRendererTests
{
    private const string UpdateVariable = "PFL_UPDATE_GOLDEN";

    public static TheoryData<string> Symbols => ["a", "i", "t"];

    [GeneratedRegex("NaN|Infinity|∞", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BadNumber();

    private static string GoldenPath(string symbol) => Repo.Path("tests", "PenForLang.Glyphs.Tests", "golden", $"{symbol}.default.svg");

    private static string SpikePath(string symbol) => Repo.Path("docs", "spikes", "glyphs-v0", $"{symbol}.svg");

    private static string Render(string symbol, SvgRenderOptions? options = null) =>
        GlyphSvgRenderer.Render(Shipped.Glyph(symbol), Shipped.Style, options);

    private static bool UpdateRequested
    {
        get
        {
            if (Environment.GetEnvironmentVariable(UpdateVariable) != "1")
            {
                return false;
            }

            // Снимки принимает человек; в CI их нельзя перезаписать молча.
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"))
                || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS")))
            {
                throw new InvalidOperationException($"{UpdateVariable}=1 is not allowed in CI (CI / GITHUB_ACTIONS is set): golden snapshots are accepted by a human.");
            }

            return true;
        }
    }

    [Theory]
    [MemberData(nameof(Symbols))]
    public void Rendering_twice_gives_the_same_string(string symbol)
    {
        Assert.Equal(Render(symbol), Render(symbol));
    }

    [Theory]
    [MemberData(nameof(Symbols))]
    public void Svg_matches_the_golden_snapshot(string symbol)
    {
        string actual = Render(symbol);
        string path = GoldenPath(symbol);
        if (UpdateRequested)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
        }

        Assert.True(File.Exists(path), $"Golden file {path} is missing. Create it by running the tests with {UpdateVariable}=1.");
        string expected = File.ReadAllText(path);
        Assert.True(
            string.Equals(expected, actual, StringComparison.Ordinal),
            $"SVG of '{symbol}' differs from {path}. If the change is intended, run the tests with {UpdateVariable}=1, review the diff and commit.");
    }

    [Theory]
    [MemberData(nameof(Symbols))]
    public void Review_copies_in_docs_spikes_are_up_to_date(string symbol)
    {
        string actual = Render(symbol);
        string path = SpikePath(symbol);
        if (UpdateRequested)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
        }

        Assert.True(File.Exists(path), $"Review copy {path} is missing. Create it by running the tests with {UpdateVariable}=1.");
        Assert.True(
            string.Equals(File.ReadAllText(path), actual, StringComparison.Ordinal),
            $"{path} is stale. Run the tests with {UpdateVariable}=1 and commit the refreshed copies.");
    }

    [Fact]
    public void Preview_page_with_the_three_inline_svgs_is_up_to_date()
    {
        string actual = BuildPreviewHtml();
        string path = Repo.Path("docs", "spikes", "glyphs-v0", "preview.html");
        if (UpdateRequested)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
        }

        Assert.True(File.Exists(path), $"Preview page {path} is missing. Create it by running the tests with {UpdateVariable}=1.");
        Assert.True(
            string.Equals(File.ReadAllText(path), actual, StringComparison.Ordinal),
            $"{path} is stale. Run the tests with {UpdateVariable}=1 and commit it. preview.png is made from it by hand: msedge --headless --screenshot=preview.png --window-size=1500,640 file:///.../preview.html");
        Assert.Equal(3, Regex.Count(actual, "<svg "));
        Assert.DoesNotContain("<img", actual, StringComparison.Ordinal);
    }

    /// <summary>Одна страница с тремя SVG, вставленными inline (идентификаторы элементов получают префикс символа, чтобы не повторяться).</summary>
    private static string BuildPreviewHtml()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<!doctype html>\n<html lang=\"ru\">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<title>Glyphs v0: a, i, t</title>\n<style>\n");
        sb.Append("body { margin: 24px; font-family: sans-serif; background: #ffffff; color: #1e1e1e; }\n");
        sb.Append("h1 { font-size: 20px; margin: 0 0 4px; }\n");
        sb.Append("p { margin: 0 0 16px; color: #5e5c64; font-size: 14px; }\n");
        sb.Append(".row { display: flex; flex-wrap: wrap; gap: 16px; }\n");
        sb.Append("figure { margin: 0; border: 1px solid #c0bfbc; border-radius: 6px; padding: 8px; }\n");
        sb.Append("figure svg { display: block; max-width: 100%; height: auto; }\n");
        sb.Append("</style>\n</head>\n<body>\n");
        sb.Append("<h1>Glyphs v0: a, i, t (zaner-bloser)</h1>\n");
        sb.Append("<p>Illustrative shapes, not yet reviewed (meta.source = illustrative, reviewed = false). Blue: body strokes; red: deferred strokes; ");
        sb.Append("grey dashed: lead-in and lead-out; green dot: start; circled numbers: stroke order; triangles: direction.</p>\n");
        sb.Append("<div class=\"row\">\n");
        foreach (string symbol in new[] { "a", "i", "t" })
        {
            string svg = Render(symbol, new SvgRenderOptions { PixelsPerXHeight = 110 });
            svg = Regex.Replace(svg, "(?<=\\sid=\")([^\"]+)(?=\")", $"{symbol}-$1");
            sb.Append("<figure>\n").Append(svg).Append("</figure>\n");
        }

        sb.Append("</div>\n</body>\n</html>\n");
        return sb.ToString();
    }

    [Theory]
    [MemberData(nameof(Symbols))]
    public void Svg_is_well_formed_with_unix_line_endings_and_finite_numbers(string symbol)
    {
        string svg = Render(symbol, new SvgRenderOptions { ShowKeyPoints = true });

        var document = System.Xml.Linq.XDocument.Parse(svg);
        Assert.Equal("svg", document.Root!.Name.LocalName);
        Assert.Equal("http://www.w3.org/2000/svg", document.Root.Name.NamespaceName);
        Assert.DoesNotContain('\r', svg);
        Assert.EndsWith("</svg>\n", svg, StringComparison.Ordinal);
        Assert.DoesNotMatch(BadNumber(), svg);
        Assert.Contains(Shipped.Glyph(symbol).Id.ToString(), svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Svg_draws_the_expected_elements_for_t()
    {
        Glyph t = Shipped.Glyph("t");
        string svg = Render("t");
        var document = System.Xml.Linq.XDocument.Parse(svg);
        IEnumerable<System.Xml.Linq.XElement> all = document.Descendants();

        Assert.Equal(t.Animation.Arrows.Length, all.Count(e => e.Name.LocalName == "polygon"));
        Assert.Equal(t.PenStrokes.Length, all.Count(e => e.Name.LocalName == "text" && e.Value is "1" or "2"));
        Assert.Contains(all, e => e.Attribute("id")?.Value == "s1");
        Assert.Contains(all, e => e.Attribute("id")?.Value == "bar");
        Assert.Contains(all, e => e.Attribute("id")?.Value == "leadIn");
        Assert.Contains(all, e => e.Attribute("id")?.Value == "leadOut");
        Assert.Contains(all, e => e.Attribute("id")?.Value == "line-baseline");
        Assert.Contains(all, e => e.Attribute("id")?.Value == "line-xheight");
        Assert.Equal(t.PenStrokes.Length, all.Single(e => e.Attribute("id")?.Value == "start-dots").Elements().Count());

        // Тело штриха — те же кривые Безье, что в данных: команда C на каждый кубический сегмент.
        string d = all.Single(e => e.Attribute("id")?.Value == "s1").Attribute("d")!.Value;
        Assert.StartsWith("M", d, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(d, " C"));
        Assert.Equal(2, Regex.Count(d, " L")); // ствол разрезан на ретрейс по leadIn и остаток
    }

    [Fact]
    public void Options_switch_the_layers_off()
    {
        string plain = Render(
            "i",
            new SvgRenderOptions
            {
                ShowGuides = false,
                ShowLeads = false,
                ShowArrows = false,
                ShowNumbers = false,
                ShowStartDots = false,
                ShowCaption = false,
            });
        string full = Render("i", new SvgRenderOptions { ShowKeyPoints = true });

        Assert.DoesNotContain("id=\"guides\"", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("leadIn", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("<polygon", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"numbers\"", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("start-dots", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("illustrative", plain, StringComparison.Ordinal);
        Assert.Contains("id=\"keypoints\"", full, StringComparison.Ordinal);
        Assert.Contains("baselineTouch", full, StringComparison.Ordinal);
        Assert.Contains("illustrative | not reviewed", full, StringComparison.Ordinal);
        Assert.True(plain.Length < full.Length);
    }

    [Fact]
    public void Pixel_scale_changes_only_the_image_size()
    {
        string small = Render("a", new SvgRenderOptions { PixelsPerXHeight = 80 });
        string large = Render("a", new SvgRenderOptions { PixelsPerXHeight = 160 });

        Assert.NotEqual(small, large);
        Assert.Equal(Regex.Replace(small, "(width|height)=\"[0-9.]+\"", string.Empty), Regex.Replace(large, "(width|height)=\"[0-9.]+\"", string.Empty));
    }
}
