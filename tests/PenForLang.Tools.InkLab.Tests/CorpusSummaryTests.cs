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
                vm.HandleClick(1.2, 1.48);
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
        TestData.WriteLog(Subject("s1"), "at-joined-001", TestData.AtWordJoined("inklab-v0.9/at-joined-001"));
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
        Assert.Equal(AnnotationState.None, entries.Single(e => e.FileName.StartsWith("at-joined-001", StringComparison.Ordinal)).Annotation);
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
        SummaryRow crossbar = summary.ByGroup.Single(r => r.Name == "word:at · joined");
        Assert.Equal(new SummaryRow("word:at · joined", 1, 0, 0, 0, 1), crossbar);
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
            AnnotationFormat.Schema, "x", CorpusStore.ComputeSha256(path), AnnotationFormat.Indexing, "word:at", "lift", "A", null, "InkLab", "at", QualityMark.Good,
            [new StrokeInfo(10, 0, 9000), new StrokeInfo(10, 0, 9000), new StrokeInfo(10, 0, 9000)],
            [new Junction(0, JunctionKind.Lifted, new PointRef(0, 9, 9000), new PointRef(1, 0, 0))], []);
        AnnotationJson.Save(CorpusStore.AnnotationPathFor(path, "A"), stale);

        Assert.Equal(AnnotationState.Draft, CorpusIndex.Describe(path).Annotation);
    }

    [Fact]
    public void Annotation_and_recording_paths_map_to_each_other()
    {
        string log = Path.Combine("c", "s1", "a-001-x.pfl.jsonl.gz");

        string annotation = CorpusStore.AnnotationPathFor(log, "Иван/Т");
        string legacy = CorpusStore.LegacyAnnotationPathFor(log);

        Assert.EndsWith("a-001-x.pfl.jsonl.gz.ann.Иван_Т.json", annotation, StringComparison.Ordinal);
        Assert.EndsWith("a-001-x.pfl.jsonl.gz.ann.json", legacy, StringComparison.Ordinal);
        Assert.Equal(log, CorpusStore.PenLogPathFor(annotation));
        Assert.Equal(log, CorpusStore.PenLogPathFor(legacy));
        Assert.Throws<ArgumentException>(() => CorpusStore.PenLogPathFor("x.json"));
        Assert.Throws<ArgumentException>(() => CorpusStore.AnnotationPathFor(log, " "));
    }

    [Fact]
    public void Superseding_moves_the_recording_and_all_its_annotations_with_one_common_prefix_on_a_name_clash()
    {
        string path = TestData.WriteLog(Subject("s1"), "x", TestData.LetterI());
        File.WriteAllText(path + ".ann.A.json", "{}");
        File.WriteAllText(path + ".ann.B.json", "{}");
        string superseded = Path.Combine(Subject("s1"), CorpusStore.SupersededFolder);

        CorpusStore.Supersede(path);
        Assert.True(File.Exists(Path.Combine(superseded, "x.pfl.jsonl.gz")));
        Assert.True(File.Exists(Path.Combine(superseded, "x.pfl.jsonl.gz.ann.A.json")));
        Assert.True(File.Exists(Path.Combine(superseded, "x.pfl.jsonl.gz.ann.B.json")));

        // Та же запись и снова разметка: имена заняты — вся пара получает ОДИН префикс «2-», файлы не расходятся.
        string again = TestData.WriteLog(Subject("s1"), "x", TestData.LetterI());
        File.WriteAllText(again + ".ann.A.json", "{}");
        CorpusStore.Supersede(again);

        Assert.True(File.Exists(Path.Combine(superseded, "2-x.pfl.jsonl.gz")));
        Assert.True(File.Exists(Path.Combine(superseded, "2-x.pfl.jsonl.gz.ann.A.json")));
        Assert.False(File.Exists(again));
        Assert.Empty(CorpusStore.FindAnnotationFiles(again));
    }

    [Fact]
    public void Writing_files_is_atomic_keeps_a_backup_and_leaves_no_temporary_file()
    {
        string path = Path.Combine(_folder.Path, "state.json");

        AtomicFile.WriteText(path, "one", keepBackup: true);
        Assert.False(File.Exists(path + ".bak"));
        AtomicFile.WriteText(path, "two", keepBackup: true);

        Assert.Equal("two", File.ReadAllText(path));
        Assert.Equal("one", File.ReadAllText(path + ".bak"));
        Assert.False(File.Exists(path + ".tmp"));

        string fresh = Path.Combine(_folder.Path, "log.bin");
        AtomicFile.WriteNew(fresh, stream => stream.WriteByte(7));
        Assert.Equal([(byte)7], File.ReadAllBytes(fresh));
        Assert.Throws<IOException>(() => AtomicFile.WriteNew(fresh, stream => stream.WriteByte(9)));
        Assert.Equal([(byte)7], File.ReadAllBytes(fresh)); // существующий файл не затёрт
        Assert.False(File.Exists(fresh + ".tmp"));
    }

    [Fact]
    public void The_recording_hash_is_the_sha256_of_the_uncompressed_jsonl()
    {
        string path = TestData.WriteLog(Subject("s1"), "x", TestData.LetterI());
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress);
        string expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(gzip));

        Assert.Equal(expected, CorpusStore.ComputeSha256(path));
        Assert.Matches("^[0-9a-f]{64}$", expected);
    }

    [Fact]
    public void Summary_has_a_source_table_and_warns_about_mouse_recordings()
    {
        CorpusSummary summary = CorpusSummary.Build(BuildCorpus());

        SummaryRow mouse = Assert.Single(summary.BySource);
        Assert.Equal("Mouse", mouse.Name);
        Assert.Equal(5, mouse.Recordings);
        Assert.Contains(summary.Warnings, w => w.Contains("мышью", StringComparison.Ordinal) && w.Contains('5'));
        string text = summary.ToMarkdown();
        Assert.Contains("## Предупреждения", text, StringComparison.Ordinal);
        Assert.Contains("## По источнику пера", text, StringComparison.Ordinal);
        Assert.Contains("| Mouse | 5 |", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_task_ids_in_one_subject_folder_are_marked_and_reported()
    {
        TestData.WriteLog(Subject("s1"), "i-001-a", TestData.LetterI("inklab-v0.9/i-001"));
        TestData.WriteLog(Subject("s1"), "i-001-b", TestData.LetterI("inklab-v0.9/i-001"));
        TestData.WriteLog(Subject("s1"), "i-002", TestData.LetterI("inklab-v0.9/i-002"));

        IReadOnlyList<CorpusEntry> entries = CorpusIndex.Scan(_folder.Path);
        CorpusSummary summary = CorpusSummary.Build(entries);

        Assert.Equal(2, entries.Count(e => e.DuplicateTaskId));
        Assert.DoesNotContain(entries.Single(e => e.FileName.StartsWith("i-002", StringComparison.Ordinal)).StateText, "дубль", StringComparison.Ordinal);
        Assert.Contains("дубль", entries.First(e => e.DuplicateTaskId).StateText, StringComparison.Ordinal);
        Assert.Contains(summary.Warnings, w => w.Contains("Дубль задания", StringComparison.Ordinal) && w.Contains("i-001", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_reports_the_annotator_of_every_annotation_file()
    {
        string path = TestData.WriteLog(Subject("s1"), "at-lift-001", TestData.AtWordLifted());
        foreach (string id in new[] { "A", "B" })
        {
            var vm = new AnnotateTabViewModel(null, () => DateTimeOffset.UnixEpoch, id);
            vm.Load(path);
            vm.IsUsable = id == "A" ? vm.IsUsable : false;
            if (id == "A")
            {
                vm.SelectedLetter = 1;
                vm.ToggleDeferred(2);
                vm.HandleClick(0.93, 0.5);
                vm.HandleClick(1.2, 1.48);
            }

            vm.Save();
        }

        CorpusEntry entry = CorpusIndex.Describe(path);

        Assert.Equal(["A", "B"], entry.AnnotatorStates.Select(a => a.Annotator).Order().ToArray());
        Assert.Equal(AnnotationState.Valid, entry.Annotation);
    }
}
