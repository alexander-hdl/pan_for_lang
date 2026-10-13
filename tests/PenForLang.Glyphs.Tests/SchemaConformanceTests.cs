using System.Text.Json;
using System.Text.Json.Nodes;
using PenForLang.Glyphs.Tests.Support;

namespace PenForLang.Glyphs.Tests;

/// <summary>Схемы в <c>schemas/</c> согласованы с файлами стиль-пакета и действительно строги.</summary>
public class SchemaConformanceTests
{
    private static readonly MiniJsonSchema StyleSchema = MiniJsonSchema.FromRepo("pfl.style.v1.schema.json");
    private static readonly MiniJsonSchema GlyphSchema = MiniJsonSchema.FromRepo("pfl.glyph.v1.schema.json");
    private static readonly MiniJsonSchema LabelsSchema = MiniJsonSchema.FromRepo("pfl.labels.v1.schema.json");

    public static TheoryData<string> GlyphFiles => ["a.default.json", "i.default.json", "t.default.json"];

    [Fact]
    public void Schema_files_are_valid_json_with_distinct_ids()
    {
        string[] files = ["pfl.style.v1.schema.json", "pfl.glyph.v1.schema.json", "pfl.labels.v1.schema.json"];
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in files)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Repo.Path("schemas", file)));
            Assert.Equal("https://json-schema.org/draft/2020-12/schema", document.RootElement.GetProperty("$schema").GetString());
            Assert.True(ids.Add(document.RootElement.GetProperty("$id").GetString()!), $"{file}: duplicate $id");
        }
    }

    [Fact]
    public void Style_json_conforms_to_the_style_schema()
    {
        Assert.Empty(StyleSchema.Validate(Repo.ReadText(Repo.StyleDirectory + "/style.json")));
    }

    [Theory]
    [MemberData(nameof(GlyphFiles))]
    public void Glyph_files_conform_to_the_glyph_schema(string file)
    {
        Assert.Empty(GlyphSchema.Validate(Repo.ReadText(Repo.StyleDirectory + "/glyphs/" + file)));
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public void Label_files_conform_to_the_labels_schema(string language)
    {
        Assert.Empty(LabelsSchema.Validate(Repo.ReadText($"{Repo.StyleDirectory}/labels/{language}.json")));
    }

    [Fact]
    public void Glyph_schema_rejects_structural_mistakes()
    {
        string valid = Repo.ReadText(Repo.StyleDirectory + "/glyphs/i.default.json");

        Assert.NotEmpty(GlyphSchema.Validate(Mutate(valid, g => g.AsObject().Remove("strokes"))));
        Assert.NotEmpty(GlyphSchema.Validate(Mutate(valid, g => g["colour"] = "blue")));
        Assert.NotEmpty(GlyphSchema.Validate(Mutate(valid, g => g["schema"] = "pfl.glyph/2")));
        Assert.NotEmpty(GlyphSchema.Validate(Mutate(valid, g => PackBuilder.Obj(g["entry"])["class"] = "E-XX")));
        Assert.NotEmpty(GlyphSchema.Validate(Mutate(valid, g => PackBuilder.Obj(PackBuilder.Arr(g["keypoints"])[0])["t"] = 1.5)));
        Assert.NotEmpty(GlyphSchema.Validate(Mutate(valid, g => PackBuilder.Obj(PackBuilder.Arr(g["strokes"])[0])["order"] = 0)));
        Assert.NotEmpty(GlyphSchema.Validate(Mutate(valid, g => PackBuilder.Obj(g["entry"])["point"] = JsonNode.Parse("[99.0, 0.0]"))));
        Assert.NotEmpty(GlyphSchema.Validate(Mutate(valid, g => PackBuilder.Obj(g["animation"])["startDot"] = "middle")));
        Assert.NotEmpty(GlyphSchema.Validate(Mutate(valid, g => PackBuilder.Obj(g["meta"]).Remove("reviewed"))));
    }

    [Fact]
    public void Glyph_schema_requires_exactly_one_geometry_key_per_segment_with_the_right_point_count()
    {
        string valid = Repo.ReadText(Repo.StyleDirectory + "/glyphs/i.default.json");
        static JsonObject Segment(JsonNode g, int i) =>
            PackBuilder.Obj(PackBuilder.Arr(PackBuilder.Obj(PackBuilder.Arr(g["strokes"])[0])["segments"])[i]);

        Assert.NotEmpty(GlyphSchema.Validate(Mutate(valid, g => Segment(g, 0).Remove("l"))));
        Assert.NotEmpty(GlyphSchema.Validate(Mutate(valid, g => Segment(g, 0)["c"] = JsonNode.Parse(Segment(g, 1)["c"]!.ToJsonString()))));
        Assert.NotEmpty(GlyphSchema.Validate(Mutate(valid, g => PackBuilder.Arr(Segment(g, 1)["c"]).RemoveAt(3))));
        Assert.NotEmpty(GlyphSchema.Validate(Mutate(valid, g => PackBuilder.Arr(Segment(g, 0)["l"]).Add(JsonNode.Parse("[0.0, 0.0]")))));
        Assert.NotEmpty(GlyphSchema.Validate(Mutate(valid, g => Segment(g, 0)["label"] = "no-dot")));
    }

    [Fact]
    public void Style_and_labels_schemas_reject_structural_mistakes()
    {
        string style = Repo.ReadText(Repo.StyleDirectory + "/style.json");

        Assert.NotEmpty(StyleSchema.Validate(Mutate(style, s => s["colour"] = "blue")));
        Assert.NotEmpty(StyleSchema.Validate(Mutate(style, s => s.AsObject().Remove("joins"))));
        Assert.NotEmpty(StyleSchema.Validate(Mutate(style, s => PackBuilder.Obj(s["lines"])["xHeight"] = 1.5)));
        Assert.NotEmpty(StyleSchema.Validate(Mutate(style, s => PackBuilder.Obj(s["displayName"]).Remove("ru"))));
        Assert.NotEmpty(StyleSchema.Validate(Mutate(style, s => s["role"] = "teacher")));
        Assert.NotEmpty(StyleSchema.Validate(Mutate(style, s => s["slantDeg"] = 80.0)));
        Assert.Empty(StyleSchema.Validate(Mutate(style, s => s["slantDeg"] = null)));
        Assert.NotEmpty(LabelsSchema.Validate("{ \"a.up\": { } }"));
        Assert.NotEmpty(LabelsSchema.Validate("{ \"not a key\": { \"text\": \"x\" } }"));
        Assert.Empty(LabelsSchema.Validate("{ \"a.up\": { \"text\": \"up\" } }"));
    }

    private static string Mutate(string json, Action<JsonNode> edit)
    {
        JsonNode node = JsonNode.Parse(json)!;
        edit(node);
        return node.ToJsonString();
    }
}
