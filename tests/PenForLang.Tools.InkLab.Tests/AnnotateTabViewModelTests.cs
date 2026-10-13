using System.IO;
using PenForLang.Ink;
using PenForLang.Tools.InkLab.Annotation;
using PenForLang.Tools.InkLab.Corpus;
using PenForLang.Tools.InkLab.Scene;
using PenForLang.Tools.InkLab.Tests.Support;
using PenForLang.Tools.InkLab.ViewModels;

namespace PenForLang.Tools.InkLab.Tests;

/// <summary>Вкладка «Разметка» без окна: клики по следу ставят границы, файл разметки пишется рядом с записью и читается обратно.</summary>
public sealed class AnnotateTabViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static AnnotateTabViewModel NewVm() => new(null, () => Now, "A");

    private string LiftedPath(string name = "lifted") => TestData.WriteLog(_folder.Path, name, TestData.AtWordLifted());

    private string JoinedPath(string name = "joined") => TestData.WriteLog(_folder.Path, name, TestData.AtWordJoined());

    [Fact]
    public void A_new_recording_gets_the_text_from_the_target_and_an_empty_junction_slot()
    {
        AnnotateTabViewModel vm = NewVm();

        Assert.True(vm.Load(LiftedPath()));

        Assert.Equal("at", vm.Text);
        Assert.Equal("word:at", vm.Target);
        JunctionRow slot = Assert.Single(vm.Junctions);
        Assert.Equal("a|t", slot.Label);
        Assert.False(slot.IsPlaced);
        Assert.True(slot.IsSelected);
        Assert.Equal(JunctionKind.Lifted, slot.Kind); // вариант записи «с отрывом» подсказывает вид стыка
        Assert.False(vm.IsValid);
        Assert.Contains(vm.Issues, i => i.Code == IssueCodes.JunctionCount);
        Assert.Equal(3, vm.Strokes.Length);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void The_connected_variant_defaults_to_connected_junctions()
    {
        AnnotateTabViewModel vm = NewVm();
        vm.Load(JoinedPath());

        Assert.Equal(JunctionKind.Connected, vm.Junctions.Single().Kind);
    }

    [Fact]
    public void Clicking_the_trace_places_a_lifted_boundary_at_the_stroke_end()
    {
        AnnotateTabViewModel vm = NewVm();
        vm.Load(LiftedPath());
        vm.SelectedLetter = 1;
        vm.Tool = AnnotateTool.Deferred;
        vm.HandleClick(1.3, 1.0); // перекладина
        vm.Tool = AnnotateTool.Boundary;

        vm.HandleClick(0.93, 0.5); // у конца штриха a

        JunctionRow j = vm.Junctions.Single();
        Assert.True(j.IsPlaced);
        Assert.Equal(new StrokePoint(0, 20), j.End);
        Assert.Equal(new StrokePoint(1, 0), j.Start);
        Assert.True(vm.IsValid);
        Assert.True(vm.IsDirty);
        Assert.DoesNotContain(vm.Issues, i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void Clicking_places_a_connected_boundary_at_the_nearest_point_inside_a_stroke()
    {
        AnnotateTabViewModel vm = NewVm();
        vm.Load(JoinedPath());
        vm.ToggleDeferred(1);

        vm.HandleClick(1.0, 0.52);

        JunctionRow j = vm.Junctions.Single();
        Assert.Equal(new StrokePoint(0, 20), j.End);
        Assert.Equal(j.End, j.Start);
        Assert.True(vm.IsValid);
    }

    [Fact]
    public void The_boundary_tool_ignores_deferred_strokes_and_far_clicks_are_rejected()
    {
        AnnotateTabViewModel vm = NewVm();
        vm.Load(LiftedPath());
        vm.SelectedLetter = 1;
        vm.ToggleDeferred(2); // перекладина

        vm.SetKind(0, JunctionKind.Connected);
        vm.HandleClick(1.3, 1.0); // ровно по перекладине — граница ляжет на ближайший основной штрих

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

        vm.HandleClick(1.5, 1.0);
        Assert.Empty(vm.Deferred);
    }

    [Fact]
    public void The_dot_of_an_i_is_marked_as_a_dot()
    {
        AnnotateTabViewModel vm = NewVm();
        vm.Load(TestData.WriteLog(_folder.Path, "i", TestData.LetterI()));
        vm.Tool = AnnotateTool.Deferred;

        vm.HandleClick(0.0, 1.52);

        DeferredRow row = Assert.Single(vm.Deferred);
        Assert.Equal(1, row.Stroke);
        Assert.Equal(DeferredPartKind.Dot, row.Kind);
        Assert.True(vm.IsValid); // одна буква — стыков нет
    }

    [Fact]
    public void Deferred_timing_is_inferred_from_stroke_order_and_can_be_overridden()
    {
        AnnotateTabViewModel vm = NewVm();
        vm.Load(LiftedPath());
        vm.ToggleDeferred(0); // после штриха 0 пишутся ещё основные штрихи → «сразу»

        Assert.Equal(DeferredTiming.Immediate, vm.Deferred.Single().Timing);

        vm.ToggleTiming(0);
        Assert.Equal(DeferredTiming.AfterWord, vm.Deferred.Single().Timing);
        vm.ToggleDeferred(2); // состав основного следа изменился, но ручное значение сохраняется
        Assert.Equal(DeferredTiming.AfterWord, vm.Deferred.Single(d => d.Stroke == 0).Timing);
        Assert.Equal(DeferredTiming.AfterWord, vm.Deferred.Single(d => d.Stroke == 2).Timing);
    }

    [Fact]
    public void Save_writes_the_annotation_file_next_to_the_recording_and_it_loads_back_identically()
    {
        string path = LiftedPath();
        AnnotateTabViewModel vm = NewVm();
        vm.Load(path);
        vm.SelectedLetter = 1;
        vm.ToggleDeferred(2);
        vm.HandleClick(0.93, 0.5);
        vm.Note = "аккуратно";

        Assert.True(vm.Save());

        string annotationPath = path + ".ann.json";
        Assert.Equal(annotationPath, CorpusStore.AnnotationPathFor(path));
        Assert.True(File.Exists(annotationPath));
        Assert.False(vm.IsDirty);

        AnnotationFile saved = AnnotationJson.Load(annotationPath);
        Assert.Equal(Path.GetFileName(path), saved.Penlog);
        Assert.Equal("word:at", saved.Target);
        Assert.Equal("A", saved.Annotator);
        Assert.Equal("2026-10-14T12:00:00Z", saved.AnnotatedAt);
        Assert.Equal("at", saved.Text);
        Assert.True(saved.Quality.Usable);
        Assert.Equal("аккуратно", saved.Quality.Note);
        Assert.Equal([21, 21, 11], saved.StrokeSamples);
        Assert.Equal(new Junction(0, JunctionKind.Lifted, new StrokePoint(0, 20), new StrokePoint(1, 0)), Assert.Single(saved.Junctions));
        Assert.Equal(new DeferredMark(2, 1, DeferredPartKind.Crossbar, DeferredTiming.AfterWord), Assert.Single(saved.Deferred));
        Assert.True(AnnotationValidator.IsValid(AnnotationValidator.Validate(saved, SceneBuilder.StrokeSampleCounts(TestData.Strokes(CorpusStore.ReadPenLog(path))))));

        AnnotateTabViewModel again = NewVm();
        again.Load(path);
        Assert.Equal("at", again.Text);
        Assert.Equal(JunctionKind.Lifted, again.Junctions.Single().Kind);
        Assert.Equal(new StrokePoint(0, 20), again.Junctions.Single().End);
        Assert.Equal(2, again.Deferred.Single().Stroke);
        Assert.Equal("аккуратно", again.Note);
        Assert.True(again.IsValid);
        Assert.False(again.IsDirty);
        Assert.Equal(AnnotationJson.Serialize(saved), AnnotationJson.Serialize(again.BuildAnnotation()));
    }

    [Fact]
    public void An_incomplete_annotation_is_saved_as_a_draft_and_listed_as_started()
    {
        string path = LiftedPath();
        AnnotateTabViewModel vm = NewVm();
        vm.Load(path);
        vm.Text = "at";
        vm.ToggleDeferred(2);

        Assert.True(vm.Save());

        Assert.False(vm.IsValid);
        Assert.Contains("черновик", vm.Status, StringComparison.Ordinal);
        Assert.Equal(AnnotationState.Draft, CorpusIndex.Describe(path).Annotation);

        vm.HandleClick(0.93, 0.5);
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

        AnnotationFile saved = AnnotationJson.Load(path + ".ann.json");
        Assert.False(saved.Quality.Usable);
        Assert.Equal([QualityFlags.Hesitation], saved.Quality.Flags);
        Assert.Equal(AnnotationState.Rejected, CorpusIndex.Describe(path).Annotation);
    }

    [Fact]
    public void Changing_the_text_rebuilds_the_junction_slots_and_keeps_placed_ones()
    {
        AnnotateTabViewModel vm = NewVm();
        vm.Load(JoinedPath());
        vm.HandleClick(1.0, 0.5);
        StrokePoint? placed = vm.Junctions.Single().End;

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
    public void Switching_the_kind_resnaps_the_boundary_and_clears_it_when_it_cannot_be_placed()
    {
        AnnotateTabViewModel vm = NewVm();
        vm.Load(LiftedPath());
        vm.SetKind(0, JunctionKind.Connected);
        vm.HandleClick(0.5, 0.5);
        Assert.Equal(new StrokePoint(0, 10), vm.Junctions.Single().End);

        vm.SetKind(0, JunctionKind.Lifted);
        Assert.Equal(new StrokePoint(0, 20), vm.Junctions.Single().End);
        Assert.Equal(new StrokePoint(1, 0), vm.Junctions.Single().Start);

        // Последний основной штрих: отрыв после него поставить некуда.
        vm.SetKind(0, JunctionKind.Connected);
        vm.ClearJunction(0);
        vm.HandleClick(1.4, 0.0); // конец ствола t (штрих 1), перекладина — основной штрих 2
        vm.ToggleDeferred(2);
        vm.SetKind(0, JunctionKind.Lifted);

        Assert.False(vm.Junctions.Single().IsPlaced);
        Assert.Contains("нет основного штриха", vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void The_scene_colours_the_trace_by_letter_and_marks_deferred_strokes()
    {
        AnnotateTabViewModel vm = NewVm();
        vm.Load(LiftedPath());
        vm.SelectedLetter = 1;
        vm.ToggleDeferred(2);
        vm.HandleClick(0.93, 0.5);

        InkScene scene = vm.Scene;

        Assert.Contains(scene.Polylines, p => p.StrokeIndex == 0 && p.Group == 0 && p.Style == SceneStyle.Normal);
        Assert.Contains(scene.Polylines, p => p.StrokeIndex == 1 && p.Group == 1);
        Assert.Contains(scene.Polylines, p => p.StrokeIndex == 2 && p.Style == SceneStyle.Deferred);
        SceneMarker marker = scene.Markers.First();
        Assert.Equal("a|t", marker.Label);
    }

    [Fact]
    public void Opening_a_folder_lists_recordings_with_their_annotation_state_and_navigation_skips_finished_ones()
    {
        string first = LiftedPath("a-first");
        string second = JoinedPath("b-second");
        AnnotateTabViewModel setup = NewVm();
        setup.Load(first);
        setup.ToggleDeferred(2);
        setup.HandleClick(0.93, 0.5);
        setup.Save();

        AnnotateTabViewModel vm = NewVm();
        vm.OpenFolder(_folder.Path);

        Assert.Equal(2, vm.Files.Count);
        Assert.Equal(AnnotationState.Valid, vm.Files[0].Annotation);
        Assert.Equal(AnnotationState.None, vm.Files[1].Annotation);

        vm.NextUnannotated();
        Assert.Equal(second, vm.SelectedEntry!.Path);
        Assert.Equal(second, vm.Path);

        vm.Move(1);
        Assert.Equal(first, vm.Path);
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

        Assert.True(File.Exists(first + ".ann.json"));
        Assert.Equal(2, AnnotationJson.Load(first + ".ann.json").Deferred.Single().Stroke);
    }

    [Fact]
    public void A_damaged_annotation_file_does_not_block_opening_the_recording()
    {
        string path = LiftedPath();
        File.WriteAllText(path + ".ann.json", "{ broken");
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
    public void An_annotation_made_for_a_different_stroke_layout_is_flagged_after_loading()
    {
        string path = LiftedPath();
        AnnotationFile stale = new(
            AnnotationFormat.Schema, Path.GetFileName(path), "word:at", "A", null, "at", QualityMark.Good, [21, 21],
            [new Junction(0, JunctionKind.Lifted, new StrokePoint(0, 20), new StrokePoint(1, 0))], []);
        AnnotationJson.Save(path + ".ann.json", stale);

        AnnotateTabViewModel vm = NewVm();
        vm.Load(path);

        Assert.False(vm.IsValid);
        Assert.Contains(vm.Issues, i => i.Code == IssueCodes.StrokeMismatch);
    }
}
