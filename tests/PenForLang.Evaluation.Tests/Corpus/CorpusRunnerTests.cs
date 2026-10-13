using System.Text.Json;
using PenForLang.Evaluation.Tests.Support;
using PenForLang.Glyphs;
using PenForLang.Ink;
using PenForLang.Testing.SynthInk;

namespace PenForLang.Evaluation.Tests.Corpus;

/// <summary>
/// Читатель корпуса и критерии корпусного теста (V0.10) на синтетическом корпусе во временной папке: тест, доказывающий, что
/// проверка ловит нарушение, важнее теста, что нарушений нет (CLAUDE.md). Разметка пишется строго по схеме
/// <c>schemas/pfl.annotation.v1.schema.json</c> (проверяется при записи).
/// </summary>
public class CorpusRunnerTests
{
    private static CorpusReport Run(SyntheticCorpus corpus, string annotator = CorpusFormat.DefaultAnnotator) =>
        CorpusRunner.Run(corpus.Root, annotator, Tester.Words, Shipped.Pack);

    private static SyntheticCorpus Protocol()
    {
        // Протокол V0.9: at-lift ×10, at-joined ×10, it-dot ×10 (здесь по 6).
        var corpus = new SyntheticCorpus();
        WordTemplate at = Tester.Word("at");
        WordTemplate it = Tester.Word("it");
        for (ulong seed = 1; seed <= 6; seed++)
        {
            corpus.Add($"at-lift-{seed}", at, Tester.Synth.Mutate(at, SynthWordMutation.LiftAtJoin, seed)!, variant: "lift");
            corpus.Add($"at-joined-{seed}", at, Tester.Synth.Mutate(at, SynthWordMutation.None, seed)!, variant: "joined");
            corpus.Add($"it-dot-{seed}", it, Tester.Synth.Mutate(it, SynthWordMutation.DeferredImmediately, seed)!, variant: "dot");
        }

        return corpus;
    }

    [Fact]
    public void The_protocol_corpus_meets_the_criteria()
    {
        using SyntheticCorpus corpus = Protocol();

        CorpusReport report = Run(corpus);

        Assert.Equal(18, report.Records.Count);
        Assert.Empty(report.Skipped);
        Assert.True(report.BoundaryPassRate >= CorpusTests.RequiredBoundaryPassRate, report.Summary);
        Assert.Equal(0, report.J1Violations);
        Assert.Equal(6, report.J1ExpectedOf("lift"));
        Assert.True(report.J1RecallOf("lift") >= CorpusTests.RequiredLiftRecall, report.Summary);
        Assert.Equal(0, report.J1ExpectedOf("dot"));
        Assert.Equal(0, report.J1ExpectedOf("joined"));
        Assert.Equal(18, report.JunctionCount);
    }

    [Fact]
    public void J1_where_the_annotation_says_connected_is_a_violation()
    {
        using var corpus = new SyntheticCorpus();
        WordTemplate at = Tester.Word("at");
        corpus.Add("wrong", at, Tester.Synth.Mutate(at, SynthWordMutation.SkipJoin, 1)!, annotationLifted: _ => false);

        CorpusReport report = Run(corpus);

        CorpusRecordResult record = Assert.Single(report.Records);
        Assert.Single(record.Junctions, j => j.J1Violation);
        Assert.Equal(1, report.J1Violations);
    }

    [Fact]
    public void A_dot_put_immediately_exempts_the_lift_so_no_j1_is_expected_or_found()
    {
        using var corpus = new SyntheticCorpus();
        WordTemplate it = Tester.Word("it");
        corpus.Add("exempt", it, Tester.Synth.Mutate(it, SynthWordMutation.DeferredImmediately, 1)!, annotationLifted: _ => true);

        CorpusReport report = Run(corpus);

        CorpusJunctionResult junction = Assert.Single(Assert.Single(report.Records).Junctions);
        Assert.True(junction.Lifted);
        Assert.True(junction.Exempt);
        Assert.False(junction.J1Expected);
        Assert.False(junction.J1);
        Assert.Equal(0, report.J1Violations);
    }

    [Fact]
    public void A_missed_j1_lowers_the_recall_but_is_not_a_violation()
    {
        using var corpus = new SyntheticCorpus();
        WordTemplate at = Tester.Word("at");
        corpus.Add("joined-but-annotated-lifted", at, Tester.Synth.Mutate(at, SynthWordMutation.None, 2)!, annotationLifted: _ => true, variant: "lift");

        CorpusReport report = Run(corpus);

        Assert.Equal(0, report.J1Violations);
        Assert.Equal(0.0, report.J1RecallOf("lift"));
        Assert.Equal(0.0, report.J1Recall);
    }

    [Fact]
    public void Boundaries_far_from_the_annotation_fail_the_pass_rate()
    {
        using var corpus = new SyntheticCorpus();
        WordTemplate at = Tester.Word("at");
        SynthWordSample sample = Tester.Synth.Mutate(at, SynthWordMutation.None, 3)!;
        SynthJoinTruth truth = sample.Joins[0];

        // Конец тела левой буквы размечен в начале штриха — заведомо далеко от выхода a (время t и номер точки согласованы).
        SynthWordSample bad = sample with { Joins = [truth with { End = truth.End with { Point = 5 } }] };
        corpus.Add("bad", at, bad);

        CorpusReport report = Run(corpus);

        CorpusRecordResult record = Assert.Single(report.Records);
        Assert.False(Assert.Single(record.Junctions).BoundariesOk);
        Assert.True(record.WorstBoundary > 0.2);
        Assert.Equal(0.0, report.BoundaryPassRate);
    }

    [Fact]
    public void The_pass_rate_counts_junctions_not_records()
    {
        using var corpus = new SyntheticCorpus();
        WordTemplate tat = Tester.Word("tat");
        SynthWordSample sample = Tester.Synth.Mutate(tat, SynthWordMutation.None, 3)!;
        SynthJoinTruth first = sample.Joins[0];
        SynthWordSample oneBad = sample with { Joins = [first with { End = first.End with { Point = 5 } }, sample.Joins[1]] };
        corpus.Add("one-bad-junction", tat, oneBad);

        CorpusReport report = Run(corpus);

        Assert.Equal(2, report.JunctionCount);
        Assert.Equal(0.5, report.BoundaryPassRate);
    }

    [Fact]
    public void Only_word_targets_of_two_or_more_letters_with_n_minus_one_junctions_count()
    {
        using var corpus = new SyntheticCorpus();
        WordTemplate at = Tester.Word("at");
        SynthWordSample sample = Tester.Synth.Mutate(at, SynthWordMutation.None, 1)!;
        corpus.Add("word", at, sample);
        corpus.Add("letter-target", at, sample, target: "a");
        corpus.Add("phrase-target", at, sample, target: "phrase:at");
        corpus.Add("rejected", at, sample, usable: false);
        WordTemplate a = Tester.Word("a");
        corpus.Add("one-letter-word", a, Tester.Synth.Mutate(a, SynthWordMutation.None, 1)!);
        SynthWordSample missingJunction = sample with { Joins = [] };
        corpus.Add("no-junctions", at, missingJunction);

        CorpusReport report = Run(corpus);

        Assert.Equal(["word.pfl.jsonl.gz"], report.Records.Select(r => r.Name));
        Assert.Equal(5, report.Skipped.Count);
        Assert.Contains(report.Skipped, s => s.Name == "letter-target.pfl.jsonl.gz" && s.Reason.Contains("not a word", StringComparison.Ordinal));
        Assert.Contains(report.Skipped, s => s.Name == "one-letter-word.pfl.jsonl.gz" && s.Reason.Contains("fewer than two", StringComparison.Ordinal));
        Assert.Contains(report.Skipped, s => s.Name == "no-junctions.pfl.jsonl.gz" && s.Reason.Contains("junctions", StringComparison.Ordinal));
        Assert.Contains(report.Skipped, s => s.Name == "rejected.pfl.jsonl.gz" && s.Reason.Contains("unusable", StringComparison.Ordinal));
    }

    [Fact]
    public void Only_the_requested_annotator_is_read_and_a_legacy_file_belongs_to_its_author()
    {
        using var corpus = new SyntheticCorpus();
        WordTemplate at = Tester.Word("at");
        SynthWordSample sample = Tester.Synth.Mutate(at, SynthWordMutation.None, 1)!;
        corpus.Add("a-and-b", at, sample, annotator: "A");
        corpus.Add("only-b", at, sample, annotator: "B");
        corpus.Add("legacy-a", at, sample, annotator: "A", legacyName: true);

        CorpusReport mainAnnotator = Run(corpus, "A");
        CorpusReport second = Run(corpus, "B");

        Assert.Equal(["a-and-b.pfl.jsonl.gz", "legacy-a.pfl.jsonl.gz"], mainAnnotator.Records.Select(r => r.Name));
        Assert.Contains(mainAnnotator.Skipped, s => s.Name == "only-b.pfl.jsonl.gz");
        Assert.Equal(["only-b.pfl.jsonl.gz"], second.Records.Select(r => r.Name));
    }

    [Fact]
    public void Superseded_recordings_are_skipped()
    {
        using var corpus = new SyntheticCorpus();
        WordTemplate at = Tester.Word("at");
        SynthWordSample sample = Tester.Synth.Mutate(at, SynthWordMutation.None, 1)!;
        corpus.Add("current", at, sample);
        corpus.Add("old-take", at, sample, subfolder: CorpusFormat.SupersededFolder);
        corpus.Add("in-a-folder", at, sample, subfolder: "session-1");

        CorpusReport report = Run(corpus);

        Assert.Equal(["current.pfl.jsonl.gz", "in-a-folder.pfl.jsonl.gz"], report.Records.Select(r => r.Name));
        Assert.Empty(report.Skipped);
    }

    [Fact]
    public void An_annotation_of_a_changed_recording_is_not_used()
    {
        using var corpus = new SyntheticCorpus();
        WordTemplate at = Tester.Word("at");
        SynthWordSample sample = Tester.Synth.Mutate(at, SynthWordMutation.None, 1)!;
        string recording = corpus.Add("changed", at, sample);
        string annotationPath = CorpusFormat.AnnotationPath(recording, CorpusFormat.DefaultAnnotator);
        string original = File.ReadAllText(annotationPath);

        string[] edits =
        [
            System.Text.RegularExpressions.Regex.Replace(original, "\"penlogSha256\":\"[0-9a-f]{64}\"", "\"penlogSha256\":\"" + new string('0', 64) + "\"", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)),
            original.Replace("inkattempt-after-last-restart", "all-strokes", StringComparison.Ordinal),
            original.Replace("\"strokes\":[{\"n\":", "\"strokes\":[{\"n\":1", StringComparison.Ordinal),
        ];
        foreach (string edited in edits)
        {
            Assert.NotEqual(original, edited);
            File.WriteAllText(annotationPath, edited);

            CorpusReport report = Run(corpus);

            Assert.Empty(report.Records);
            Assert.Contains(report.Skipped, s => s.Reason.Contains("does not match the recording", StringComparison.Ordinal));
        }

        File.WriteAllText(annotationPath, original);
        Assert.Single(Run(corpus).Records);
    }

    [Fact]
    public void Points_are_found_by_time_before_the_sample_number()
    {
        using var corpus = new SyntheticCorpus();
        WordTemplate at = Tester.Word("at");
        SynthWordSample sample = Tester.Synth.Mutate(at, SynthWordMutation.SkipJoin, 1)!;
        string recording = corpus.Add("by-time", at, sample);
        CorpusRecording read = CorpusFormat.ReadRecording(recording);
        SynthJoinTruth truth = sample.Joins[0];
        string annotationPath = CorpusFormat.AnnotationPath(recording, CorpusFormat.DefaultAnnotator);

        // Номер точки в файле искажён (запись пересобрали), время верное: точка находится по времени.
        string json = File.ReadAllText(annotationPath).Replace($"\"sample\":{truth.End.Point}", "\"sample\":0", StringComparison.Ordinal);
        File.WriteAllText(annotationPath, json);
        AnnotationLoad load = CorpusFormat.ReadAnnotation(read, CorpusFormat.DefaultAnnotator);

        CorpusJunction junction = Assert.Single(load.Annotation!.Junctions);
        Assert.Equal(new CorpusPoint(truth.End.Stroke, truth.End.Point), junction.End);
        Assert.Equal(new CorpusPoint(truth.Start.Stroke, truth.Start.Point), junction.Start);
        Assert.True(junction.Lifted);
        Assert.True(junction.End.Stroke < junction.Start.Stroke, "End.stroke < Start.stroke <=> the pen was lifted");
    }

    [Fact]
    public void The_reader_takes_gzip_by_signature_and_plain_jsonl_too()
    {
        using var corpus = new SyntheticCorpus();
        WordTemplate at = Tester.Word("at");
        string gz = corpus.Add("zipped", at, Tester.Synth.Mutate(at, SynthWordMutation.None, 1)!);
        string plain = Path.Combine(corpus.Root, "plain-copy.jsonl");
        using (var input = new MemoryStream(File.ReadAllBytes(gz)))
        using (var unzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress))
        using (FileStream output = File.Create(plain))
        {
            unzip.CopyTo(output);
        }

        CorpusRecording zipped = CorpusFormat.ReadRecording(gz);
        CorpusRecording unzipped = CorpusFormat.ReadRecording(plain);

        Assert.Equal(zipped.Sha256, unzipped.Sha256);
        Assert.Equal(zipped.Ink.Strokes.Length, unzipped.Ink.Strokes.Length);
        Assert.Equal("at", CorpusFormat.WordTarget(zipped));
    }

    [Fact]
    public void The_synthetic_annotation_is_valid_against_the_schema_and_the_checker_rejects_a_broken_one()
    {
        using var corpus = new SyntheticCorpus();
        WordTemplate at = Tester.Word("at");
        SynthWordSample sample = Tester.Synth.Mutate(at, SynthWordMutation.LiftAtJoin, 1)!;
        string recording = corpus.Add("valid", at, sample);
        string json = File.ReadAllText(CorpusFormat.AnnotationPath(recording, CorpusFormat.DefaultAnnotator));
        var schema = MiniJsonSchema.FromRepo("pfl.annotation.v1.schema.json");

        Assert.Empty(schema.Validate(json));
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal("pfl.annotation/1", document.RootElement.GetProperty("schema").GetString());
        Assert.NotEmpty(schema.Validate(json.Replace("\"confidence\":\"high\"", "\"confidence\":\"sure\"", StringComparison.Ordinal)));
        Assert.NotEmpty(schema.Validate(json.Replace("\"indexing\":", "\"indexing_\":", StringComparison.Ordinal)));
    }

    [Fact]
    public void An_empty_folder_gives_an_empty_report()
    {
        using var corpus = new SyntheticCorpus();

        CorpusReport report = Run(corpus);

        Assert.Empty(report.Records);
        Assert.Equal(1.0, report.BoundaryPassRate);
    }
}
