using PenForLang.Evaluation.Tests.Support;
using PenForLang.Glyphs;
using PenForLang.Ink;
using PenForLang.Testing.SynthInk;

namespace PenForLang.Evaluation.Tests;

/// <summary>
/// Зашумлённые корректные образцы («чистые» попытки ребёнка): ни одной ложной ошибки процесса и отложенных штрихов
/// (ARCHITECTURE.md §6.8: P1/P2/P3 ≤ 2%; здесь синтетика — регрессия реализации, не go/no-go, A08).
/// </summary>
public class CleanSamplesTests
{
    private const int SampleCount = 50;

    public static TheoryData<string> Letters => ["a", "i", "t"];

    [Theory]
    [MemberData(nameof(Letters))]
    public void Fifty_clean_noisy_samples_give_no_false_process_or_deferred_findings(string symbol)
    {
        Glyph glyph = Shipped.Glyph(symbol);
        for (ulong seed = 1; seed <= SampleCount; seed++)
        {
            EvaluationResult result = Tester.Evaluate(Tester.Synth.Clean(glyph, seed), glyph);

            string context = $"{symbol} seed {seed}";
            Assert.True(result.Summary.Decision == Decision.Target, $"{context}: decision {result.Summary.Decision}");
            Assert.True(
                !result.Summary.Findings.Any(f => Tester.ProcessAndDeferred.Contains(f.Code)),
                $"{context}: false findings {string.Join(", ", result.Summary.Findings.Select(f => f.Code))}");
            Assert.True(result.Summary.Letters[0].ProcessOk == true, $"{context}: process not ok");
            Assert.True(result.Summary.Form >= 0.6, $"{context}: form {result.Summary.Form:F3} below the 'correct' threshold 0.6");
            Assert.True(result.Summary.Deferred.All(d => d.Status == DeferredStatus.Present && d.Timing == DeferredTiming.AfterWord), $"{context}: deferred");
        }
    }

    [Theory]
    [MemberData(nameof(Letters))]
    public void Clean_samples_are_never_flagged_as_wrong_direction_or_start_even_with_a_wide_spread(string symbol)
    {
        // Вариации в полтора раза шире «разумных детских»: P1/P2 по-прежнему не срабатывают.
        var wide = new SynthInkGenerator(
            Shipped.Style,
            new SynthOptions
            {
                ScaleMin = 0.75,
                ScaleMax = 1.4,
                SlantRangeDeg = 20.0,
                RotationRangeDeg = 4.5,
                BaselineDrift = 0.09,
                WobbleAmplitude = 0.06,
                TremorSigma = 0.0075,
            });
        Glyph glyph = Shipped.Glyph(symbol);
        int falseP2 = 0;
        int falseStops = 0;
        for (ulong seed = 1; seed <= SampleCount; seed++)
        {
            EvaluationResult result = Tester.Evaluate(wide.Clean(glyph, seed), glyph);
            falseP2 += result.Summary.Has(ErrorCode.P2) ? 1 : 0;
            falseStops += result.Summary.Findings.Any(f => f.Code is ErrorCode.P1 or ErrorCode.P3 or ErrorCode.P4 or ErrorCode.P5) ? 1 : 0;
        }

        Assert.Equal(0, falseP2);
        Assert.True(falseStops <= 1, $"{falseStops} of {SampleCount} wide-spread clean samples got a false P1/P3/P4/P5");
    }

    [Theory]
    [MemberData(nameof(Letters))]
    public void Clean_samples_form_is_high_on_median(string symbol)
    {
        Glyph glyph = Shipped.Glyph(symbol);
        var forms = new List<double>();
        for (ulong seed = 1; seed <= SampleCount; seed++)
        {
            forms.Add(Tester.Evaluate(Tester.Synth.Clean(glyph, seed), glyph).Summary.Form);
        }

        forms.Sort();
        Assert.InRange(forms[SampleCount / 2], 0.9, 1.0);
        Assert.InRange(forms[0], 0.6, 1.0);
    }

    [Fact]
    public void Same_seed_gives_the_same_sample_and_another_seed_a_different_one()
    {
        Glyph glyph = Shipped.Glyph("t");
        RulerInk first = Tester.Synth.Clean(glyph, 5);
        RulerInk again = Tester.Synth.Clean(glyph, 5);
        RulerInk other = Tester.Synth.Clean(glyph, 6);

        Assert.Equal(first.Strokes.Length, again.Strokes.Length);
        for (int s = 0; s < first.Strokes.Length; s++)
        {
            Assert.True(first.Strokes[s].Points.SequenceEqual(again.Strokes[s].Points), "same seed must give identical points");
        }

        Assert.False(first.Strokes[0].Points.SequenceEqual(other.Strokes[0].Points));
    }
}
