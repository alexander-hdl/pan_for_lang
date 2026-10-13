using PenForLang.Evaluation.Tests.Support;
using PenForLang.Glyphs;
using PenForLang.Ink;
using PenForLang.Testing.SynthInk;

namespace PenForLang.Evaluation.Tests;

/// <summary>Инварианты оценщика (ARCHITECTURE.md §10.4) и детерминизм (§6.6, A19).</summary>
public class InvariantTests
{
    public static TheoryData<string> Letters => ["a", "i", "t"];

    [Theory]
    [MemberData(nameof(Letters))]
    public void Repeated_calls_give_identical_results(string symbol)
    {
        Glyph glyph = Shipped.Glyph(symbol);
        foreach (ulong seed in new ulong[] { 1, 2, 3 })
        {
            RulerInk ink = Tester.Synth.Clean(glyph, seed);
            string first = ResultText.Describe(Tester.Evaluate(ink, glyph));
            string second = ResultText.Describe(Tester.Evaluate(ink, glyph));

            Assert.Equal(first, second);
        }
    }

    [Theory]
    [MemberData(nameof(Letters))]
    public void A_fresh_evaluator_with_the_same_config_gives_the_same_result_as_a_warm_one(string symbol)
    {
        Glyph glyph = Shipped.Glyph(symbol);
        SynthSample mutated = Tester.Synth.Mutate(glyph, SynthMutation.WrongStart, 7)!;
        EvaluationResult warm = Tester.Evaluate(mutated.Ink, glyph);

        EvaluationResult fresh = Shipped.NewEvaluator().Evaluate(new LetterEvaluationRequest(mutated.Ink, glyph, Shipped.Style));

        Assert.Equal(ResultText.Describe(warm), ResultText.Describe(fresh));
    }

    [Fact]
    public void Evaluation_is_thread_safe_and_deterministic_under_concurrent_use()
    {
        Glyph glyph = Shipped.Glyph("a");
        RulerInk ink = Tester.Synth.Clean(glyph, 11);
        string expected = ResultText.Describe(Tester.Evaluate(ink, glyph));
        var evaluator = Shipped.NewEvaluator();
        var results = new string[8];

        var threads = Enumerable.Range(0, results.Length).Select(i => new Thread(() =>
        {
            for (int k = 0; k < 20; k++)
            {
                results[i] = ResultText.Describe(evaluator.Evaluate(new LetterEvaluationRequest(ink, glyph, Shipped.Style)));
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        Assert.All(results, r => Assert.Equal(expected, r));
    }

    [Theory]
    [MemberData(nameof(Letters))]
    public void Shifting_the_ink_along_the_line_changes_neither_decisions_nor_form(string symbol)
    {
        Glyph glyph = Shipped.Glyph(symbol);
        foreach (ulong seed in new ulong[] { 1, 2, 3, 4, 5 })
        {
            RulerInk ink = Tester.Synth.Clean(glyph, seed);
            EvaluationResult origin = Tester.Evaluate(ink, glyph);
            EvaluationResult shifted = Tester.Evaluate(Tester.Shift(ink, 3.7, 0.0), glyph);

            Assert.Equal(origin.Summary.Findings.Select(f => f.Code), shifted.Summary.Findings.Select(f => f.Code));
            Assert.Equal(origin.Summary.Form, shifted.Summary.Form, 0.01);
        }
    }

    [Theory]
    [MemberData(nameof(Letters))]
    public void Shifting_the_ink_off_the_baseline_is_a_ruler_finding_not_a_process_error(string symbol)
    {
        Glyph glyph = Shipped.Glyph(symbol);
        RulerInk ink = Tester.Synth.Ideal(glyph);
        EvaluationResult lifted = Tester.Evaluate(Tester.Shift(ink, 0.0, 0.5), glyph);
        EvaluationResult origin = Tester.Evaluate(ink, glyph);

        Assert.DoesNotContain(lifted.Summary.Findings, f => Tester.ProcessAndDeferred.Contains(f.Code));
        Assert.Contains(lifted.Summary.Findings, f => f.Code is ErrorCode.F4 or ErrorCode.F5);
        Assert.True(lifted.Summary.Form < origin.Summary.Form, "ruler position is part of form (F4, F5)");
    }

    [Fact]
    public void Stronger_noise_lowers_form_on_average()
    {
        Glyph glyph = Shipped.Glyph("a");
        double Mean(double factor)
        {
            var generator = new SynthInkGenerator(
                Shipped.Style,
                new SynthOptions
                {
                    ScaleMin = 1.0,
                    ScaleMax = 1.0,
                    SlantRangeDeg = 0.0,
                    RotationRangeDeg = 0.0,
                    BaselineDrift = 0.0,
                    StrokeOffsetSigma = 0.02 * factor,
                    WobbleAmplitude = 0.05 * factor,
                    TremorSigma = 0.004 * factor,
                });
            return Enumerable.Range(1, 30).Average(seed => Tester.Evaluate(generator.Clean(glyph, (ulong)seed), glyph).Summary.Form);
        }

        double calm = Mean(0.5);
        double normal = Mean(2.0);
        double rough = Mean(5.0);
        double wild = Mean(10.0);

        Assert.True(calm >= normal, $"calm {calm:F3} vs normal {normal:F3}");
        Assert.True(normal > rough, $"normal {normal:F3} vs rough {rough:F3}");
        Assert.True(rough > wild, $"rough {rough:F3} vs wild {wild:F3}");
    }

    [Fact]
    public void Time_stamps_do_not_affect_the_result_only_the_order_of_strokes_does()
    {
        Glyph glyph = Shipped.Glyph("t");
        RulerInk ink = Tester.Synth.Clean(glyph, 6);
        RulerInk slowed = ink with
        {
            Strokes = [.. ink.Strokes.Select(s => s with { Points = [.. s.Points.Select(p => p with { TimeUs = p.TimeUs * 3, Pressure = 0.5f })] })],
        };

        Assert.Equal(
            Tester.Evaluate(ink, glyph).Summary.Form,
            Tester.Evaluate(slowed, glyph).Summary.Form,
            1e-12);
    }

    [Fact]
    public void Stroke_order_in_the_ink_list_does_not_matter_only_the_time_does()
    {
        Glyph glyph = Shipped.Glyph("t");
        RulerInk ink = Tester.Synth.Clean(glyph, 8);
        RulerInk reordered = ink with { Strokes = [.. ink.Strokes.Reverse()] };

        EvaluationResult a = Tester.Evaluate(ink, glyph);
        EvaluationResult b = Tester.Evaluate(reordered, glyph);

        Assert.Equal(a.Summary.Form, b.Summary.Form, 1e-9);
        Assert.Equal(a.Summary.Deferred.Select(d => d.Timing), b.Summary.Deferred.Select(d => d.Timing));
    }
}
