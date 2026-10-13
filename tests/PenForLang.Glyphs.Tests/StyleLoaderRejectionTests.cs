using System.Text.Json.Nodes;
using PenForLang.Glyphs.Loading;
using PenForLang.Glyphs.Tests.Support;
using static PenForLang.Glyphs.Tests.Support.PackBuilder;

namespace PenForLang.Glyphs.Tests;

/// <summary>Загрузчик отвергает некорректные стиль-пакеты: у каждой фикстуры свой код, путь к полю и сообщение.</summary>
public class StyleLoaderRejectionTests
{
    private sealed record Bad(Action<PackBuilder> Edit, string Code, string File, string Path, string Message);

    private static JsonObject Segment(PackBuilder b, int index) => Obj(Arr(Obj(Arr(b.Glyph["strokes"])[0])["segments"])[index]);

    private static readonly Dictionary<string, Bad> Fixtures = new(StringComparer.Ordinal)
    {
        ["syntax_error"] = new(
            b => b.RawGlyphText("{ \"schema\": \"pfl.glyph/1\", \"id\": "),
            "json.syntax", "glyphs/i.default.json", "", "invalid JSON"),
        ["wrong_schema_version"] = new(
            b => b.EditGlyph(g => g["schema"] = "pfl.glyph/2"),
            "glyph.schema", "glyphs/i.default.json", "schema", "unsupported schema 'pfl.glyph/2'"),
        ["missing_strokes"] = new(
            b => b.EditGlyph(g => g.Remove("strokes")),
            "json.missing", "glyphs/i.default.json", "strokes", "required property 'strokes' is missing"),
        ["unknown_property"] = new(
            b => b.EditGlyph(g => g["colour"] = "blue"),
            "json.unknown", "glyphs/i.default.json", "colour", "unknown property 'colour'"),
        ["cubic_with_three_points"] = new(
            b => Arr(Segment(b, 1)["c"]).RemoveAt(3),
            "json.length", "glyphs/i.default.json", "strokes[0].segments[1].c", "expected 4 element(s) but found 3"),
        ["coordinate_out_of_range"] = new(
            b => Arr(Arr(Segment(b, 1)["c"])[1])[0] = 99.0,
            "json.range", "glyphs/i.default.json", "strokes[0].segments[1].c[1][0]", "outside the allowed range"),
        ["segment_with_both_kinds"] = new(
            b => Segment(b, 0)["c"] = JsonNode.Parse(Segment(b, 1)["c"]!.ToJsonString()),
            "glyph.segment.kind", "glyphs/i.default.json", "strokes[0].segments[0]", "found both"),
        ["segment_without_geometry"] = new(
            b => Segment(b, 0).Remove("l"),
            "glyph.segment.kind", "glyphs/i.default.json", "strokes[0].segments[0]", "exactly one of 'c'"),
        ["segment_tempo_zero"] = new(
            b => Segment(b, 0)["tempo"] = 0.0,
            "json.range", "glyphs/i.default.json", "strokes[0].segments[0].tempo", "outside the allowed range"),
        ["duplicate_stroke_id"] = new(
            b => b.EditGlyph(g =>
            {
                JsonNode copy = JsonNode.Parse(Arr(g["strokes"])[0]!.ToJsonString())!;
                copy["order"] = 2;
                Arr(g["strokes"]).Add(copy);
            }),
            "glyph.stroke.duplicate", "glyphs/i.default.json", "strokes[1].id", "already used by strokes[0]"),
        ["stroke_order_not_sequential"] = new(
            b => Obj(Arr(b.Glyph["strokes"])[0])["order"] = 3,
            "glyph.stroke.order", "glyphs/i.default.json", "strokes[0].order", "expected 1"),
        ["gap_between_segments"] = new(
            b => Shift(Arr(Segment(b, 1)["c"])[0], 0.2, 0.0),
            "glyph.segment.gap", "glyphs/i.default.json", "strokes[0].segments[1]", "gap"),
        ["corner_without_cusp_keypoint"] = new(
            b => Arr(Segment(b, 1)["c"])[1] = JsonNode.Parse("[0.9, 0.4]"),
            "glyph.segment.g1", "glyphs/i.default.json", "strokes[0].segments[1]", "add a 'cusp' keypoint"),
        ["zero_length_dot"] = new(
            b => b.EditGlyph(g => Arr(Obj(Arr(g["deferred"])[0])["segments"])[0] = JsonNode.Parse("{ \"l\": [[0.756, 1.45], [0.756, 1.45]] }")),
            "glyph.segment.length", "glyphs/i.default.json", "deferred[0].segments[0]", "zero length"),
        ["entry_point_off_the_body"] = new(
            b => b.EditGlyph(g => Shift(Obj(g["entry"])["point"], 0.2, 0.0)),
            "glyph.entry.point", "glyphs/i.default.json", "entry.point", "is not the start of the first stroke"),
        ["exit_point_off_the_body"] = new(
            b => b.EditGlyph(g => Shift(Obj(g["exit"])["point"], 0.0, 0.3)),
            "glyph.exit.point", "glyphs/i.default.json", "exit.point", "is not the end of the last stroke"),
        ["entry_tangent_against_lead_in"] = new(
            b => b.EditGlyph(g => Obj(g["entry"])["tangent"] = JsonNode.Parse("[-1.0, 0.1]")),
            "glyph.entry.tangent", "glyphs/i.default.json", "entry.tangent", "degrees from the direction at the end of leadIn"),
        ["unknown_entry_class"] = new(
            b => b.EditGlyph(g => Obj(g["entry"])["class"] = "E-XX"),
            "glyph.class", "glyphs/i.default.json", "entry.class", "unknown entry class 'E-XX'"),
        ["entry_class_not_in_style_rules"] = new(
            b =>
            {
                b.EditGlyph(g => Obj(g["entry"])["class"] = "E-HUMP");
                b.EditStyle(s => RemoveRules(s, r => r["family"] is null || Arr(r["entry"]).Any(e => (string?)e == "E-HUMP")));
            },
            "glyph.entry.class.undeclared", "glyphs/i.default.json", "entry.class", "is not used by any join rule"),
        ["exit_class_not_in_style_rules"] = new(
            b =>
            {
                b.EditGlyph(g => Obj(g["exit"])["class"] = "X-DESC");
                b.EditStyle(s => RemoveRules(s, r => (string?)r["exit"] == "X-DESC"));
            },
            "glyph.exit.class.undeclared", "glyphs/i.default.json", "exit.class", "is not used by any join rule"),
        ["deferred_anchored_to_unknown_stroke"] = new(
            b => b.EditGlyph(g => Obj(Obj(Arr(g["deferred"])[0])["anchor"])["stroke"] = "s9"),
            "glyph.deferred.anchor.stroke", "glyphs/i.default.json", "deferred[0].anchor.stroke", "anchored to unknown stroke 's9'"),
        ["deferred_without_anchor"] = new(
            b => b.EditGlyph(g => Obj(Arr(g["deferred"])[0]).Remove("anchor")),
            "json.missing", "glyphs/i.default.json", "deferred[0].anchor", "required property 'anchor' is missing"),
        ["deferred_far_from_anchor"] = new(
            b => b.EditGlyph(g => Shift(Obj(Obj(Arr(g["deferred"])[0])["anchor"])["offset"], 0.5, 0.0)),
            "glyph.deferred.anchor.offset", "glyphs/i.default.json", "deferred[0].anchor", "puts the deferred stroke at"),
        ["left_to_right_stroke_goes_left"] = new(
            b => b.EditGlyph(g =>
            {
                JsonObject dot = Obj(Arr(g["deferred"])[0]);
                dot["direction"] = "ltr";
                Arr(dot["segments"])[0] = JsonNode.Parse("{ \"l\": [[0.78, 1.45], [0.756, 1.45]] }");
            }),
            "glyph.deferred.direction", "glyphs/i.default.json", "deferred[0].direction", "does not go left to right"),
        ["keypoint_on_unknown_stroke"] = new(
            b => b.EditGlyph(g => Obj(Arr(g["keypoints"])[1])["stroke"] = "zz"),
            "glyph.keypoint.stroke", "glyphs/i.default.json", "keypoints[1].stroke", "unknown stroke 'zz'"),
        ["keypoint_t_above_one"] = new(
            b => b.EditGlyph(g => Obj(Arr(g["keypoints"])[1])["t"] = 1.5),
            "json.range", "glyphs/i.default.json", "keypoints[1].t", "outside the allowed range"),
        ["ymax_keypoint_not_at_a_maximum"] = new(
            b => b.EditGlyph(g => Arr(g["keypoints"]).Add(JsonNode.Parse("{ \"type\": \"ymax\", \"stroke\": \"s1\", \"t\": 0.5 }"))),
            "glyph.keypoint.extremum", "glyphs/i.default.json", "keypoints[3]", "is not a local maximum"),
        ["features_miss_deferred"] = new(
            b => b.EditGlyph(g => g["features"] = new JsonArray()),
            "glyph.features.mismatch", "glyphs/i.default.json", "features", "'deferred' is missing from features"),
        ["retrace_check_on_plain_segment"] = new(
            b => b.EditGlyph(g => g["checks"] = JsonNode.Parse("{ \"retrace\": [ { \"stroke\": \"s1\", \"segment\": 0 } ] }")),
            "glyph.checks.retrace", "glyphs/i.default.json", "checks.retrace[0].segment", "is not marked 'retrace': true"),
        ["duplicate_arrow"] = new(
            b => b.EditGlyph(g => Arr(Obj(g["animation"])["arrows"]).Add(JsonNode.Parse(Arr(Obj(g["animation"])["arrows"])[0]!.ToJsonString()))),
            "glyph.animation.arrow.duplicate", "glyphs/i.default.json", "animation.arrows[2].t", "duplicates another arrow"),
        ["arrow_at_stroke_end"] = new(
            b => b.EditGlyph(g => Obj(Arr(Obj(g["animation"])["arrows"])[0])["t"] = 1.0),
            "glyph.animation.arrow.t", "glyphs/i.default.json", "animation.arrows[0].t", "strictly between 0 and 1"),
        ["duplicate_number"] = new(
            b => b.EditGlyph(g => Arr(Obj(g["animation"])["numbers"]).Add(JsonNode.Parse("{ \"stroke\": \"s1\", \"at\": [0.9, 1.1] }"))),
            "glyph.animation.number.duplicate", "glyphs/i.default.json", "animation.numbers[2].stroke", "already has a number"),
        ["numbers_for_some_strokes_only"] = new(
            b => b.EditGlyph(g => Arr(Obj(g["animation"])["numbers"]).RemoveAt(1)),
            "glyph.animation.number.missing", "glyphs/i.default.json", "animation.numbers", "number all strokes or none"),
        ["start_dot_on_missing_lead_in"] = new(
            b => b.EditGlyph(g => g["leadIn"] = null),
            "glyph.animation.startDot", "glyphs/i.default.json", "animation.startDot", "has no leadIn"),
        ["reviewed_without_reviewer"] = new(
            b => b.EditGlyph(g => Obj(g["meta"])["reviewed"] = true),
            "glyph.meta.reviewer", "glyphs/i.default.json", "meta.reviewer", "names the reviewer"),
        ["glyph_of_another_style"] = new(
            b => b.EditGlyph(g => g["id"] = "other-style/i.default"),
            "glyph.id.style", "glyphs/i.default.json", "id", "belongs to style 'other-style'"),
        ["file_name_differs_from_id"] = new(
            b => b.GlyphFileName("x.default.json"),
            "glyph.file.name", "glyphs/x.default.json", "id", "expected 'i.default.json'"),
        ["label_missing_in_english"] = new(
            b => b.EditEnLabels(l => l.Remove("i.dot")),
            "labels.missing", "labels/en.json", "i.dot", "has no text for language 'en'"),
        ["package_without_glyphs"] = new(
            b => b.WithoutGlyphs(),
            "pack.glyphs.empty", "style.json", "", "has no glyphs"),
        ["style_x_height_not_one"] = new(
            b => b.EditStyle(s => Obj(s["lines"])["xHeight"] = 1.5),
            "style.lines", "style.json", "lines.xHeight", "xHeight must be 1"),
        ["trademark_in_display_name"] = new(
            b => b.EditStyle(s => Obj(s["displayName"])["en"] = "Zaner-Bloser cursive"),
            "style.displayName.trademark", "style.json", "displayName.en", "trademark word 'zaner'"),
        ["display_name_without_russian"] = new(
            b => b.EditStyle(s => Obj(s["displayName"]).Remove("ru")),
            "json.missing", "style.json", "displayName.ru", "display name for language 'ru' is required"),
        ["unknown_exit_class_in_join_rule"] = new(
            b => b.EditStyle(s => Obj(Arr(Obj(s["joins"])["rules"])[0])["exit"] = "X-ZZ"),
            "style.joins.class", "style.json", "joins.rules[0].exit", "unknown exit class 'X-ZZ'"),
        ["duplicate_join_rule"] = new(
            b => b.EditStyle(s => Arr(Obj(s["joins"])["rules"]).Add(JsonNode.Parse(Arr(Obj(s["joins"])["rules"])[0]!.ToJsonString()))),
            "style.joins.duplicate", "style.json", "joins.rules[10]", "duplicate join rule for exit/entry pair"),
        ["style_directory_differs_from_id"] = new(
            b => b.InDirectory("styles/other"),
            "style.id.directory", "style.json", "id", "does not match the package directory name 'other'"),
    };

    public static TheoryData<string> FixtureNames => [.. Fixtures.Keys];

    private static void RemoveRules(JsonObject style, Func<JsonObject, bool> remove)
    {
        JsonArray rules = Arr(Obj(style["joins"])["rules"]);
        for (int i = rules.Count - 1; i >= 0; i--)
        {
            if (remove(Obj(rules[i])))
            {
                rules.RemoveAt(i);
            }
        }
    }

    private static StyleLoadResult Load(string name)
    {
        var builder = new PackBuilder();
        Fixtures[name].Edit(builder);
        return builder.Load();
    }

    [Fact]
    public void At_least_ten_distinct_bad_fixtures_exist()
    {
        Assert.True(Fixtures.Count >= 10);
        Assert.True(Fixtures.Values.Select(f => f.Code).Distinct(StringComparer.Ordinal).Count() >= 10);
    }

    [Fact]
    public void The_unmodified_pack_loads_without_issues()
    {
        StyleLoadResult result = new PackBuilder().Load();

        Assert.True(result.Succeeded, string.Join("\n", result.Issues));
        Assert.Empty(result.Issues);
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Fixture_is_rejected_with_its_own_code_path_and_message(string name)
    {
        Bad bad = Fixtures[name];

        StyleLoadResult result = Load(name);

        Assert.False(result.Succeeded, $"{name}: the pack must not load");
        Assert.Null(result.Pack);
        ValidationIssue? issue = result.Errors.FirstOrDefault(i => i.Code == bad.Code && i.Path == bad.Path);
        Assert.True(
            issue is not null,
            $"{name}: expected code '{bad.Code}' at '{bad.Path}', got:\n{string.Join("\n", result.Issues)}");
        Assert.Equal(bad.File, issue.File);
        Assert.Contains(bad.Message, issue.Message, StringComparison.Ordinal);
        Assert.True(issue.Message.Length >= 20, $"{name}: the message is too short to be useful: '{issue.Message}'");
        Assert.Equal(IssueSeverity.Error, issue.Severity);
    }

    [Fact]
    public void Every_fixture_produces_a_message_no_other_fixture_produces()
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string name, Bad bad) in Fixtures)
        {
            ValidationIssue issue = Load(name).Errors.First(i => i.Code == bad.Code && i.Path == bad.Path);
            string key = issue.Message;
            Assert.True(seen.TryAdd(key, name), $"'{name}' and '{(seen.TryGetValue(key, out string? other) ? other : string.Empty)}' share the message: {key}");
        }
    }

    [Fact]
    public void LoadOrThrow_reports_the_issues_in_the_exception()
    {
        var builder = new PackBuilder();
        Fixtures["gap_between_segments"].Edit(builder);

        var ex = Assert.Throws<StyleLoadException>(() => StyleLoader.LoadOrThrow(builder.BuildSource(), builder.Directory));

        Assert.Contains("glyph.segment.gap", ex.Message, StringComparison.Ordinal);
        Assert.Contains("strokes[0].segments[1]", ex.Message, StringComparison.Ordinal);
        Assert.Contains(ex.Issues, i => i.Code == "glyph.segment.gap");
    }

    [Fact]
    public void Issues_of_one_bad_glyph_do_not_hide_the_other_glyphs()
    {
        // Два глифа с ошибками в разных файлах: оба должны быть названы в одном проходе.
        var source = new PenForLang.Core.InMemoryContentSource();
        string prefix = Repo.StyleDirectory + "/";
        source.Add(prefix + "style.json", Repo.ReadText(prefix + "style.json"));
        source.Add(prefix + "labels/ru.json", Repo.ReadText(prefix + "labels/ru.json"));
        source.Add(prefix + "labels/en.json", Repo.ReadText(prefix + "labels/en.json"));
        JsonNode i = JsonNode.Parse(Repo.ReadText(prefix + "glyphs/i.default.json"))!;
        i["advance"] = 9.0;
        JsonNode t = JsonNode.Parse(Repo.ReadText(prefix + "glyphs/t.default.json"))!;
        Obj(t["entry"])["class"] = "E-QQ";
        source.Add(prefix + "glyphs/i.default.json", i.ToJsonString());
        source.Add(prefix + "glyphs/t.default.json", t.ToJsonString());

        StyleLoadResult result = StyleLoader.Load(source, Repo.StyleDirectory);

        Assert.Contains(result.Errors, i => i.File == "glyphs/i.default.json" && i.Path == "advance");
        Assert.Contains(result.Errors, i => i.File == "glyphs/t.default.json" && i.Path == "entry.class");
    }
}
