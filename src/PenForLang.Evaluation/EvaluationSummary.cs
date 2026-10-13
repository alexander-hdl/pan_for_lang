using System.Collections.Immutable;
using PenForLang.Glyphs;

namespace PenForLang.Evaluation;

/// <summary>Версия оценщика и снимок конфигурации, по которым получен результат.</summary>
/// <param name="Version">Версия алгоритма (<see cref="EvaluatorVersion.Current"/>).</param>
/// <param name="ConfigHash">SHA-256 канонического JSON <c>scoring.json</c> (без блока <c>meta</c>), hex в нижнем регистре.</param>
public sealed record EvaluatorInfo(string Version, string ConfigHash);

/// <summary>
/// Участок эталона, к которому относится находка: без сохранения чернил по нему можно нарисовать иллюстрацию ошибки
/// на эталоне (ARCHITECTURE.md §6.4.14, A01).
/// </summary>
/// <param name="StrokeId">Идентификатор штриха эталона (<c>s1</c>, <c>bar</c>); <c>null</c>, если участок — вся буква.</param>
/// <param name="Part">Часть штриха.</param>
/// <param name="FromT">Начало участка, доля длины дуги части, [0, 1].</param>
/// <param name="ToT">Конец участка, доля длины дуги части, [0, 1].</param>
public sealed record TemplateSpan(string? StrokeId, TemplatePart Part, double FromT, double ToT)
{
    /// <summary>Вся буква.</summary>
    public static TemplateSpan Whole { get; } = new(null, TemplatePart.Body, 0.0, 1.0);
}

/// <summary>Место находки: штрих эталона и/или штрих рукописи.</summary>
/// <param name="Place">Вид места.</param>
/// <param name="LetterIndex">Индекс буквы в цели (для изолированной буквы — 0).</param>
/// <param name="StrokeId">Идентификатор штриха эталона, к которому привязана находка, или <c>null</c> (например, лишний штрих).</param>
/// <param name="InkStrokeIndex">
/// <see cref="Ink.RulerStroke.Index"/> штриха рукописи, на котором найдена ошибка, или <c>null</c> (штриха нет: P5, D1).
/// </param>
public sealed record FindingLocation(FindingPlace Place, int LetterIndex, string? StrokeId, int? InkStrokeIndex);

/// <summary>
/// Находка оценщика. Для объяснимости обратной связи несёт измеренное значение и порог.
/// </summary>
/// <param name="Code">Код ошибки.</param>
/// <param name="Where">Место.</param>
/// <param name="Severity">Тяжесть, [0, 1]; у информационных находок — 0.</param>
/// <param name="Span">Участок эталона.</param>
/// <param name="Kind">Ошибка или информация.</param>
/// <param name="Measured">Измеренное значение метрики, вызвавшее находку (единицы — x-height, градусы, штуки), или <c>null</c>.</param>
/// <param name="Threshold">Порог, который превышен, или <c>null</c>.</param>
public sealed record Finding(
    ErrorCode Code,
    FindingLocation Where,
    double Severity,
    TemplateSpan Span,
    FindingKind Kind = FindingKind.Error,
    double? Measured = null,
    double? Threshold = null);

/// <summary>
/// Процессные показатели изолированной буквы (ARCHITECTURE.md §6.4.13). При <see cref="Decision.Unrecognized"/> процесс
/// не оценивался, все значения — <c>null</c> (а не «всё верно»).
/// </summary>
/// <param name="StartOk">Старт каждого штриха в допуске (нет P1).</param>
/// <param name="DirectionOk">Направление каждого штриха и обход овалов и петель верны (нет P2).</param>
/// <param name="OrderOk">Порядок основных штрихов верен (нет P3).</param>
/// <param name="StrokeCountDelta">Число лишних и пропущенных основных штрихов.</param>
/// <param name="RetraceOk">Ретрейс выполнен по написанному (нет P6).</param>
public sealed record ProcessSummary(bool? StartOk, bool? DirectionOk, bool? OrderOk, int? StrokeCountDelta, bool? RetraceOk)
{
    /// <summary>Процесс не оценивался (U0).</summary>
    public static ProcessSummary NotEvaluated { get; } = new(null, null, null, null, null);
}

/// <summary>Итог по букве.</summary>
/// <param name="Index">Индекс буквы в цели.</param>
/// <param name="Target">Целевой символ.</param>
/// <param name="Written">Написанный символ (в v0 совпадает с целью).</param>
/// <param name="Context">Контекст.</param>
/// <param name="Identity">Код идентичности (L*) или <c>null</c>: в v0 не определяется.</param>
/// <param name="Form">Форма, [0, 1].</param>
/// <param name="ProcessOk">Процесс верен (старт ∧ направление ∧ порядок ∧ лишних и пропущенных штрихов меньше <c>process.strokeCount.extraForStop</c>) или <c>null</c> при <see cref="Decision.Unrecognized"/>.</param>
/// <param name="SegmentationConfident">Для изолированной буквы всегда истинно, кроме <see cref="Decision.Unrecognized"/>.</param>
/// <param name="Process">Процессные показатели.</param>
public sealed record LetterSummary(
    int Index,
    string Target,
    string Written,
    LetterContext Context,
    ErrorCode? Identity,
    double Form,
    bool? ProcessOk,
    bool SegmentationConfident,
    ProcessSummary Process);

/// <summary>
/// Итог по соединению двух соседних букв слова (ARCHITECTURE.md §6.2, §6.4.9). Вне слова соединений нет.
/// </summary>
/// <param name="Index">Индекс соединения, с 0.</param>
/// <param name="Left">Индекс левой буквы.</param>
/// <param name="Right">Индекс правой буквы.</param>
/// <param name="Family">Семейство правила стиля (<c>JF1</c>…) или <c>null</c> для разрыва.</param>
/// <param name="Tall">Вход правой буквы высокий (подъём к верху буквы).</param>
/// <param name="Present">J1 не выдаётся (§6.4.9): в диапазоне соединения нет отрыва пера, либо отрыв помечен <paramref name="LiftExpected"/>.</param>
/// <param name="LiftExpected">В диапазоне есть отрыв, и он ожидаем: разрыв по правилу стиля или рядом поставлен отложенный штрих «сразу» (A03).</param>
/// <param name="Score">Качество соединения, [0, 1]: 1 — нарисовано без замечаний или отрыв ожидаем, 0 — отрыв (J1) либо замечания J2–J4.</param>
public sealed record JoinSummary(int Index, int Left, int Right, string? Family, bool Tall, bool Present, bool LiftExpected, double Score);

/// <summary>Итог по слоту отложенного штриха.</summary>
/// <param name="LetterIndex">Индекс буквы в цели.</param>
/// <param name="StrokeId">Идентификатор слота в глифе (<c>dot</c>, <c>bar</c>).</param>
/// <param name="Kind">Вид отложенного штриха.</param>
/// <param name="Status">Статус.</param>
/// <param name="Timing">Когда поставлен.</param>
public sealed record DeferredSummary(int LetterIndex, string StrokeId, DeferredKind Kind, DeferredStatus Status, DeferredTiming Timing);

/// <summary>
/// Стабильный фасад результата оценки для потребителей (Learner, Feedback, Reporting; A07). Поля после заморозки
/// контрактов v1 только добавляются. Результат неизменяем.
/// </summary>
/// <param name="Info">Версия оценщика и снимок конфигурации.</param>
/// <param name="Kind">Вид цели.</param>
/// <param name="TargetText">Целевой текст.</param>
/// <param name="Decision">Решение о написанном.</param>
/// <param name="WrittenText">Написанный текст (в v0 равен цели).</param>
/// <param name="Confidence">Уверенность.</param>
/// <param name="Letters">Итоги по буквам.</param>
/// <param name="Deferred">Итоги по отложенным штрихам.</param>
/// <param name="Findings">Находки в детерминированном порядке (по приоритету методики §7.3, затем по месту).</param>
/// <remarks>Для слова добавлено <see cref="Joins"/> (поля фасада только добавляются, §6.2): итоги по соединениям.</remarks>
public sealed record EvaluationSummary(
    EvaluatorInfo Info,
    TargetKind Kind,
    string TargetText,
    Decision Decision,
    string WrittenText,
    Confidence Confidence,
    ImmutableArray<LetterSummary> Letters,
    ImmutableArray<DeferredSummary> Deferred,
    ImmutableArray<Finding> Findings)
{
    /// <summary>Итоги по соединениям слова (пусто для буквы).</summary>
    public ImmutableArray<JoinSummary> Joins { get; init; } = [];

    /// <summary>Форма единственной буквы цели.</summary>
    public double Form => Letters.IsDefaultOrEmpty ? 0.0 : Letters[0].Form;

    /// <summary>Есть ли находка с указанным кодом.</summary>
    /// <param name="code">Код.</param>
    public bool Has(ErrorCode code) => Findings.Any(f => f.Code == code);

    /// <summary>Находки с указанным кодом.</summary>
    /// <param name="code">Код.</param>
    public IEnumerable<Finding> Of(ErrorCode code) => Findings.Where(f => f.Code == code);
}

/// <summary>Результат оценки: стабильный фасад и предварительная детализация (ARCHITECTURE.md §6.2).</summary>
/// <param name="Summary">Стабильный фасад.</param>
/// <param name="Detail">Детализация (v0-provisional; потребители, кроме UI и InkLab, на неё не опираются).</param>
public sealed record EvaluationResult(EvaluationSummary Summary, EvaluationDetail Detail);
