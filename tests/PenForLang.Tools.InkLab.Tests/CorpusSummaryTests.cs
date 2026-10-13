using System.IO;
using PenForLang.Tools.InkLab.Annotation;
using PenForLang.Tools.InkLab.Corpus;
using PenForLang.Tools.InkLab.Tests.Support;
using PenForLang.Tools.InkLab.ViewModels;

namespace PenForLang.Tools.InkLab.Tests;

public sealed class CorpusSummaryTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private string Subject(string id) => Path.Combine(_folder.Path, id);

    private static void Annotate(string path, bool usable = true, bool complete = true)
    {
        var vm = new AnnotateTabViewModel(null, () => DateTimeOffset.UnixEpoch, "A");
        vm.Load(path);
        if (!usable)
        {
            vm.IsUsable = false;
        }
        else if (complete)
        {
            if (vm.Target == "word:at")
            {
                vm.SelectedLetter = 1;
                vm.ToggleDeferred(vm.Strokes.Length - 1);
                vm.HandleClick(0.93, 0.5);
            }
        }
        else
        {
            vm.ToggleDeferred(vm.Strokes.Length - 1);
        }

        vm.Save();
    }

    private IReadOnlyList<CorpusEntry> BuildCorpus()
    {
        // Субъект 1: два слова «с отрывом» (одно размечено верно, одно начато), одно «слитно» (без разметки), одна буква i (брак).
        string lift1 = TestData.WriteLog(Subject("s1"), "at-lift-001", TestData.AtWordLifted("inklab-v0.9/at-lift-001"));
        string lift2 = TestData.WriteLog(Subject("s1"), "at-lift-002", TestData.AtWordLifted("inklab-v0.9/at-lift-002"));
        TestData.WriteLog(Subject("s1"), "at-crossbar-001", TestData.AtWordJoined("inklab-v0.9/at-crossbar-001"));
        string i1 = TestData.WriteLog(Subject("s1"), "i-001", TestData.LetterI("inklab-v0.9/i-001"));

        // Субъект 2: одна буква i без разметки.
        TestData.WriteLog(Subject("s2"), "i-001", TestData.LetterI("inklab-v0.9/i-001"));

        Annotate(lift1);
        Annotate(lift2, complete: false);
        Annotate(i1, usable: false);
        return CorpusIndex.Scan(_folder.Path);
    }

    [Fact]
    public void Scan_describes_every_recording_with_target_variant_subject_and_annotation_state()
    {
        IReadOnlyList<CorpusEntry> entries = BuildCorpus();

        Assert.Equal(5, entries.Count);
        CorpusEntry lift1 = entries.Single(e => e.FileName.StartsWith("at-lift-001", StringComparison.Ordinal));
        Assert.Equal("word:at", lift1.Target);
        Assert.Equal("lift", lift1.Variant);
        Assert.Equal("tester", lift1.Subject);
        Assert.Equal(AnnotationState.Valid, lift1.Annotation);
        Assert.Equal(AnnotationState.Draft, entries.Single(e => e.FileName.StartsWith("at-lift-002", StringComparison.Ordinal)).Annotation);
        Assert.Equal(AnnotationState.None, entries.Single(e => e.FileName.StartsWith("at-crossbar-001", StringComparison.Ordinal)).Annotation);
        Assert.Equal(AnnotationState.Rejected, entries.Single(e => e.Target == "glyph:i" && e.Annotation != AnnotationState.None).Annotation);
        Assert.Equal("word:at · lift", lift1.Group);
    }

    [Fact]
    public void Summary_counts_recordings_by_kind_and_how_many_are_annotated()
    {
        CorpusSummary summary = CorpusSummary.Build(BuildCorpus());

        Assert.Equal(5, summary.Total);
        Assert.Equal(0, summary.Unreadable);

        SummaryRow lift = summary.ByGroup.Single(r => r.Name == "word:at · lift");
        Assert.Equal(new SummaryRow("word:at · lift", 2, Valid: 1, Rejected: 0, Draft: 1, None: 0), lift);
        SummaryRow crossbar = summary.ByGroup.Single(r => r.Name == "word:at · crossbar");
        Assert.Equal(new SummaryRow("word:at · crossbar", 1, 0, 0, 0, 1), crossbar);
        SummaryRow letters = summary.ByGroup.Single(r => r.Name == "glyph:i");
        Assert.Equal(2, letters.Recordings);
        Assert.Equal(1, letters.Rejected);
        Assert.Equal(1, letters.None);

        Assert.Equal(new SummaryRow("Всего", 5, Valid: 1, Rejected: 1, Draft: 1, None: 2), summary.Overall);
    }

    [Fact]
    public void Summary_by_subject_uses_the_subject_from_the_header()
    {
        // Заголовок тестовых записей один и тот же («tester»), поэтому все записи — один субъект.
        CorpusSummary summary = CorpusSummary.Build(BuildCorpus());

        SummaryRow row = Assert.Single(summary.BySubject);
        Assert.Equal("tester", row.Name);
        Assert.Equal(5, row.Recordings);
    }

    [Fact]
    public void Unreadable_files_are_counted_but_do_not_break_the_summary()
    {
        BuildCorpus();
        File.WriteAllText(Path.Combine(Subject("s2"), "broken" + CorpusStore.PenLogExtension), "garbage");

        CorpusSummary summary = CorpusSummary.Build(CorpusIndex.Scan(_folder.Path));

        Assert.Equal(6, summary.Total);
        Assert.Equal(1, summary.Unreadable);
        Assert.Equal(5, summary.Overall.Recordings);
    }

    [Fact]
    public void Superseded_recordings_are_not_part_of_the_corpus()
    {
        string keep = TestData.WriteLog(Subject("s1"), "keep", TestData.LetterI());
        string old = TestData.WriteLog(Subject("s1"), "old", TestData.LetterI());
        CorpusStore.Supersede(old);

        IReadOnlyList<string> files = CorpusStore.EnumeratePenLogs(_folder.Path);

        Assert.Equal(keep, Assert.Single(files));
        Assert.True(File.Exists(Path.Combine(Subject("s1"), CorpusStore.SupersededFolder, "old" + CorpusStore.PenLogExtension)));
    }

    [Fact]
    public void Markdown_contains_both_tables_and_the_totals()
    {
        CorpusSummary summary = CorpusSummary.Build(BuildCorpus());

        string text = summary.ToMarkdown(_folder.Path);

        Assert.Contains("# Сводка корпуса InkLab", text, StringComparison.Ordinal);
        Assert.Contains("| word:at · lift | 2 | 1 | 0 | 1 | 0 |", text, StringComparison.Ordinal);
        Assert.Contains("| tester | 5 |", text, StringComparison.Ordinal);
        Assert.Contains("Размечено верно: 2 из 5 (годных 1, брак 1); начато с ошибками: 1; без разметки: 2.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_tab_refreshes_and_saves_a_markdown_file_into_the_corpus_root()
    {
        BuildCorpus();
        var vm = new SummaryTabViewModel(_folder.Path);
        Assert.False(vm.SaveCommand.CanExecute(null));

        vm.Refresh();

        Assert.Equal(5, vm.Summary!.Total);
        Assert.Equal(vm.Summary.ByGroup.Count, vm.ByGroup.Count);
        Assert.Single(vm.BySubject);
        Assert.True(vm.SaveCommand.CanExecute(null));
        string? path = vm.Save();
        Assert.Equal(Path.Combine(_folder.Path, SummaryTabViewModel.SummaryFileName), path);
        Assert.Equal(vm.Text, File.ReadAllText(path!));
    }

    [Fact]
    public void Summary_of_a_missing_folder_reports_it_without_throwing()
    {
        var vm = new SummaryTabViewModel(Path.Combine(_folder.Path, "nope"));

        vm.Refresh();

        Assert.Null(vm.Summary);
        Assert.Contains("не найдена", vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void Annotation_state_is_draft_when_the_annotation_does_not_match_the_recording_any_more()
    {
        string path = TestData.WriteLog(Subject("s1"), "x", TestData.AtWordLifted());
        AnnotationFile stale = new(
            AnnotationFormat.Schema, "x", "word:at", "A", null, "at", QualityMark.Good, [10, 10, 10],
            [new Junction(0, JunctionKind.Lifted, new StrokePoint(0, 9), new StrokePoint(1, 0))], []);
        AnnotationJson.Save(CorpusStore.AnnotationPathFor(path), stale);

        Assert.Equal(AnnotationState.Draft, CorpusIndex.Describe(path).Annotation);
    }

    [Fact]
    public void Annotation_and_recording_paths_map_to_each_other()
    {
        string log = Path.Combine("c", "s1", "a-001-x.pfl.jsonl.gz");

        string annotation = CorpusStore.AnnotationPathFor(log);

        Assert.EndsWith("a-001-x.pfl.jsonl.gz.ann.json", annotation, StringComparison.Ordinal);
        Assert.Equal(log, CorpusStore.PenLogPathFor(annotation));
        Assert.Throws<ArgumentException>(() => CorpusStore.PenLogPathFor("x.json"));
    }
}
