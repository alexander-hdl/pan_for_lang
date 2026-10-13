using System.Text.Json.Nodes;
using PenForLang.Core;
using PenForLang.Glyphs.Loading;
using PenForLang.Glyphs.Tests.Support;
using static PenForLang.Glyphs.Tests.Support.PackBuilder;

namespace PenForLang.Glyphs.Tests;

/// <summary>Раскладка пакета: лишние и отсутствующие файлы, корневой каталог, ссылки между файлами.</summary>
public class StyleLoaderLayoutTests
{
    [Fact]
    public void A_package_in_the_root_of_the_source_loads_with_an_empty_directory()
    {
        var source = new InMemoryContentSource();
        foreach (string file in new[] { "style.json", "glyphs/i.default.json", "labels/ru.json", "labels/en.json" })
        {
            source.Add(file, Repo.ReadText(Repo.StyleDirectory + "/" + file));
        }

        // Метки a. и t. в этом пакете не используются: это предупреждения, не ошибки.
        StyleLoadResult result = StyleLoader.Load(source, string.Empty);

        Assert.True(result.Succeeded, string.Join("\n", result.Issues));
        Assert.All(result.Issues, issue => Assert.Equal(IssueSeverity.Warning, issue.Severity));
        Assert.Contains(result.Warnings, w => w.Code == "labels.unused" && w.Path == "a.rollUp");
        Assert.Single(result.Pack!.Glyphs);
    }

    [Fact]
    public void Unexpected_files_are_warnings_and_do_not_block_loading()
    {
        PackBuilder builder = new();
        InMemoryContentSource source = builder.BuildSource();
        source.Add(builder.Directory + "/glyphs/readme.txt", "notes");
        source.Add(builder.Directory + "/labels/ru.bak", "{}");

        StyleLoadResult result = StyleLoader.Load(source, builder.Directory);

        Assert.True(result.Succeeded, string.Join("\n", result.Issues));
        Assert.Contains(result.Warnings, w => w.Code == "pack.glyphs.unexpected" && w.File == "glyphs/readme.txt");
        Assert.Contains(result.Warnings, w => w.Code == "labels.unexpected" && w.File == "labels/ru.bak");
    }

    [Fact]
    public void A_missing_style_file_is_one_clear_error()
    {
        StyleLoadResult result = StyleLoader.Load(new InMemoryContentSource(), "styles/nothing");

        ValidationIssue issue = Assert.Single(result.Errors);
        Assert.Equal("file.missing", issue.Code);
        Assert.Equal("style.json", issue.File);
        Assert.Contains("styles/nothing/style.json", issue.Message, StringComparison.Ordinal);
        Assert.Null(result.Pack);
    }

    [Fact]
    public void A_missing_labels_file_is_an_error_when_glyphs_use_labels()
    {
        PackBuilder builder = new();
        InMemoryContentSource source = builder.BuildSource();
        var withoutRu = new InMemoryContentSource();
        foreach (string file in source.List(string.Empty).Where(f => !f.EndsWith("labels/ru.json", StringComparison.Ordinal)))
        {
            using Stream stream = source.Open(file);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            withoutRu.Add(file, buffer.ToArray());
        }

        StyleLoadResult result = StyleLoader.Load(withoutRu, builder.Directory);

        Assert.Contains(result.Errors, e => e.Code == "labels.file.missing" && e.File == "labels/ru.json");
    }

    [Fact]
    public void A_character_needs_a_default_variant()
    {
        StyleLoadResult result = new PackBuilder()
            .GlyphFileName("i.alt.json")
            .EditGlyph(g =>
            {
                g["id"] = "zaner-bloser/i.alt";
                g["variant"] = "alt";
            })
            .Load();

        ValidationIssue issue = Assert.Single(result.Errors);
        Assert.Equal("pack.variant.default", issue.Code);
        Assert.Contains("character 'i' has no 'default' variant (found: alt)", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_variant_rule_must_point_to_an_existing_glyph_of_a_present_character()
    {
        StyleLoadResult result = new PackBuilder()
            .EditStyle(s => Arr(s["variantRules"]).Add(JsonNode.Parse("{ \"char\": \"i\", \"when\": { \"prevExit\": \"X-HIGH\" }, \"variant\": \"after-high\" }")))
            .Load();

        ValidationIssue issue = Assert.Single(result.Errors);
        Assert.Equal("pack.variantRules.variant", issue.Code);
        Assert.Equal("variantRules[3].variant", issue.Path);
        Assert.Contains("'i.after-high'", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Style_settings_are_read_into_the_manifest()
    {
        StyleManifest style = Shipped.Style;

        Assert.Equal(20.0, style.SlantDeg);
        Assert.Equal(SlantScoring.Consistency, style.SlantScoring); // абсолютный наклон не сверен с прописью
        Assert.Equal(0.15, style.LetterSpacing);
        Assert.Equal(10, style.Joins.Rules.Length);
        Assert.Equal(EntryClass.UpTall, style.Joins.TallEntryClass);
        JoinRule jf2 = style.Joins.Rules.Single(r => r.Family == "JF2");
        Assert.Equal(JoinShape.HermiteOver, jf2.Shape);
        Assert.Equal(0.12, jf2.Params["overshootU"]);
        Assert.True(style.Joins.Rules.Single(r => r.Family == "JF4").High);
        Assert.Contains(style.Joins.Rules, r => r.Family is null && r.AnyEntry && r.Shape == JoinShape.Break);
        Assert.Equal(3, style.VariantRules.Length);
        Assert.Equal(new VariantRule("e", ExitClass.High, "after-high"), style.VariantRules[0]);
        Assert.Equal(["m", "n"], style.MotorFamilies[0]);
        Assert.Equal(CapitalJoinMode.Break, style.CapitalJoinMode);
        Assert.Equal(1.6, style.Animation.SpeedXhPerSec);
        Assert.Equal(120, style.Animation.CuspPauseMs);
    }

    [Fact]
    public void A_null_slant_is_accepted_while_only_the_consistency_is_scored()
    {
        StyleLoadResult result = new PackBuilder().EditStyle(s => s["slantDeg"] = null).Load();

        Assert.True(result.Succeeded, string.Join("\n", result.Issues));
        Assert.Null(result.Pack!.Manifest.SlantDeg);
        Assert.Equal(SlantScoring.Consistency, result.Pack.Manifest.SlantScoring);
    }

    [Fact]
    public void Absolute_slant_scoring_is_accepted_with_a_numeric_slant()
    {
        StyleLoadResult result = new PackBuilder().EditStyle(s => s["slantScoring"] = "absolute").Load();

        Assert.True(result.Succeeded, string.Join("\n", result.Issues));
        Assert.Equal(SlantScoring.Absolute, result.Pack!.Manifest.SlantScoring);
    }

    [Fact]
    public void Join_class_names_round_trip()
    {
        foreach (EntryClass value in Enum.GetValues<EntryClass>())
        {
            Assert.True(JoinClasses.TryParseEntry(value.ToWireName(), out EntryClass parsed));
            Assert.Equal(value, parsed);
        }

        foreach (ExitClass value in Enum.GetValues<ExitClass>())
        {
            Assert.True(JoinClasses.TryParseExit(value.ToWireName(), out ExitClass parsed));
            Assert.Equal(value, parsed);
        }

        Assert.False(JoinClasses.TryParseEntry("X-BASE", out _));
        Assert.False(JoinClasses.TryParseExit(null, out _));
    }

    [Fact]
    public void Content_path_violations_in_the_directory_argument_throw()
    {
        Assert.Throws<ArgumentException>(() => StyleLoader.Load(new InMemoryContentSource(), "../outside"));
        Assert.Throws<ArgumentNullException>(() => StyleLoader.Load(null!, "styles/x"));
    }
}
