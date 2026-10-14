using System.Collections.Immutable;
using PenForLang.Core.Geometry;

namespace PenForLang.Glyphs;

/// <summary>
/// Сборка эталона слова из глифов и соединений (ARCHITECTURE.md §3.2.4, §6.4.4):
/// <list type="bullet">
/// <item>расстановка: следующий глиф стоит в <c>u = advance + letterSpacing</c> предыдущего, <c>u = 0</c> — левый край тела первой буквы;</item>
/// <item>соединение — по правилу стиля (<see cref="JoinPolicy"/>): класс выхода левой × класс входа правой; геометрию строит
/// <see cref="JoinGenerator"/> от точки и направления выхода к точке и направлению входа; разрыв (<c>break</c>, нет правила)
/// даёт ожидаемый отрыв без геометрии;</item>
/// <item>входящий штрих первой и выходящий штрих последней буквы остаются (подход и уход слова); входящие и выходящие штрихи
/// внутренних букв заменены соединениями;</item>
/// <item>отложенные штрихи слова стоят в конце (после основных), в порядке букв.</item>
/// </list>
/// Чистая детерминированная функция.
/// </summary>
public static class WordTemplateBuilder
{
    private const double TallEntryHeight = 1.0 + 1e-6;

    /// <summary>Выбирает глифы для букв слова с учётом <c>variantRules</c> (например, <c>e</c> после высокого выхода).</summary>
    /// <param name="pack">Стиль-пакет.</param>
    /// <param name="text">Слово из букв стиля (без пробелов).</param>
    /// <exception cref="ArgumentException">Слово пусто или содержит букву без глифа.</exception>
    public static ImmutableArray<Glyph> ResolveGlyphs(StylePack pack, string text)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            throw new ArgumentException("A word needs at least one letter.", nameof(text));
        }

        var glyphs = ImmutableArray.CreateBuilder<Glyph>(text.Length);
        ExitClass? previousExit = null;
        foreach (char c in text)
        {
            string symbol = c.ToString();
            Glyph glyph = pack.GetGlyph(symbol);
            if (previousExit is { } exit)
            {
                foreach (VariantRule rule in pack.Manifest.VariantRules)
                {
                    if (rule.Symbol == symbol && rule.PrevExit == exit && pack.TryGetGlyph(symbol, rule.Variant, out Glyph? variant))
                    {
                        glyph = variant;
                        break;
                    }
                }
            }

            glyphs.Add(glyph);
            previousExit = glyph.Exit.Class;
        }

        return glyphs.MoveToImmutable();
    }

    /// <summary>Собирает эталон слова из стиль-пакета.</summary>
    /// <param name="pack">Стиль-пакет.</param>
    /// <param name="text">Слово.</param>
    public static WordTemplate Build(StylePack pack, string text)
    {
        ImmutableArray<Glyph> glyphs = ResolveGlyphs(pack, text);
        return Build(pack.Manifest, glyphs, text);
    }

    /// <summary>
    /// Собирает эталон слова из заданной последовательности глифов (для гипотез: позиция заменена другим глифом).
    /// </summary>
    /// <param name="style">Стиль: расстановка, правила соединений.</param>
    /// <param name="glyphs">Глифы по порядку (не пусто).</param>
    /// <param name="text">Текст слова; <c>null</c> — символы глифов подряд.</param>
    public static WordTemplate Build(StyleManifest style, IReadOnlyList<Glyph> glyphs, string? text = null)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(glyphs);
        if (glyphs.Count == 0)
        {
            throw new ArgumentException("A word needs at least one letter.", nameof(glyphs));
        }

        text ??= string.Concat(glyphs.Select(g => g.Symbol));

        var letters = ImmutableArray.CreateBuilder<WordLetter>(glyphs.Count);
        double offset = 0.0;
        for (int k = 0; k < glyphs.Count; k++)
        {
            Glyph g = glyphs[k];
            var shift = new Vec2(offset, 0.0);
            letters.Add(new WordLetter(k, g, offset, g.Entry.Point + shift, g.Entry.Tangent, g.Exit.Point + shift, g.Exit.Tangent));
            offset += g.Advance + style.LetterSpacing;
        }

        ImmutableArray<WordLetter> placed = letters.MoveToImmutable();
        var joins = ImmutableArray.CreateBuilder<WordJoin>(Math.Max(0, placed.Length - 1));
        for (int k = 0; k + 1 < placed.Length; k++)
        {
            joins.Add(MakeJoin(style, placed[k], placed[k + 1]));
        }

        ImmutableArray<WordJoin> joined = joins.ToImmutable();
        ImmutableArray<WordPenStroke> penStrokes = AssemblePenStrokes(placed, joined);
        ImmutableArray<WordDeferred> deferred = CollectDeferred(placed);
        return new WordTemplate(text, style, placed, joined, penStrokes, deferred);
    }

    /// <summary>Ищет правило соединения: класс выхода левой буквы × класс входа правой. <c>null</c> — правила нет (разрыв).</summary>
    /// <param name="policy">Правила стиля.</param>
    /// <param name="exit">Класс выхода.</param>
    /// <param name="entry">Класс входа.</param>
    public static JoinRule? FindRule(JoinPolicy policy, ExitClass exit, EntryClass entry)
    {
        ArgumentNullException.ThrowIfNull(policy);
        foreach (JoinRule rule in policy.Rules)
        {
            if (rule.Exit == exit && (rule.AnyEntry || rule.Entry.Contains(entry)))
            {
                return rule;
            }
        }

        return null;
    }

    private static WordJoin MakeJoin(StyleManifest style, WordLetter left, WordLetter right)
    {
        GlyphEntry entry = right.Glyph.Entry;
        JoinRule? rule = FindRule(style.Joins, left.Glyph.Exit.Class, entry.Class);
        bool tall = entry.Class == style.Joins.TallEntryClass || (entry.Height ?? 1.0) > TallEntryHeight;
        GlyphPath? path = rule is null ? null : JoinGenerator.Generate(rule, left.ExitPoint, left.ExitTangent, right.EntryPoint, right.EntryTangent);
        JoinShape shape = path is null ? JoinShape.Break : rule!.Shape;
        return new WordJoin(
            left.Index,
            rule,
            rule?.Family,
            shape,
            tall,
            rule?.High ?? false,
            path,
            left.ExitPoint,
            left.ExitTangent,
            right.EntryPoint,
            right.EntryTangent);
    }

    private static ImmutableArray<WordPenStroke> AssemblePenStrokes(ImmutableArray<WordLetter> letters, ImmutableArray<WordJoin> joins)
    {
        var strokes = ImmutableArray.CreateBuilder<WordPenStroke>();
        var parts = new List<WordPart>();
        double distance = 0.0;

        void Add(WordPartKind kind, int letter, string? strokeId, GlyphPath path)
        {
            parts.Add(new WordPart(kind, letter, strokeId, path, distance, distance + path.Length));
            distance += path.Length;
        }

        void Flush()
        {
            if (parts.Count > 0)
            {
                strokes.Add(new WordPenStroke(strokes.Count, [.. parts]));
                parts.Clear();
                distance = 0.0;
            }
        }

        for (int k = 0; k < letters.Length; k++)
        {
            WordLetter letter = letters[k];
            var shift = new Vec2(letter.OffsetU, 0.0);
            ImmutableArray<GlyphStroke> glyphStrokes = [.. letter.Glyph.Strokes.OrderBy(s => s.Order)];
            for (int s = 0; s < glyphStrokes.Length; s++)
            {
                // Начало штриха слова: первая буква, второй и далее штрих буквы, а также буква после разрыва (иначе тело продолжает соединение).
                bool continuesJoin = s == 0 && k > 0 && joins[k - 1].Path is not null;
                if (!continuesJoin)
                {
                    Flush();
                    if (s == 0 && k == 0 && letter.Glyph.LeadIn is { } lead)
                    {
                        Add(WordPartKind.LeadIn, 0, null, lead.Translated(shift));
                    }
                }

                Add(WordPartKind.Body, k, glyphStrokes[s].Id, glyphStrokes[s].Path.Translated(shift));
                bool lastStroke = s == glyphStrokes.Length - 1;
                if (lastStroke && k + 1 < letters.Length && joins[k].Path is { } join)
                {
                    Add(WordPartKind.Join, k, null, join);
                }

                if (lastStroke && k + 1 == letters.Length && letter.Glyph.LeadOut is { } leadOut)
                {
                    Add(WordPartKind.LeadOut, k, null, leadOut.Translated(shift));
                }
            }
        }

        Flush();
        return strokes.ToImmutable();
    }

    private static ImmutableArray<WordDeferred> CollectDeferred(ImmutableArray<WordLetter> letters)
    {
        var result = ImmutableArray.CreateBuilder<WordDeferred>();
        foreach (WordLetter letter in letters)
        {
            var shift = new Vec2(letter.OffsetU, 0.0);
            foreach (DeferredStroke slot in letter.Glyph.Deferred)
            {
                GlyphStroke? anchorStroke = letter.Glyph.FindStroke(slot.Anchor.StrokeId);
                Vec2 anchorPoint = (anchorStroke is null ? slot.Path.Start : anchorStroke.Path.PointAtFraction(slot.Anchor.T)) + shift;
                result.Add(new WordDeferred(
                    letter.Index,
                    slot.Id,
                    slot.Kind,
                    slot.Direction,
                    slot.Path.Translated(shift),
                    slot.Anchor.StrokeId,
                    slot.Anchor.T,
                    anchorPoint,
                    slot.Anchor.Offset));
            }
        }

        return result.ToImmutable();
    }
}
