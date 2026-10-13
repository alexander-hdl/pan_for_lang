using System.Text.Json;
using System.Text.Json.Nodes;
using PenForLang.Glyphs.Tests.Support;
using PenForLang.Tools.InkLab.Annotation;
using PenForLang.Tools.InkLab.Tests.Support;
using PenForLang.Tools.InkLab.ViewModels;

namespace PenForLang.Tools.InkLab.Tests;

/// <summary>
/// Схема <c>schemas/pfl.annotation.v1.schema.json</c> согласована с форматом, который пишет InkLab, и действительно строга
/// (как <c>SchemaConformanceTests</c> у глифов): файлы разметки проходят схему, структурные ошибки — нет.
/// </summary>
public sealed class AnnotationSchemaTests : IDisposable
{
    private static readonly MiniJsonSchema Schema = MiniJsonSchema.FromRepo("pfl.annotation.v1.schema.json");
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static AnnotateTabViewModel NewVm() => new(null, () => new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero), "A");

    /// <summary>Файл разметки, записанный настоящим редактором: слово с отрывом, отложенная перекладина, заметка.</summary>
    private string RealAnnotationJson()
    {
        string path = TestData.WriteLog(_folder.Path, "at-lift-001", TestData.AtWordLifted());
        AnnotateTabViewModel vm = NewVm();
        vm.Load(path);
        vm.SelectedLetter = 1;
        vm.ToggleDeferred(2);
        vm.ToggleTimingOpinion(2);
        vm.HandleClick(0.93, 0.5);
        vm.HandleClick(1.2, 1.48);
        vm.SetConfidence(0, Confidence.Low);
        vm.Note = "проверка";
        vm.Flags.Single(f => f.Name == QualityFlags.Hesitation).IsChecked = true;
        Assert.True(vm.Save());
        return File.ReadAllText(path + ".ann.A.json");
    }

    private static string Mutate(string json, Action<JsonNode> change)
    {
        JsonNode node = JsonNode.Parse(json)!;
        change(node);
        return node.ToJsonString();
    }

    [Fact]
    public void The_schema_file_is_valid_json_with_the_expected_id()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Repo.Path("schemas", "pfl.annotation.v1.schema.json")));

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", document.RootElement.GetProperty("$schema").GetString());
        Assert.Equal("urn:pfl:schema:annotation:1", document.RootElement.GetProperty("$id").GetString());
        Assert.Equal("pfl.annotation/1", document.RootElement.GetProperty("title").GetString());
        string description = document.RootElement.GetProperty("description").GetString()!;
        Assert.Contains("inkattempt-after-last-restart", description, StringComparison.Ordinal);
        Assert.Contains("JUNCTION CONVENTION", description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_written_by_the_editor_conforms_to_the_schema()
    {
        Assert.Empty(Schema.Validate(RealAnnotationJson()));
    }

    [Fact]
    public void Serialized_annotations_of_every_shape_conform_to_the_schema()
    {
        string path = TestData.WriteLog(_folder.Path, "joined", TestData.AtWordJoined());
        AnnotateTabViewModel connected = NewVm();
        connected.Load(path);
        connected.ToggleDeferred(1);
        connected.HandleClick(1.0, 0.52, shift: true);
        connected.Save();
        Assert.Empty(Schema.Validate(File.ReadAllText(path + ".ann.A.json")));

        string rejectPath = TestData.WriteLog(_folder.Path, "reject", TestData.LetterI());
        AnnotateTabViewModel reject = NewVm();
        reject.Load(rejectPath);
        reject.IsUsable = false;
        reject.Save();
        Assert.Empty(Schema.Validate(File.ReadAllText(rejectPath + ".ann.A.json")));
    }

    [Fact]
    public void The_schema_rejects_structural_mistakes()
    {
        string valid = RealAnnotationJson();

        Assert.NotEmpty(Schema.Validate(Mutate(valid, n => n.AsObject().Remove("indexing"))));
        Assert.NotEmpty(Schema.Validate(Mutate(valid, n => n.AsObject().Remove("penlogSha256"))));
        Assert.NotEmpty(Schema.Validate(Mutate(valid, n => n["penlogSha256"] = "XYZ")));
        Assert.NotEmpty(Schema.Validate(Mutate(valid, n => n["schema"] = "pfl.annotation/2")));
        Assert.NotEmpty(Schema.Validate(Mutate(valid, n => n["indexing"] = "all-strokes")));
        Assert.NotEmpty(Schema.Validate(Mutate(valid, n => n["colour"] = "blue"))); // неизвестное поле
        Assert.NotEmpty(Schema.Validate(Mutate(valid, n => n["annotator"] = string.Empty)));
        Assert.NotEmpty(Schema.Validate(Mutate(valid, n => n["strokes"]![0]!.AsObject().Remove("t1"))));
        Assert.NotEmpty(Schema.Validate(Mutate(valid, n => n["junctions"]![0]!["kind"] = "glued")));
        Assert.NotEmpty(Schema.Validate(Mutate(valid, n => n["junctions"]![0]!["confidence"] = "sure")));
        Assert.NotEmpty(Schema.Validate(Mutate(valid, n => n["junctions"]![0]!["end"]!.AsObject().Remove("t"))));
        Assert.NotEmpty(Schema.Validate(Mutate(valid, n => n["junctions"]![0]!["start"]!["sample"] = -1)));
        Assert.NotEmpty(Schema.Validate(Mutate(valid, n => n["junctions"]![0]!["end"]!["extra"] = 1)));
        Assert.NotEmpty(Schema.Validate(Mutate(valid, n => n["deferred"]![0]!["kind"] = "blob")));
        Assert.NotEmpty(Schema.Validate(Mutate(valid, n => n["deferred"]![0]!["timing"] = "later")));
        Assert.NotEmpty(Schema.Validate(Mutate(valid, n => n["quality"]!["flags"]![0] = "mystery")));
        Assert.NotEmpty(Schema.Validate(Mutate(valid, n => n["quality"]!.AsObject().Remove("usable"))));
    }

    [Fact]
    public void Every_property_the_editor_writes_is_known_to_the_schema_and_the_reader_rejects_unknown_ones()
    {
        string valid = RealAnnotationJson();

        // Схема закрытая (additionalProperties: false везде), поэтому «записанное редактором проходит схему» уже означает полное соответствие.
        Assert.Empty(Schema.Validate(valid));
        Assert.Contains("\"timingOpinion\"", valid, StringComparison.Ordinal);
        Assert.Throws<AnnotationFormatException>(() => AnnotationJson.Deserialize(Mutate(valid, n => n["colour"] = "blue")));
        Assert.Empty(Schema.Validate(AnnotationJson.Serialize(AnnotationJson.Deserialize(valid))));
    }
}
