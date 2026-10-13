using System.Diagnostics.CodeAnalysis;

namespace PenForLang.Glyphs;

/// <summary>
/// Идентификатор глифа вида <c>&lt;styleId&gt;/&lt;char&gt;.&lt;variant&gt;</c>, например <c>zaner-bloser/a.default</c>
/// (схема <c>pfl.glyph/1</c>, ARCHITECTURE.md §3.2.4).
/// </summary>
public readonly record struct GlyphId(string StyleId, string Symbol, string Variant)
{
    /// <summary>Строковое представление в формате схемы.</summary>
    public override string ToString() => $"{StyleId}/{Symbol}.{Variant}";

    /// <summary>Разбирает идентификатор; возвращает <c>false</c> для некорректной строки.</summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out GlyphId id)
    {
        id = default;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        int slash = text.IndexOf('/', StringComparison.Ordinal);
        int dot = text.LastIndexOf('.');
        if (slash <= 0 || dot <= slash + 1 || dot == text.Length - 1)
        {
            return false;
        }

        string variant = text[(dot + 1)..];
        if (variant.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        id = new GlyphId(text[..slash], text[(slash + 1)..dot], variant);
        return true;
    }

    /// <summary>Разбирает идентификатор или бросает исключение.</summary>
    /// <exception cref="FormatException">Строка не соответствует формату.</exception>
    public static GlyphId Parse(string text) =>
        TryParse(text, out GlyphId id) ? id : throw new FormatException($"Invalid glyph id: '{text}'.");
}
