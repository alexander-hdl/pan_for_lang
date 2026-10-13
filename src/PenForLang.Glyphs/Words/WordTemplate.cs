using System.Collections.Immutable;
using PenForLang.Core.Geometry;

namespace PenForLang.Glyphs;

/// <summary>Вид участка основного штриха слова.</summary>
public enum WordPartKind
{
    /// <summary>Входящий штрих первой буквы слова (подход к телу).</summary>
    LeadIn,

    /// <summary>Тело буквы (основной штрих глифа).</summary>
    Body,

    /// <summary>Соединение между буквами (сгенерированная геометрия).</summary>
    Join,

    /// <summary>Выходящий штрих последней буквы слова.</summary>
    LeadOut,
}

/// <summary>
/// Участок основного штриха слова: тело буквы, соединение, подход или выход. Координаты — линейка слова
/// (<c>u = 0</c> — левый край тела первой буквы, §3.2.4).
/// </summary>
/// <param name="Kind">Вид.</param>
/// <param name="Letter">Для тела — индекс буквы; для соединения — индекс левой буквы; для подхода — 0; для выхода — последняя буква.</param>
/// <param name="StrokeId">Идентификатор основного штриха глифа (для тела) или <c>null</c>.</param>
/// <param name="Path">Траектория участка в координатах слова.</param>
/// <param name="FromDistance">Расстояние от начала штриха слова до начала участка.</param>
/// <param name="ToDistance">Расстояние от начала штриха слова до конца участка.</param>
public sealed record WordPart(WordPartKind Kind, int Letter, string? StrokeId, GlyphPath Path, double FromDistance, double ToDistance);

/// <summary>Основной штрих слова: то, что пишется одним касанием пера (подход, тела букв и соединения между ними).</summary>
public sealed class WordPenStroke
{
    /// <summary>Создаёт штрих слова из участков, идущих подряд.</summary>
    /// <param name="index">Номер штриха в слове, с 0.</param>
    /// <param name="parts">Участки в порядке написания (не пусто).</param>
    public WordPenStroke(int index, ImmutableArray<WordPart> parts)
    {
        if (parts.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A pen stroke needs at least one part.", nameof(parts));
        }

        Index = index;
        Parts = parts;
        Path = new GlyphPath(parts.SelectMany(p => p.Path.Segments));
    }

    /// <summary>Номер штриха в слове, с 0.</summary>
    public int Index { get; }

    /// <summary>Участки по порядку.</summary>
    public ImmutableArray<WordPart> Parts { get; }

    /// <summary>Полная траектория штриха (все участки подряд).</summary>
    public GlyphPath Path { get; }

    /// <summary>Находит участок тела буквы <paramref name="letter"/> (штрих глифа <paramref name="strokeId"/>), если он в этом штрихе.</summary>
    /// <param name="letter">Индекс буквы.</param>
    /// <param name="strokeId">Идентификатор штриха глифа.</param>
    public WordPart? FindBody(int letter, string strokeId) =>
        Parts.FirstOrDefault(p => p.Kind == WordPartKind.Body && p.Letter == letter && string.Equals(p.StrokeId, strokeId, StringComparison.Ordinal));
}

/// <summary>Буква слова: глиф, смещение и ожидаемые точки входа и выхода тела (координаты слова).</summary>
/// <param name="Index">Индекс буквы, с 0.</param>
/// <param name="Glyph">Глиф (после выбора варианта по <c>variantRules</c>).</param>
/// <param name="OffsetU">Сдвиг глифа по u: <c>Σ (advance + letterSpacing)</c> предыдущих букв.</param>
/// <param name="EntryPoint">Ожидаемая точка входа (начало тела).</param>
/// <param name="EntryTangent">Единичное направление прихода в точку входа.</param>
/// <param name="ExitPoint">Ожидаемая точка выхода (конец тела).</param>
/// <param name="ExitTangent">Единичное направление ухода.</param>
public sealed record WordLetter(
    int Index,
    Glyph Glyph,
    double OffsetU,
    Vec2 EntryPoint,
    Vec2 EntryTangent,
    Vec2 ExitPoint,
    Vec2 ExitTangent);

/// <summary>
/// Соединение между соседними буквами слова. Геометрия <see cref="Path"/> задана, если правило не разрыв; иначе
/// <c>null</c>: ожидаемый отрыв (<see cref="ExpectedLift"/>).
/// </summary>
/// <param name="Index">Индекс соединения, с 0 (между буквами <c>Index</c> и <c>Index + 1</c>).</param>
/// <param name="Rule">Применённое правило стиля или <c>null</c>, если ни одно не подошло (разрыв).</param>
/// <param name="Family">Семейство <c>JF1</c>…<c>JF9</c> или <c>null</c>.</param>
/// <param name="Shape">Форма соединения.</param>
/// <param name="Tall">Вход следующей буквы высокий (<c>E-UP-TALL</c> или <c>entry.height</c> выше x-height).</param>
/// <param name="High">Высокое соединение (<c>high</c> в правиле, J2).</param>
/// <param name="Path">Траектория соединения или <c>null</c> для разрыва.</param>
/// <param name="ExitPoint">Точка выхода левой буквы.</param>
/// <param name="ExitTangent">Направление ухода.</param>
/// <param name="EntryPoint">Точка входа правой буквы.</param>
/// <param name="EntryTangent">Направление прихода.</param>
public sealed record WordJoin(
    int Index,
    JoinRule? Rule,
    string? Family,
    JoinShape Shape,
    bool Tall,
    bool High,
    GlyphPath? Path,
    Vec2 ExitPoint,
    Vec2 ExitTangent,
    Vec2 EntryPoint,
    Vec2 EntryTangent)
{
    /// <summary>Левая буква.</summary>
    public int Left => Index;

    /// <summary>Правая буква.</summary>
    public int Right => Index + 1;

    /// <summary>Эталон ждёт здесь отрыва пера (разрыв по правилу стиля).</summary>
    public bool ExpectedLift => Path is null;
}

/// <summary>
/// Отложенный штрих слова: пишется после слова (политика стиля, §3.2.3). Положение — в координатах слова; привязка — к
/// основному штриху своей буквы.
/// </summary>
/// <param name="Letter">Индекс буквы, которой принадлежит штрих.</param>
/// <param name="SlotId">Идентификатор слота в глифе (<c>dot</c>, <c>bar</c>).</param>
/// <param name="Kind">Вид.</param>
/// <param name="Direction">Направление написания.</param>
/// <param name="Path">Траектория в координатах слова.</param>
/// <param name="AnchorStrokeId">Основной штрих глифа, к которому привязан штрих.</param>
/// <param name="AnchorT">Доля длины дуги штриха глифа (точка привязки).</param>
/// <param name="AnchorPoint">Точка привязки в координатах слова.</param>
/// <param name="AnchorOffset">Смещение от точки привязки до середины отложенного штриха.</param>
public sealed record WordDeferred(
    int Letter,
    string SlotId,
    DeferredKind Kind,
    DeferredDirection Direction,
    GlyphPath Path,
    string AnchorStrokeId,
    double AnchorT,
    Vec2 AnchorPoint,
    Vec2 AnchorOffset);

/// <summary>
/// Эталон слова (ARCHITECTURE.md §6.4.4, 6.4.5): глифы, расставленные по <c>advance + letterSpacing</c>, соединения между
/// ними, основные штрихи слова с разметкой участков (тела букв, соединения, ожидаемые точки входа и выхода) и отложенные
/// штрихи в конце. Неизменяем. Строится <see cref="WordTemplateBuilder"/>.
/// </summary>
public sealed class WordTemplate
{
    /// <summary>Создаёт эталон. Используйте <see cref="WordTemplateBuilder"/>.</summary>
    /// <param name="text">Текст слова.</param>
    /// <param name="style">Стиль.</param>
    /// <param name="letters">Буквы.</param>
    /// <param name="joins">Соединения (букв − 1).</param>
    /// <param name="penStrokes">Основные штрихи в порядке написания.</param>
    /// <param name="deferred">Отложенные штрихи в порядке написания (после слова).</param>
    public WordTemplate(
        string text,
        StyleManifest style,
        ImmutableArray<WordLetter> letters,
        ImmutableArray<WordJoin> joins,
        ImmutableArray<WordPenStroke> penStrokes,
        ImmutableArray<WordDeferred> deferred)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(style);
        Text = text;
        Style = style;
        Letters = letters;
        Joins = joins;
        PenStrokes = penStrokes;
        Deferred = deferred;
    }

    /// <summary>Текст слова.</summary>
    public string Text { get; }

    /// <summary>Стиль, по которому построен эталон.</summary>
    public StyleManifest Style { get; }

    /// <summary>Буквы по порядку.</summary>
    public ImmutableArray<WordLetter> Letters { get; }

    /// <summary>Соединения между соседними буквами.</summary>
    public ImmutableArray<WordJoin> Joins { get; }

    /// <summary>Основные штрихи слова в порядке написания.</summary>
    public ImmutableArray<WordPenStroke> PenStrokes { get; }

    /// <summary>Отложенные штрихи слова в порядке написания (после основных).</summary>
    public ImmutableArray<WordDeferred> Deferred { get; }

    /// <summary>Правый край тела последней буквы (<c>offset + advance</c>), x-height.</summary>
    public double Advance => Letters.IsDefaultOrEmpty ? 0.0 : Letters[^1].OffsetU + Letters[^1].Glyph.Advance;
}
