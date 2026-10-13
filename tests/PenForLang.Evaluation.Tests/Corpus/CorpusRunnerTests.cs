using PenForLang.Evaluation.Tests.Support;
using PenForLang.Glyphs;
using PenForLang.Testing.SynthInk;

namespace PenForLang.Evaluation.Tests.Corpus;

/// <summary>
/// Читатель корпуса и критерии корпусного теста (V0.10) на синтетическом корпусе во временной папке: тест, доказывающий, что
/// проверка ловит нарушение, важнее теста, что нарушений нет (CLAUDE.md).
/// </summary>
public class CorpusRunnerTests
{
    private static CorpusReport Run(SyntheticCorpus corpus, string annotator = CorpusFormat.DefaultAnnotator) =>
        CorpusRunner.Run(corpus.Root, annotator, Tester.Words, Shipped.Pack);

    private static SyntheticCorpus Protocol()
    {
        // Протокол V0.9: at-lift ×10, at-joined ×10, it-dot ×10 (здесь по 5).
        var corpus = new SyntheticCorpus();
        WordTemplate at = Tester.Word("at");
        WordTemplate it = Tester.Word("it");
        for (ulong seed = 1; seed <= 5; seed++)
        {
            corpus.Add($"at-lift-{seed}", at, Tester.Synth.Mutate(at, SynthWordMutation.SkipJoin, seed)!);
            corpus.Add($"at-joined-{seed}", at, Tester.Synth.Mutate(at, SynthWordMutation.None, seed)!);
            corpus.Add($"it-dot-{seed}", it, Tester.Synth.Mutate(it, SynthWordMutation.DeferredImmediately, seed)!);
        }

        return corpus;
    }

    [Fact]
    public void The_protocol_corpus_meets_the_criteria()
    {
        using SyntheticCorpus corpus = Protocol();

        CorpusReport report = Run(corpus);

        Assert.Equal(15, report.Records.Count);
        Assert.Equal(0, report.Skipped);
        Assert.True(report.BoundaryPassRate >= 0.9, report.Summary);
        Assert.Equal(0, report.J1Violations);
        Assert.Equal(1.0, report.J1Recall);
        Assert.Equal(5, report.Records.Count(r => r.J1Expected == 1 && r.J1Found == 1));
        Assert.All(report.Records.Where(r => r.Name.StartsWith("it-dot", StringComparison.Ordinal)), r => Assert.Equal(0, r.J1Expected));
    }

    [Fact]
    public void J1_where_the_annotation_says_connected_is_a_violation()
    {
        using var corpus = new SyntheticCorpus();
        WordTemplate at = Tester.Word("at");
        corpus.Add("wrong", at, Tester.Synth.Mutate(at, SynthWordMutation.SkipJoin, 1)!, annotationLifted: _ => false);

        CorpusReport report = Run(corpus);

        CorpusRecordResult record = Assert.Single(report.Records);
        Assert.Single(record.J1Violations);
        Assert.Equal(1, report.J1Violations);
    }

    [Fact]
    public void A_dot_put_immediately_exempts_the_lift_so_no_j1_is_expected_or_found()
    {
        using var corpus = new SyntheticCorpus();
        WordTemplate it = Tester.Word("it");
        SynthWordSample immediate = Tester.Synth.Mutate(it, SynthWordMutation.DeferredImmediately, 1)!;
        // Разметка: отрыв есть и точка поставлена сразу — отрыв ожидаем, J1 не ждём и не находим.
        corpus.Add("exempt", it, immediate);

        CorpusReport report = Run(corpus);

        Assert.Equal(0, report.J1Violations);
        Assert.Equal(0, Assert.Single(report.Records).J1Expected);
    }

    [Fact]
    public void A_missed_j1_lowers_the_recall_but_is_not_a_violation()
    {
        using var corpus = new SyntheticCorpus();
        WordTemplate at = Tester.Word("at");
        corpus.Add("joined-but-annotated-lifted", at, Tester.Synth.Mutate(at, SynthWordMutation.None, 2)!, annotationLifted: _ => true);

        CorpusReport report = Run(corpus);

        Assert.Equal(0, report.J1Violations);
        Assert.Equal(0.0, report.J1Recall);
    }

    [Fact]
    public void Boundaries_far_from_the_annotation_fail_the_pass_rate()
    {
        using var corpus = new SyntheticCorpus();
        WordTemplate at = Tester.Word("at");
        SynthWordSample sample = Tester.Synth.Mutate(at, SynthWordMutation.None, 3)!;
        // Конец тела левой буквы размечен на 60-й точке штриха — заведомо далеко от выхода a.
        SynthJoinTruth truth = sample.Joins[0];
        SynthWordSample bad = sample with { Joins = [truth with { End = truth.End with { Point = 60 } }] };
        corpus.Add("bad", at, bad);

        CorpusReport report = Run(corpus);

        CorpusRecordResult record = Assert.Single(report.Records);
        Assert.False(record.BoundariesOk);
        Assert.True(record.WorstBoundary > 0.2);
        Assert.Equal(0.0, report.BoundaryPassRate);
    }

    [Fact]
    public void Only_the_main_annotator_usable_records_and_known_letters_are_read()
    {
        using var corpus = new SyntheticCorpus();
        WordTemplate at = Tester.Word("at");
        SynthWordSample sample = Tester.Synth.Mutate(at, SynthWordMutation.None, 1)!;
        corpus.Add("a-and-b", at, sample, annotator: "A");
        corpus.Add("only-b", at, sample, annotator: "B");
        corpus.Add("rejected", at, sample, usable: false);

        CorpusReport mainAnnotator = Run(corpus, "A");
        CorpusReport second = Run(corpus, "B");

        Assert.Equal(3, mainAnnotator.Records.Count + mainAnnotator.Skipped); // a-and-b; rejected — брак; only-b — нет разметки A
        Assert.Equal(["a-and-b.pfl.jsonl.gz"], mainAnnotator.Records.Select(r => r.Name));
        Assert.Contains(second.Records, r => r.Name == "only-b.pfl.jsonl.gz");
    }

    [Fact]
    public void Annotation_points_bound_by_time_are_resolved_to_the_nearest_sample()
    {
        using var corpus = new SyntheticCorpus();
        WordTemplate at = Tester.Word("at");
        SynthWordSample sample = Tester.Synth.Mutate(at, SynthWordMutation.SkipJoin, 1)!;
        string recording = corpus.Add("by-time", at, sample);
        PenForLang.Ink.RulerInk ink = CorpusFormat.ReadInk(recording);
        SynthJoinTruth truth = sample.Joins[0];
        PenForLang.Ink.RulerPoint end = ink.Strokes.Single(s => s.Index == truth.End.Stroke).Points[truth.End.Point];
        PenForLang.Ink.RulerPoint start = ink.Strokes.Single(s => s.Index == truth.Start.Stroke).Points[truth.Start.Point];
        string json = $$"""
            { "schema": "pfl.annotation/1", "text": "at", "quality": { "usable": true, "flags": [] },
              "junctions": [ { "after": 0, "end": { "stroke": {{truth.End.Stroke}}, "t": {{end.TimeUs}} }, "start": { "stroke": {{truth.Start.Stroke}}, "t": {{start.TimeUs}} } } ],
              "deferred": [] }
            """;
        File.WriteAllText(CorpusFormat.AnnotationPath(recording, "T"), json);

        CorpusAnnotation annotation = CorpusFormat.ReadAnnotation(recording, "T", ink)!;

        CorpusJunction junction = Assert.Single(annotation.Junctions);
        Assert.Equal(new CorpusPoint(truth.End.Stroke, truth.End.Point), junction.End);
        Assert.Equal(new CorpusPoint(truth.Start.Stroke, truth.Start.Point), junction.Start);
        Assert.True(junction.Lifted, "no 'kind': a lift is End.stroke < Start.stroke");
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
