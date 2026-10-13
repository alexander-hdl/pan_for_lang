using System.Collections.Immutable;
using PenForLang.Evaluation;
using PenForLang.Glyphs;

namespace PenForLang.App.Wpf.Lesson;

/// <summary>Статус элемента по модели знаний (METHODOLOGY.md §8.2); от него зависит частота сводной обратной связи (§7.2).</summary>
public enum ElementStatus
{
    /// <summary>Изучается: обратная связь после каждой попытки.</summary>
    Learning,

    /// <summary>Знакома: после каждой попытки.</summary>
    Familiar,

    /// <summary>Освоена: после серии из N попыток; стоп-ошибки — после каждой.</summary>
    Mastered,

    /// <summary>Устойчива: только при выходе за коридор допуска и при стоп-ошибках.</summary>
    Stable,
}

/// <summary>Контекст планирования обратной связи.</summary>
/// <param name="Target">Целевой элемент (буква).</param>
/// <param name="StepCode">Код упражнения шага.</param>
/// <param name="Level">Уровень опоры.</param>
/// <param name="Status">Статус элемента; в фиксированном сценарии V0.8 — <see cref="ElementStatus.Learning"/>.</param>
/// <param name="AttemptNo">Номер попытки в шаге, с 1.</param>
/// <param name="History">История подсказок.</param>
/// <param name="Settings">Числа защиты от перегруза и затухания.</param>
public sealed record FeedbackContext(
    string Target,
    string StepCode,
    SupportLevel Level,
    ElementStatus Status,
    int AttemptNo,
    HintHistory History,
    FeedbackSettings Settings);

/// <summary>
/// Временный планировщик обратной связи V0.8 (замена будущего <c>Lesson.Feedback.FeedbackPlanner</c>): из <see cref="EvaluationSummary"/>
/// и контекста выбирает одну подсказку по приоритету методики §7.3, наложение на эталоне и действие. Чистая функция, без окна.
/// <list type="bullet">
/// <item><b>Приоритет:</b> P1/P2, затем P3–P6, F2, D1–D3, F4/F5, F1; остальные находки записываются, но не показываются.</item>
/// <item><b>D4</b> никогда не даёт подсказки и ошибки: засчитывается без штрафа (решение заказчика).</item>
/// <item><b>U0</b> и низкая уверенность: нейтральное «давай ещё раз» без ошибки, одна повторная попытка; повтор — дальше без оценки.</item>
/// <item><b>Перегруз:</b> одна подсказка не больше <c>repeatLimit − 1</c> раз подряд; на последнем повторе текст не повторяется, вместо него
/// «давай посмотрим ещё раз вместе»: анимация заново и опора вверх (§7.4).</item>
/// <item><b>Затухание:</b> частота по статусу элемента (§7.2); после двух процессных ошибок подряд — на ступень выше.</item>
/// <item><b>Похвала</b> — процесс, не личность: за исправленную после подсказки часть (подсветка зелёным) и за первую чистую попытку шага.</item>
/// </list>
/// </summary>
public static class MiniFeedback
{
    /// <summary>Строит план для результата оценки.</summary>
    public static FeedbackPlan Plan(EvaluationSummary summary, FeedbackContext context)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(context);
        FeedbackSettings settings = context.Settings;
        HintHistory history = context.History;
        string target = context.Target;

        if (summary.Decision == Decision.Unrecognized || summary.Confidence == Confidence.Low)
        {
            // §4.1: при Confidence.Low или U0 негативных подсказок нет. §7.4: одна повторная попытка, затем дальше без оценки.
            return history.UnrecognizedStreak(target) < settings.UnrecognizedRetries
                ? new FeedbackPlan(
                    FeedbackKeys.Unrecognized, null, null, null, FeedbackAction.None, FeedbackTone.Neutral,
                    CountsAsAttempt: false, FeedbackReason.Unrecognized, ImmutableDictionary<string, string>.Empty)
                : new FeedbackPlan(
                    FeedbackKeys.Next, null, null, null, FeedbackAction.None, FeedbackTone.Neutral,
                    CountsAsAttempt: true, FeedbackReason.SkipUnrecognized, ImmutableDictionary<string, string>.Empty);
        }

        List<Candidate> candidates = Candidates(summary);
        if (candidates.Count > 0)
        {
            Candidate top = candidates[0];
            if (!ShouldGiveFeedback(context, top.Finding, summary))
            {
                return FeedbackPlan.Silent(FeedbackReason.Faded, top.Finding.Code);
            }

            int repeats = history.ConsecutiveRepeats(target, top.Key) + 1;
            if (repeats >= settings.RepeatLimit)
            {
                // §7.4: текст не повторяется; вместо него — повтор анимации этой части и опора вверх.
                return new FeedbackPlan(
                    FeedbackKeys.LookAgain,
                    null,
                    top.Finding.Code,
                    top.Highlight,
                    FeedbackAction.ReplayAnimation | FeedbackAction.AddAid,
                    FeedbackTone.Neutral,
                    CountsAsAttempt: true,
                    FeedbackReason.Overload,
                    ImmutableDictionary<string, string>.Empty);
            }

            return new FeedbackPlan(
                top.Key,
                null,
                top.Finding.Code,
                top.Highlight,
                IsProcessCode(top.Finding.Code) ? FeedbackAction.ReplayAnimation : FeedbackAction.None,
                FeedbackTone.Corrective,
                CountsAsAttempt: true,
                FeedbackReason.Hint,
                ParametersFor(top.Finding, summary));
        }

        return PlanForCleanAttempt(summary, context);
    }

    /// <summary>Ключ подсказки для кода находки; <c>null</c> — кода в подсказках нет (D4 никогда; в изолированной букве нет L, J, K).</summary>
    public static string? KeyFor(ErrorCode code) => code switch
    {
        ErrorCode.P1 => FeedbackKeys.Start,
        ErrorCode.P2 => FeedbackKeys.Direction,
        ErrorCode.P3 => FeedbackKeys.Order,
        ErrorCode.P4 or ErrorCode.D3 => FeedbackKeys.ExtraStroke,
        ErrorCode.P5 => FeedbackKeys.MissingStroke,
        ErrorCode.P6 => FeedbackKeys.Retrace,
        ErrorCode.F2 => FeedbackKeys.Closed,
        ErrorCode.F4 => FeedbackKeys.Size,
        ErrorCode.F5 => FeedbackKeys.Baseline,
        ErrorCode.F1 => FeedbackKeys.Shape,
        ErrorCode.D1 => FeedbackKeys.DeferredMissing,
        ErrorCode.D2 => FeedbackKeys.DeferredPlace,
        _ => null,
    };

    /// <summary>Приоритет по методике §7.3: меньше — важнее.</summary>
    public static int Priority(ErrorCode code) => code switch
    {
        ErrorCode.P1 or ErrorCode.P2 => 3,
        ErrorCode.P3 or ErrorCode.P4 or ErrorCode.P5 or ErrorCode.P6 => 4,
        ErrorCode.F2 or ErrorCode.F3 => 6,
        ErrorCode.D1 or ErrorCode.D2 or ErrorCode.D3 => 7,
        ErrorCode.F4 or ErrorCode.F5 => 8,
        ErrorCode.F1 => 9,
        _ => 100,
    };

    private static bool IsProcessCode(ErrorCode code) => code is ErrorCode.P1 or ErrorCode.P2 or ErrorCode.P3 or ErrorCode.P4 or ErrorCode.P5 or ErrorCode.P6;

    private static List<Candidate> Candidates(EvaluationSummary summary)
    {
        var list = new List<Candidate>();
        foreach (Finding finding in summary.Findings)
        {
            // Информационные находки (D4, P2 перекладины справа налево) — не ошибки; D4 не показывается и при строгом режиме.
            if (finding.Kind != FindingKind.Error || finding.Code == ErrorCode.D4)
            {
                continue;
            }

            if (KeyFor(finding.Code) is not { } key)
            {
                continue;
            }

            list.Add(new Candidate(finding, key, HighlightFor(finding)));
        }

        return [.. list.OrderBy(c => Priority(c.Finding.Code)).ThenByDescending(c => c.Finding.Severity)];
    }

    private static HighlightSpec HighlightFor(Finding finding) => finding.Code switch
    {
        ErrorCode.P1 => new HighlightSpec(HighlightKind.Start, finding.Span),
        ErrorCode.F1 or ErrorCode.F4 or ErrorCode.F5 => new HighlightSpec(HighlightKind.Whole, TemplateSpan.Whole),
        _ => new HighlightSpec(HighlightKind.Span, finding.Span),
    };

    private static ImmutableDictionary<string, string> ParametersFor(Finding finding, EvaluationSummary summary)
    {
        if (finding.Code is not (ErrorCode.D1 or ErrorCode.D2))
        {
            return ImmutableDictionary<string, string>.Empty;
        }

        DeferredSummary? slot = summary.Deferred.FirstOrDefault(d => string.Equals(d.StrokeId, finding.Where.StrokeId, StringComparison.Ordinal));
        string part = slot?.Kind switch
        {
            DeferredKind.Bar => "bar",
            DeferredKind.Cross => "cross",
            _ => "dot",
        };
        return ImmutableDictionary<string, string>.Empty.Add("part", part);
    }

    /// <summary>Частота сводной обратной связи по статусу элемента (§7.2).</summary>
    private static bool ShouldGiveFeedback(FeedbackContext context, Finding top, EvaluationSummary summary)
    {
        FeedbackSettings settings = context.Settings;
        if (settings.StopCodes.Contains(top.Code))
        {
            return true;
        }

        ElementStatus status = context.Status;
        if (status > ElementStatus.Learning && context.History.ProcessErrorStreak(context.Target) >= settings.ProcessErrorsToRaise)
        {
            status--; // после 2 процессных ошибок подряд частота сразу возвращается на ступень выше
        }

        return status switch
        {
            ElementStatus.Mastered => context.AttemptNo % settings.MasteredEveryN == 0,
            ElementStatus.Stable => summary.Form < settings.StableFormFloor,
            _ => true,
        };
    }

    private static FeedbackPlan PlanForCleanAttempt(EvaluationSummary summary, FeedbackContext context)
    {
        HintRecord? previous = context.History.Last(context.Target);

        // Исправленная после подсказки часть: конкретная похвала процесса и зелёная подсветка того же участка (§7.4, §7.5).
        if (previous is { Reason: FeedbackReason.Hint or FeedbackReason.Overload, Code: { } fixedCode } && PraiseFor(fixedCode) is { } fixedPraise)
        {
            return new FeedbackPlan(
                null, fixedPraise, null, previous.Highlight, FeedbackAction.None, FeedbackTone.Positive,
                CountsAsAttempt: true, FeedbackReason.Praise, ImmutableDictionary<string, string>.Empty);
        }

        // Первая чистая попытка шага; дальше похвала затухает (не больше одной за шаг).
        bool processOk = summary.Letters.Length > 0 && summary.Letters[0].ProcessOk == true;
        if (processOk && !context.History.PraisedInStep(context.Target, context.StepCode))
        {
            return new FeedbackPlan(
                null, FeedbackKeys.PraisePath, null, null, FeedbackAction.None, FeedbackTone.Positive,
                CountsAsAttempt: true, FeedbackReason.Praise, ImmutableDictionary<string, string>.Empty);
        }

        return FeedbackPlan.Silent(FeedbackReason.Clean);
    }

    private static string? PraiseFor(ErrorCode code) => code switch
    {
        ErrorCode.P1 => FeedbackKeys.PraiseStart,
        ErrorCode.P2 => FeedbackKeys.PraiseDirection,
        ErrorCode.P3 => FeedbackKeys.PraiseOrder,
        ErrorCode.P4 or ErrorCode.P5 or ErrorCode.D1 or ErrorCode.D2 or ErrorCode.D3 => FeedbackKeys.PraiseStrokes,
        ErrorCode.P6 or ErrorCode.F1 or ErrorCode.F2 or ErrorCode.F4 or ErrorCode.F5 => FeedbackKeys.PraiseShape,
        _ => null,
    };

    /// <summary>Запись истории для плана: что сказано и была ли процессная ошибка.</summary>
    public static HintRecord Record(FeedbackPlan plan, FeedbackContext context)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);
        bool processError = plan.Reason is FeedbackReason.Hint or FeedbackReason.Overload or FeedbackReason.Faded
            && plan.Code is { } code
            && context.Settings.ProcessCodes.Contains(code);
        return new HintRecord(context.Target, context.StepCode, context.AttemptNo, plan.Reason, plan.HintKey, plan.Code, plan.Highlight, processError);
    }

    private sealed record Candidate(Finding Finding, string Key, HighlightSpec Highlight);
}
