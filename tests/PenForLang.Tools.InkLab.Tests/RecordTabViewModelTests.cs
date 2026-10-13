using System.IO;
using System.IO.Compression;
using PenForLang.Core;
using PenForLang.Ink;
using PenForLang.Testing;
using PenForLang.Tools.InkLab.Corpus;
using PenForLang.Tools.InkLab.Protocol;
using PenForLang.Tools.InkLab.Recording;
using PenForLang.Tools.InkLab.Tests.Support;
using PenForLang.Tools.InkLab.ViewModels;

namespace PenForLang.Tools.InkLab.Tests;

/// <summary>Вкладка «Запись» без окна: фейковый источник пера, фейковые часы, настоящие файлы во временной папке.</summary>
public sealed class RecordTabViewModelTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeMonotonicClock _clock = new();
    private readonly FakePenInputSource _source = new();
    private int _stamp;

    public RecordTabViewModelTests()
    {
        _source.StartAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    public void Dispose() => _folder.Dispose();

    private RecordTabViewModel NewVm(RecordingProtocol? protocol = null, string subject = "adult-1")
    {
        // Время для имён файлов растёт на секунду при каждом обращении: имена уникальны и предсказуемы.
        var vm = new RecordTabViewModel(
            _folder.Path,
            _clock,
            () => new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero).AddSeconds(_stamp++),
            protocol ?? RecordingProtocol.CreateDefault(letterRepeats: 3, wordRepeats: 4))
        {
            SubjectId = subject,
            IsInputEnabled = true,
            Context = TestData.Context,
        };
        vm.Attach(_source);
        return vm;
    }

    /// <summary>Рисует штрих (u, v в x-height) в текущий момент и сдвигает часы.</summary>
    private void Draw(params (double U, double V)[] points)
    {
        long nowUs = MonotonicTime.ToMicroseconds(_clock.Ticks, _clock.Frequency);
        _source.Push(TestData.Stroke(nowUs + 1000, 1, points));
        _clock.Advance(TimeSpan.FromMilliseconds(300));
    }

    private static (double U, double V)[] Stem() => TestData.Line(0.0, 1.0, 0.0, 0.0, 12);

    private static PenLog ReadRaw(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return PenLogReader.Read(gzip);
    }

    private string SubjectDir(string subject = "adult-1") => Path.Combine(_folder.Path, subject);

    [Fact]
    public void Start_requires_a_subject_id_and_a_valid_age()
    {
        RecordTabViewModel vm = NewVm(subject: " ");
        vm.StartSession();
        Assert.False(vm.IsSessionActive);
        Assert.Contains("идентификатор", vm.Message, StringComparison.Ordinal);

        vm.SubjectId = "adult-1";
        vm.AgeText = "abc";
        vm.StartSession();
        Assert.False(vm.IsSessionActive);

        vm.AgeText = "41";
        vm.StartSession();
        Assert.True(vm.IsSessionActive);
        Assert.Equal("a-001", vm.CurrentPrompt!.Id);
        Assert.True(File.Exists(Path.Combine(SubjectDir(), CorpusStore.ProgressFileName)));
    }

    [Fact]
    public void Done_writes_a_valid_gzip_penlog_with_task_subject_and_events()
    {
        RecordTabViewModel vm = NewVm();
        vm.StartSession();
        vm.ReferenceStarted();
        Draw(Stem());
        vm.ReferenceEnded();

        vm.Done();

        string path = Assert.Single(Directory.GetFiles(SubjectDir(), "*" + CorpusStore.PenLogExtension));
        Assert.Equal(path, vm.LastWrittenPath);
        Assert.StartsWith("a-001-", Path.GetFileName(path), StringComparison.Ordinal);

        PenLog log = ReadRaw(path); // PenLogReader проверяет формат, порядок времени и обязательные поля
        Assert.Equal("inklab-v0.9/a-001", log.Header.Task!.TaskId);
        Assert.Equal("INKLAB", log.Header.Task.Exercise);
        Assert.Equal("glyph:a", log.Header.Task.Target);
        Assert.Equal("copy", log.Header.Task.Mode);
        Assert.Equal("right", log.Header.Task.Handedness);
        Assert.Equal(new PenLogSubject("adult-1", null, PenSubjectKind.Adult), log.Header.Subject);
        Assert.Null(log.Header.Consent);
        Assert.Equal(TestData.Frame.XHeightPx, log.Header.Guides.XHeightPx);

        Assert.Equal(12, log.Samples.Count());
        Assert.Equal(PenPhase.Down, log.Samples.First().Phase);
        Assert.Equal(PenPhase.Up, log.Samples.Last().Phase);
        Assert.True(log.Samples.All(s => s.TimeUs >= 0));

        InkEvent start = Assert.Single(log.Events, e => e.Name == InkEvent.Names.StimulusStart);
        Assert.Equal("glyph/a", start.Data!["key"]);
        Assert.Contains(log.Events, e => e.Name == InkEvent.Names.StimulusEnd);
        InkAttempt attempt = log.ToInkAttempt();
        Assert.Equal(InkCompletion.DoneButton, attempt.Completion);
        Assert.Single(attempt.Strokes);
    }

    [Fact]
    public void Done_saves_progress_and_moves_to_the_next_prompt_and_clears_the_canvas()
    {
        RecordTabViewModel vm = NewVm();
        vm.StartSession();
        int clears = 0;
        vm.ClearInkRequested += () => clears++;

        Draw(Stem());
        vm.Done();

        Assert.Equal("a-002", vm.CurrentPrompt!.Id);
        Assert.False(vm.HasInk);
        Assert.Equal(1, clears);
        Assert.Contains("Записано 1 из", vm.ProgressText, StringComparison.Ordinal);

        ProtocolProgress saved = ProtocolProgress.FromJson(File.ReadAllText(Path.Combine(SubjectDir(), CorpusStore.ProgressFileName)));
        Assert.Equal(PromptStatus.Done, saved.StatusOf("a-001"));
        Assert.Equal("a-002", saved.Current);
        Assert.Equal(Path.GetFileName(vm.LastWrittenPath!), saved.Prompts["a-001"].File);
    }

    [Fact]
    public void Left_handedness_and_the_optional_consent_number_reach_the_header()
    {
        RecordTabViewModel vm = NewVm();
        vm.IsLeftHanded = true;
        vm.ConsentText = "17";
        vm.AgeText = "35";
        vm.StartSession();
        Draw(Stem());
        vm.Done();

        PenLog log = ReadRaw(vm.LastWrittenPath!);
        Assert.Equal("left", log.Header.Task!.Handedness);
        Assert.Equal(new PenLogConsent(17, "trajectory_recording", "1.0"), log.Header.Consent);
        Assert.Equal(35, log.Header.Subject!.AgeYears);
        Assert.Equal(PenSubjectKind.Adult, log.Header.Subject.Kind);
    }

    [Fact]
    public void Done_without_strokes_is_refused_and_writes_nothing()
    {
        RecordTabViewModel vm = NewVm();
        vm.StartSession();

        vm.Done();

        Assert.Equal("a-001", vm.CurrentPrompt!.Id);
        Assert.Empty(Directory.GetFiles(SubjectDir(), "*" + CorpusStore.PenLogExtension));
        Assert.Contains("нет штрихов", vm.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Done_while_the_pen_is_down_is_refused()
    {
        RecordTabViewModel vm = NewVm();
        vm.StartSession();
        long nowUs = MonotonicTime.ToMicroseconds(_clock.Ticks, _clock.Frequency);
        _source.Push(TestData.Stroke(nowUs + 1000, 1, Stem()).Take(5).ToArray()); // без Up

        Assert.True(vm.IsPenDown);
        vm.Done();

        Assert.Empty(Directory.GetFiles(SubjectDir(), "*" + CorpusStore.PenLogExtension));
        Assert.Contains("поднимите перо", vm.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Done_without_a_ruler_context_keeps_the_ink_and_writes_nothing()
    {
        RecordTabViewModel vm = NewVm();
        vm.StartSession();
        vm.Context = null;
        Draw(Stem());

        vm.Done();

        Assert.Empty(Directory.GetFiles(SubjectDir(), "*" + CorpusStore.PenLogExtension));
        Assert.True(vm.HasInk);
        vm.Context = TestData.Context;
        vm.Done();
        Assert.Single(Directory.GetFiles(SubjectDir(), "*" + CorpusStore.PenLogExtension));
    }

    [Fact]
    public void Redo_clears_the_canvas_and_records_a_restart_event_inside_the_same_penlog()
    {
        RecordTabViewModel vm = NewVm();
        vm.StartSession();
        int clears = 0;
        vm.ClearInkRequested += () => clears++;

        Draw(TestData.Line(0.0, 0.0, 0.3, 0.3, 6)); // неудачная попытка
        vm.Redo();
        Draw(Stem());
        vm.Done();

        Assert.Equal(2, clears); // «Заново» и переход к следующей подсказке
        PenLog log = ReadRaw(vm.LastWrittenPath!);
        Assert.Single(log.Events, e => e.Name == InkEvent.Names.RestartButton);
        Assert.Equal(1, log.ToInkAttempt().Restarts);
        Assert.Single(log.ToInkAttempt().Strokes); // штрихи до «Заново» в попытку не входят
        Assert.Equal(2, log.ToInkAttempt(includeStrokesBeforeRestart: true).Strokes.Length);

        ProtocolProgress saved = ProtocolProgress.FromJson(File.ReadAllText(Path.Combine(SubjectDir(), CorpusStore.ProgressFileName)));
        Assert.Equal(1, saved.Prompts["a-001"].Restarts);
    }

    [Fact]
    public void Redo_is_refused_while_the_pen_is_down()
    {
        RecordTabViewModel vm = NewVm();
        vm.StartSession();
        long nowUs = MonotonicTime.ToMicroseconds(_clock.Ticks, _clock.Frequency);
        _source.Push(TestData.Stroke(nowUs + 1000, 1, Stem()).Take(4).ToArray());

        vm.Redo();

        Assert.Equal(0, vm.Restarts);
        Assert.Contains("Поднимите перо", vm.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Skip_marks_the_prompt_skipped_without_a_file_and_shows_the_next()
    {
        RecordTabViewModel vm = NewVm();
        vm.StartSession();

        vm.Skip();

        Assert.Equal("a-002", vm.CurrentPrompt!.Id);
        Assert.Empty(Directory.GetFiles(SubjectDir(), "*" + CorpusStore.PenLogExtension));
        Assert.Equal(PromptStatus.Skipped, vm.Prompts[0].Status);
        Assert.Contains("пропущено 1", vm.ProgressText, StringComparison.Ordinal);
    }

    [Fact]
    public void Back_and_rerecord_supersede_the_old_file_and_keep_one_current_recording()
    {
        RecordTabViewModel vm = NewVm();
        vm.StartSession();
        Draw(Stem());
        vm.Done();
        string first = vm.LastWrittenPath!;
        Assert.True(vm.BackCommand.CanExecute(null));

        vm.Back();
        Assert.Equal("a-001", vm.CurrentPrompt!.Id);
        Draw(TestData.Line(0.0, 1.0, 0.1, 0.0, 10));
        vm.Done();

        string second = vm.LastWrittenPath!;
        Assert.NotEqual(first, second);
        Assert.False(File.Exists(first));
        Assert.True(File.Exists(Path.Combine(SubjectDir(), CorpusStore.SupersededFolder, Path.GetFileName(first))));
        Assert.Equal(second, Assert.Single(Directory.GetFiles(SubjectDir(), "*" + CorpusStore.PenLogExtension)));
        Assert.Equal("a-002", vm.CurrentPrompt!.Id);

        ProtocolProgress saved = ProtocolProgress.FromJson(File.ReadAllText(Path.Combine(SubjectDir(), CorpusStore.ProgressFileName)));
        Assert.Equal(Path.GetFileName(second), saved.Prompts["a-001"].File);
        Assert.Equal(2, saved.Prompts["a-001"].Takes);
        Assert.Single(CorpusStore.EnumeratePenLogs(_folder.Path));
    }

    [Fact]
    public void A_new_view_model_resumes_where_the_previous_session_stopped()
    {
        RecordTabViewModel first = NewVm();
        first.StartSession();
        Draw(Stem());
        first.Done();
        first.Skip();
        first.EndSession();

        RecordTabViewModel second = NewVm();
        second.StartSession();

        Assert.Equal("a-003", second.CurrentPrompt!.Id);
        Assert.Contains("Записано 1 из", second.ProgressText, StringComparison.Ordinal);
        Assert.Equal(PromptStatus.Done, second.Prompts[0].Status);
        Assert.Equal(PromptStatus.Skipped, second.Prompts[1].Status);
        Assert.True(second.Prompts[2].IsCurrent);
        Assert.Contains("Продолжаем", second.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Different_subjects_have_separate_folders_and_progress()
    {
        RecordTabViewModel one = NewVm(subject: "adult-1");
        one.StartSession();
        Draw(Stem());
        one.Done();

        RecordTabViewModel two = NewVm(subject: "adult-2");
        two.StartSession();

        Assert.Equal("a-001", two.CurrentPrompt!.Id);
        Assert.Single(Directory.GetFiles(SubjectDir("adult-1"), "*" + CorpusStore.PenLogExtension));
        Assert.Empty(Directory.GetFiles(SubjectDir("adult-2"), "*" + CorpusStore.PenLogExtension));
        two.EndSession();
        Assert.Contains("adult-1", two.KnownSubjects);
        Assert.Contains("adult-2", two.KnownSubjects);
    }

    [Fact]
    public void Word_prompt_records_the_variant_and_the_word_target()
    {
        RecordingProtocol protocol = RecordingProtocol.Create(
            "inklab-v0.9",
            [new ProtocolStep("word:at", 2, [PromptVariants.Lift, PromptVariants.Joined])]);
        RecordTabViewModel vm = NewVm(protocol);
        vm.StartSession();
        vm.ReferenceStarted();
        Draw(TestData.Line(0.0, 0.5, 1.0, 0.5, 10));
        Draw(TestData.Line(1.2, 1.5, 1.4, 0.0, 10));
        vm.Done();

        PenLog log = ReadRaw(vm.LastWrittenPath!);
        Assert.Equal("word:at", log.Header.Task!.Target);
        Assert.Equal("inklab-v0.9/at-lift-001", log.Header.Task.TaskId);
        InkEvent start = Assert.Single(log.Events, e => e.Name == InkEvent.Names.StimulusStart);
        Assert.Equal("word/at", start.Data!["key"]);
        Assert.Equal("lift", start.Data["variant"]);
        Assert.Equal(2, log.ToInkAttempt().Strokes.Length);

        Assert.Equal("at-joined-001", vm.CurrentPrompt!.Id);
    }

    [Fact]
    public void Touching_during_the_reference_closes_the_stimulus_before_the_first_point()
    {
        RecordTabViewModel vm = NewVm();
        vm.StartSession();
        vm.ReferenceStarted();
        Draw(Stem());
        vm.Done();

        PenLog log = ReadRaw(vm.LastWrittenPath!);
        long stimulusEnd = log.Events.Single(e => e.Name == InkEvent.Names.StimulusEnd).TimeUs;
        Assert.True(stimulusEnd <= log.Samples.First().TimeUs);
    }

    [Fact]
    public void Samples_are_ignored_when_the_tab_is_not_active()
    {
        RecordTabViewModel vm = NewVm();
        vm.StartSession();
        vm.IsInputEnabled = false;

        Draw(Stem());

        Assert.False(vm.HasInk);
        Assert.Equal(0, vm.ContactSamples);
    }

    [Fact]
    public void Hover_points_are_not_recorded()
    {
        RecordTabViewModel vm = NewVm();
        vm.StartSession();
        long nowUs = MonotonicTime.ToMicroseconds(_clock.Ticks, _clock.Frequency);
        PenSample hover = new(nowUs + 500, 300, 300, 0, 0, 0, PenPhase.Hover, PenButtons.None, SampleFlags.None, 1);
        _source.Push(hover);

        Assert.False(vm.HasInk);
        Draw(Stem());
        vm.Done();
        Assert.Equal(12, ReadRaw(vm.LastWrittenPath!).Samples.Count());
    }

    [Fact]
    public void Passing_the_whole_protocol_completes_it()
    {
        RecordingProtocol protocol = RecordingProtocol.CreateDefault(letterRepeats: 1, wordRepeats: 0);
        RecordTabViewModel vm = NewVm(protocol);
        vm.StartSession();

        for (int i = 0; i < 3; i++)
        {
            Draw(Stem());
            vm.Done();
        }

        Assert.True(vm.IsComplete);
        Assert.Null(vm.CurrentPrompt);
        Assert.Equal(3, Directory.GetFiles(SubjectDir(), "*" + CorpusStore.PenLogExtension).Length);
        Assert.False(vm.DoneCommand.CanExecute(null));
        Assert.Contains("Протокол пройден", vm.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_write_keeps_the_attempt_and_the_next_done_retries_it()
    {
        RecordTabViewModel vm = NewVm();
        vm.StartSession();
        Draw(Stem());

        // Папку субъекта подменяем файлом: запись невозможна.
        Directory.Delete(SubjectDir(), recursive: true);
        File.WriteAllText(SubjectDir(), "blocker");
        vm.Done();

        Assert.Equal("a-001", vm.CurrentPrompt!.Id);
        Assert.Contains("Не удалось сохранить", vm.Message, StringComparison.Ordinal);

        File.Delete(SubjectDir());
        vm.Done();

        Assert.Equal("a-002", vm.CurrentPrompt!.Id);
        PenLog log = ReadRaw(vm.LastWrittenPath!);
        Assert.Equal(12, log.Samples.Count());
        Assert.Equal(InkCompletion.DoneButton, log.ToInkAttempt().Completion);
    }

    [Fact]
    public void Header_guides_match_the_ruler_context_and_pxpermm_is_stored_once()
    {
        RecordTabViewModel vm = NewVm();
        vm.StartSession();
        Draw(Stem());
        vm.Done();

        PenLog log = ReadRaw(vm.LastWrittenPath!);

        Assert.Equal(7.5, log.Header.Screen.PxPerMm);
        Assert.Equal(log.Header.Screen.PxPerMm, log.Header.Guides.PxPerMm);
        Assert.Equal(200, log.Header.Guides.OriginXPx);
        Assert.Equal(600, log.Header.Guides.BaselineYPx);
    }

    [Fact]
    public void The_prompt_and_its_variant_are_recorded_at_the_start_of_the_attempt_even_without_showing_the_reference()
    {
        RecordingProtocol protocol = RecordingProtocol.Create("inklab-v0.9", [new ProtocolStep("word:it", 1, [PromptVariants.Dot])]);
        RecordTabViewModel vm = NewVm(protocol);
        vm.StartSession();
        Draw(TestData.Line(0.0, 1.0, 0.0, 0.0, 10)); // эталон не показывали
        vm.Done();

        PenLog log = ReadRaw(vm.LastWrittenPath!);

        InkEvent prompt = Assert.Single(log.Events, e => e.Name == "prompt");
        Assert.Equal("word/it", prompt.Data!["key"]);
        Assert.Equal("dot", prompt.Data["variant"]);
        Assert.Equal("it-dot-001", prompt.Data["promptId"]);
        Assert.Equal(0, prompt.TimeUs);
        Assert.DoesNotContain(log.Events, e => e.Name == InkEvent.Names.StimulusStart);
    }

    [Fact]
    public void The_ruler_is_fixed_at_the_first_touch_and_a_change_during_the_attempt_blocks_done_until_redo()
    {
        RecordTabViewModel vm = NewVm();
        vm.StartSession();
        RecordingContext other = TestData.Context with { Guides = new GuideFrame(300, 650, 120, 7.5, 3.0, null, TestData.Frame.Style) };
        vm.Context = other; // до касания линейка ещё может меняться
        Draw(Stem());

        vm.Context = TestData.Context; // окно изменилось, когда чернила уже на холсте
        Assert.Contains("изменились", vm.Message, StringComparison.Ordinal);
        vm.Done();

        Assert.Empty(Directory.GetFiles(SubjectDir(), "*" + CorpusStore.PenLogExtension));
        Assert.True(vm.HasInk);

        vm.Redo();
        Draw(Stem());
        vm.Done();

        // Заголовок — по линейке, действовавшей на первом касании после «Заново» (TestData.Context), а не по первой версии.
        PenLog log = ReadRaw(vm.LastWrittenPath!);
        Assert.Equal(TestData.Frame.OriginXPx, log.Header.Guides.OriginXPx);
    }

    [Fact]
    public void A_ruler_change_after_the_first_touch_is_reported_even_for_a_dpi_only_change()
    {
        RecordTabViewModel vm = NewVm();
        vm.StartSession();
        Draw(Stem());

        vm.Context = TestData.Context with { Screen = TestData.Screen with { DpiScale = 1.5 } };

        Assert.Contains("изменились", vm.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_saved_subject_fills_the_form_and_wins_over_conflicting_form_values_with_a_warning()
    {
        RecordTabViewModel first = NewVm();
        first.IsLeftHanded = true;
        first.AgeText = "41";
        first.ConsentText = "7";
        first.StartSession();
        Draw(Stem());
        first.Done();
        first.EndSession();

        // Выбор известного субъекта заполняет форму сразу.
        RecordTabViewModel second = NewVm(subject: "other");
        second.SubjectId = "adult-1";
        Assert.True(second.IsLeftHanded);
        Assert.Equal("41", second.AgeText);
        Assert.Equal("7", second.ConsentText);

        // Форму испортили до запуска: берётся сохранённое, с предупреждением.
        second.IsLeftHanded = false;
        second.AgeText = "20";
        second.StartSession();

        Assert.True(second.IsSessionActive);
        Assert.True(second.IsLeftHanded);
        Assert.Equal("41", second.AgeText);
        Assert.Contains("сохранённого прогресса", second.Message, StringComparison.Ordinal);
        Draw(Stem());
        second.Done();
        Assert.Equal("left", ReadRaw(second.LastWrittenPath!).Header.Task!.Handedness);
    }

    [Fact]
    public void A_progress_save_failure_keeps_the_written_recording_and_is_retried_at_the_next_action()
    {
        RecordTabViewModel vm = NewVm();
        vm.StartSession();
        Draw(Stem());
        Directory.CreateDirectory(Path.Combine(SubjectDir(), CorpusStore.ProgressFileName + ".tmp")); // временный файл прогресса создать нельзя

        vm.Done();

        Assert.Single(Directory.GetFiles(SubjectDir(), "*" + CorpusStore.PenLogExtension)); // запись на диске
        Assert.Equal("a-002", vm.CurrentPrompt!.Id);
        Assert.True(vm.IsProgressUnsaved);
        Assert.Contains("прогресс записать не удалось", vm.Message, StringComparison.Ordinal);

        Directory.Delete(Path.Combine(SubjectDir(), CorpusStore.ProgressFileName + ".tmp"));
        vm.Skip();

        Assert.False(vm.IsProgressUnsaved);
        ProtocolProgress saved = ProtocolProgress.FromJson(File.ReadAllText(Path.Combine(SubjectDir(), CorpusStore.ProgressFileName)));
        Assert.Equal(PromptStatus.Done, saved.StatusOf("a-001"));
        Assert.Equal(PromptStatus.Skipped, saved.StatusOf("a-002"));
    }

    [Fact]
    public void A_failed_move_of_the_old_recording_does_not_cancel_the_new_one()
    {
        RecordTabViewModel vm = NewVm();
        vm.StartSession();
        Draw(Stem());
        vm.Done();
        string old = vm.LastWrittenPath!;
        File.WriteAllText(Path.Combine(SubjectDir(), CorpusStore.SupersededFolder), "blocker"); // на месте подпапки файл
        vm.Back();
        Draw(TestData.Line(0.0, 1.0, 0.1, 0.0, 10));

        vm.Done();

        Assert.NotEqual(old, vm.LastWrittenPath);
        Assert.True(File.Exists(vm.LastWrittenPath));
        Assert.True(File.Exists(old)); // осталась на месте
        Assert.Contains("superseded", vm.Message, StringComparison.Ordinal);
        ProtocolProgress saved = ProtocolProgress.FromJson(File.ReadAllText(Path.Combine(SubjectDir(), CorpusStore.ProgressFileName)));
        Assert.Equal(Path.GetFileName(vm.LastWrittenPath), saved.Prompts["a-001"].File);

        // При следующем запуске сверка уберёт лишний файл.
        File.Delete(Path.Combine(SubjectDir(), CorpusStore.SupersededFolder));
        vm.EndSession();
        RecordTabViewModel again = NewVm();
        again.StartSession();
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(vm.LastWrittenPath));
    }

    [Fact]
    public void A_lost_progress_file_is_rebuilt_from_the_penlog_headers_on_start()
    {
        RecordTabViewModel first = NewVm();
        first.StartSession();
        Draw(Stem());
        first.Done();
        Draw(Stem());
        first.Done();
        first.EndSession();
        File.Delete(Path.Combine(SubjectDir(), CorpusStore.ProgressFileName));
        File.Delete(Path.Combine(SubjectDir(), CorpusStore.ProgressFileName + ".bak"));

        RecordTabViewModel second = NewVm();
        second.StartSession();

        Assert.Equal("a-003", second.CurrentPrompt!.Id);
        Assert.Equal(PromptStatus.Done, second.Prompts[0].Status);
        Assert.Equal(PromptStatus.Done, second.Prompts[1].Status);
        Assert.Contains("восстановлено", second.Message, StringComparison.Ordinal);
        Assert.Equal(2, Directory.GetFiles(SubjectDir(), "*" + CorpusStore.PenLogExtension).Length);
    }

    [Fact]
    public void A_corrupt_progress_file_falls_back_to_the_backup_and_the_files_fill_the_gap()
    {
        RecordTabViewModel first = NewVm();
        first.StartSession();
        Draw(Stem());
        first.Done();
        Draw(Stem());
        first.Done();
        first.EndSession();
        File.WriteAllText(Path.Combine(SubjectDir(), CorpusStore.ProgressFileName), "{ повреждён");

        RecordTabViewModel second = NewVm();
        second.StartSession();

        Assert.True(second.IsSessionActive);
        Assert.Equal("a-003", second.CurrentPrompt!.Id);
        Assert.Contains(".bak", second.Message, StringComparison.Ordinal);
        Assert.Equal(PromptStatus.Done, second.Prompts[1].Status);
    }

    [Fact]
    public void Duplicate_files_of_one_prompt_keep_the_newest_and_the_other_goes_to_superseded()
    {
        RecordTabViewModel first = NewVm();
        first.StartSession();
        Draw(Stem());
        first.Done();
        string kept = first.LastWrittenPath!;
        first.EndSession();
        string older = Path.Combine(SubjectDir(), "a-001-19990101-000000" + CorpusStore.PenLogExtension);
        File.Copy(kept, older);
        File.SetLastWriteTimeUtc(older, new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        RecordTabViewModel second = NewVm();
        second.StartSession();

        Assert.True(File.Exists(kept));
        Assert.False(File.Exists(older));
        Assert.True(File.Exists(Path.Combine(SubjectDir(), CorpusStore.SupersededFolder, Path.GetFileName(older))));
        Assert.Single(CorpusStore.EnumeratePenLogs(_folder.Path));
    }

    [Fact]
    public void A_missing_recording_returns_its_prompt_to_pending()
    {
        RecordTabViewModel first = NewVm();
        first.StartSession();
        Draw(Stem());
        first.Done();
        File.Delete(first.LastWrittenPath!);
        first.EndSession();

        RecordTabViewModel second = NewVm();
        second.StartSession();

        Assert.Equal(PromptStatus.Pending, second.Prompts[0].Status); // записать нужно заново; место остановки (a-002) сохранено
        Assert.Equal(0, second.Prompts.Count(p => p.Status == PromptStatus.Done));
    }
}
