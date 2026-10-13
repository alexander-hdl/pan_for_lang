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
/// <see cref="LetterEvaluator"/>, подсказка — <see cref="MiniFeedback"/>, время фаз — управляемые часы.
/// </summary>
public class LessonViewModelTests
{
    public static TheoryData<string> Letters => new() { "a", "i", "t" };

    [Theory]
    [MemberData(nameof(Letters))]
    public async Task A_wrong_start_gives_fb_start_with_the_start_point_highlighted(string letter)
    {
        LessonViewModel vm = NewLesson(letter);
        Begin(vm); // показ → обводка (E02)

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

        // Оценщик увидел именно P1.
        EvaluationSummary summary = vm.Outcomes[^1].Summary!;
        Assert.Contains(summary.Findings, f => f.Code == ErrorCode.P1);

        // Текст на выбранном языке (RU по умолчанию) и действие: анимация показывается снова.
        Assert.Equal(Content.Locale("ru").Get(FeedbackKeys.Start), vm.HintText);
        Assert.Equal(string.Empty, vm.PraiseText);
        Assert.True(plan.Action.HasFlag(FeedbackAction.ReplayAnimation));
    }

    [Theory]
    [MemberData(nameof(Letters))]
    public async Task A_clean_attempt_gets_no_hint_only_a_process_praise_once_per_step(string letter)
    {
        LessonViewModel vm = NewLesson(letter);
        Begin(vm);
        FeedbackPlan trace = await AttemptAsync(vm, Ideal(letter));
        Assert.Null(trace.HintKey);
        Assert.Equal(FeedbackKeys.PraisePath, trace.PraiseKey);
        Assert.Equal(FeedbackAction.None, trace.Action);
        Assert.Null(trace.Highlight);
        Assert.Equal(Content.Locale("ru").Get(FeedbackKeys.PraisePath), vm.PraiseText);
        Assert.Equal(string.Empty, vm.HintText);
        Assert.True(vm.Outcomes[^1].Summary!.Letters[0].ProcessOk);

        // Обводка чистая с первого раза: второй попытки нет, дальше копия.
        vm.Next();
        Assert.Equal(StepKind.Copy, vm.Step.Kind);
        FeedbackPlan first = await AttemptAsync(vm, Ideal(letter));
        Assert.Equal(FeedbackKeys.PraisePath, first.PraiseKey); // новый шаг — одна похвала
        vm.Next();
        FeedbackPlan second = await AttemptAsync(vm, Ideal(letter));
        Assert.Null(second.HintKey);
        Assert.Null(second.PraiseKey); // похвала затухает
        Assert.Equal(Content.Locale("ru").Get("ui.accepted"), vm.HintText);
    }

    [Theory]
    [MemberData(nameof(Letters))]
    public async Task The_scenario_goes_show_trace_three_copies_two_memory_attempts_then_finishes(string letter)
    {
        LessonViewModel vm = NewLesson(letter);
        var seen = new List<(LessonPhase Phase, StepKind Kind, string Code, int Attempt, SupportLevel Level)>();
        void Snap() => seen.Add((vm.Phase, vm.Step.Kind, vm.Step.Code, vm.AttemptInStep, vm.Step.Level));

        Assert.Equal(LessonPhase.Demo, vm.Phase);
        Assert.Equal(["E01", "E02", "E03", "E04", "E04"], vm.Steps.Select(s => s.Code));
        Assert.Equal([StepKind.Demo, StepKind.Trace, StepKind.Copy, StepKind.Memory, StepKind.Memory], vm.Steps.Select(s => s.Kind));
        Assert.False(vm.AcceptsInk);
        Assert.True(vm.DemoVisible);
        Snap();

        Begin(vm);
        while (vm.Phase != LessonPhase.Finished)
        {
            Snap();
            await AttemptAsync(vm, Ideal(letter));
            vm.Next();
        }

        Assert.Equal(
            [
                (LessonPhase.Demo, StepKind.Demo, "E01", 0, SupportLevel.L4),
                (LessonPhase.Writing, StepKind.Trace, "E02", 1, SupportLevel.L4),
                (LessonPhase.Writing, StepKind.Copy, "E03", 1, SupportLevel.L3),
                (LessonPhase.Writing, StepKind.Copy, "E03", 2, SupportLevel.L3),
                (LessonPhase.Writing, StepKind.Copy, "E03", 3, SupportLevel.L3),
                (LessonPhase.Look, StepKind.Memory, "E04", 1, SupportLevel.L2),
                (LessonPhase.Look, StepKind.Memory, "E04", 1, SupportLevel.L1),
            ],
            seen);
        Assert.Equal(6, vm.CompletedAttempts);
        Assert.Equal(6, vm.TotalAttempts);
        Assert.Equal(6, vm.Outcomes.Count);
        Assert.False(vm.CanNext);
        Assert.False(vm.CanDone);
        Assert.Equal(Content.Locale("ru").Get("ui.finished"), vm.InstructionText);
        Assert.Equal(["E02", "E03", "E03", "E03", "E04", "E04"], vm.Outcomes.Select(o => o.StepCode));
        Assert.All(vm.Outcomes, o => Assert.Null(o.Plan.HintKey));
    }

    [Fact]
    public void The_demo_plays_itself_twice_and_next_opens_after_the_first_full_show_with_the_verbal_label()
    {
        LessonViewModel vm = NewLesson();
        var replays = new List<ReplayReason>();
        vm.ReplayRequested += replays.Add;

        Assert.False(vm.CanNext); // пока не досмотрели первый показ, «Дальше» нет
        Assert.True(vm.CanShowAgain);
        Assert.Contains("назад по кругу", vm.HintText, StringComparison.Ordinal); // словесная метка из ключей сегментов глифа (a.backAround)
        Assert.Equal(Content.Phrase("a", "ru"), vm.HintText);

        vm.DemoPlaybackFinished();
        Assert.True(vm.CanNext);
        Assert.Equal([ReplayReason.Auto], replays); // второй проигрыш — сам

        vm.DemoPlaybackFinished();
        Assert.Equal([ReplayReason.Auto], replays); // больше сам не повторяет

        vm.ShowAgain(); // «Покажи ещё раз» — по кнопке
        Assert.Equal([ReplayReason.Auto, ReplayReason.Button], replays);

        vm.ToggleLanguage();
        Assert.Contains("back around", vm.HintText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_second_trace_attempt_is_given_only_after_a_process_error_in_the_first()
    {
        LessonViewModel clean = NewLesson();
        Begin(clean);
        Assert.Equal(6, clean.TotalAttempts); // 1 (обводка, минимум) + 3 + 2
        await AttemptAsync(clean, Ideal("a"));
        clean.Next();
        Assert.Equal(StepKind.Copy, clean.Step.Kind);

        LessonViewModel wrong = NewLesson();
        Begin(wrong);
        await AttemptAsync(wrong, Mutated("a", SynthMutation.WrongStart));
        Assert.Equal(7, wrong.TotalAttempts); // обводка стала двухпопыточной
        wrong.Next();
        Assert.Equal(StepKind.Trace, wrong.Step.Kind);
        Assert.Equal(2, wrong.AttemptInStep);
        await AttemptAsync(wrong, Ideal("a"));
        wrong.Next();
        Assert.Equal(StepKind.Copy, wrong.Step.Kind); // две попытки — максимум
        Assert.Equal(7, wrong.TotalAttempts);
    }

    [Fact]
    public async Task Model_and_layers_follow_the_step_trace_under_the_pen_copy_above_memory_flash_then_hidden()
    {
        LessonViewModel vm = NewLesson();
        Assert.False(vm.ModelUnderPen);
        Assert.False(vm.ModelAbove);
        Assert.True(vm.DemoVisible); // показ: анимация с нумерованными стрелками

        Begin(vm);
        Assert.True(vm.ModelUnderPen); // обводка: эталон бледно поверх линеек
        Assert.False(vm.ModelAbove);
        Assert.False(vm.DemoVisible);
        Assert.True(vm.HighlightOnWritingFrame);
        await AttemptAsync(vm, Ideal("a"));
        vm.Next();

        Assert.Equal(StepKind.Copy, vm.Step.Kind);
        Assert.False(vm.ModelUnderPen); // копия: эталон над строкой
        Assert.True(vm.ModelAbove);
        Assert.True(vm.DemoVisible);
        Assert.False(vm.HighlightOnWritingFrame);
        Assert.True(vm.ArrowsVisible);
        Assert.False(vm.StartDotOnWritingRow);

        for (int i = 0; i < 3; i++)
        {
            await AttemptAsync(vm, Ideal("a"));
            vm.Next();
        }

        // Письмо по памяти, L2: эталон виден, потом скрыт, пауза, письмо с точкой старта на строке.
        Assert.Equal(LessonPhase.Look, vm.Phase);
        Assert.True(vm.ModelAbove);
        Assert.False(vm.StartDotOnWritingRow);
        Advance(vm, 2001);
        Assert.Equal(LessonPhase.Delay, vm.Phase);
        Assert.False(vm.ModelAbove);
        Assert.False(vm.DemoVisible);
        Advance(vm, 1001);
        Assert.Equal(LessonPhase.Writing, vm.Phase);
        Assert.True(vm.StartDotOnWritingRow);
        Assert.False(vm.ModelAbove);
        Assert.False(vm.ModelUnderPen);
    }

    [Fact]
    public async Task Arrows_on_the_copy_model_hide_after_the_second_correct_attempt()
    {
        LessonViewModel vm = NewLesson();
        Begin(vm);
        await AttemptAsync(vm, Ideal("a"));
        vm.Next();

        var visible = new List<bool>();
        for (int i = 0; i < 3; i++)
        {
            visible.Add(vm.ArrowsVisible);
            await AttemptAsync(vm, Ideal("a"));
            vm.Next();
        }

        Assert.Equal([true, true, false], visible);
        Assert.Equal(LessonPhase.Look, vm.Phase);
        Assert.True(vm.ArrowsVisible); // правило только для копии
    }

    [Fact]
    public async Task Memory_attempts_show_the_model_two_seconds_hide_it_pause_one_second_and_accept_no_pen_until_write()
    {
        LessonViewModel vm = NewLesson();
        var stimulus = new List<bool>();
        vm.StimulusChanged += stimulus.Add;
        Begin(vm);
        await AttemptAsync(vm, Ideal("a"));
        vm.Next();
        for (int i = 0; i < 3; i++)
        {
            await AttemptAsync(vm, Ideal("a"));
            vm.Next();
        }

        // L2: вспышка 2000 мс.
        Assert.Equal(LessonPhase.Look, vm.Phase);
        Assert.Equal(SupportLevel.L2, vm.Step.Level);
        Assert.Equal(2000, vm.Step.FlashMs);
        Assert.Equal(1000, vm.Step.DelayMs);
        Assert.Equal(Content.Locale("ru").Get("ui.step.memoryLook"), vm.InstructionText);
        await WriteAsync(vm, Ideal("a")); // перо не принимается: эталон ещё виден
        Advance(vm, 1999);
        Assert.Equal(LessonPhase.Look, vm.Phase);
        Advance(vm, 2);
        Assert.Equal(LessonPhase.Delay, vm.Phase);
        Assert.Equal([true, false], stimulus.TakeLast(2));
        await WriteAsync(vm, Ideal("a")); // пауза: только линейки, перо не принимается
        Advance(vm, 998);
        Assert.Equal(LessonPhase.Delay, vm.Phase);
        Advance(vm, 2);
        Assert.Equal(LessonPhase.Writing, vm.Phase);
        Assert.Equal(Content.Locale("ru").Get("ui.step.memoryWriteDot"), vm.InstructionText);

        // Всё, что писали в Look и Delay, потеряно: пустая попытка.
        await vm.DoneAsync();
        Assert.Equal(LessonPhase.Writing, vm.Phase);
        Assert.DoesNotContain(vm.Outcomes, o => o.StepCode == "E04");

        FeedbackPlan plan = await AttemptAsync(vm, Ideal("a"));
        Assert.Null(plan.HintKey);
        Assert.True(vm.ModelAbove); // после попытки эталон рядом со своей записью
        Assert.Equal(Content.Locale("ru").Get("ui.step.memoryCompare"), vm.InstructionText);

        // L1: без точки старта.
        vm.Next();
        Assert.Equal(SupportLevel.L1, vm.Step.Level);
        Assert.Equal(LessonPhase.Look, vm.Phase);
        PassMemoryPause(vm);
        Assert.False(vm.StartDotOnWritingRow);
        Assert.Equal(Content.Locale("ru").Get("ui.step.memoryWrite"), vm.InstructionText);
    }

    [Fact]
    public async Task The_soft_cue_fires_once_on_a_wrong_start_of_the_first_stroke_while_tracing()
    {
        LessonViewModel vm = NewLesson();
        var cues = new List<Vec2>();
        vm.LiveCueRaised += cues.Add;
        Begin(vm);

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
        Begin(vm);

        await WriteAsync(vm, Ideal(letter));

        Assert.Empty(cues);
        Assert.Equal(0, vm.LiveCueSerial);
    }

    [Fact]
    public async Task The_soft_cue_is_off_in_the_copy_step_and_in_memory_L1_but_on_in_memory_L2()
    {
        LessonViewModel vm = NewLesson();
        Begin(vm);
        await AttemptAsync(vm, Ideal("a"));
        vm.Next();
        Assert.Equal(StepKind.Copy, vm.Step.Kind);
        var cues = new List<Vec2>();
        vm.LiveCueRaised += cues.Add;

        await WriteAsync(vm, Mutated("a", SynthMutation.WrongStart));
        Assert.Empty(cues); // копия (L3): мгновенный сигнал выключен
        await vm.DoneAsync();
        vm.Next();
        await AttemptAsync(vm, Ideal("a"));
        vm.Next();
        await AttemptAsync(vm, Ideal("a"));
        vm.Next();

        Assert.Equal(SupportLevel.L2, vm.Step.Level);
        PassMemoryPause(vm);
        await WriteAsync(vm, Mutated("a", SynthMutation.WrongStart));
        Assert.Single(cues); // L2: точка старта видна, сигнал включён
        await vm.DoneAsync();
        vm.Next();

        Assert.Equal(SupportLevel.L1, vm.Step.Level);
        PassMemoryPause(vm);
        await WriteAsync(vm, Mutated("a", SynthMutation.WrongStart));
        Assert.Single(cues); // L1: без точки и без сигнала
    }

    [Fact]
    public async Task The_soft_cue_is_limited_to_one_per_attempt_even_after_restart()
    {
        LessonViewModel vm = NewLesson();
        var cues = new List<Vec2>();
        vm.LiveCueRaised += cues.Add;
        Begin(vm);

        await WriteAsync(vm, Mutated("a", SynthMutation.WrongStart));
        vm.Restart();
        await WriteAsync(vm, Mutated("a", SynthMutation.WrongStart));

        Assert.Single(cues);
    }

    [Fact]
    public async Task One_hint_three_times_in_a_row_changes_the_method_and_adds_an_aid_for_the_next_copy_attempt()
    {
        LessonViewModel vm = NewLesson();
        Begin(vm);

        // Обводка: две P1 подряд (вторая попытка нужна из-за ошибки); копия: третья P1 подряд — смена способа.
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
    public async Task A_memory_overload_turns_the_next_memory_attempt_into_a_copy_with_the_model()
    {
        // Цепочка P1 подряд: обводка 1 и 2 (две подсказки), копия 1 — смена способа (3-я), копия 2 и 3 — две подсказки, память L2 — 3-я: смена способа.
        LessonViewModel vm = NewLesson();
        Begin(vm);
        ulong seed = 30;
        for (int i = 0; i < 2; i++)
        {
            await AttemptAsync(vm, Mutated("a", SynthMutation.WrongStart, seed++));
            vm.Next();
        }

        for (int i = 0; i < 3; i++)
        {
            await AttemptAsync(vm, Mutated("a", SynthMutation.WrongStart, seed++));
            vm.Next();
        }

        Assert.Equal(SupportLevel.L2, vm.Step.Level);
        FeedbackPlan overload = await AttemptAsync(vm, Mutated("a", SynthMutation.WrongStart, seed++));
        Assert.Equal(FeedbackKeys.LookAgain, overload.HintKey);
        Assert.Equal(FeedbackReason.Overload, overload.Reason);
        Assert.Equal(FeedbackAction.ReplayAnimation | FeedbackAction.AddAid, overload.Action);

        vm.Next();

        // Следующая попытка по памяти — как копия с эталоном: без вспышки и паузы, эталон над строкой, перо принимается сразу.
        Assert.Equal(SupportLevel.L1, vm.Step.Level);
        Assert.Equal(LessonPhase.Writing, vm.Phase);
        Assert.True(vm.ModelAbove);
        Assert.False(vm.StartDotOnWritingRow);
        Assert.Equal(Content.Locale("ru").Get("ui.step.memoryAided"), vm.InstructionText);
    }

    [Fact]
    public async Task U0_gives_one_retry_with_a_neutral_message_then_moves_on_without_scoring()
    {
        LessonViewModel vm = NewLesson();
        Begin(vm);

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
        Assert.Equal(StepKind.Copy, vm.Step.Kind); // процессной ошибки нет: второй обводки не будет
        Assert.Equal(1, vm.AttemptInStep);
    }

    [Fact]
    public async Task A_deferred_stroke_put_immediately_gives_no_error_hint_D4_is_free()
    {
        LessonViewModel vm = NewLesson("t");
        Begin(vm);

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
        Begin(vm);

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
        Begin(vm);
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
        Begin(vm);

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
        Begin(vm);
        int baseline = cleared;
        await WriteAsync(vm, Mutated("a", SynthMutation.WrongStart));

        vm.Restart();
        Assert.Equal(1, vm.Restarts);
        Assert.DoesNotContain(ReplayReason.Restarts, replays);

        vm.Restart();
        Assert.Equal(2, vm.Restarts);
        Assert.Contains(ReplayReason.Restarts, replays);
        Assert.Equal(baseline + 2, cleared);
        Assert.Equal(Content.Locale("ru").Get(FeedbackKeys.LookAgain), vm.HintText);

        // После «Заново» чернила прежней попытки не оцениваются: пустая попытка.
        await vm.DoneAsync();
        Assert.Equal(LessonPhase.Writing, vm.Phase);
        Assert.Empty(vm.Outcomes);

        // Новая запись оценивается как обычно.
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
        Begin(vm);
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
    public async Task A_stale_result_is_dropped_when_the_lesson_restarts_during_evaluation()
    {
        var gate = new TaskCompletionSource();
        var runtime = new LessonRuntime(
            work => Task.Run(async () =>
            {
                await gate.Task;
                return work();
            }),
            null,
            PenTimeBase.TaskRelative,
            LessonRuntime.Inline().Device);
        LessonViewModel vm = NewLesson(runtime: runtime);
        Begin(vm);
        await WriteAsync(vm, Ideal("a"));
        Task done = vm.DoneAsync();
        Assert.Equal(LessonPhase.Evaluating, vm.Phase);

        vm.RestartLesson(); // «Сначала» во время оценки
        gate.SetResult();
        await done;

        Assert.Equal(LessonPhase.Demo, vm.Phase); // результат прежней попытки не вернул урок в «обратную связь»
        Assert.Null(vm.Plan);
        Assert.Empty(vm.Outcomes);
        Assert.Empty(vm.History.Records);
        Assert.Equal(0, vm.CompletedAttempts);
    }

    [Fact]
    public async Task An_evaluation_that_takes_too_long_gives_a_thank_you_and_an_unscored_attempt()
    {
        var never = new TaskCompletionSource<EvaluationResult>();
        var runtime = new LessonRuntime(
            _ => never.Task,
            null,
            PenTimeBase.TaskRelative,
            LessonRuntime.Inline().Device,
            TimeSpan.FromMilliseconds(50));
        LessonViewModel vm = NewLesson(runtime: runtime);
        Begin(vm);
        await WriteAsync(vm, Ideal("a"));

        await vm.DoneAsync();

        Assert.Equal(LessonPhase.Feedback, vm.Phase);
        FeedbackPlan plan = vm.Plan!;
        Assert.Equal(FeedbackKeys.Next, plan.HintKey);
        Assert.Equal(FeedbackReason.EvaluationFailed, plan.Reason);
        Assert.True(plan.CountsAsAttempt);
        Assert.Null(vm.Outcomes[^1].Summary);
        Assert.Contains("Timeout", vm.LastError, StringComparison.Ordinal);
        Assert.Equal(1, vm.CompletedAttempts);
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
        Begin(vm);

        FeedbackPlan plan = await AttemptAsync(vm, Ideal("a"));

        Assert.Equal(FeedbackKeys.Next, plan.HintKey);
        Assert.Equal(FeedbackReason.EvaluationFailed, plan.Reason);
        Assert.True(plan.CountsAsAttempt);
        Assert.Null(vm.Outcomes[^1].Summary);
        Assert.Contains("boom", vm.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Touching_the_line_in_feedback_goes_on_and_the_first_stroke_is_already_in_the_new_attempt()
    {
        LessonViewModel vm = NewLesson();
        Begin(vm);
        await AttemptAsync(vm, Mutated("a", SynthMutation.WrongStart));
        Assert.Equal(LessonPhase.Feedback, vm.Phase);
        Assert.Equal(1, vm.AttemptInStep);
        var cleared = 0;
        vm.InkClearRequested += () => cleared++;

        await WriteAsync(vm, Ideal("a")); // без «Дальше»: просто пишем

        Assert.Equal(LessonPhase.Writing, vm.Phase);
        Assert.Equal(2, vm.AttemptInStep); // вторая попытка обводки (первая была с процессной ошибкой)
        Assert.Equal(1, cleared); // экран очищен перед штрихом
        await vm.DoneAsync();
        Assert.Equal(2, vm.Outcomes.Count);
        Assert.Null(vm.Outcomes[^1].Plan.HintKey); // штрих попал в новую попытку и оценился
        Assert.True(vm.Outcomes[^1].Summary!.Letters[0].ProcessOk);
    }

    [Fact]
    public async Task A_hover_or_touch_outside_feedback_does_not_skip_it_and_a_message_waits_for_the_child()
    {
        LessonViewModel vm = NewLesson();
        Begin(vm);
        await AttemptAsync(vm, Ideal("a")); // есть сообщение (похвала)

        Advance(vm, 60_000);
        Assert.Equal(LessonPhase.Feedback, vm.Phase); // с сообщением само не уходит

        vm.OnSamples([new PenSample(1, 10, 10, 0, 0, 0, PenPhase.Hover, PenButtons.None, SampleFlags.None, 1)]);
        Assert.Equal(LessonPhase.Feedback, vm.Phase); // наведение — не касание
    }

    [Fact]
    public async Task A_clean_attempt_without_a_message_moves_on_by_itself_after_the_configured_pause()
    {
        LessonViewModel vm = NewLesson();
        Begin(vm);
        await AttemptAsync(vm, Ideal("a"));
        vm.Next();
        await AttemptAsync(vm, Ideal("a")); // копия 1: похвала
        vm.Next();
        FeedbackPlan silent = await AttemptAsync(vm, Ideal("a")); // копия 2: без сообщения
        Assert.False(silent.HasMessage);
        int pause = Content.Config.Feedback.AutoAdvanceMs;

        Advance(vm, pause - 1);
        Assert.Equal(LessonPhase.Feedback, vm.Phase);
        Advance(vm, 2);

        Assert.Equal(LessonPhase.Writing, vm.Phase);
        Assert.Equal(3, vm.AttemptInStep);
    }

    [Fact]
    public async Task Letters_can_be_changed_only_at_the_start_and_after_the_end_and_restart_the_lesson()
    {
        LessonViewModel vm = NewLesson();
        Assert.True(vm.CanSelectLetter);
        vm.SelectLetter("t");
        Assert.Equal("t", vm.Letter);
        Assert.Equal(LessonPhase.Demo, vm.Phase);

        Begin(vm);
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

        Begin(vm);
        await vm.DoneAsync(); // попытка пуста: показ не дал чернил
        Assert.Empty(vm.Outcomes);
    }

    [Fact]
    public async Task Highlight_resolves_on_the_model_for_a_closed_oval_span()
    {
        LessonViewModel vm = NewLesson();
        Begin(vm);

        FeedbackPlan plan = await AttemptAsync(vm, Mutated("a", SynthMutation.OpenOval));

        Assert.NotNull(plan.HintKey);
        Assert.NotNull(vm.Highlight);
        Assert.NotEmpty(vm.Highlight.Polylines);
        Assert.All(vm.Highlight.Polylines, line => Assert.True(line.Length >= 2));
    }
}
