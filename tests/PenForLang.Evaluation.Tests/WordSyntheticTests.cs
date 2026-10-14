using System.Collections.Immutable;
using PenForLang.Evaluation.Tests.Support;
using PenForLang.Glyphs;
using PenForLang.Ink;
using PenForLang.Testing.SynthInk;

namespace PenForLang.Evaluation.Tests;

/// <summary>
/// Оценка слова на синтетике (V0.10, ARCHITECTURE.md §6.4.4–6.4.8, §10.3): самооценка эталона, чистые образцы, отрыв на
/// соединении (J1 и граница в пределах 0,2 x-h), точка и перекладина «сразу» (D4 и ни одного J1), слитно (без J1),
/// пропуск соединения, отрыв внутри буквы. Синтетика проверяет регрессию реализации, а не точность на детях (A08).
/// </summary>
public class WordSyntheticTests
{
    private const double BoundaryTolerance = 0.2;
    private const ulong Seeds = 20;

    public static TheoryData<string> TargetWords => ["at", "it"];

    public static TheoryData<string> ManyWords => ["at", "it", "ta", "tat", "ait", "iti", "aai"];

    private static double Distance(InkPoint point, SynthInkPoint truth) =>
        Math.Sqrt(Math.Pow(point.U - truth.U, 2) + Math.Pow(point.V - truth.V, 2));

    private static ErrorCode[] Joins => [ErrorCode.J1, ErrorCode.J2, ErrorCode.J3, ErrorCode.J4];

    private static ErrorCode[] ProcessAndDeferredAndJoins => [.. Tester.ProcessAndDeferred, .. Joins];

    private static SynthWordSample Sample(string text, SynthWordMutation mutation, ulong seed) =>
        Tester.Synth.Mutate(Tester.Word(text), mutation, seed) ?? throw new InvalidOperationException($"{mutation} does not apply to '{text}'");

    // ---------------------------------- самооценка эталона ----------------------------------

    [Theory]
    [MemberData(nameof(ManyWords))]
    public void The_ideal_word_evaluates_itself_without_findings(string text)
    {
        EvaluationResult result = Tester.EvaluateWord(Tester.Synth.Ideal(Tester.Word(text)), text);
        EvaluationSummary s = result.Summary;

        Assert.Equal(Decision.Target, s.Decision);
        Assert.Equal(TargetKind.Word, s.Kind);
        Assert.Equal(text, s.TargetText);
        Assert.Equal(text, s.WrittenText);
        Assert.Empty(s.Findings);
        Assert.Equal(Confidence.High, s.Confidence);
        Assert.Equal(text.Length, s.Letters.Length);
        Assert.All(s.Letters, l =>
        {
            Assert.True(l.Form >= 0.999, $"letter {l.Index} form {l.Form}");
            Assert.True(l.ProcessOk);
            Assert.True(l.SegmentationConfident);
        });
        Assert.Equal(text.Length - 1, s.Joins.Length);
        Assert.All(s.Joins, j =>
        {
            Assert.True(j.Present);
            Assert.False(j.LiftExpected);
            Assert.Equal(1.0, j.Score);
        });
        Assert.All(s.Deferred, d =>
        {
            Assert.Equal(DeferredStatus.Present, d.Status);
            Assert.Equal(DeferredTiming.AfterWord, d.Timing);
        });
        Assert.Empty(result.Detail.Word!.Lifts);
        Assert.All(result.Detail.Word.Boundaries, b => Assert.True(b.EndConfident && b.StartConfident));
    }

    [Theory]
    [MemberData(nameof(TargetWords))]
    public void Clean_noisy_samples_have_no_process_deferred_or_join_findings(string text)
    {
        for (ulong seed = 1; seed <= Seeds; seed++)
        {
            EvaluationResult result = Tester.EvaluateWord(Tester.Synth.Clean(Tester.Word(text), seed), text);

            Assert.Equal(Decision.Target, result.Summary.Decision);
            Assert.DoesNotContain(result.Summary.Findings, f => ProcessAndDeferredAndJoins.Contains(f.Code));
            Assert.DoesNotContain(result.Summary.Findings, f => f.Code is ErrorCode.F1 or ErrorCode.F2 or ErrorCode.F3);
            Assert.All(result.Summary.Joins, j => Assert.True(j.Present && !j.LiftExpected, $"seed {seed}"));
        }
    }

    // ---------------------------------- слитно ----------------------------------

    [Theory]
    [MemberData(nameof(TargetWords))]
    public void Connected_writing_has_no_j1_and_the_boundaries_are_within_0_2_xh(string text)
    {
        int total = 0;
        int within = 0;
        for (ulong seed = 1; seed <= Seeds; seed++)
        {
            SynthWordSample sample = Sample(text, SynthWordMutation.None, seed);
            EvaluationResult result = Tester.EvaluateWord(sample.Ink, text);

            Assert.False(result.Summary.Has(ErrorCode.J1), $"seed {seed}");
            foreach (SynthJoinTruth truth in sample.Joins)
            {
                WordBoundary b = result.Detail.Word!.Boundaries[truth.Join];
                Assert.False(b.Lifted);
                total += 2;
                within += (Distance(b.BodyEnd, truth.End) <= BoundaryTolerance ? 1 : 0) + (Distance(b.BodyStart, truth.Start) <= BoundaryTolerance ? 1 : 0);
            }
        }

        Assert.True(within >= 0.98 * total, $"{within} of {total} boundaries are within {BoundaryTolerance} x-h");
    }

    // ---------------------------------- отрыв на соединении ----------------------------------

    [Theory]
    [InlineData("at", SynthWordMutation.LiftAtJoin)]
    [InlineData("it", SynthWordMutation.LiftAtJoin)]
    [InlineData("at", SynthWordMutation.SkipJoin)]
    [InlineData("it", SynthWordMutation.SkipJoin)]
    [InlineData("tat", SynthWordMutation.LiftAtJoin)]
    [InlineData("ait", SynthWordMutation.SkipJoin)]
    public void A_pen_lift_on_a_join_gives_j1_there_and_the_boundary_stays_within_0_2_xh(string text, SynthWordMutation mutation)
    {
        int total = 0;
        int within = 0;
        for (ulong seed = 1; seed <= Seeds; seed++)
        {
            SynthWordSample sample = Sample(text, mutation, seed);
            EvaluationResult result = Tester.EvaluateWord(sample.Ink, text);
            EvaluationSummary s = result.Summary;

            Assert.Equal(Decision.Target, s.Decision);
            foreach (ExpectedFinding expected in sample.Expected)
            {
                Finding j1 = Assert.Single(s.Of(expected.Code), f => f.Where.StrokeId == expected.StrokeId);
                Assert.Equal(FindingPlace.Join, j1.Where.Place);
                Assert.Equal(FindingKind.Error, j1.Kind);
                Assert.True(j1.Severity > 0.0);
                Assert.Equal(TemplatePart.Join, j1.Span.Part);
            }

            Assert.Equal(sample.Joins.Length, s.Of(ErrorCode.J1).Count());
            Assert.All(sample.Forbidden, code => Assert.False(s.Has(code), $"seed {seed}: unexpected {code}"));
            Assert.All(s.Joins, j =>
            {
                Assert.False(j.Present);
                Assert.False(j.LiftExpected);
                Assert.Equal(0.0, j.Score);
            });
            foreach (SynthJoinTruth truth in sample.Joins)
            {
                WordBoundary b = result.Detail.Word!.Boundaries[truth.Join];
                Assert.True(b.Lifted);
                Assert.Contains(result.Detail.Word.Lifts, l => l.Join == truth.Join && !l.Expected);
                total += 2;
                within += (Distance(b.BodyEnd, truth.End) <= BoundaryTolerance ? 1 : 0) + (Distance(b.BodyStart, truth.Start) <= BoundaryTolerance ? 1 : 0);
            }

            Assert.Equal(0, result.Detail.Word!.LiftsInsideLetter);
        }

        Assert.True(within >= 0.9 * total, $"{within} of {total} boundaries are within {BoundaryTolerance} x-h");
    }

    // ---------------------------------- точка и перекладина сразу ----------------------------------

    [Theory]
    [InlineData("it", "dot")]
    [InlineData("ta", "bar")]
    [InlineData("tit", "bar")]
    [InlineData("iti", "dot")]
    public void A_dot_or_bar_put_immediately_gives_d4_and_no_j1(string text, string slot)
    {
        for (ulong seed = 1; seed <= Seeds; seed++)
        {
            SynthWordSample sample = Sample(text, SynthWordMutation.DeferredImmediately, seed);
            EvaluationResult result = Tester.EvaluateWord(sample.Ink, text);
            EvaluationSummary s = result.Summary;

            Assert.Equal(Decision.Target, s.Decision);
            Finding d4 = s.Of(ErrorCode.D4).First(f => f.Where.StrokeId == slot);
            Assert.Equal(FindingKind.Info, d4.Kind);
            Assert.Equal(0.0, d4.Severity);
            Assert.Equal(sample.Expected.Length, s.Of(ErrorCode.D4).Count());
            Assert.Contains(s.Deferred, d => d.StrokeId == slot && d.Timing == DeferredTiming.Immediate && d.Status == DeferredStatus.Present);

            // Ни одного J1: отрыв рядом с поставленным сразу штрихом ожидаем (A03).
            Assert.False(s.Has(ErrorCode.J1), $"seed {seed}");
            Assert.All(sample.Forbidden, code => Assert.False(s.Has(code), $"seed {seed}: unexpected {code}"));
            Assert.Contains(s.Joins, j => j.Present && j.LiftExpected && j.Score == 1.0);
            Assert.Contains(result.Detail.Word!.Lifts, l => l.Expected);
            Assert.Equal(0, result.Detail.Word.LiftsInsideLetter);
            Assert.All(s.Letters, l => Assert.True(l.ProcessOk));
        }
    }

    [Fact]
    public void The_same_lift_without_a_dot_between_is_j1_so_the_exemption_is_really_the_dot()
    {
        for (ulong seed = 1; seed <= Seeds; seed++)
        {
            RulerInk withDot = Sample("it", SynthWordMutation.DeferredImmediately, seed).Ink;
            RulerStroke dot = withDot.Strokes.OrderBy(s => s.Points.Length).First(s => s.Points.Length < 80);
            RulerInk withoutDot = withDot with { Strokes = [.. withDot.Strokes.Where(s => s.Index != dot.Index)] };

            EvaluationSummary summary = Tester.EvaluateWord(withoutDot, "it").Summary;

            Assert.True(summary.Has(ErrorCode.J1), $"seed {seed}: the lift is not expected without the dot");
            Assert.True(summary.Has(ErrorCode.D1), "the dot is missing now");
            Assert.False(summary.Has(ErrorCode.D4));
        }
    }

    [Fact]
    public void Strict_acceptance_makes_d4_an_error_but_still_no_j1()
    {
        SynthWordSample sample = Sample("it", SynthWordMutation.DeferredImmediately, 1);

        EvaluationSummary s = Tester.EvaluateWord(sample.Ink, "it", policy: DeferredAcceptPolicy.AfterWord).Summary;

        Finding d4 = Assert.Single(s.Of(ErrorCode.D4));
        Assert.Equal(FindingKind.Error, d4.Kind);
        Assert.True(d4.Severity > 0.0);
        Assert.False(s.Has(ErrorCode.J1));
    }

    [Fact]
    public void A_bar_after_the_stem_at_the_end_of_the_word_is_after_the_word_so_neither_d4_nor_j1()
    {
        // У последней буквы «сразу после ствола» и «после слова» — одно и то же.
        for (ulong seed = 1; seed <= Seeds; seed++)
        {
            SynthWordSample sample = Sample("at", SynthWordMutation.DeferredImmediately, seed);
            EvaluationSummary s = Tester.EvaluateWord(sample.Ink, "at").Summary;

            Assert.Empty(sample.Expected);
            Assert.False(s.Has(ErrorCode.D4));
            Assert.False(s.Has(ErrorCode.J1));
            Assert.All(s.Deferred, d => Assert.Equal(DeferredTiming.AfterWord, d.Timing));
        }
    }

    // ---------------------------------- прочие мутации ----------------------------------

    [Theory]
    [InlineData("at")]
    [InlineData("it")]
    [InlineData("tit")]
    public void A_missing_dot_or_bar_is_d1_and_nothing_else_about_joins_or_timing(string text)
    {
        for (ulong seed = 1; seed <= Seeds; seed++)
        {
            SynthWordSample sample = Sample(text, SynthWordMutation.MissingDeferred, seed);
            EvaluationSummary s = Tester.EvaluateWord(sample.Ink, text).Summary;

            Assert.Equal(sample.Expected.Length, s.Of(ErrorCode.D1).Count());
            Assert.All(sample.Forbidden, code => Assert.False(s.Has(code), $"seed {seed}: unexpected {code}"));
            Assert.All(s.Deferred, d => Assert.Equal(DeferredStatus.Missing, d.Status));
        }
    }

    [Theory]
    [MemberData(nameof(TargetWords))]
    public void A_lift_inside_a_letter_is_counted_and_is_neither_j1_nor_a_stop(string text)
    {
        for (ulong seed = 1; seed <= Seeds; seed++)
        {
            SynthWordSample sample = Sample(text, SynthWordMutation.LiftInsideLetter, seed);
            EvaluationResult result = Tester.EvaluateWord(sample.Ink, text);

            Assert.Equal(sample.LiftsInsideLetter, result.Detail.Word!.LiftsInsideLetter);
            Assert.Equal(sample.LiftsInsideLetter, result.Detail.Word.Lifts.Count(l => l.Letter >= 0 && !l.Expected));
            Assert.All(sample.Forbidden, code => Assert.False(result.Summary.Has(code), $"seed {seed}: unexpected {code}"));
            Assert.DoesNotContain(result.Summary.Findings, f => f.Code is ErrorCode.P3 or ErrorCode.P4 or ErrorCode.P5);
            Assert.All(result.Summary.Joins, j => Assert.True(j.Present));
        }
    }

    // ---------------------------------- границы и уверенность ----------------------------------

    [Fact]
    public void Boundaries_report_the_stroke_and_point_of_the_original_ink()
    {
        SynthWordSample sample = Sample("at", SynthWordMutation.SkipJoin, 3);

        WordBoundary b = Tester.EvaluateWord(sample.Ink, "at").Detail.Word!.Boundaries[0];

        RulerStroke end = sample.Ink.Strokes.Single(s => s.Index == b.BodyEnd.InkStrokeIndex);
        RulerStroke start = sample.Ink.Strokes.Single(s => s.Index == b.BodyStart.InkStrokeIndex);
        Assert.True(b.BodyEnd.InkStrokeIndex < b.BodyStart.InkStrokeIndex, "End.stroke < Start.stroke <=> the pen was lifted");
        Assert.Equal(end.Points[b.BodyEnd.PointIndex].U, b.BodyEnd.U);
        Assert.Equal(start.Points[b.BodyStart.PointIndex].V, b.BodyStart.V);
        Assert.Equal(start.Points[b.BodyStart.PointIndex].TimeUs, b.BodyStart.TimeUs);
        Assert.Equal(end.Points.Length - 1, b.BodyEnd.PointIndex);
        Assert.Equal(0, b.BodyStart.PointIndex);
    }

    [Fact]
    public void Boundary_confidence_drives_segmentation_confidence_and_inner_process_ok()
    {
        // С недостижимым порогом уверенности ни одна граница не уверенная: процесс внутренней буквы не считается (null),
        // уверенность слова не выше Medium (§6.4.5, §6.4.13); находки по соединениям и отложенным остаются.
        string json = Shipped.ConfigText.Replace("\"minRiseCost\": 0.05", "\"minRiseCost\": 900", StringComparison.Ordinal);
        var evaluator = new WordEvaluator(PenForLang.Evaluation.Config.ScoringConfigLoader.Parse(System.Text.Encoding.UTF8.GetBytes(json)));
        WordTemplate word = Tester.Word("at");
        SynthWordSample sample = Sample("at", SynthWordMutation.None, 4);

        EvaluationResult result = evaluator.Evaluate(new WordEvaluationRequest(sample.Ink, word));

        Assert.False(result.Summary.Letters[1].SegmentationConfident);
        Assert.Null(result.Summary.Letters[1].ProcessOk);
        Assert.NotNull(result.Summary.Letters[0].ProcessOk);
        Assert.Equal(Confidence.Medium, result.Summary.Confidence);
        Assert.Contains(result.Detail.Word!.Boundaries, b => !b.StartConfident);
        Assert.Equal(Confidence.High, Tester.EvaluateWord(sample.Ink, "at").Summary.Confidence);
    }

    // ---------------------------------- не распознано, устойчивость ----------------------------------

    [Fact]
    public void Scribbles_and_empty_ink_are_unrecognized_without_findings()
    {
        RulerInk scribble = Tester.Synth.Mutate(Shipped.Glyph("a"), SynthMutation.Scribble, 1)!.Ink;
        var empty = new RulerInk([], null, 10.0, false);

        foreach (RulerInk ink in new[] { scribble, empty })
        {
            EvaluationSummary s = Tester.EvaluateWord(ink, "at").Summary;

            Assert.Equal(Decision.Unrecognized, s.Decision);
            Assert.Equal(Confidence.Low, s.Confidence);
            Assert.Empty(s.Findings);
            Assert.Empty(s.Joins);
            Assert.All(s.Letters, l => Assert.Null(l.ProcessOk));
        }
    }

    [Fact]
    public void Another_word_is_u0_not_a_clean_word()
    {
        // Опознавания букв (L*) в v0 нет; чужое слово не должно выглядеть безупречным: «не распознано» (U0), без находок.
        EvaluationSummary s = Tester.EvaluateWord(Tester.Synth.Clean(Tester.Word("it"), 3), "at").Summary;

        Assert.Equal(Decision.Unrecognized, s.Decision);
        Assert.Equal(Confidence.Low, s.Confidence);
        Assert.Empty(s.Findings);
    }

    [Fact]
    public void An_immediate_mark_of_another_letter_does_not_exempt_a_lift_on_a_distant_join()
    {
        // W10-17: отрыв ожидаем только у буквы назначенного слота. В «tat» перекладина первой t поставлена сразу (отрыв на соединении 0
        // ожидаем), а отрыв посреди соединения 1 — не её и остаётся J1.
        for (ulong seed = 1; seed <= 10; seed++)
        {
            SynthWordSample sample = Sample("tat", SynthWordMutation.DeferredImmediately, seed);
            SynthJoinTruth join1 = sample.Joins[1];
            RulerStroke main = sample.Ink.Strokes.Single(s => s.Index == join1.End.Stroke);
            int cut = (join1.End.Point + join1.Start.Point) / 2;
            RulerStroke head = main with { Points = [.. main.Points.Take(cut + 1)] };
            RulerStroke tail = main with { Index = 100, Points = [.. main.Points.Skip(cut)] };
            RulerInk ink = sample.Ink with { Strokes = [.. sample.Ink.Strokes.Select(s => s.Index == main.Index ? head : s), tail] };

            EvaluationSummary s = Tester.EvaluateWord(ink, "tat").Summary;

            Finding j1 = Assert.Single(s.Of(ErrorCode.J1));
            Assert.Equal(1, j1.Where.LetterIndex);
            Assert.True(s.Joins[0].LiftExpected, $"seed {seed}: the lift at join 0 is expected (bar of letter 0)");
            Assert.False(s.Joins[1].Present);
        }
    }

    [Fact]
    public void Evaluation_is_deterministic_and_independent_of_the_evaluator_instance()
    {
        SynthWordSample sample = Sample("it", SynthWordMutation.DeferredImmediately, 9);

        string first = Describe(Tester.EvaluateWord(sample.Ink, "it"));
        string second = Describe(Tester.EvaluateWord(sample.Ink, "it"));
        string fresh = Describe(new WordEvaluator(Shipped.Config).Evaluate(new WordEvaluationRequest(sample.Ink, WordTemplateBuilder.Build(Shipped.Pack, "it"))));

        Assert.Equal(first, second);
        Assert.Equal(first, fresh);
    }

    [Fact]
    public void Evaluation_does_not_depend_on_where_the_word_stands_on_the_line()
    {
        SynthWordSample sample = Sample("at", SynthWordMutation.LiftAtJoin, 5);

        EvaluationSummary moved = Tester.EvaluateWord(Tester.Shift(sample.Ink, 3.7, 0.0), "at").Summary;

        Assert.Equal(Decision.Target, moved.Decision);
        Assert.Single(moved.Of(ErrorCode.J1));
        Assert.DoesNotContain(moved.Findings, f => f.Code is ErrorCode.D1 or ErrorCode.D2 or ErrorCode.D3);
    }

    private static string Describe(EvaluationResult r)
    {
        var sb = new System.Text.StringBuilder();
        EvaluationSummary s = r.Summary;
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{s.Decision}/{s.Confidence}/{s.Info.ConfigHash};");
        foreach (LetterSummary l in s.Letters)
        {
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"L{l.Index}:{l.Form:R}:{l.ProcessOk}:{l.SegmentationConfident};");
        }

        foreach (JoinSummary j in s.Joins)
        {
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"J{j.Index}:{j.Present}:{j.LiftExpected}:{j.Score:R};");
        }

        foreach (Finding f in s.Findings)
        {
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"F{f.Code}:{f.Where.Place}:{f.Where.LetterIndex}:{f.Where.StrokeId}:{f.Severity:R}:{f.Measured:R};");
        }

        foreach (WordBoundary b in r.Detail.Word!.Boundaries)
        {
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"B{b.Join}:{b.BodyEnd.PointIndex}:{b.BodyStart.PointIndex}:{b.EndRise:R};");
        }

        return sb.ToString();
    }
}
