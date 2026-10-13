using System.Text.Json;
using System.Text.Json.Nodes;
using PenForLang.Core;
using PenForLang.Glyphs.Loading;

namespace PenForLang.Glyphs.Tests.Support;

/// <summary>
/// Собирает в памяти минимальный стиль-пакет (<c>style.json</c> + глиф <c>i</c> + метки) из файлов репозитория
/// с точечными правками — основа фикстур с намеренными ошибками.
/// </summary>
internal sealed class PackBuilder
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private JsonNode _style = JsonNode.Parse(Repo.ReadText(Repo.StyleDirectory + "/style.json"))!;
    private JsonNode _glyph = JsonNode.Parse(Repo.ReadText(Repo.StyleDirectory + "/glyphs/i.default.json"))!;
    private JsonNode _ru = JsonNode.Parse(Repo.ReadText(Repo.StyleDirectory + "/labels/ru.json"))!;
    private JsonNode _en = JsonNode.Parse(Repo.ReadText(Repo.StyleDirectory + "/labels/en.json"))!;
    private string? _glyphText;

    public PackBuilder()
    {
        // Только метки глифа i: чужие ключи дали бы предупреждения «не используется».
        foreach (JsonObject labels in new[] { _ru.AsObject(), _en.AsObject() })
        {
            foreach (string key in labels.Select(p => p.Key).Where(k => !k.StartsWith("i.", StringComparison.Ordinal)).ToList())
            {
                labels.Remove(key);
            }
        }
    }

    private string _glyphFile = "i.default.json";
    private bool _withGlyph = true;

    public string Directory { get; private set; } = Repo.StyleDirectory;

    public JsonObject Style => _style.AsObject();

    public JsonObject Glyph => _glyph.AsObject();

    public JsonObject En => _en.AsObject();

    public JsonObject Ru => _ru.AsObject();

    public PackBuilder EditStyle(Action<JsonObject> edit)
    {
        edit(Style);
        return this;
    }

    public PackBuilder EditGlyph(Action<JsonObject> edit)
    {
        edit(Glyph);
        return this;
    }

    public PackBuilder EditEnLabels(Action<JsonObject> edit)
    {
        edit(En);
        return this;
    }

    public PackBuilder RawGlyphText(string text)
    {
        _glyphText = text;
        return this;
    }

    public PackBuilder GlyphFileName(string name)
    {
        _glyphFile = name;
        return this;
    }

    public PackBuilder WithoutGlyphs()
    {
        _withGlyph = false;
        return this;
    }

    public PackBuilder InDirectory(string directory)
    {
        Directory = directory;
        return this;
    }

    public StyleLoadResult Load() => StyleLoader.Load(BuildSource(), Directory);

    public InMemoryContentSource BuildSource()
    {
        var source = new InMemoryContentSource();
        string prefix = Directory + "/";
        source.Add(prefix + "style.json", Serialize(_style));
        if (_withGlyph)
        {
            source.Add(prefix + "glyphs/" + _glyphFile, _glyphText ?? Serialize(_glyph));
        }

        source.Add(prefix + "labels/ru.json", Serialize(_ru));
        source.Add(prefix + "labels/en.json", Serialize(_en));
        return source;
    }

    public static JsonObject Obj(JsonNode? node) => node!.AsObject();

    public static JsonArray Arr(JsonNode? node) => node!.AsArray();

    /// <summary>Сдвигает точку <c>[u, v]</c>.</summary>
    public static void Shift(JsonNode? point, double du, double dv)
    {
        JsonArray array = Arr(point);
        array[0] = (double)array[0]! + du;
        array[1] = (double)array[1]! + dv;
    }

    private static string Serialize(JsonNode node) => node.ToJsonString(Indented);
}
