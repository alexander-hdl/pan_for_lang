using System.IO;
using PenForLang.Ink;
using PenForLang.Tools.InkLab.Annotation;
using PenForLang.Tools.InkLab.Corpus;
using PenForLang.Tools.InkLab.Scene;
using PenForLang.Tools.InkLab.Tests.Support;
using PenForLang.Tools.InkLab.ViewModels;

namespace PenForLang.Tools.InkLab.Tests;

/// <summary>Вкладка «Разметка» без окна: два клика на стык, файл на разметчика, привязка к записи, отказоустойчивость.</summary>
public sealed class AnnotateTabViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static AnnotateTabViewModel NewVm(string annotator = "A") => new(null, () => Now, annotator);

    private string LiftedPath(string name = "lifted") => TestData.WriteLog(_folder.Path, name, TestData.AtWordLifted());

    private string JoinedPath(string name = "joined") => TestData.WriteLog(_folder.Path, name, TestData.AtWordJoined());

    /// <summary>Отметить перекладину (штрих 2 слова с отрывом) отложенной и поставить стык a|t: выход — у конца a, вход — у верха ствола t.</summary>
    private static void AnnotateLifted(AnnotateTabViewModel vm)
    {
        vm.SelectedLetter = 1;
        vm.ToggleDeferred(2);
        vm.HandleClick(0.93, 0.5); // выход a
        vm.HandleClick(1.2, 1.48); // вход t
    }

    [Fact]
    public void A_new_recording_gets_the_text_from_the_target_and_an_empty_junction_slot_waiting_for_the_exit()
    {
        AnnotateTabViewModel vm = NewVm();

        Assert.True(vm.Load(LiftedPath()));

        Assert.Equal("at", vm.Text);
        Assert.Equal("word:at", vm.Target);
        Assert.Equal("lift", vm.Variant);
        JunctionRow slot = Assert.Single(vm.Junctions);
        Assert.Equal("a|t", slot.Label);
        Assert.False(slot.IsPlaced);
        Assert.True(slot.IsSelected);
        Assert.Equal(PlacePhase.End, slot.Phase);
        Assert.False(vm.IsValid);
        Assert.Contains(vm.Issues, i => i.Code == IssueCodes.JunctionCount);
        Assert.Equal(3, vm.Strokes.Length);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void Two_clicks_place_the_exit_and_then_the_entry_and_the_kind_follows_the_strokes()
    {
        AnnotateTabViewModel vm = NewVm();
        vm.Load(LiftedPath());
        vm.SelectedLetter = 1;
        vm.ToggleDeferred(2);

        vm.HandleClick(0.93, 0.5);

        JunctionRow j = vm.Junctions.Single();
        Assert.Equal(new StrokePoint(0, 19), j.End);
        Assert.Null(j.Start);
        Assert.Equal(PlacePhase.Start, j.Phase);
        Assert.False(j.IsPlaced);
        Assert.Contains("вход не поставлен", j.PointText, StringComparison.Ordinal);
        Assert.Contains(vm.Scene.Markers, m => m.Label == "a|"); // выход виден сразу

        vm.HandleClick(1.2, 1.48);

        Assert.Equal(new StrokePoint(1, 0), j.Start);
        Assert.True(j.IsPlaced);
        Assert.Equal(JunctionKind.Lifted, j.Kind); // разные штрихи — отрыв пера, выбирать вид не нужно
        Assert.Equal("отрыв пера", j.KindText);
        Assert.True(vm.IsValid);
        Assert.True(vm.IsDirty);
        Assert.Contains(vm.Scene.Markers, m => m.Label == "|t");
    }

    [Fact]
    public void Points_of_a_lifted_junction_can_be_anywhere_inside_their_strokes()
    {
        AnnotateTabViewModel vm = NewVm();
        vm.Load(LiftedPath());
        vm.ToggleDeferred(2);

        vm.HandleClick(0.5, 0.5); // середина штриха a
        vm.HandleClick(1.3, 0.75); // середина ствола t

        JunctionRow j = vm.Junctions.Single();
        Assert.Equal(new StrokePoint(0, 10), j.End);
        Assert.Equal(1, j.Start!.Value.Stroke);
        Assert.InRange(j.Start.Value.Sample, 5, 15);
        Assert.Equal(JunctionKind.Lifted, j.Kind);
        Assert.True(vm.IsValid);
    }

    [Fact]
    public void Shift_click_places_the_boundary_as_one_point_and_the_kind_is_connected_inside_one_stroke()
    {
        AnnotateTabViewModel vm = NewVm();
        vm.Load(JoinedPath());
        vm.ToggleDeferred(1);

        vm.HandleClick(1.0, 0.52, shift: true);

        JunctionRow j = vm.Junctions.Single();
        Assert.Equal(new StrokePoint(0, 20), j.End);
        Assert.Equal(j.End, j.Start);
        Assert.Equal(JunctionKind.Connected, j.Kind);
        Assert.StartsWith("одна точка", j.PointText, StringComparison.Ordinal);
        Assert.True(vm.IsValid);
    }

    [Fact]
    public void A_connected_junction_has_a_connecting_stroke_between_exit_and_entry()
    {
        AnnotateTabViewModel vm = NewVm();
        vm.Load(JoinedPath());
        vm.ToggleDeferred(1);

        vm.HandleClick(0.75, 0.5); // выход a
        vm.HandleClick(1.1, 0.3); // вход t дальше по тому же штриху

        JunctionRow j = vm.Junctions.Single();
        Assert.Equal(JunctionKind.Connected, j.Kind);
        Assert.True(j.Start!.Value > j.End!.Value);
        Assert.True(vm.IsValid);
        Assert.Contains(vm.Scene.Polylines, p => p.Group == SceneBuilder.JoinGroup);
    }

    [Fact]
    public void The_entry_cannot_be_placed_before_the_exit()
    {
        AnnotateTabViewModel vm = NewVm();
        vm.Load(JoinedPath());
        vm.ToggleDeferred(1);
        vm.HandleClick(1.0, 0.5); // выход

        vm.HandleClick(0.2, 0.5); // вход «раньше» выхода: допустимые точки — только после выхода

        JunctionRow j = vm.Junctions.Single();
        Assert.True(j.Start!.Value >= j.End!.Value);
    }

    [Fact]
    public void Points_are_limited_to_the_stretch_between_neighbouring_junctions()
    {
        AnnotateTabViewModel vm = NewVm();
        vm.Load(TestData.WriteLog(_folder.Path, "three", TestData.Log(
            "inklab-v0.9/x-001",
            "word:ati",
            [TestData.Stroke(0, 1, TestData.Line(0, 0.5, 3, 0.5, 61))])));
        Assert.Equal(2, vm.Junctions.Count);

        vm.SelectJunction(0);
        vm.HandleClick(2.0, 0.5, shift: true); // стык 0|1 в точке 40
        vm.SelectJunction(1);
        vm.HandleClick(1.5, 0.5, shift: true); // стык 1|2 «раньше» стыка 0|1: ближайшая допустимая точка — после него

        Assert.True(vm.Junctions[1].End!.Value > vm.Junctions[0].Start!.Value);
    }

    [Fact]
    public void Arrow_nudges_move_the_last_placed_point_by_one_sample_and_cross_stroke_boundaries()
    {
        AnnotateTabViewModel vm = NewVm();
        vm.Load(LiftedPath());
        vm.ToggleDeferred(2);
        vm.HandleClick(0.93, 0.5); // выход (0,19)

        vm.Nudge(+1);
        Assert.Equal(new StrokePoint(0, 20), vm.Junctions.Single().End);
        vm.Nudge(+1); // за конец штриха — на начало следующего основного
        Assert.Equal(new StrokePoint(1, 0), vm.Junctions.Single().End);
        vm.Nudge(-1);
        Assert.Equal(new StrokePoint(0, 20), vm.Junctions.Single().End);

        vm.HandleClick(1.2, 1.48); // вход (1,0): теперь активна именно эта точка
        vm.Nudge(+1);
        Assert.Equal(new StrokePoint(1, 1), vm.Junctions.Single().Start);
        vm.Nudge(-1);
        vm.Nudge(-1); // вход не может уйти раньше выхода (0,20): назад на (0,20) можно, дальше — нет
        Assert.True(vm.Junctions.Single().Start!.Value >= vm.Junctions.Single().End!.Value);
    }

    [Fact]
    public void Selecting_a_placed_junction_lets_the_annotator_place_it_again()
    {
        AnnotateTabViewModel vm = NewVm();
        vm.Load(LiftedPath());
        AnnotateLifted(vm);

        vm.SelectJunction(0);
        Assert.Equal(PlacePhase.End, vm.Junctions.Single().Phase);
        vm.HandleClick(0.5, 0.5);

        Assert.Equal(new StrokePoint(0, 10), vm.Junctions.Single().End);
        Assert.Null(vm.Junctions.Single().Start); // вход нужно поставить заново
        Assert.False(vm.IsValid);
    }

    [Fact]
    public void The_boundary_tool_ignores_deferred_strokes_and_far_clicks_are_rejected()
    {
        AnnotateTabViewModel vm = NewVm();
        vm.Load(LiftedPath());
        vm.SelectedLetter = 1;
        vm.ToggleDeferred(2); // перекладина

        vm.HandleClick(1.3, 1.0, shift: true); // ровно по перекладине — граница ляжет на ближайший основной штрих

        Assert.NotEqual(2, vm.Junctions.Single().End!.Value.Stroke);

        vm.ClearJunction(0);
        vm.HandleClick(10, 10);
        Assert.False(vm.Junctions.Single().IsPlaced);
        Assert.Contains("мимо", vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void The_deferred_tool_marks_and_unmarks_a_stroke_for_the_selected_letter()
    {
        AnnotateTabViewModel vm = NewVm();
        vm.Load(LiftedPath());
        vm.SelectedLetter = 1;
        vm.Tool = AnnotateTool.Deferred;

        vm.HandleClick(1.5, 1.0);

        DeferredRow row = Assert.Single(vm.Deferred);
        Assert.Equal(2, row.Stroke);
        Assert.Equal(1, row.Letter);
        Assert.Equal(DeferredPartKind.Crossbar, row.Kind); // буква t → перекладина
        Assert.Equal(DeferredTiming.AfterWord, row.Timing);
        Assert.Null(row.Opinion);

        vm.HandleClick(1.5, 1.0);
        Assert.Empty(vm.Deferred);
    }

    [Fact]
    public void The_dot_of_an_i_is_marked_as_a_dot_and_a_missing_dot_is_a_warning()
    {
        AnnotateTabViewModel vm = NewVm();
        vm.Load(TestData.WriteLog(_folder.Path, "i", TestData.LetterI()));
        Assert.Contains(vm.Issues, i => i.Code == IssueCodes.DeferredMissing);

        vm.Tool = AnnotateTool.Deferred;
        vm.HandleClick(0.0, 1.52);

        DeferredRow row = Assert.Single(vm.Deferred);
        Assert.Equal(1, row.Stroke);
        Assert.Equal(DeferredPartKind.Dot, row.Kind);
        Assert.True(vm.IsValid); // одна буква — стыков нет
        Assert.DoesNotContain(vm.Issues, i => i.Code == IssueCodes.DeferredMissing);
    }

    [Fact]
    public void Deferred_timing_is_derived_from_stroke_order_and_the_annotators_opinion_is_separate()
    {
        AnnotateTabViewModel vm = NewVm();
        vm.Load(LiftedPath());
        vm.ToggleDeferred(0); // после штриха 0 пишутся ещё основные штрихи → «сразу»

        Assert.Equal(DeferredTiming.Immediate, vm.Deferred.Single().Timing);

        vm.ToggleTimingOpinion(0);
        Assert.Equal(DeferredTiming.Immediate, vm.Deferred.Single().Timing); // выведенное не меняется
        Assert.Equal(DeferredTiming.AfterWord, vm.Deferred.Single().Opinion);
        Assert.Contains(vm.Issues, i => i.Code == IssueCodes.TimingOpinion);

        vm.ToggleDeferred(2); // состав основного следа изменился: выведенное время пересчитано
        Assert.Equal(DeferredTiming.Immediate, vm.Deferred.Single(d => d.Stroke == 0).Timing); // после штриха 0 по-прежнему пишется штрих 1
        Assert.Equal(DeferredTiming.AfterWord, vm.Deferred.Single(d => d.Stroke == 2).Timing);

        vm.ToggleTimingOpinion(0);
        Assert.Null(vm.Deferred.Single(d => d.Stroke == 0).Opinion);
    }

    [Fact]
    public void Marking_a_stroke_deferred_resets_junctions_that_pointed_into_it()
    {
        AnnotateTabViewModel vm = NewVm();
        vm.Load(LiftedPath());
        vm.HandleClick(1.3, 1.0, shift: true); // граница на перекладине (штрих 2 пока основной)
        Assert.Equal(2, vm.Junctions.Single().End!.Value.Stroke);

        vm.ToggleDeferred(2);

        Assert.False(vm.Junctions.Single().IsPlaced);
        Assert.Null(vm.Junctions.Single().End);
        Assert.Contains("сброшены", vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_writes_one_file_per_annotator_next_to_the_recording_and_it_loads_back_identically()
    {
        string path = LiftedPath();
        AnnotateTabViewModel vm = NewVm();
        vm.Load(path);
        AnnotateLifted(vm);
        vm.Note = "аккуратно";
        vm.SetConfidence(0, Confidence.Medium);

        Assert.True(vm.Save());

        string annotationPath = path + ".ann.A.json";
        Assert.Equal(annotationPath, CorpusStore.AnnotationPathFor(path, "A"));
        Assert.Equal(annotationPath, vm.AnnotationPath);
        Assert.True(File.Exists(annotationPath));
        Assert.False(File.Exists(path + ".ann.json"));
        Assert.False(vm.IsDirty);

        AnnotationFile saved = AnnotationJson.Load(annotationPath);
        Assert.Equal(Path.GetFileName(path), saved.Penlog);
        Assert.Equal(CorpusStore.ComputeSha256(path), saved.PenlogSha256);
        Assert.Equal(AnnotationFormat.Indexing, saved.Indexing);
        Assert.Equal("word:at", saved.Target);
        Assert.Equal("lift", saved.Variant);
        Assert.Equal("A", saved.Annotator);
        Assert.Equal("2026-10-14T12:00:00Z", saved.AnnotatedAt);
        Assert.Equal("InkLab", saved.Tool);
        Assert.Equal("at", saved.Text);
        Assert.Equal("аккуратно", saved.Quality.Note);
        Assert.Equal(3, saved.Strokes.Length);
        Assert.Equal(new StrokeInfo(21, 0, 100_000), saved.Strokes[0]);
        Junction j = Assert.Single(saved.Junctions);
        Assert.Equal(JunctionKind.Lifted, j.Kind);
        Assert.Equal(Confidence.Medium, j.Confidence);
        Assert.Equal(new PointRef(0, 19, 95_000), j.End);
        Assert.Equal(new PointRef(1, 0, 200_000), j.Start);
        Assert.Equal(new DeferredMark(2, 1, DeferredPartKind.Crossbar, DeferredTiming.AfterWord), Assert.Single(saved.Deferred));
        Assert.True(AnnotationValidator.IsValid(AnnotationValidator.Validate(saved, TestData.Strokes(CorpusStore.ReadPenLog(path)), CorpusStore.ComputeSha256(path))));

        AnnotateTabViewModel again = NewVm();
        again.Load(path);
        Assert.Equal("at", again.Text);
        Assert.Equal(new StrokePoint(0, 19), again.Junctions.Single().End);
        Assert.Equal(Confidence.Medium, again.Junctions.Single().Confidence);
        Assert.Equal(2, again.Deferred.Single().Stroke);
        Assert.Equal("аккуратно", again.Note);
        Assert.True(again.IsValid);
        Assert.False(again.IsDirty);
        Assert.Equal(AnnotationJson.Serialize(saved), AnnotationJson.Serialize(again.BuildAnnotation()));
    }

    [Fact]
    public void Each_annotator_has_his_own_file_and_other_annotations_are_read_only_context()
    {
        string path = LiftedPath();
        AnnotateTabViewModel a = NewVm("A");
        a.Load(path);
        AnnotateLifted(a);
        a.Save();
        string aFile = path + ".ann.A.json";
        string before = File.ReadAllText(aFile);

        AnnotateTabViewModel b = NewVm("B");
        b.Load(path);

        Assert.False(b.IsValid); // разметка A не подхватывается: у B своя, пока пустая
        Assert.DoesNotContain(b.Junctions, j => j.IsPlaced);
        AnnotatorState other = Assert.Single(b.OtherAnnotations);
        Assert.Equal("A", other.Annotator);
        Assert.Equal(AnnotationState.Valid, other.State);

        b.IsUsable = false;
        b.Save();

        Assert.Equal(before, File.ReadAllText(aFile)); // файл A не тронут
        Assert.True(File.Exists(path + ".ann.B.json"));
        Assert.Equal("B", AnnotationJson.Load(path + ".ann.B.json").Annotator);
        Assert.Equal(AnnotationState.Valid, CorpusIndex.Describe(path).Annotation); // лучшее среди разметчиков
        Assert.Equal(2, CorpusIndex.Describe(path).AnnotatorStates.Count);
    }

    [Fact]
    public void The_annotator_is_set_by_the_session_not_by_the_loaded_file()
    {
        string path = LiftedPath();
        AnnotateTabViewModel a = NewVm("A");
        a.Load(path);
        AnnotateLifted(a);
        a.Save();
        string bad = AnnotationJson.Serialize(AnnotationJson.Load(path + ".ann.A.json") with { Annotator = "Z" });
        File.WriteAllText(path + ".ann.B.json", bad); // файл B, в котором автором назван кто-то другой

        AnnotateTabViewModel b = NewVm("B");
        b.Load(path);
        b.IsUsable = false;
        b.Save();

        Assert.Equal("B", b.Annotator);
        Assert.Equal("B", AnnotationJson.Load(path + ".ann.B.json").Annotator);
    }

    [Fact]
    public void A_legacy_annotation_file_is_read_as_the_file_of_the_author_named_inside()
    {
        string path = LiftedPath();
        AnnotateTabViewModel a = NewVm("A");
        a.Load(path);
        AnnotateLifted(a);
        a.Save();
        File.Move(path + ".ann.A.json", path + ".ann.json"); // прежнее имя файла

        AnnotateTabViewModel again = NewVm("A");
        again.Load(path);
        Assert.True(again.IsValid);
        Assert.Contains("прежнего вида", again.Status, StringComparison.Ordinal);
        Assert.Equal("A", CorpusIndex.Describe(path).AnnotatorStates.Single().Annotator);
        Assert.True(CorpusIndex.Describe(path).AnnotatorStates.Single().Legacy);

        AnnotateTabViewModel other = NewVm("B");
        other.Load(path);
        Assert.False(other.IsValid);
        Assert.Equal("A", Assert.Single(other.OtherAnnotations).Annotator);
    }

    [Fact]
    public void The_annotator_id_must_not_be_empty()
    {
        Assert.Throws<ArgumentException>(() => NewVm(" "));
        AnnotateTabViewModel vm = NewVm();
        vm.Annotator = "  ";
        Assert.Equal("A", vm.Annotator);
    }

    [Fact]
    public void The_annotator_id_is_sanitized_in_the_file_name()
    {
        string path = LiftedPath();
        AnnotateTabViewModel vm = NewVm("Иван/Т 2");
        vm.Load(path);
        vm.IsUsable = false;
        vm.Save();

        Assert.True(File.Exists(path + ".ann.Иван_Т_2.json"));
        Assert.Equal("Иван/Т 2", AnnotationJson.Load(path + ".ann.Иван_Т_2.json").Annotator);
    }

    [Fact]
    public void A_failed_save_cancels_leaving_the_record_and_the_work_stays_dirty()
    {
        string first = LiftedPath("a-first");
        LiftedPath("b-second");
        AnnotateTabViewModel vm = NewVm();
        vm.OpenFolder(_folder.Path);
        vm.SelectedEntry = vm.Files[0];
        vm.ToggleDeferred(2);
        Directory.CreateDirectory(first + ".ann.A.json"); // на месте файла разметки каталог: записать нельзя

        vm.SelectedEntry = vm.Files[1];

        Assert.Equal(first, vm.Path); // переход отменён
        Assert.Same(vm.Files[0], vm.SelectedEntry);
        Assert.True(vm.IsDirty);
        Assert.Contains("Не удалось сохранить", vm.Status, StringComparison.Ordinal);
        Assert.Single(vm.Deferred);
        Assert.False(vm.OpenFolder(_folder.Path)); // «Обновить» тоже не должно терять правки
        Assert.False(vm.Refresh());

        Directory.Delete(first + ".ann.A.json");
        Assert.True(vm.Refresh());
        Assert.True(File.Exists(first + ".ann.A.json"));
    }

    [Fact]
    public void Switching_to_another_recording_saves_unsaved_work_as_a_draft()
    {
        string first = LiftedPath("a-first");
        LiftedPath("b-second");
        AnnotateTabViewModel vm = NewVm();
        vm.OpenFolder(_folder.Path);
        vm.SelectedEntry = vm.Files[0];
        vm.ToggleDeferred(2);

        vm.SelectedEntry = vm.Files[1];

        Assert.True(File.Exists(first + ".ann.A.json"));
        Assert.Equal(2, AnnotationJson.Load(first + ".ann.A.json").Deferred.Single().Stroke);
    }

    [Fact]
    public void Refresh_saves_first_and_then_rereads_the_folder()
    {
        string first = LiftedPath("a-first");
        AnnotateTabViewModel vm = NewVm();
        vm.OpenFolder(_folder.Path);
        vm.SelectedEntry = vm.Files[0];
        vm.ToggleDeferred(2);

        Assert.True(vm.Refresh());

        Assert.True(File.Exists(first + ".ann.A.json"));
        Assert.Equal(AnnotationState.Draft, vm.Files[0].Annotation);
    }

    [Fact]
    public void An_incomplete_annotation_is_saved_as_a_draft_and_listed_as_started()
    {
        string path = LiftedPath();
        AnnotateTabViewModel vm = NewVm();
        vm.Load(path);
        vm.ToggleDeferred(2);

        Assert.True(vm.Save());

        Assert.False(vm.IsValid);
        Assert.Contains("черновик", vm.Status, StringComparison.Ordinal);
        Assert.Equal(AnnotationState.Draft, CorpusIndex.Describe(path).Annotation);

        vm.HandleClick(0.93, 0.5);
        vm.HandleClick(1.2, 1.48);
        vm.Save();
        Assert.Equal(AnnotationState.Valid, CorpusIndex.Describe(path).Annotation);
    }

    [Fact]
    public void A_rejected_recording_needs_no_boundaries()
    {
        string path = LiftedPath();
        AnnotateTabViewModel vm = NewVm();
        vm.Load(path);
        vm.IsUsable = false;
        vm.Flags.Single(f => f.Name == QualityFlags.Hesitation).IsChecked = true;

        Assert.True(vm.IsValid);
        vm.Save();

        AnnotationFile saved = AnnotationJson.Load(path + ".ann.A.json");
        Assert.False(saved.Quality.Usable);
        Assert.Equal([QualityFlags.Hesitation], saved.Quality.Flags);
        Assert.Equal(AnnotationState.Rejected, CorpusIndex.Describe(path).Annotation);
    }

    [Fact]
    public void Editing_the_text_keeps_placed_junctions_in_the_cache_so_a_short_detour_does_not_lose_them()
    {
        AnnotateTabViewModel vm = NewVm();
        vm.Load(JoinedPath());
        vm.HandleClick(1.0, 0.5, shift: true);
        StrokePoint? placed = vm.Junctions.Single().End;

        vm.Text = "a"; // стирание по символу: стыков больше нет
        Assert.Empty(vm.Junctions);
        vm.Text = "at"; // и снова набор: граница вернулась
        Assert.Equal(placed, vm.Junctions.Single().End);

        vm.Text = "ati";
        Assert.Equal(2, vm.Junctions.Count);
        Assert.Equal(placed, vm.Junctions[0].End);
        Assert.False(vm.Junctions[1].IsPlaced);
        Assert.False(vm.IsValid);

        vm.Text = "a t "; // пробелы отбрасываются
        Assert.Equal("at", vm.Text);
        Assert.Single(vm.Junctions);
    }

    [Fact]
    public void The_scene_colours_the_trace_by_letter_and_marks_deferred_strokes()
    {
        AnnotateTabViewModel vm = NewVm();
        vm.Load(LiftedPath());
        AnnotateLifted(vm);

        InkScene scene = vm.Scene;

        Assert.Contains(scene.Polylines, p => p.StrokeIndex == 0 && p.Group == 0 && p.Style == SceneStyle.Normal);
        Assert.Contains(scene.Polylines, p => p.StrokeIndex == 1 && p.Group == 1);
        Assert.Contains(scene.Polylines, p => p.StrokeIndex == 2 && p.Style == SceneStyle.Deferred);
    }

    [Fact]
    public void Opening_a_folder_lists_recordings_and_navigation_skips_the_ones_this_annotator_finished()
    {
        string first = LiftedPath("a-first");
        string second = JoinedPath("b-second");
        AnnotateTabViewModel setup = NewVm();
        setup.Load(first);
        AnnotateLifted(setup);
        setup.Save();

        AnnotateTabViewModel vm = NewVm();
        vm.OpenFolder(_folder.Path);

        Assert.Equal(2, vm.Files.Count);
        Assert.Equal(AnnotationState.Valid, vm.Files[0].Annotation);
        Assert.Equal(AnnotationState.None, vm.Files[1].Annotation);

        vm.NextUnannotated();
        Assert.Equal(second, vm.SelectedEntry!.Path);

        vm.Move(1);
        Assert.Equal(first, vm.Path);

        // Другому разметчику первая запись «не размечена»: у каждого своя разметка.
        AnnotateTabViewModel other = NewVm("B");
        other.OpenFolder(_folder.Path);
        other.NextUnannotated();
        Assert.Equal(first, other.SelectedEntry!.Path);
    }

    [Fact]
    public void A_damaged_annotation_file_does_not_block_opening_the_recording()
    {
        string path = LiftedPath();
        File.WriteAllText(path + ".ann.A.json", "{ broken");
        AnnotateTabViewModel vm = NewVm();

        Assert.True(vm.Load(path));

        Assert.Contains("не читается", vm.Status, StringComparison.Ordinal);
        Assert.Equal("at", vm.Text);
        Assert.Equal(AnnotationState.Draft, CorpusIndex.Describe(path).Annotation);
    }

    [Fact]
    public void A_broken_recording_is_reported_and_does_not_throw()
    {
        string path = Path.Combine(_folder.Path, "bad" + CorpusStore.PenLogExtension);
        File.WriteAllText(path, "this is not gzip");
        AnnotateTabViewModel vm = NewVm();

        Assert.False(vm.Load(path));

        Assert.Contains("не читается", vm.Status, StringComparison.Ordinal);
        vm.HandleClick(0, 0);
        Assert.False(vm.Save());
    }

    [Fact]
    public void An_annotation_made_for_different_strokes_is_flagged_until_it_is_checked_and_saved_again()
    {
        string path = LiftedPath();
        AnnotateTabViewModel setup = NewVm();
        setup.Load(path);
        AnnotateLifted(setup);
        setup.Save();
        string file = path + ".ann.A.json";
        AnnotationFile stale = AnnotationJson.Load(file);
        AnnotationJson.Save(file, stale with { Strokes = [new StrokeInfo(21, 0, 100_000), new StrokeInfo(21, 200_000, 300_000)] });

        AnnotateTabViewModel vm = NewVm();
        vm.Load(path);

        Assert.False(vm.IsValid);
        Assert.Contains(vm.Issues, i => i.Code == IssueCodes.StrokeMismatch);
        Assert.Contains("изменилась", vm.Status, StringComparison.Ordinal);
        Assert.Equal(AnnotationState.Draft, CorpusIndex.Describe(path).Annotation);

        vm.Save(); // разметчик проверил границы и сохранил: привязка обновлена
        Assert.True(vm.IsValid);
        Assert.Equal(AnnotationState.Valid, CorpusIndex.Describe(path).Annotation);
    }

    [Fact]
    public void An_annotation_of_a_replaced_recording_fails_the_hash_check()
    {
        string path = LiftedPath();
        AnnotateTabViewModel setup = NewVm();
        setup.Load(path);
        AnnotateLifted(setup);
        setup.Save();
        string file = path + ".ann.A.json";
        AnnotationJson.Save(file, AnnotationJson.Load(file) with { PenlogSha256 = new string('0', 64) });

        AnnotateTabViewModel vm = NewVm();
        vm.Load(path);

        Assert.False(vm.IsValid);
        Assert.Contains(vm.Issues, i => i.Code == IssueCodes.HashMismatch);
    }
}
