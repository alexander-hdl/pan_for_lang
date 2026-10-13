using System.Collections.Immutable;
using PenForLang.Evaluation;

namespace PenForLang.App.Wpf.Lesson;

/// <summary>Ключи подсказок и похвалы (тексты — в <c>content/locales</c>; домен выдаёт только ключи, ARCHITECTURE.md §8.2).</summary>
public static class FeedbackKeys
{
    /// <summary>P1: неверная точка старта.</summary>
    public const string Start = "fb.start";

    /// <summary>P2: обратное направление.</summary>
    public const string Direction = "fb.direction";

    /// <summary>P3: неверный порядок штрихов.</summary>
    public const string Order = "fb.order";

    /// <summary>P4 и D3: лишний штрих.</summary>
    public const string ExtraStroke = "fb.extraStroke";

    /// <summary>P5: пропущенный штрих.</summary>
    public const string MissingStroke = "fb.missingStroke";

    /// <summary>P6: ретрейс не по написанному.</summary>
    public const string Retrace = "fb.retrace";

    /// <summary>F2: овал не замкнут.</summary>
    public const string Closed = "fb.closed";

    /// <summary>F4: не достаёт линеек или выходит за них.</summary>
    public const string Size = "fb.size";

    /// <summary>F5: посадка на базовую линию.</summary>
    public const string Baseline = "fb.baseline";

    /// <summary>F1: общее отклонение формы.</summary>
    public const string Shape = "fb.shape";

    /// <summary>D1: нет точки или перекладины.</summary>
    public const string DeferredMissing = "fb.deferredMissing";

    /// <summary>D2: точка или перекладина не на месте.</summary>
    public const string DeferredPlace = "fb.deferredPlace";

    /// <summary>U0: не распознано; нейтральное «давай ещё раз».</summary>
    public const string Unrecognized = "fb.unrecognized";

    /// <summary>Повторная U0 или сбой оценки: «спасибо, дальше» без оценки.</summary>
    public const string Next = "fb.next";

    /// <summary>Смена способа после повторов одной подсказки: «давай посмотрим ещё раз вместе».</summary>
    public const string LookAgain = "fb.lookAgain";

    /// <summary>Похвала: после исправленного старта.</summary>
    public const string PraiseStart = "praise.process.start";

    /// <summary>Похвала: после исправленного направления.</summary>
    public const string PraiseDirection = "praise.process.direction";

    /// <summary>Похвала: после исправленного порядка.</summary>
    public const string PraiseOrder = "praise.process.order";

    /// <summary>Похвала: после исправленного числа штрихов или отложенных штрихов.</summary>
    public const string PraiseStrokes = "praise.process.strokes";

    /// <summary>Похвала: после исправленной формы.</summary>
    public const string PraiseShape = "praise.process.shape";

    /// <summary>Похвала: первая чистая попытка шага (старт, направление и порядок верны).</summary>
    public const string PraisePath = "praise.process.path";

    /// <summary>Все ключи подсказок (без похвалы).</summary>
    public static IReadOnlyList<string> Hints { get; } =
    [
        Start, Direction, Order, ExtraStroke, MissingStroke, Retrace, Closed, Size, Baseline, Shape,
        DeferredMissing, DeferredPlace, Unrecognized, Next, LookAgain,
    ];

    /// <summary>Все ключи похвалы.</summary>
    public static IReadOnlyList<string> Praises { get; } =
        [PraiseStart, PraiseDirection, PraiseOrder, PraiseStrokes, PraiseShape, PraisePath];
}

/// <summary>Как показать участок эталона.</summary>
public enum HighlightKind
{
    /// <summary>Точка старта пульсирует, начало эталона подсвечено (P1).</summary>
    Start,

    /// <summary>Участок эталона <see cref="HighlightSpec.Span"/>.</summary>
    Span,

    /// <summary>Вся буква.</summary>
    Whole,
}

/// <summary>Что подсветить на эталоне. Координаты в линейке получает <see cref="TemplateGeometry"/>.</summary>
/// <param name="Kind">Вид подсветки.</param>
/// <param name="Span">Участок эталона из находки оценщика.</param>
public sealed record HighlightSpec(HighlightKind Kind, TemplateSpan Span);

/// <summary>Действие, которое программа делает вместе с подсказкой.</summary>
[Flags]
public enum FeedbackAction
{
    /// <summary>Ничего (только подсказка и наложение).</summary>
    None = 0,

    /// <summary>Показать анимацию буквы снова.</summary>
    ReplayAnimation = 1,

    /// <summary>Добавить опору: эталон под перо на несколько попыток.</summary>
    AddAid = 2,
}

/// <summary>Тон сообщения: влияет на цвет наложения (без красных крестов, METHODOLOGY.md §7.6).</summary>
public enum FeedbackTone
{
    /// <summary>Нейтральный.</summary>
    Neutral,

    /// <summary>Подсказка: участок подсвечен тёплым цветом.</summary>
    Corrective,

    /// <summary>Исправленная часть подсвечена зелёным.</summary>
    Positive,
}

/// <summary>Почему план такой (для журнала и тестов).</summary>
public enum FeedbackReason
{
    /// <summary>Одна подсказка по приоритету.</summary>
    Hint,

    /// <summary>Подсказка повторилась слишком часто: вместо текста — смена способа.</summary>
    Overload,

    /// <summary>Похвала процесса.</summary>
    Praise,

    /// <summary>Чисто, говорить нечего.</summary>
    Clean,

    /// <summary>Ошибка есть, но частота обратной связи на этой ступени её не показывает.</summary>
    Faded,

    /// <summary>U0: не распознано, одна повторная попытка.</summary>
    Unrecognized,

    /// <summary>U0 повторилась: идём дальше без оценки.</summary>
    SkipUnrecognized,

    /// <summary>Оценщик не отработал: попытка без оценки.</summary>
    EvaluationFailed,
}

/// <summary>
/// План обратной связи на одну попытку (ARCHITECTURE.md §4.1): не больше одной подсказки и не больше одной похвалы, что подсветить
/// на эталоне и что сделать (анимация, опора). Тексты по ключам берёт слой интерфейса.
/// </summary>
/// <param name="HintKey">Ключ подсказки или <c>null</c>.</param>
/// <param name="PraiseKey">Ключ похвалы процесса или <c>null</c>.</param>
/// <param name="Code">Код находки, из-за которой выбрана подсказка (для U0 и чистой попытки — <c>null</c>).</param>
/// <param name="Highlight">Что подсветить на эталоне или <c>null</c>.</param>
/// <param name="Action">Действие вместе с подсказкой.</param>
/// <param name="Tone">Тон.</param>
/// <param name="CountsAsAttempt">Попытка засчитывается в шаг; для первой U0 — нет (одна повторная попытка).</param>
/// <param name="Reason">Причина плана.</param>
/// <param name="Parameters">Параметры текста (<c>part</c> → <c>dot</c>/<c>bar</c>/<c>cross</c>).</param>
public sealed record FeedbackPlan(
    string? HintKey,
    string? PraiseKey,
    ErrorCode? Code,
    HighlightSpec? Highlight,
    FeedbackAction Action,
    FeedbackTone Tone,
    bool CountsAsAttempt,
    FeedbackReason Reason,
    ImmutableDictionary<string, string> Parameters)
{
    /// <summary>Есть ли что показать ребёнку текстом.</summary>
    public bool HasMessage => HintKey is not null || PraiseKey is not null;

    /// <summary>Нейтральный план без сообщения.</summary>
    public static FeedbackPlan Silent(FeedbackReason reason, ErrorCode? code = null) =>
        new(null, null, code, null, FeedbackAction.None, FeedbackTone.Neutral, true, reason, ImmutableDictionary<string, string>.Empty);
}
