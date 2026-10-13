using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using PenForLang.Core;

namespace PenForLang.Glyphs.Loading;

/// <summary>
/// Загрузчик стиль-пакета через порт <see cref="IContentSource"/> (без файлового ввода-вывода в домене).
/// Читает <c>style.json</c>, <c>glyphs/*.json</c>, <c>labels/*.json</c>, проверяет структуру и инварианты
/// (ARCHITECTURE.md §3.2.7) и собирает <see cref="StylePack"/>. Все замечания собираются разом, с путём к полю.
/// </summary>
public static partial class StyleLoader
{
    /// <summary>Файл описания стиля внутри каталога пакета.</summary>
    public const string StyleFileName = "style.json";

    /// <summary>Каталог глифов внутри пакета.</summary>
    public const string GlyphsDirectory = "glyphs";

    /// <summary>Каталог словесных меток внутри пакета.</summary>
    public const string LabelsDirectory = "labels";

    private const long MaxFileBytes = 1L << 20;

    private static readonly string[] RequiredLabelLanguages = ["ru", "en"];

    [GeneratedRegex(@"^[A-Za-z0-9]+(\.[A-Za-z0-9]+)+$")]
    private static partial Regex LabelKeyPattern();

    [GeneratedRegex(@"^[a-z]{2}\.json$")]
    private static partial Regex LabelFileName();

    /// <summary>
    /// Загружает и проверяет пакет. Не бросает исключений из-за содержимого пакета: все проблемы возвращаются
    /// в <see cref="StyleLoadResult.Issues"/>.
    /// </summary>
    /// <param name="source">Источник контента.</param>
    /// <param name="styleDirectory">Каталог пакета относительно корня источника (<c>styles/zaner-bloser</c>); пустая строка — корень.</param>
    /// <exception cref="ArgumentException">Каталог — некорректный путь контента.</exception>
    public static StyleLoadResult Load(IContentSource source, string styleDirectory)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(styleDirectory);
        ContentPath.Validate(styleDirectory, allowEmpty: true);

        string prefix = styleDirectory.Length == 0 ? string.Empty : styleDirectory + "/";
        var issues = new List<ValidationIssue>();

        StyleManifest? manifest = LoadManifest(source, prefix, styleDirectory, issues);
        var glyphs = LoadGlyphs(source, prefix, manifest, issues, out Dictionary<string, List<string>> labelUsage);
        CheckPackage(manifest, glyphs, issues);
        var labels = LoadLabels(source, prefix, labelUsage, issues);

        bool hasErrors = issues.Any(i => i.Severity == IssueSeverity.Error);
        StylePack? pack = !hasErrors && manifest is not null
            ? new StylePack(manifest, glyphs.Select(g => g.Glyph), labels)
            : null;
        return new StyleLoadResult(pack, [.. issues]);
    }

    /// <summary>Загружает пакет или бросает <see cref="StyleLoadException"/> со всеми замечаниями.</summary>
    /// <param name="source">Источник контента.</param>
    /// <param name="styleDirectory">Каталог пакета относительно корня источника.</param>
    /// <exception cref="StyleLoadException">Пакет содержит ошибки.</exception>
    public static StylePack LoadOrThrow(IContentSource source, string styleDirectory)
    {
        StyleLoadResult result = Load(source, styleDirectory);
        return result.Pack ?? throw new StyleLoadException(result.Issues);
    }

    private static StyleManifest? LoadManifest(IContentSource source, string prefix, string directory, List<ValidationIssue> issues)
    {
        var sink = new IssueSink(StyleFileName, issues);
        using JsonDocument? document = ReadJson(source, prefix + StyleFileName, sink);
        if (document is null)
        {
            return null;
        }

        StyleManifest? manifest = StyleManifestParser.Parse(document.RootElement, sink);
        if (manifest is not null && directory.Length > 0)
        {
            string name = directory[(directory.LastIndexOf('/') + 1)..];
            if (!string.Equals(name, manifest.Id.Value, StringComparison.Ordinal))
            {
                sink.Error("id", "style.id.directory", $"style id '{manifest.Id.Value}' does not match the package directory name '{name}'");
            }
        }

        return manifest;
    }

    private sealed record LoadedGlyph(string File, Glyph Glyph);

    private static List<LoadedGlyph> LoadGlyphs(
        IContentSource source,
        string prefix,
        StyleManifest? manifest,
        List<ValidationIssue> issues,
        out Dictionary<string, List<string>> labelUsage)
    {
        labelUsage = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var result = new List<LoadedGlyph>();
        string glyphsPrefix = prefix + GlyphsDirectory + "/";

        foreach (string full in source.List(prefix + GlyphsDirectory))
        {
            string relative = full[prefix.Length..];
            string name = relative[(GlyphsDirectory.Length + 1)..];
            var sink = new IssueSink(relative, issues);
            if (name.Contains('/', StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.Ordinal))
            {
                sink.Warning(string.Empty, "pack.glyphs.unexpected", "unexpected file in the glyphs directory (only glyphs/<char>.<variant>.json are read)");
                continue;
            }

            using JsonDocument? document = ReadJson(source, glyphsPrefix + name, sink);
            if (document is null)
            {
                continue;
            }

            Glyph? glyph = GlyphParser.Parse(document.RootElement, sink);
            if (glyph is null)
            {
                continue;
            }

            string expectedName = $"{glyph.Symbol}.{glyph.Variant}.json";
            if (!string.Equals(name, expectedName, StringComparison.Ordinal))
            {
                sink.Error("id", "glyph.file.name", $"file name '{name}' does not match glyph id '{glyph.Id}' (expected '{expectedName}')");
            }

            if (manifest is not null)
            {
                GlyphValidator.Validate(glyph, manifest, sink);
            }

            CollectLabels(glyph, relative, labelUsage);
            result.Add(new LoadedGlyph(relative, glyph));
        }

        return result;
    }

    private static void CollectLabels(Glyph glyph, string file, Dictionary<string, List<string>> usage)
    {
        void Collect(GlyphPath? path, string pathName)
        {
            if (path is null)
            {
                return;
            }

            for (int i = 0; i < path.Segments.Length; i++)
            {
                if (path.Segments[i].Label is { } key)
                {
                    if (!usage.TryGetValue(key, out List<string>? where))
                    {
                        where = [];
                        usage[key] = where;
                    }

                    where.Add($"{file}: {pathName}[{i}]");
                }
            }
        }

        Collect(glyph.LeadIn, "leadIn.segments");
        for (int i = 0; i < glyph.Strokes.Length; i++)
        {
            Collect(glyph.Strokes[i].Path, $"strokes[{i}].segments");
        }

        for (int i = 0; i < glyph.Deferred.Length; i++)
        {
            Collect(glyph.Deferred[i].Path, $"deferred[{i}].segments");
        }

        Collect(glyph.LeadOut, "leadOut.segments");
    }

    private static void CheckPackage(StyleManifest? manifest, List<LoadedGlyph> glyphs, List<ValidationIssue> issues)
    {
        if (manifest is null)
        {
            return;
        }

        var sink = new IssueSink(StyleFileName, issues);
        if (glyphs.Count == 0)
        {
            sink.Error(string.Empty, "pack.glyphs.empty", "the package has no glyphs (glyphs/<char>.<variant>.json)");
            return;
        }

        var ids = new Dictionary<GlyphId, string>();
        foreach (LoadedGlyph loaded in glyphs)
        {
            if (!ids.TryAdd(loaded.Glyph.Id, loaded.File))
            {
                new IssueSink(loaded.File, issues).Error("id", "pack.glyph.duplicate", $"glyph id '{loaded.Glyph.Id}' is already defined in {ids[loaded.Glyph.Id]}");
            }
        }

        foreach (IGrouping<string, LoadedGlyph> bySymbol in glyphs.GroupBy(g => g.Glyph.Symbol, StringComparer.Ordinal))
        {
            if (bySymbol.All(g => g.Glyph.Variant != "default"))
            {
                new IssueSink(bySymbol.First().File, issues).Error(
                    "variant",
                    "pack.variant.default",
                    $"character '{bySymbol.Key}' has no 'default' variant (found: {string.Join(", ", bySymbol.Select(g => g.Glyph.Variant))})");
            }
        }

        for (int i = 0; i < manifest.VariantRules.Length; i++)
        {
            VariantRule rule = manifest.VariantRules[i];
            bool symbolPresent = glyphs.Any(g => g.Glyph.Symbol == rule.Symbol);
            if (symbolPresent && !glyphs.Any(g => g.Glyph.Symbol == rule.Symbol && g.Glyph.Variant == rule.Variant))
            {
                sink.Error($"variantRules[{i}].variant", "pack.variantRules.variant", $"variant rule refers to glyph '{rule.Symbol}.{rule.Variant}', which does not exist in the package");
            }
        }
    }

    private static Dictionary<string, IReadOnlyDictionary<string, string>> LoadLabels(
        IContentSource source,
        string prefix,
        Dictionary<string, List<string>> usage,
        List<ValidationIssue> issues)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        var languages = new SortedSet<string>(RequiredLabelLanguages, StringComparer.Ordinal);
        foreach (string full in source.List(prefix + LabelsDirectory))
        {
            string name = full[(prefix.Length + LabelsDirectory.Length + 1)..];
            if (LabelFileName().IsMatch(name))
            {
                languages.Add(name[..^".json".Length]);
            }
            else
            {
                new IssueSink($"{LabelsDirectory}/{name}", issues).Warning(string.Empty, "labels.unexpected", "unexpected file in the labels directory (only labels/<language>.json, e.g. ru.json, are read)");
            }
        }

        foreach (string language in languages)
        {
            string relative = $"{LabelsDirectory}/{language}.json";
            var sink = new IssueSink(relative, issues);
            if (!source.Exists(prefix + relative))
            {
                if (usage.Count > 0)
                {
                    sink.Error(string.Empty, "labels.file.missing", $"the glyphs use {usage.Count} label key(s) but the labels file for language '{language}' is missing");
                }

                continue;
            }

            using JsonDocument? document = ReadJson(source, prefix + relative, sink);
            if (document is null)
            {
                continue;
            }

            Dictionary<string, string>? labels = ParseLabels(document.RootElement, sink);
            if (labels is null)
            {
                continue;
            }

            foreach ((string key, List<string> where) in usage.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (!labels.ContainsKey(key))
                {
                    sink.Error(key, "labels.missing", $"label key '{key}' (used at {where[0]}) has no text for language '{language}'");
                }
            }

            foreach (string key in labels.Keys.Where(k => !usage.ContainsKey(k)))
            {
                sink.Warning(key, "labels.unused", $"label key '{key}' is not used by any glyph");
            }

            result[language] = labels;
        }

        return result;
    }

    private static Dictionary<string, string>? ParseLabels(JsonElement root, IssueSink sink)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            sink.Error(string.Empty, "json.type", $"expected an object but found {ObjectReader.Describe(root)}");
            return null;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        bool ok = true;
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!LabelKeyPattern().IsMatch(property.Name))
            {
                sink.Error(property.Name, "labels.key", $"label key '{property.Name}' must look like '<char>.<name>' (letters and digits)");
                ok = false;
                continue;
            }

            if (ObjectReader.Open(property.Value, property.Name, sink) is not { } o)
            {
                ok = false;
                continue;
            }

            string? text = o.String("text");
            o.String("audio", required: false);
            o.RejectUnknown();
            if (text is null)
            {
                ok = false;
                continue;
            }

            result[property.Name] = text;
        }

        return ok ? result : null;
    }

    private static JsonDocument? ReadJson(IContentSource source, string fullPath, IssueSink sink)
    {
        try
        {
            if (!source.Exists(fullPath))
            {
                sink.Error(string.Empty, "file.missing", $"file '{fullPath}' does not exist");
                return null;
            }

            using Stream stream = source.Open(fullPath);
            if (stream.CanSeek && stream.Length > MaxFileBytes)
            {
                sink.Error(string.Empty, "file.size", $"file is larger than {MaxFileBytes / 1024} KiB");
                return null;
            }

            return JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
                MaxDepth = 32,
                AllowDuplicateProperties = false,
            });
        }
        catch (JsonException ex)
        {
            string position = ex.LineNumber is { } line ? $" (line {line + 1}, column {(ex.BytePositionInLine ?? 0) + 1})" : string.Empty;
            sink.Error(string.Empty, "json.syntax", $"invalid JSON{position}: {ex.Message}");
            return null;
        }
        catch (FileNotFoundException)
        {
            sink.Error(string.Empty, "file.missing", $"file '{fullPath}' does not exist");
            return null;
        }
        catch (ArgumentException ex)
        {
            sink.Error(string.Empty, "file.path", $"file '{fullPath}' cannot be read: {ex.Message}");
            return null;
        }
    }
}
