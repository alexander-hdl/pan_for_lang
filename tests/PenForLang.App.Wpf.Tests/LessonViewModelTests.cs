using PenForLang.App.Wpf.Lesson;
using PenForLang.Core.Geometry;
using PenForLang.Evaluation;
using PenForLang.Glyphs;
using PenForLang.Ink;
using PenForLang.Testing.SynthInk;
using static PenForLang.App.Wpf.Tests.LessonTestSupport;

namespace PenForLang.App.Wpf.Tests;

/// <summary>
/// Сценарий урока V0.8 без окна: ввод идёт через <see cref="ReplayPenSource"/> (penlog из SynthInk), оценка — настоящий
/// <see cref="LetterEvaluator"/>, подсказка — <see cref="MiniFeedback"/>.
/// </summary>
public class LessonViewModelTests
{
    public static TheoryData<string> Letters => new() { "a", "i", "t" };

    [Theory]
    [MemberData(nameof(Letters))]
    public async Task A_wrong_start_gives_fb_start_with_the_start_point_highlighted(string letter)
    {
        LessonViewModel vm = NewLesson(letter);
        vm.Next(); // показ → обводка 1

        FeedbackPlan plan = await AttemptAsync(vm, Mutated(letter, SynthMutation.WrongStart));

        Assert.Equal(FeedbackKeys.Start, plan.HintKey);
        Assert.Equal(ErrorCode.P1, plan.Code);
        Assert.Equal(HighlightKind.Start, plan.Highlight!.Kind);
        Assert.Equal(FeedbackReason.Hint, plan.Reason);

        // Подсветка на эталоне: точка старта — настоящее начало буквы, участок — начало эталона (участок из находки оценщика).
        Glyph glyph = Content.GetGlyph(letter);
        ResolvedHighlight highlight = vm.Highlight!;
        Assert.Equal(HighlightKind.Start, highlight.Kind);
        Assert.Equal(TemplateGeometry.StartPoint(glyph), highlight.StartPoint);
        Assert.NotEmpty(highlight.Polylines);
        Assert.Equal(FeedbackTone.Corrective, plan.Tone);

        // Оценщик увидел именно P1 и ничего про время точки.
        EvaluationSummary summary = vm.Outcomes[^1].Summary!;
        Assert.Contains(summary.Findings, f => f.Code == ErrorCode.P1);

        // Текст на выбранном языке (RU по умолчанию) и действие: анимация показывается снова.
        Assert.Equal(Content.Locale("ru").Get(FeedbackKeys.Start), vm.HintText);
        Assert.Equal(string.Empty, vm.PraiseText);
        Assert.True(plan.Action.HasFlag(FeedbackAction.ReplayAnimation));
    }

    [Theory]
    [MemberData(nameof(Letters))]
    public async Task A_clean_attempt_gets_no_hint_only_a_process_praise_once(string letter)
    {
        LessonViewModel vm = NewLesson(letter);
        vm.Next();

        FeedbackPlan first = await AttemptAsync(vm, Ideal(letter));

        Assert.Null(first.HintKey);
        Assert.Equal(FeedbackKeys.PraisePath, first.PraiseKey);
        Assert.Equal(FeedbackAction.None, first.Action);
        Assert.Null(first.Highlight);
        Assert.Equal(Content.Locale("ru").Get(FeedbackKeys.PraisePath), vm.PraiseText);
        Assert.Equal(string.Empty, vm.HintText);
        Assert.True(vm.Outcomes[^1].Summary!.Letters[0].ProcessOk);

        vm.Next();
        FeedbackPlan second = await AttemptAsync(vm, Ideal(letter));
        Assert.Null(second.HintKey);
        Assert.Null(second.PraiseKey); // похвала затухает
        Assert.Equal(Content.Locale("ru").Get("ui.accepted"), vm.HintText);
    }

    [Theory]
    [MemberData(nameof(Letters))]
    public async Task The_scenario_goes_demo_then_three_traces_then_two_copies_then_finishes(string letter)
    {
        LessonViewModel vm = NewLesson(letter);
        var seen = new List<(LessonPhase Phase, StepKind Kind, string Code, int Attempt)>();
        void Snap() => seen.Add((vm.Phase, vm.Step.Kind, vm.Step.Code, vm.AttemptInStep));

        Assert.Equal(LessonPhase.Demo, vm.Phase);
        Assert.Equal(["E01", "E03", "E04"], vm.Steps.Select(s => s.Code));
        Assert.Equal(5, vm.TotalAttempts);
        Assert.False(vm.AcceptsInk);
        Assert.True(vm.DemoVisible);
        Snap();

        vm.Next();
        while (vm.Phase == LessonPhase.Writing)
        {
            Snap();
            await AttemptAsync(vm, Ideal(letter));
            vm.Next();
        }

        Assert.Equal(
            [
                (LessonPhase.Demo, StepKind.Demo, "E01", 0),
                (LessonPhase.Writing, StepKind.Trace, "E03", 1),
                (LessonPhase.Writing, StepKind.Trace, "E03", 2),
                (LessonPhase.Writing, StepKind.Trace, "E03", 3),
                (LessonPhase.Writing, StepKind.Copy, "E04", 1),
                (LessonPhase.Writing, StepKind.Copy, "E04", 2),
            ],
            seen);
        Assert.Equal(LessonPhase.Finished, vm.Phase);
        Assert.Equal(5, vm.CompletedAttempts);
        Assert.Equal(5, vm.Outcomes.Count);
        Assert.False(vm.CanNext);
        Assert.False(vm.CanDone);
        Assert.Equal(Content.Locale("ru").Get("ui.finished"), vm.InstructionText);
        Assert.Equal(["E03", "E03", "E03", "E04", "E04"], vm.Outcomes.Select(o => o.StepCode));
        Assert.All(vm.Outcomes, o => Assert.Null(o.Plan.HintKey));
    }

    [Fact]
    public async Task Model_and_layers_follow_the_step_trace_under_the_pen_copy_beside_with_free_line()
    {
        LessonViewModel vm = NewLesson();
        Assert.False(vm.ModelUnderPen);
        Assert.False(vm.ModelBeside);
        Assert.True(vm.DemoVisible); // показ: анимация с нумерованными стрелками

        vm.Next();
        Assert.True(vm.ModelUnderPen); // обводка: эталон бледно поверх линеек
        Assert.False(vm.ModelBeside);
        Assert.False(vm.DemoVisible);
        Assert.True(vm.HighlightOnWritingFrame);

        for (int i = 0; i < 3; i++)
        {
            await AttemptAsync(vm, Ideal("a"));
            vm.Next();
        }

        Assert.Equal(StepKind.Copy, vm.Step.Kind);
        Assert.False(vm.ModelUnderPen); // копия: эталон рядом, писать на свободной строке
        Assert.True(vm.ModelBeside);
        Assert.True(vm.DemoVisible);
        Assert.False(vm.HighlightOnWritingFrame);
    }

    [Fact]
    public async Task The_soft_cue_fires_once_on_a_wrong_start_of_the_first_stroke_while_tracing()
    {
        LessonViewModel vm = NewLesson();
        var cues = new List<Vec2>();
        vm.LiveCueRaised += cues.Add;
        vm.Next();

        await WriteAsync(vm, Mutated("a", SynthMutation.WrongStart));

        // Сигнал пришёл по ходу письма, до «Готово»: мигает точка старта эталона; попытка не прервана.
        Assert.Equal(LessonPhase.Writing, vm.Phase);
        Vec2 start = Assert.Single(cues);
        Assert.Equal(TemplateGeometry.StartPoint(Content.GetGlyph("a")), start);
        Assert.Equal(1, vm.LiveCueSerial);
        Assert.True(vm.AcceptsInk);
    }

    [Theory]
    [MemberData(nameof(Letters))]
    public async Task The_soft_cue_stays_silent_for_a_correct_start(string letter)
    {
        LessonViewModel vm = NewLesson(letter);
        var cues = new List<Vec2>();
        vm.LiveCueRaised += cues.Add;
        vm.Next();

        await WriteAsync(vm, Ideal(letter));

        Assert.Empty(cues);
        Assert.Equal(0, vm.LiveCueSerial);
    }

    [Fact]
    public async Task The_soft_cue_is_only_for_the_trace_step_not_for_copying_on_a_free_line()
    {
        LessonViewModel vm = NewLesson();
        vm.Next();
        for (int i = 0; i < 3; i++)
        {
            await AttemptAsync(vm, Ideal("a"));
            vm.Next();
        }

        Assert.Equal(StepKind.Copy, vm.Step.Kind);
        var cues = new List<Vec2>();
        vm.LiveCueRaised += cues.Add;

        await WriteAsync(vm, Mutated("a", SynthMutation.WrongStart));

        Assert.Empty(cues);
    }

    [Fact]
    public async Task The_soft_cue_is_limited_to_one_per_attempt_even_after_restart()
    {
        LessonViewModel vm = NewLesson();
        var cues = new List<Vec2>();
        vm.LiveCueRaised += cues.Add;
        vm.Next();

        await WriteAsync(vm, Mutated("a", SynthMutation.WrongStart));
        vm.Restart();
        await WriteAsync(vm, Mutated("a", SynthMutation.WrongStart));

        Assert.Single(cues);
    }

    [Fact]
    public async Task One_hint_three_times_in_a_row_changes_the_method_and_adds_an_aid_for_two_copy_attempts()
    {
        LessonViewModel vm = NewLesson();
        vm.Next();

        // Обводка: чистая, затем две P1 подряд; копия: третья P1 подряд — смена способа.
        await AttemptAsync(vm, Ideal("a"));
        vm.Next();
        Assert.Equal(FeedbackKeys.Start, (await AttemptAsync(vm, Mutated("a", SynthMutation.WrongStart, 5))).HintKey);
        vm.Next();
        Assert.Equal(FeedbackKeys.Start, (await AttemptAsync(vm, Mutated("a", SynthMutation.WrongStart, 6))).HintKey);
        vm.Next();
        Assert.Equal(StepKind.Copy, vm.Step.Kind);
        Assert.False(vm.ModelUnderPen);

        var replays = new List<ReplayReason>();
        vm.ReplayRequested += replays.Add;
        FeedbackPlan change = await AttemptAsync(vm, Mutated("a", SynthMutation.WrongStart, 7));

        Assert.Equal(FeedbackKeys.LookAgain, change.HintKey);
        Assert.Equal(FeedbackReason.Overload, change.Reason);
        Assert.Equal(FeedbackAction.ReplayAnimation | FeedbackAction.AddAid, change.Action);
        Assert.Equal([ReplayReason.Overload], replays);
        Assert.Equal(Content.Locale("ru").Get(FeedbackKeys.LookAgain), vm.HintText);

        // Опора добавлена: следующая копия идёт с эталоном под пером (бледно поверх линеек).
        vm.Next();
        Assert.Equal(2, vm.AttemptInStep);
        Assert.True(vm.ModelUnderPen);
    }

    [Fact]
    public async Task U0_gives_one_retry_with_a_neutral_message_then_moves_on_without_scoring()
    {
        LessonViewModel vm = NewLesson();
        vm.Next();

        FeedbackPlan first = await AttemptAsync(vm, Mutated("a", SynthMutation.Scribble));

        Assert.Equal(FeedbackKeys.Unrecognized, first.HintKey);
        Assert.Equal(FeedbackReason.Unrecognized, first.Reason);
        Assert.False(first.CountsAsAttempt);
        Assert.Equal(Decision.Unrecognized, vm.Outcomes[^1].Summary!.Decision);
        Assert.Equal(Content.Locale("ru").Get(FeedbackKeys.Unrecognized), vm.HintText);
        Assert.Null(vm.Highlight);
        Assert.Equal(0, vm.CompletedAttempts);

        vm.Next();
        Assert.Equal(1, vm.AttemptInStep); // тот же номер попытки
        Assert.Equal(LessonPhase.Writing, vm.Phase);

        FeedbackPlan second = await AttemptAsync(vm, Mutated("a", SynthMutation.Scribble, 4));
        Assert.Equal(FeedbackKeys.Next, second.HintKey);
        Assert.True(second.CountsAsAttempt);
        Assert.Equal(1, vm.CompletedAttempts);

        vm.Next();
        Assert.Equal(2, vm.AttemptInStep);
    }

    [Fact]
    public async Task A_deferred_stroke_put_immediately_gives_no_error_hint_D4_is_free()
    {
        LessonViewModel vm = NewLesson("t");
        vm.Next();

        FeedbackPlan plan = await AttemptAsync(vm, Mutated("t", SynthMutation.DeferredImmediately));

        EvaluationSummary summary = vm.Outcomes[^1].Summary!;
        Finding d4 = Assert.Single(summary.Findings, f => f.Code == ErrorCode.D4);
        Assert.Equal(FindingKind.Info, d4.Kind);
        Assert.Null(plan.HintKey);
        Assert.Equal(FeedbackTone.Positive, plan.Tone);
        Assert.NotEqual(FeedbackReason.Hint, plan.Reason);
        Assert.True(plan.CountsAsAttempt);
    }

    [Fact]
    public async Task A_missing_bar_names_the_part_and_highlights_its_place_on_the_model()
    {
        LessonViewModel vm = NewLesson("t");
        vm.Next();

        FeedbackPlan plan = await AttemptAsync(vm, Mutated("t", SynthMutation.MissingDeferred));

        Assert.Equal(FeedbackKeys.DeferredMissing, plan.HintKey);
        Assert.Equal("bar", plan.Parameters["part"]);
        Assert.Equal(HighlightKind.Span, plan.Highlight!.Kind);
        Assert.Equal("bar", plan.Highlight.Span.StrokeId);
        Assert.Contains("чёрточку", vm.HintText, StringComparison.Ordinal);
        Assert.Single(vm.Highlight!.Polylines);
        Assert.Null(vm.Highlight.StartPoint);
    }

    [Fact]
    public async Task The_language_switch_changes_the_texts_without_changing_the_state()
    {
        LessonViewModel vm = NewLesson();
        vm.Next();
        await AttemptAsync(vm, Mutated("a", SynthMutation.WrongStart));
        string ru = vm.HintText;
        string stepRu = vm.InstructionText;

        vm.ToggleLanguage();

        Assert.Equal("en", vm.Language);
        Assert.Equal(Content.Locale("en").Get(FeedbackKeys.Start), vm.HintText);
        Assert.NotEqual(ru, vm.HintText);
        Assert.NotEqual(stepRu, vm.InstructionText);
        Assert.Equal(LessonPhase.Feedback, vm.Phase);
        Assert.Equal(FeedbackKeys.Start, vm.Plan!.HintKey);

        vm.ToggleLanguage();
        Assert.Equal(ru, vm.HintText);
    }

    [Fact]
    public async Task An_empty_attempt_is_not_evaluated_and_asks_to_write_first()
    {
        LessonViewModel vm = NewLesson();
        vm.Next();

        await vm.DoneAsync();

        Assert.Equal(LessonPhase.Writing, vm.Phase);
        Assert.Empty(vm.Outcomes);
        Assert.Equal(Content.Locale("ru").Get("ui.writeFirst"), vm.HintText);
    }

    [Fact]
    public async Task Restart_clears_the_canvas_without_penalty_and_a_second_restart_replays_the_animation()
    {
        LessonViewModel vm = NewLesson();
        var cleared = 0;
        var replays = new List<ReplayReason>();
        vm.InkClearRequested += () => cleared++;
        vm.ReplayRequested += replays.Add;
        vm.Next();
        int baseline = cleared;
        await WriteAsync(vm, Mutated("a", SynthMutation.WrongStart));

        vm.Restart();
        Assert.Equal(1, vm.Restarts);
        Assert.Empty(replays);

        vm.Restart();
        Assert.Equal(2, vm.Restarts);
        Assert.Equal([ReplayReason.Restarts], replays);
        Assert.Equal(baseline + 2, cleared);
        Assert.Equal(Content.Locale("ru").Get(FeedbackKeys.LookAgain), vm.HintText);

        // После «Заново» чернила прежней попытки не оцениваются: пустая попытка.
        await vm.DoneAsync();
        Assert.Equal(LessonPhase.Writing, vm.Phase);
        Assert.Empty(vm.Outcomes);

        // Новая запись оценивается как обычно, а число «Заново» остаётся в попытке.
        FeedbackPlan plan = await AttemptAsync(vm, Ideal("a"));
        Assert.Null(plan.HintKey);
    }

    [Fact]
    public async Task Evaluation_runs_off_the_calling_thread_and_does_not_block_it()
    {
        var evaluationThreads = new List<int>();
        var gate = new TaskCompletionSource();
        var runtime = new LessonRuntime(
            work => Task.Run(async () =>
            {
                evaluationThreads.Add(Environment.CurrentManagedThreadId);
                await gate.Task;
                return work();
            }),
            null,
            PenTimeBase.TaskRelative,
            LessonRuntime.Inline().Device);
        LessonViewModel vm = NewLesson(runtime: runtime);
        vm.Next();
        await WriteAsync(vm, Ideal("a"));

        int callerThread = Environment.CurrentManagedThreadId;
        Task done = vm.DoneAsync();

        // Пока оценка не вернулась, вызывающий поток свободен, а фаза — «оценивается»; ввод не принимается.
        Assert.False(done.IsCompleted);
        Assert.Equal(LessonPhase.Evaluating, vm.Phase);
        Assert.False(vm.AcceptsInk);
        Assert.False(vm.CanNext);
        Assert.Equal(Content.Locale("ru").Get("ui.evaluating"), vm.HintText);

        gate.SetResult();
        await done;

        Assert.Equal(LessonPhase.Feedback, vm.Phase);
        Assert.Single(evaluationThreads);
        Assert.NotEqual(callerThread, evaluationThreads[0]);
    }

    [Fact]
    public async Task An_evaluator_failure_gives_a_thank_you_and_an_unscored_attempt_not_a_crash()
    {
        var runtime = new LessonRuntime(
            _ => Task.FromException<EvaluationResult>(new InvalidOperationException("boom")),
            null,
            PenTimeBase.TaskRelative,
            LessonRuntime.Inline().Device);
        LessonViewModel vm = NewLesson(runtime: runtime);
        vm.Next();

        FeedbackPlan plan = await AttemptAsync(vm, Ideal("a"));

        Assert.Equal(FeedbackKeys.Next, plan.HintKey);
        Assert.Equal(FeedbackReason.EvaluationFailed, plan.Reason);
        Assert.True(plan.CountsAsAttempt);
        Assert.Null(vm.Outcomes[^1].Summary);
        Assert.Contains("boom", vm.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Letters_can_be_changed_only_at_the_start_and_after_the_end_and_restart_the_lesson()
    {
        LessonViewModel vm = NewLesson();
        Assert.True(vm.CanSelectLetter);
        vm.SelectLetter("t");
        Assert.Equal("t", vm.Letter);
        Assert.Equal(LessonPhase.Demo, vm.Phase);

        vm.Next();
        Assert.False(vm.CanSelectLetter);
        vm.SelectLetter("i");
        Assert.Equal("t", vm.Letter);

        await AttemptAsync(vm, Ideal("t"));
        Assert.Single(vm.History.Records);
        vm.RestartLesson();
        Assert.Equal(LessonPhase.Demo, vm.Phase);
        Assert.Empty(vm.History.Records);
        Assert.Empty(vm.Outcomes);
        Assert.Throws<KeyNotFoundException>(() => new LessonViewModel(Content, LessonRuntime.Inline(), "ru", "z"));
    }

    [Fact]
    public async Task Pen_samples_outside_a_writing_attempt_are_ignored()
    {
        LessonViewModel vm = NewLesson();

        await WriteAsync(vm, Ideal("a")); // показ: письма нет

        vm.Next();
        await vm.DoneAsync(); // попытка пуста: показ не дал чернил
        Assert.Empty(vm.Outcomes);
    }

    [Fact]
    public async Task Highlight_resolves_on_the_model_for_a_closed_oval_span()
    {
        LessonViewModel vm = NewLesson();
        vm.Next();

        FeedbackPlan plan = await AttemptAsync(vm, Mutated("a", SynthMutation.OpenOval));

        Assert.NotNull(plan.HintKey);
        Assert.NotNull(vm.Highlight);
        Assert.NotEmpty(vm.Highlight.Polylines);
        Assert.All(vm.Highlight.Polylines, line => Assert.True(line.Length >= 2));
    }
}
