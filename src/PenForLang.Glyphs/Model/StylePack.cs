using System.Diagnostics.CodeAnalysis;

namespace PenForLang.Glyphs;

/// <summary>
/// Загруженный и проверенный стиль-пакет: описание стиля, глифы и словесные метки (ARCHITECTURE.md §3.2).
/// Создаётся <see cref="Loading.StyleLoader"/>; неизменяем.
/// </summary>
public sealed class StylePack
{
    private const string PhraseSeparator = " — ";

    /// <summary>Создаёт пакет. Не проверяет согласованность — для этого есть <see cref="Loading.StyleLoader"/>.</summary>
    /// <param name="manifest">Описание стиля.</param>
    /// <param name="glyphs">Глифы.</param>
    /// <param name="labels">Метки: язык → ключ → текст.</param>
    public StylePack(
        StyleManifest manifest,
        IEnumerable<Glyph> glyphs,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> labels)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(glyphs);
        ArgumentNullException.ThrowIfNull(labels);
        Manifest = manifest;
        var sorted = new SortedDictionary<string, Glyph>(StringComparer.Ordinal);
        foreach (Glyph glyph in glyphs)
        {
            sorted.Add(glyph.Id.ToString(), glyph);
        }

        Glyphs = [.. sorted.Values];
        Labels = labels;
    }

    /// <summary>Описание стиля.</summary>
    public StyleManifest Manifest { get; }

    /// <summary>Все глифы, отсортированные по идентификатору (<see cref="StringComparer.Ordinal"/>).</summary>
    public IReadOnlyList<Glyph> Glyphs { get; }

    /// <summary>Словесные метки: язык → ключ → текст.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Labels { get; }

    /// <summary>Ищет глиф по символу и варианту.</summary>
    /// <param name="symbol">Символ (<c>a</c>).</param>
    /// <param name="variant">Вариант (<c>default</c>).</param>
    /// <param name="glyph">Найденный глиф.</param>
    public bool TryGetGlyph(string symbol, string variant, [NotNullWhen(true)] out Glyph? glyph)
    {
        glyph = Glyphs.FirstOrDefault(g =>
            string.Equals(g.Symbol, symbol, StringComparison.Ordinal) && string.Equals(g.Variant, variant, StringComparison.Ordinal));
        return glyph is not null;
    }

    /// <summary>Ищет глиф по идентификатору.</summary>
    /// <param name="id">Идентификатор.</param>
    /// <param name="glyph">Найденный глиф.</param>
    public bool TryGetGlyph(GlyphId id, [NotNullWhen(true)] out Glyph? glyph)
    {
        glyph = Glyphs.FirstOrDefault(g => g.Id == id);
        return glyph is not null;
    }

    /// <summary>Возвращает глиф или бросает исключение.</summary>
    /// <param name="symbol">Символ.</param>
    /// <param name="variant">Вариант.</param>
    /// <exception cref="KeyNotFoundException">Глифа нет.</exception>
    public Glyph GetGlyph(string symbol, string variant = "default") =>
        TryGetGlyph(symbol, variant, out Glyph? glyph)
            ? glyph
            : throw new KeyNotFoundException($"Style '{Manifest.Id}' has no glyph '{symbol}.{variant}'.");

    /// <summary>Текст метки на языке <paramref name="language"/>; <c>null</c>, если ключа нет.</summary>
    /// <param name="language">Язык (<c>ru</c>, <c>en</c>).</param>
    /// <param name="key">Ключ метки (<c>a.backAround</c>).</param>
    public string? GetLabelText(string language, string key) =>
        Labels.TryGetValue(language, out IReadOnlyDictionary<string, string>? map) && map.TryGetValue(key, out string? text) ? text : null;

    /// <summary>
    /// Собирает словесную метку буквы из меток сегментов: входящий штрих, основные штрихи, отложенные; подряд идущие
    /// одинаковые ключи сливаются; части разделены тире. Выходящий штрих (<see cref="Glyph.LeadOut"/>) не входит.
    /// Ключи без текста на языке пропускаются.
    /// </summary>
    /// <param name="glyph">Глиф.</param>
    /// <param name="language">Язык метки (<c>ru</c>, <c>en</c>).</param>
    public string BuildPhrase(Glyph glyph, string language)
    {
        ArgumentNullException.ThrowIfNull(glyph);
        var parts = new List<string>();
        string? previous = null;

        void Add(GlyphPath? path)
        {
            if (path is null)
            {
                return;
            }

            foreach (GlyphSegment segment in path.Segments)
            {
                if (segment.Label is null || string.Equals(segment.Label, previous, StringComparison.Ordinal))
                {
                    continue;
                }

                previous = segment.Label;
                if (GetLabelText(language, segment.Label) is { } text)
                {
                    parts.Add(text);
                }
            }
        }

        Add(glyph.LeadIn);
        foreach (PenStroke stroke in glyph.PenStrokes)
        {
            Add(stroke.Path);
        }

        return string.Join(PhraseSeparator, parts);
    }
}
