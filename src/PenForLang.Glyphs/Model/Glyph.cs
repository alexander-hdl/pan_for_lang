using System.Collections.Immutable;
using PenForLang.Core.Geometry;

namespace PenForLang.Glyphs;

/// <summary>Регистр буквы глифа.</summary>
public enum GlyphCase
{
    /// <summary>Строчная (<c>lower</c>).</summary>
    Lower,

    /// <summary>Заглавная (<c>upper</c>).</summary>
    Upper,
}

/// <summary>Вид отложенного штриха (пишется после слова).</summary>
public enum DeferredKind
{
    /// <summary>Точка (над i, j).</summary>
    Dot,

    /// <summary>Перекладина (t, f).</summary>
    Bar,

    /// <summary>Крест (x).</summary>
    Cross,
}

/// <summary>Направление написания отложенного штриха.</summary>
public enum DeferredDirection
{
    /// <summary>Любое (точка).</summary>
    Any,

    /// <summary>Слева направо (<c>ltr</c>; перекладина).</summary>
    LeftToRight,
}

/// <summary>Тип ключевой точки штриха — мягкий якорь для оценщика.</summary>
public enum KeyPointType
{
    /// <summary>Начало штриха.</summary>
    Start,

    /// <summary>Конец штриха.</summary>
    End,

    /// <summary>Локальный максимум v.</summary>
    YMax,

    /// <summary>Локальный минимум v.</summary>
    YMin,

    /// <summary>Точка возврата (разворот направления).</summary>
    Cusp,

    /// <summary>Верх петли.</summary>
    LoopTop,

    /// <summary>Вершина горба.</summary>
    HumpTop,

    /// <summary>Касание базовой линии.</summary>
    BaselineTouch,
}

/// <summary>Линия линейки стиля.</summary>
public enum LineName
{
    /// <summary>Линия асцендеров.</summary>
    Ascender,

    /// <summary>Линия заглавных.</summary>
    Cap,

    /// <summary>Линия x-height (v = 1).</summary>
    XHeight,

    /// <summary>Базовая линия (v = 0).</summary>
    Baseline,

    /// <summary>Линия десцендеров.</summary>
    Descender,
}

/// <summary>Какой экстремум проверяет <see cref="ReachCheck"/>.</summary>
public enum ExtremeKind
{
    /// <summary>Максимум v.</summary>
    Max,

    /// <summary>Минимум v.</summary>
    Min,
}

/// <summary>Где на анимации показывается точка старта.</summary>
public enum StartDotMode
{
    /// <summary>Нет точки старта.</summary>
    None,

    /// <summary>В начале входящего штриха (<see cref="Glyph.LeadIn"/>) — настоящего начала изолированной буквы.</summary>
    LeadIn,

    /// <summary>В начале тела (первого основного штриха).</summary>
    Body,
}

/// <summary>Вход в букву: класс, точка начала тела и направление подхода к ней.</summary>
/// <param name="Class">Класс входа.</param>
/// <param name="Point">Точка начала тела (совпадает с началом первого штриха).</param>
/// <param name="Tangent">
/// Единичное направление движения при подходе к <paramref name="Point"/> — конец <see cref="Glyph.LeadIn"/>; используется
/// как касательная Эрмита соединения.
/// </param>
/// <param name="Height">Высота входа в x-height, если отличается от обычной (t: 1,5); иначе <c>null</c>.</param>
public sealed record GlyphEntry(EntryClass Class, Vec2 Point, Vec2 Tangent, double? Height);

/// <summary>Выход из буквы: класс, точка конца тела и направление выхода.</summary>
/// <param name="Class">Класс выхода.</param>
/// <param name="Point">Точка конца тела (совпадает с концом последнего основного штриха).</param>
/// <param name="Tangent">Единичное направление движения при выходе (конец тела = начало <see cref="Glyph.LeadOut"/>).</param>
public sealed record GlyphExit(ExitClass Class, Vec2 Point, Vec2 Tangent);

/// <summary>Основной штрих: непрерывная траектория при одном касании пера.</summary>
/// <param name="Id">Идентификатор штриха, уникальный в глифе (например <c>s1</c>).</param>
/// <param name="Order">Порядок написания, с 1 без пропусков.</param>
/// <param name="Path">Траектория в порядке и направлении написания.</param>
public sealed record GlyphStroke(string Id, int Order, GlyphPath Path);

/// <summary>Привязка отложенного штриха к основному.</summary>
/// <param name="StrokeId">Основной штрих, к которому привязан отложенный.</param>
/// <param name="T">Доля длины дуги штриха <paramref name="StrokeId"/> (точка привязки).</param>
/// <param name="Offset">Смещение от точки привязки до середины отложенного штриха (по длине дуги), в x-height.</param>
public sealed record StrokeAnchor(string StrokeId, double T, Vec2 Offset);

/// <summary>Отложенный штрих (точка, перекладина, крест): ставится после слова (политика стиля).</summary>
/// <param name="Id">Идентификатор, уникальный среди всех штрихов глифа.</param>
/// <param name="Kind">Вид.</param>
/// <param name="Direction">Направление написания.</param>
/// <param name="Path">Траектория.</param>
/// <param name="Anchor">Привязка к основному штриху.</param>
public sealed record DeferredStroke(string Id, DeferredKind Kind, DeferredDirection Direction, GlyphPath Path, StrokeAnchor Anchor);

/// <summary>Ключевая точка штриха (мягкий якорь для оценщика).</summary>
/// <param name="Type">Тип.</param>
/// <param name="StrokeId">Основной штрих.</param>
/// <param name="T">Доля длины дуги штриха, [0, 1].</param>
public sealed record GlyphKeyPoint(KeyPointType Type, string StrokeId, double T);

/// <summary>Проверка достижения: экстремум v основных штрихов лежит на <see cref="Target"/> или, если он не задан, на линии стиля.</summary>
/// <param name="Line">Линия; может быть <c>null</c>, если задан <paramref name="Target"/>.</param>
/// <param name="Extreme">Какой экстремум.</param>
/// <param name="Target">Явная высота v вместо линии (например, 1,5 для t) или <c>null</c>.</param>
public sealed record ReachCheck(LineName? Line, ExtremeKind Extreme, double? Target);

/// <summary>Участок штриха по доле длины дуги.</summary>
/// <param name="StrokeId">Основной штрих.</param>
/// <param name="From">Начало участка, [0, 1).</param>
/// <param name="To">Конец участка, (From, 1].</param>
public sealed record StrokeRange(string StrokeId, double From, double To);

/// <summary>Сегмент штриха, который должен быть ретрейсом.</summary>
/// <param name="StrokeId">Основной штрих.</param>
/// <param name="Segment">Индекс сегмента.</param>
public sealed record RetraceCheck(string StrokeId, int Segment);

/// <summary>Исключения из допусков по умолчанию (<c>scoring.json</c>): замкнутость, петли, ретрейс, достижение линий.</summary>
/// <param name="Reach">Достижение линий.</param>
/// <param name="Closed">Замкнутые участки (овал).</param>
/// <param name="Loops">Петли.</param>
/// <param name="Retrace">Ретрейсы.</param>
public sealed record GlyphChecks(
    ImmutableArray<ReachCheck> Reach,
    ImmutableArray<StrokeRange> Closed,
    ImmutableArray<StrokeRange> Loops,
    ImmutableArray<RetraceCheck> Retrace)
{
    /// <summary>Пустой набор проверок.</summary>
    public static GlyphChecks Empty { get; } = new([], [], [], []);
}

/// <summary>Номер штриха на анимации: где рисуется цифра.</summary>
/// <param name="StrokeId">Основной или отложенный штрих.</param>
/// <param name="At">Положение значка в координатах линейки.</param>
public sealed record AnimationNumber(string StrokeId, Vec2 At);

/// <summary>Стрелка направления на анимации.</summary>
/// <param name="StrokeId">Основной или отложенный штрих.</param>
/// <param name="T">Доля длины дуги штриха (тела), (0, 1).</param>
public sealed record AnimationArrow(string StrokeId, double T);

/// <summary>Метаданные анимации глифа: точка старта, номера и стрелки.</summary>
/// <param name="StartDot">Где показывается точка старта.</param>
/// <param name="Numbers">Номера штрихов.</param>
/// <param name="Arrows">Стрелки направления.</param>
public sealed record GlyphAnimationMeta(StartDotMode StartDot, ImmutableArray<AnimationNumber> Numbers, ImmutableArray<AnimationArrow> Arrows);

/// <summary>Происхождение и статус ревью глифа.</summary>
/// <param name="Author">Автор (человек или <c>agent:V0.4</c>).</param>
/// <param name="Source">Источник форм: <c>illustrative</c> (иллюстративные), <c>authored</c> (по прописи), <c>traced</c> (по подложке).</param>
/// <param name="Reviewed">Формы сверены человеком с прописью.</param>
/// <param name="Reviewer">Кто сверил (обязателен при <paramref name="Reviewed"/>).</param>
/// <param name="License">Лицензия данных или <c>null</c>.</param>
/// <param name="Notes">Примечания или <c>null</c>.</param>
public sealed record GlyphMeta(string Author, string Source, bool Reviewed, string? Reviewer, string? License, string? Notes);

/// <summary>Штрих в порядке написания (основной или отложенный) — общий порядок для нумерации, анимации и рендера.</summary>
/// <param name="Id">Идентификатор штриха.</param>
/// <param name="Number">Номер, с 1 (основные по <see cref="GlyphStroke.Order"/>, затем отложенные в порядке перечисления).</param>
/// <param name="IsDeferred">Это отложенный штрих.</param>
/// <param name="Path">Траектория (тело, без входящего и выходящего штрихов).</param>
/// <param name="Deferred">Описание отложенного штриха или <c>null</c> для основного.</param>
public sealed record PenStroke(string Id, int Number, bool IsDeferred, GlyphPath Path, DeferredStroke? Deferred);

/// <summary>
/// Глиф стиля письма: символ × вариант (схема <c>pfl.glyph/1</c>, ARCHITECTURE.md §3.2.4). Координаты — линейка
/// (§3.1): baseline = 0, x-height = 1, ось v вверх. Неизменяем.
/// </summary>
/// <param name="Id">Идентификатор <c>&lt;styleId&gt;/&lt;char&gt;.&lt;variant&gt;</c>.</param>
/// <param name="Case">Регистр.</param>
/// <param name="Advance">Ширина тела без соединений, x-height.</param>
/// <param name="Entry">Вход.</param>
/// <param name="Exit">Выход.</param>
/// <param name="LeadIn">Входящий штрих изолированной буквы (подъём к началу тела) или <c>null</c>.</param>
/// <param name="LeadOut">Выходящий штрих изолированной буквы (продолжение выхода) или <c>null</c>.</param>
/// <param name="Strokes">Основные штрихи в порядке написания.</param>
/// <param name="Deferred">Отложенные штрихи.</param>
/// <param name="KeyPoints">Ключевые точки.</param>
/// <param name="Features">Признаки тела: <c>retrace</c>, <c>closedOval</c>, <c>deferred</c>, <c>humps:N</c> и др.</param>
/// <param name="Checks">Исключения из проверок.</param>
/// <param name="Animation">Метаданные анимации.</param>
/// <param name="Meta">Происхождение и ревью.</param>
public sealed record Glyph(
    GlyphId Id,
    GlyphCase Case,
    double Advance,
    GlyphEntry Entry,
    GlyphExit Exit,
    GlyphPath? LeadIn,
    GlyphPath? LeadOut,
    ImmutableArray<GlyphStroke> Strokes,
    ImmutableArray<DeferredStroke> Deferred,
    ImmutableArray<GlyphKeyPoint> KeyPoints,
    ImmutableArray<string> Features,
    GlyphChecks Checks,
    GlyphAnimationMeta Animation,
    GlyphMeta Meta)
{
    /// <summary>Символ глифа (<c>a</c>).</summary>
    public string Symbol => Id.Symbol;

    /// <summary>Вариант глифа (<c>default</c>, <c>after-high</c>).</summary>
    public string Variant => Id.Variant;

    /// <summary>Все штрихи в порядке написания: основные по <see cref="GlyphStroke.Order"/>, затем отложенные.</summary>
    public ImmutableArray<PenStroke> PenStrokes
    {
        get
        {
            var result = ImmutableArray.CreateBuilder<PenStroke>(Strokes.Length + Deferred.Length);
            int number = 1;
            foreach (GlyphStroke stroke in Strokes.OrderBy(s => s.Order))
            {
                result.Add(new PenStroke(stroke.Id, number++, false, stroke.Path, null));
            }

            foreach (DeferredStroke deferred in Deferred)
            {
                result.Add(new PenStroke(deferred.Id, number++, true, deferred.Path, deferred));
            }

            return result.MoveToImmutable();
        }
    }

    /// <summary>Находит основной штрих по идентификатору.</summary>
    /// <param name="strokeId">Идентификатор.</param>
    /// <returns>Штрих или <c>null</c>.</returns>
    public GlyphStroke? FindStroke(string strokeId) => Strokes.FirstOrDefault(s => string.Equals(s.Id, strokeId, StringComparison.Ordinal));

    /// <summary>Находит штрих (основной или отложенный) по идентификатору в порядке написания.</summary>
    /// <param name="strokeId">Идентификатор.</param>
    /// <returns>Штрих или <c>null</c>.</returns>
    public PenStroke? FindPenStroke(string strokeId) => PenStrokes.FirstOrDefault(s => string.Equals(s.Id, strokeId, StringComparison.Ordinal));
}
