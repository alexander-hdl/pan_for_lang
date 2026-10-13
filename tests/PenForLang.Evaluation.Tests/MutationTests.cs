using PenForLang.Evaluation.Tests.Support;
using PenForLang.Glyphs;
using PenForLang.Testing.SynthInk;

namespace PenForLang.Evaluation.Tests;

/// <summary>
/// Мутации SynthInk-lite (ARCHITECTURE.md §10.3): оценщик находит ровно внесённую ошибку — верный код и привязка к штриху
/// эталона, без запрещённых побочных находок. Это регрессия реализации на собственной модели (A08), а не точность на детях.
/// </summary>
public class MutationTests
{
    private const int Seeds = 20;

    public static TheoryData<string, SynthMutation> Applicable
    {
        get
        {
            var data = new TheoryData<string, SynthMutation>();
            foreach (string symbol in Tester.Letters)
            {
                Glyph glyph = Shipped.Glyph(symbol);
                foreach (SynthMutation mutation in Enum.GetValues<SynthMutation>())
                {
                    if (mutation is SynthMutation.None or SynthMutation.Scribble)
                    {
                        continue;
                    }

                    if (Tester.Synth.Mutate(glyph, mutation, 1) is not null)
                    {
                        data.Add(symbol, mutation);
                    }
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Applicable))]
    public void Each_mutation_is_found_with_the_expected_code_and_binding(string symbol, SynthMutation mutation)
    {
        Glyph glyph = Shipped.Glyph(symbol);
        for (ulong seed = 1; seed <= Seeds; seed++)
        {
            SynthSample sample = Tester.Synth.Mutate(glyph, mutation, seed)!;
            EvaluationResult result = Tester.Evaluate(sample.Ink, glyph);
            string context = $"{symbol} {mutation} seed {seed}: [{string.Join(", ", result.Summary.Findings.Select(f => f.Code + "@" + f.Where.StrokeId))}]";

            Assert.NotEmpty(sample.Expected);
            foreach (ExpectedFinding expected in sample.Expected)
            {
                Assert.True(
                    result.Summary.Findings.Any(f => f.Code == expected.Code && (expected.StrokeId is null || f.Where.StrokeId == expected.StrokeId)),
                    $"{context}: missing {expected.Code}@{expected.StrokeId}");
            }

            foreach (ErrorCode forbidden in sample.Forbidden)
            {
                Assert.True(!result.Summary.Has(forbidden), $"{context}: forbidden {forbidden}");
            }
        }
    }

    [Theory]
    [InlineData("a", SynthMutation.WrongStart, ErrorCode.P1)]
    [InlineData("i", SynthMutation.ReversedStroke, ErrorCode.P2)]
    [InlineData("t", SynthMutation.MissingDeferred, ErrorCode.D1)]
    public void Mutation_findings_carry_a_template_span_and_measure_for_feedback(string symbol, SynthMutation mutation, ErrorCode code)
    {
        Glyph glyph = Shipped.Glyph(symbol);
        EvaluationResult result = Tester.Evaluate(Tester.Synth.Mutate(glyph, mutation, 3)!.Ink, glyph);

        Finding finding = Assert.Single(result.Summary.Of(code));
        Assert.NotNull(finding.Span.StrokeId);
        Assert.InRange(finding.Span.FromT, 0.0, 1.0);
        Assert.InRange(finding.Span.ToT, finding.Span.FromT, 1.0);
        Assert.InRange(finding.Severity, 0.0, 1.0);
        Assert.Equal(FindingKind.Error, finding.Kind);
        if (code == ErrorCode.P1)
        {
            Assert.True(finding.Measured > finding.Threshold);
            Assert.NotNull(finding.Where.InkStrokeIndex);
        }
    }

    [Theory]
    [InlineData("i")]
    [InlineData("t")]
    public void Deferred_stroke_placed_immediately_is_not_penalized_only_reported_as_info_D4(string symbol)
    {
        Glyph glyph = Shipped.Glyph(symbol);
        for (ulong seed = 1; seed <= Seeds; seed++)
        {
            SynthSample immediate = Tester.Synth.Mutate(glyph, SynthMutation.DeferredImmediately, seed)!;
            EvaluationResult result = Tester.Evaluate(immediate.Ink, glyph);

            Finding d4 = Assert.Single(result.Summary.Of(ErrorCode.D4));
            Assert.Equal(FindingKind.Info, d4.Kind);
            Assert.Equal(0.0, d4.Severity);
            Assert.Equal(DeferredTiming.Immediate, Assert.Single(result.Summary.Deferred).Timing);
            Assert.Equal(DeferredStatus.Present, result.Summary.Deferred[0].Status);

            // Решение заказчика: «сразу» не снижает form, не даёт ошибок процесса и не влияет на process_ok.
            Assert.True(result.Summary.Letters[0].ProcessOk);
            Assert.DoesNotContain(result.Summary.Findings, f => f.Kind == FindingKind.Error && Tester.ProcessAndDeferred.Contains(f.Code));
            Assert.DoesNotContain(result.Detail.FormComponents, c => c.Name == "deferred" && c.Score < 1.0);
        }
    }

    [Fact]
    public void Form_does_not_depend_on_when_the_deferred_stroke_was_put()
    {
        Glyph glyph = Shipped.Glyph("t");
        var lateForms = new List<double>();
        var earlyForms = new List<double>();
        for (ulong seed = 1; seed <= Seeds; seed++)
        {
            // Одинаковое зерно: те же вариации, отличается только порядок штрихов.
            lateForms.Add(Tester.Evaluate(Tester.Synth.Mutate(glyph, SynthMutation.None, seed)!.Ink, glyph).Summary.Form);
            earlyForms.Add(Tester.Evaluate(Tester.Synth.Mutate(glyph, SynthMutation.DeferredImmediately, seed)!.Ink, glyph).Summary.Form);
        }

        Assert.Equal(lateForms.Average(), earlyForms.Average(), 0.03);
    }

    [Fact]
    public void Strict_policy_turns_D4_into_an_error_with_the_configured_severity()
    {
        Glyph glyph = Shipped.Glyph("t");
        SynthSample immediate = Tester.Synth.Mutate(glyph, SynthMutation.DeferredImmediately, 2)!;

        EvaluationResult strict = Tester.Evaluate(immediate.Ink, glyph, policy: DeferredAcceptPolicy.AfterWord);
        EvaluationResult either = Tester.Evaluate(immediate.Ink, glyph, policy: DeferredAcceptPolicy.Either);

        Finding d4 = Assert.Single(strict.Summary.Of(ErrorCode.D4));
        Assert.Equal(FindingKind.Error, d4.Kind);
        Assert.Equal(Shipped.Config.Deferred.StrictTimingSeverity, d4.Severity);
        Assert.Equal(FindingKind.Info, Assert.Single(either.Summary.Of(ErrorCode.D4)).Kind);
        Assert.Equal(either.Summary.Form, strict.Summary.Form, 1e-9);
    }

    [Fact]
    public void Deferred_stroke_after_the_main_trace_gives_no_D4()
    {
        Glyph glyph = Shipped.Glyph("i");
        EvaluationResult result = Tester.Evaluate(Tester.Synth.Clean(glyph, 4), glyph);

        Assert.False(result.Summary.Has(ErrorCode.D4));
    }

    [Fact]
    public void Reversed_stroke_is_reported_as_P2_and_not_as_a_wrong_start()
    {
        foreach (string symbol in Tester.Letters)
        {
            Glyph glyph = Shipped.Glyph(symbol);
            for (ulong seed = 1; seed <= Seeds; seed++)
            {
                EvaluationResult result = Tester.Evaluate(Tester.Synth.Mutate(glyph, SynthMutation.ReversedStroke, seed)!.Ink, glyph);

                Assert.True(result.Summary.Has(ErrorCode.P2), $"{symbol} seed {seed}");
                Assert.False(result.Summary.Has(ErrorCode.P1), $"{symbol} seed {seed}: P1 duplicates P2");
                Assert.True(result.Detail.Strokes[0].Reversed);
                Assert.False(result.Summary.Letters[0].ProcessOk);
            }
        }
    }

    [Fact]
    public void Wrong_start_is_P1_and_keeps_direction_ok()
    {
        Glyph glyph = Shipped.Glyph("a");
        for (ulong seed = 1; seed <= Seeds; seed++)
        {
            EvaluationResult result = Tester.Evaluate(Tester.Synth.Mutate(glyph, SynthMutation.WrongStart, seed)!.Ink, glyph);

            Assert.True(result.Summary.Has(ErrorCode.P1), $"seed {seed}");
            Assert.False(result.Summary.Has(ErrorCode.P2), $"seed {seed}");
            Assert.False(result.Summary.Process().StartOk);
            Assert.True(result.Summary.Process().DirectionOk);
        }
    }

    [Fact]
    public void Open_oval_is_F2_and_a_parallel_retrace_is_P6_for_a()
    {
        Glyph glyph = Shipped.Glyph("a");
        for (ulong seed = 1; seed <= Seeds; seed++)
        {
            EvaluationResult open = Tester.Evaluate(Tester.Synth.Mutate(glyph, SynthMutation.OpenOval, seed)!.Ink, glyph);
            EvaluationResult retrace = Tester.Evaluate(Tester.Synth.Mutate(glyph, SynthMutation.ParallelRetrace, seed)!.Ink, glyph);

            Assert.True(open.Summary.Has(ErrorCode.F2), $"open oval seed {seed}");
            Assert.False(open.Summary.Has(ErrorCode.P6), $"open oval seed {seed}: closure opened at the start must not break the retrace");
            Assert.True(retrace.Summary.Has(ErrorCode.P6), $"retrace seed {seed}");
            Assert.False(retrace.Summary.Process().RetraceOk);
        }
    }

    [Theory]
    [InlineData("a")]
    [InlineData("i")]
    [InlineData("t")]
    public void Scribbles_are_not_recognized_and_get_no_findings(string symbol)
    {
        Glyph glyph = Shipped.Glyph(symbol);
        for (ulong seed = 1; seed <= Seeds; seed++)
        {
            EvaluationResult result = Tester.Evaluate(Tester.Synth.Mutate(glyph, SynthMutation.Scribble, seed)!.Ink, glyph);

            Assert.Equal(Decision.Unrecognized, result.Summary.Decision);
            Assert.Equal(Confidence.Low, result.Summary.Confidence);
            Assert.Empty(result.Summary.Findings);
            Assert.Null(result.Summary.Letters[0].ProcessOk);
        }
    }

    [Fact]
    public void Inapplicable_mutations_return_null()
    {
        Assert.Null(Tester.Synth.Mutate(Shipped.Glyph("a"), SynthMutation.MissingDeferred, 1));
        Assert.Null(Tester.Synth.Mutate(Shipped.Glyph("a"), SynthMutation.MissingStroke, 1));
        Assert.Null(Tester.Synth.Mutate(Shipped.Glyph("i"), SynthMutation.SwappedStrokeOrder, 1));
        Assert.Null(Tester.Synth.Mutate(Shipped.Glyph("t"), SynthMutation.OpenOval, 1));
    }
}

internal static class SummaryExtensions
{
    /// <summary>Процессные показатели единственной буквы.</summary>
    public static ProcessSummary Process(this EvaluationSummary summary) => summary.Letters[0].Process;
}
