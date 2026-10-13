using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using PenForLang.Core;

namespace PenForLang.App.Wpf.Lesson;

/// <summary>
/// Каталог строк интерфейса одного языка (<c>content/locales/&lt;lang&gt;.json</c>, схема <c>pfl.locale/1</c>, ARCHITECTURE.md §8.2).
/// Домен выдаёт только ключи и параметры; тексты берутся отсюда. Подстановка параметров — <c>{имя}</c>.
/// </summary>
public sealed partial class LocaleCatalog
{
    private readonly ImmutableDictionary<string, LocaleString> _strings;

    private LocaleCatalog(string language, ImmutableDictionary<string, LocaleString> strings)
    {
        Language = language;
        _strings = strings;
    }

    /// <summary>Код языка (<c>ru</c>, <c>en</c>).</summary>
    public string Language { get; }

    /// <summary>Все строки: ключ → запись.</summary>
    public IReadOnlyDictionary<string, LocaleString> Strings => _strings;

    /// <summary>Загружает каталог языка из источника контента.</summary>
    /// <exception cref="InvalidDataException">Файла нет или он не соответствует схеме.</exception>
    public static LocaleCatalog Load(IContentSource source, string language)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(language);
        string path = "locales/" + language + ".json";
        if (!source.Exists(path))
        {
            throw new InvalidDataException($"Locale '{path}' not found.");
        }

        using Stream stream = source.Open(path);
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Разбирает текст каталога.</summary>
    /// <exception cref="InvalidDataException">Текст не соответствует схеме.</exception>
    public static LocaleCatalog Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schema", out JsonElement schema)
                || !string.Equals(schema.GetString(), "pfl.locale/1", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Locale schema must be 'pfl.locale/1'.");
            }

            if (!root.TryGetProperty("lang", out JsonElement lang) || lang.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(lang.GetString()))
            {
                throw new InvalidDataException("Locale needs 'lang'.");
            }

            if (!root.TryGetProperty("strings", out JsonElement strings) || strings.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Locale needs 'strings'.");
            }

            ImmutableDictionary<string, LocaleString>.Builder map = ImmutableDictionary.CreateBuilder<string, LocaleString>(StringComparer.Ordinal);
            foreach (JsonProperty property in strings.EnumerateObject())
            {
                if (!property.Value.TryGetProperty("text", out JsonElement text) || text.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException($"String '{property.Name}' needs 'text'.");
                }

                bool child = property.Value.TryGetProperty("child", out JsonElement childFlag) && childFlag.ValueKind == JsonValueKind.True;
                map[property.Name] = new LocaleString(text.GetString()!, child);
            }

            return new LocaleCatalog(lang.GetString()!, map.ToImmutable());
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Locale is not valid JSON: " + ex.Message, ex);
        }
    }

    /// <summary>Есть ли ключ.</summary>
    public bool Contains(string key) => _strings.ContainsKey(key);

    /// <summary>Текст по ключу с подстановкой параметров; если ключа нет — сам ключ (видимая, но не падающая ошибка).</summary>
    /// <param name="key">Ключ.</param>
    /// <param name="parameters">Параметры <c>{имя}</c> → значение; может быть <c>null</c>.</param>
    public string Get(string key, IReadOnlyDictionary<string, string>? parameters = null)
    {
        if (!_strings.TryGetValue(key, out LocaleString? entry))
        {
            return key;
        }

        if (parameters is null || parameters.Count == 0)
        {
            return entry.Text;
        }

        return PlaceholderPattern().Replace(entry.Text, match =>
            parameters.TryGetValue(match.Groups[1].Value, out string? value) ? value : match.Value);
    }

    /// <summary>Имена параметров строки (<c>{имя}</c>), по порядку и без повторов.</summary>
    public static IReadOnlyList<string> Placeholders(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return [.. PlaceholderPattern().Matches(text).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)];
    }

    [GeneratedRegex(@"\{([A-Za-z][A-Za-z0-9]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();
}

/// <summary>Строка каталога.</summary>
/// <param name="Text">Текст с параметрами <c>{имя}</c>.</param>
/// <param name="Child">Фраза для ребёнка: не больше 12 слов (METHODOLOGY.md §7.6).</param>
public sealed record LocaleString(string Text, bool Child);
