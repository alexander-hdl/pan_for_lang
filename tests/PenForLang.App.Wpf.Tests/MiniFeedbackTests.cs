using PenForLang.App.Wpf.Lesson;
using PenForLang.Evaluation;
using PenForLang.Glyphs;
using static PenForLang.App.Wpf.Tests.LessonTestSupport;

namespace PenForLang.App.Wpf.Tests;

/// <summary>Таблица «находка → ключ, подсветка, действие» и правила METHODOLOGY §7 для чистого планировщика (без окна и без оценщика).</summary>
public class MiniFeedbackTests
{
    public static TheoryData<ErrorCode, string, HighlightKind, FeedbackAction> Table => new()
    {
        { ErrorCode.P1, FeedbackKeys.Start, HighlightKind.Start, FeedbackAction.ReplayAnimation },
        { ErrorCode.P2, FeedbackKeys.Direction, HighlightKind.Span, FeedbackAction.ReplayAnimation },
        { ErrorCode.P3, FeedbackKeys.Order, HighlightKind.Span, FeedbackAction.ReplayAnimation },
        { ErrorCode.P4, FeedbackKeys.ExtraStroke, HighlightKind.Span, FeedbackAction.ReplayAnimation },
        { ErrorCode.P5, FeedbackKeys.MissingStroke, HighlightKind.Span, FeedbackAction.ReplayAnimation },
        { ErrorCode.P6, FeedbackKeys.Retrace, HighlightKind.Span, FeedbackAction.ReplayAnimation },
        { ErrorCode.F2, FeedbackKeys.Closed, HighlightKind.Span, FeedbackAction.None },
        { ErrorCode.D1, FeedbackKeys.DeferredMissing, HighlightKind.Span, FeedbackAction.None },
        { ErrorCode.D2, FeedbackKeys.DeferredPlace, HighlightKind.Span, FeedbackAction.None },
        { ErrorCode.D3, FeedbackKeys.ExtraStroke, HighlightKind.Span, FeedbackAction.None },
        { ErrorCode.F4, FeedbackKeys.Size, HighlightKind.Whole, FeedbackAction.None },
        { ErrorCode.F5, FeedbackKeys.Baseline, HighlightKind.Whole, FeedbackAction.None },
        { ErrorCode.F1, FeedbackKeys.Shape, HighlightKind.Whole, FeedbackAction.None },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void Each_error_finding_maps_to_one_hint_with_highlight_and_action(ErrorCode code, string key, HighlightKind kind, FeedbackAction action)
    {
        Finding finding = Find(code, from: 0.1, to: 0.4);

        FeedbackPlan plan = MiniFeedback.Plan(Summary([finding]), Context());

        Assert.Equal(key, plan.HintKey);
        Assert.Null(plan.PraiseKey);
        Assert.Equal(code, plan.Code);
        Assert.NotNull(plan.Highlight);
        Assert.Equal(kind, plan.Highlight.Kind);
        Assert.Equal(action, plan.Action);
        Assert.Equal(FeedbackTone.Corrective, plan.Tone);
        Assert.True(plan.CountsAsAttempt);
        Assert.Equal(FeedbackReason.Hint, plan.Reason);
        Assert.Equal(key, MiniFeedback.KeyFor(code));

        // Участок эталона — ровно тот, что дал оценщик (кроме «всей буквы» у F1, F4, F5).
        if (kind == HighlightKind.Span || kind == HighlightKind.Start)
        {
            Assert.Equal(finding.Span, plan.Highlight.Span);
        }
        else
        {
            Assert.Equal(TemplateSpan.Whole, plan.Highlight.Span);
        }
    }

    [Theory]
    [InlineData(ErrorCode.F3)]
    [InlineData(ErrorCode.F6)]
    [InlineData(ErrorCode.K1)]
    [InlineData(ErrorCode.J1)]
    [InlineData(ErrorCode.L2)]
    public void Codes_without_a_hint_in_v0_are_recorded_but_never_shown(ErrorCode code)
    {
        FeedbackPlan plan = MiniFeedback.Plan(Summary([Find(code)], processOk: false), Context());

        Assert.Null(plan.HintKey);
        Assert.Null(MiniFeedback.KeyFor(code));
    }

    [Fact]
    public void Deferred_hints_carry_the_part_name_for_the_text()
    {
        var dot = new DeferredSummary(0, "dot", DeferredKind.Dot, DeferredStatus.Missing, DeferredTiming.None);
        var bar = new DeferredSummary(0, "bar", DeferredKind.Bar, DeferredStatus.Misplaced, DeferredTiming.AfterWord);

        FeedbackPlan missingDot = MiniFeedback.Plan(Summary([Find(ErrorCode.D1, strokeId: "dot")], deferred: [dot]), Context());
        FeedbackPlan placeBar = MiniFeedback.Plan(Summary([Find(ErrorCode.D2, strokeId: "bar")], deferred: [bar]), Context());

        Assert.Equal("dot", missingDot.Parameters["part"]);
        Assert.Equal("bar", placeBar.Parameters["part"]);
    }

    [Fact]
    public void Only_one_hint_per_attempt_chosen_by_methodology_priority()
    {
        // Порядок методики §7.3: P1/P2 (3) → P3–P6 (4) → F2 (6) → D1–D3 (7) → F4/F5 (8) → F1 (9). Входной порядок не важен.
        ErrorCode[] byPriority = [ErrorCode.P1, ErrorCode.P3, ErrorCode.F2, ErrorCode.D1, ErrorCode.F4, ErrorCode.F1];
        for (int skip = 0; skip < byPriority.Length; skip++)
        {
            Finding[] findings = [.. byPriority.Skip(skip).Reverse().Select(c => Find(c))];

            FeedbackPlan plan = MiniFeedback.Plan(Summary(findings, processOk: false), Context());

            Assert.Equal(MiniFeedback.KeyFor(byPriority[skip]), plan.HintKey);
        }

        Assert.True(MiniFeedback.Priority(ErrorCode.P1) < MiniFeedback.Priority(ErrorCode.P3));
        Assert.True(MiniFeedback.Priority(ErrorCode.P6) < MiniFeedback.Priority(ErrorCode.F2));
        Assert.True(MiniFeedback.Priority(ErrorCode.D3) < MiniFeedback.Priority(ErrorCode.F4));
        Assert.True(MiniFeedback.Priority(ErrorCode.F5) < MiniFeedback.Priority(ErrorCode.F1));
    }

    [Fact]
    public void Equal_priority_goes_to_the_heavier_finding_then_to_the_first_in_the_summary()
    {
        Finding light = Find(ErrorCode.P3, severity: 0.2, strokeId: "s1");
        Finding heavy = Find(ErrorCode.P5, severity: 0.8, strokeId: "s2");

        FeedbackPlan plan = MiniFeedback.Plan(Summary([light, heavy], processOk: false), Context());

        Assert.Equal(FeedbackKeys.MissingStroke, plan.HintKey);
        Assert.Equal("s2", plan.Highlight!.Span.StrokeId);
    }

    [Fact]
    public void D4_never_gives_a_hint_and_never_counts_as_an_error()
    {
        foreach (FindingKind kind in new[] { FindingKind.Info, FindingKind.Error })
        {
            // В том числе строгий режим (D4 как ошибка): в V0 подсказки о времени точки нет совсем.
            FeedbackPlan alone = MiniFeedback.Plan(Summary([Find(ErrorCode.D4, kind)]), Context());
            Assert.Null(alone.HintKey);
            Assert.Equal(FeedbackTone.Positive, alone.Tone);
            Assert.NotEqual(FeedbackReason.Hint, alone.Reason);
            Assert.False(MiniFeedback.Record(alone, Context()).ProcessError);
        }

        // D4 вместе с настоящей ошибкой: подсказка про ошибку, не про D4.
        FeedbackPlan withError = MiniFeedback.Plan(Summary([Find(ErrorCode.D4, FindingKind.Info), Find(ErrorCode.D1)]), Context());
        Assert.Equal(FeedbackKeys.DeferredMissing, withError.HintKey);
    }

    [Fact]
    public void Informational_findings_are_never_hints()
    {
        FeedbackPlan plan = MiniFeedback.Plan(Summary([Find(ErrorCode.P2, FindingKind.Info), Find(ErrorCode.F4, FindingKind.Info)]), Context());

        Assert.Null(plan.HintKey);
        Assert.NotEqual(FeedbackReason.Hint, plan.Reason);
    }

    [Fact]
    public void U0_gives_a_neutral_try_again_without_an_error_and_a_single_retry()
    {
        EvaluationSummary u0 = Summary([], Decision.Unrecognized, Confidence.Low, form: 0.0, processOk: false);

        FeedbackPlan first = MiniFeedback.Plan(u0, Context());

        Assert.Equal(FeedbackKeys.Unrecognized, first.HintKey);
        Assert.Equal(FeedbackTone.Neutral, first.Tone);
        Assert.Null(first.Code);
        Assert.Null(first.Highlight);
        Assert.False(first.CountsAsAttempt); // одна повторная попытка в том же номере
        Assert.Equal(FeedbackReason.Unrecognized, first.Reason);
        Assert.False(MiniFeedback.Record(first, Context()).ProcessError);

        // Повторная U0: идём дальше без оценки («спасибо, дальше»), попытка засчитана.
        HintHistory history = HintHistory.Empty.Add(MiniFeedback.Record(first, Context()));
        FeedbackPlan second = MiniFeedback.Plan(u0, Context(history));
        Assert.Equal(FeedbackKeys.Next, second.HintKey);
        Assert.True(second.CountsAsAttempt);
        Assert.Equal(FeedbackReason.SkipUnrecognized, second.Reason);
    }

    [Fact]
    public void Low_confidence_is_treated_like_U0_and_never_gives_a_negative_hint()
    {
        EvaluationSummary low = Summary([Find(ErrorCode.P1), Find(ErrorCode.F1)], Decision.Target, Confidence.Low, processOk: false);

        FeedbackPlan plan = MiniFeedback.Plan(low, Context());

        Assert.Equal(FeedbackKeys.Unrecognized, plan.HintKey);
        Assert.Null(plan.Code);
        Assert.Equal(FeedbackTone.Neutral, plan.Tone);
    }

    [Fact]
    public void The_same_hint_three_times_in_a_row_is_replaced_by_a_change_of_method()
    {
        EvaluationSummary p1 = Summary([Find(ErrorCode.P1, from: 0, to: 0.15)], processOk: false);
        HintHistory history = HintHistory.Empty;
        var plans = new List<FeedbackPlan>();
        for (int attempt = 1; attempt <= 4; attempt++)
        {
            FeedbackContext context = Context(history, attempt);
            FeedbackPlan plan = MiniFeedback.Plan(p1, context);
            plans.Add(plan);
            history = history.Add(MiniFeedback.Record(plan, context));
        }

        // 1 и 2: текст подсказки. 3: текст не повторяется — анимация заново и опора вверх. 4: цепочка началась заново.
        Assert.Equal([FeedbackKeys.Start, FeedbackKeys.Start, FeedbackKeys.LookAgain, FeedbackKeys.Start], plans.Select(p => p.HintKey));
        Assert.Equal(FeedbackReason.Overload, plans[2].Reason);
        Assert.Equal(FeedbackAction.ReplayAnimation | FeedbackAction.AddAid, plans[2].Action);
        Assert.Equal(FeedbackTone.Neutral, plans[2].Tone);
        Assert.Equal(ErrorCode.P1, plans[2].Code);
        Assert.NotNull(plans[2].Highlight); // подсвечивается та же часть
    }

    [Fact]
    public void A_different_hint_or_a_clean_attempt_breaks_the_repeat_chain()
    {
        HintHistory history = HintHistory.Empty;
        FeedbackPlan Step(EvaluationSummary summary, int attempt)
        {
            FeedbackContext context = Context(history, attempt);
            FeedbackPlan plan = MiniFeedback.Plan(summary, context);
            history = history.Add(MiniFeedback.Record(plan, context));
            return plan;
        }

        EvaluationSummary p1 = Summary([Find(ErrorCode.P1)], processOk: false);
        EvaluationSummary p2 = Summary([Find(ErrorCode.P2)], processOk: false);
        EvaluationSummary clean = Summary([]);

        Assert.Equal(FeedbackKeys.Start, Step(p1, 1).HintKey);
        Assert.Equal(FeedbackKeys.Start, Step(p1, 2).HintKey);
        Assert.Equal(FeedbackKeys.Direction, Step(p2, 3).HintKey); // другая подсказка
        Assert.Equal(FeedbackKeys.Start, Step(p1, 4).HintKey);
        Step(clean, 5);
        Assert.Equal(FeedbackKeys.Start, Step(p1, 6).HintKey);
        Assert.Equal(FeedbackKeys.Start, Step(p1, 7).HintKey); // 2-я после чистой — не 3-я
    }

    [Fact]
    public void Frequency_fades_with_the_status_of_the_element()
    {
        EvaluationSummary shape = Summary([Find(ErrorCode.F1)], form: 0.9, processOk: true);
        EvaluationSummary poor = Summary([Find(ErrorCode.F1)], form: 0.5, processOk: true);
        EvaluationSummary start = Summary([Find(ErrorCode.P1)], form: 0.9, processOk: false);

        // Изучается / знакома: после каждой попытки.
        for (int n = 1; n <= 4; n++)
        {
            Assert.NotNull(MiniFeedback.Plan(shape, Context(attemptNo: n, status: ElementStatus.Learning)).HintKey);
            Assert.NotNull(MiniFeedback.Plan(shape, Context(attemptNo: n, status: ElementStatus.Familiar)).HintKey);
        }

        // Освоена: сводка после серии из N попыток; стоп-ошибка — после каждой.
        int every = Content.Config.Feedback.MasteredEveryN;
        for (int n = 1; n <= 2 * every; n++)
        {
            FeedbackPlan plan = MiniFeedback.Plan(shape, Context(attemptNo: n, status: ElementStatus.Mastered));
            Assert.Equal(n % every == 0, plan.HintKey is not null);
            if (plan.HintKey is null)
            {
                Assert.Equal(FeedbackReason.Faded, plan.Reason);
                Assert.Equal(ErrorCode.F1, plan.Code); // ошибка записана для модели знаний, но не показана
            }

            Assert.NotNull(MiniFeedback.Plan(start, Context(attemptNo: n, status: ElementStatus.Mastered)).HintKey);
        }

        // Устойчива: только за коридором допуска (form ниже порога) и при стоп-ошибках.
        Assert.Null(MiniFeedback.Plan(shape, Context(status: ElementStatus.Stable)).HintKey);
        Assert.NotNull(MiniFeedback.Plan(poor, Context(status: ElementStatus.Stable)).HintKey);
        Assert.NotNull(MiniFeedback.Plan(start, Context(status: ElementStatus.Stable)).HintKey);
    }

    [Fact]
    public void Two_process_errors_in_a_row_raise_the_frequency_one_step()
    {
        // Фоновая ошибка формы при статусе «освоена» не показывается, но после двух процессных ошибок подряд частота выше.
        EvaluationSummary shape = Summary([Find(ErrorCode.F1)], form: 0.9, processOk: true);
        Assert.Null(MiniFeedback.Plan(shape, Context(attemptNo: 1, status: ElementStatus.Mastered)).HintKey);

        HintHistory history = HintHistory.Empty;
        foreach (ErrorCode code in new[] { ErrorCode.P4, ErrorCode.P5 })
        {
            FeedbackContext context = Context(history);
            FeedbackPlan plan = MiniFeedback.Plan(Summary([Find(code)], processOk: false), context);
            history = history.Add(MiniFeedback.Record(plan, context));
        }

        Assert.Equal(2, history.ProcessErrorStreak("a"));
        Assert.NotNull(MiniFeedback.Plan(shape, Context(history, attemptNo: 1, status: ElementStatus.Mastered)).HintKey);
    }

    [Fact]
    public void Praise_follows_a_fixed_hint_names_the_process_and_highlights_the_fixed_part_green()
    {
        EvaluationSummary p1 = Summary([Find(ErrorCode.P1, from: 0, to: 0.15)], processOk: false);
        FeedbackContext first = Context();
        FeedbackPlan hint = MiniFeedback.Plan(p1, first);
        HintHistory history = HintHistory.Empty.Add(MiniFeedback.Record(hint, first));

        FeedbackPlan fixedPlan = MiniFeedback.Plan(Summary([]), Context(history, 2));

        Assert.Null(fixedPlan.HintKey);
        Assert.Equal(FeedbackKeys.PraiseStart, fixedPlan.PraiseKey);
        Assert.Equal(FeedbackTone.Positive, fixedPlan.Tone);
        Assert.Equal(hint.Highlight, fixedPlan.Highlight);
        Assert.Equal(FeedbackReason.Praise, fixedPlan.Reason);
        Assert.False(MiniFeedback.Record(fixedPlan, Context(history, 2)).ProcessError);
    }

    [Theory]
    [InlineData(ErrorCode.P1, FeedbackKeys.PraiseStart)]
    [InlineData(ErrorCode.P2, FeedbackKeys.PraiseDirection)]
    [InlineData(ErrorCode.P3, FeedbackKeys.PraiseOrder)]
    [InlineData(ErrorCode.P5, FeedbackKeys.PraiseStrokes)]
    [InlineData(ErrorCode.D1, FeedbackKeys.PraiseStrokes)]
    [InlineData(ErrorCode.F2, FeedbackKeys.PraiseShape)]
    [InlineData(ErrorCode.F4, FeedbackKeys.PraiseShape)]
    public void Fixed_error_praise_names_what_was_fixed(ErrorCode code, string praise)
    {
        FeedbackContext first = Context();
        FeedbackPlan hint = MiniFeedback.Plan(Summary([Find(code)], processOk: false), first);
        HintHistory history = HintHistory.Empty.Add(MiniFeedback.Record(hint, first));

        Assert.Equal(praise, MiniFeedback.Plan(Summary([]), Context(history, 2)).PraiseKey);
    }

    [Fact]
    public void A_clean_first_attempt_of_a_step_gets_one_process_praise_then_silence()
    {
        FeedbackContext first = Context();
        FeedbackPlan one = MiniFeedback.Plan(Summary([]), first);
        Assert.Null(one.HintKey);
        Assert.Equal(FeedbackKeys.PraisePath, one.PraiseKey);
        Assert.Equal(FeedbackTone.Positive, one.Tone);

        HintHistory history = HintHistory.Empty.Add(MiniFeedback.Record(one, first));
        FeedbackPlan two = MiniFeedback.Plan(Summary([]), Context(history, 2));
        Assert.Null(two.HintKey);
        Assert.Null(two.PraiseKey); // похвала затухает: не больше одной на шаг
        Assert.Equal(FeedbackReason.Clean, two.Reason);

        // Новый шаг — снова одна похвала.
        Assert.Equal(FeedbackKeys.PraisePath, MiniFeedback.Plan(Summary([]), Context(history, 1, step: "E04")).PraiseKey);
    }

    [Fact]
    public void A_clean_attempt_with_a_process_error_in_the_summary_gets_no_praise()
    {
        // Процесс не верен, но показать нечего (например, только F3): хвалить нельзя.
        FeedbackPlan plan = MiniFeedback.Plan(Summary([Find(ErrorCode.F3)], processOk: false), Context());

        Assert.Null(plan.PraiseKey);
        Assert.Null(plan.HintKey);
    }

    [Fact]
    public void A_plan_never_contains_more_than_one_hint_and_one_praise()
    {
        // Подсказка и похвала взаимоисключающи в одном плане (методика §7.5: не больше 1 похвалы; §7.3: одна подсказка).
        foreach (ErrorCode code in Enum.GetValues<ErrorCode>())
        {
            FeedbackPlan plan = MiniFeedback.Plan(Summary([Find(code)], processOk: false), Context());
            Assert.False(plan.HintKey is not null && plan.PraiseKey is not null, code.ToString());
        }
    }
}
