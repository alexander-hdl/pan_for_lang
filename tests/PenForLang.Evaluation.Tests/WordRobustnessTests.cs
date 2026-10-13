using System.Collections.Immutable;
using PenForLang.Evaluation.Tests.Support;
using PenForLang.Ink;
using PenForLang.Testing.SynthInk;

namespace PenForLang.Evaluation.Tests;

/// <summary>Странные входы слова не роняют оценщик и дают определённое решение (никаких исключений на живом пере).</summary>
public class WordRobustnessTests
{
    private static RulerStroke Stroke(int index, params (double U, double V)[] points) =>
        new(index, [.. points.Select((p, i) => new RulerPoint(p.U, p.V, 1_000_000 + (index * 100_000L) + (i * 5000L), 0.5f))], false);

    private static RulerInk Ink(params RulerStroke[] strokes) => new([.. strokes], null, 10.0, false);

    [Fact]
    public void A_single_point_and_tiny_strokes_are_handled()
    {
        foreach (RulerInk ink in new[]
        {
            Ink(Stroke(0, (1.0, 0.5))),
            Ink(Stroke(0, (1.0, 0.5), (1.01, 0.5))),
            Ink(Stroke(0, (1.0, 0.5)), Stroke(1, (1.2, 0.5)), Stroke(2, (1.4, 0.5))),
        })
        {
            EvaluationResult result = Tester.EvaluateWord(ink, "at");

            Assert.Equal(Decision.Unrecognized, result.Summary.Decision);
            Assert.Equal(Confidence.Low, result.Summary.Confidence);
        }
    }

    [Fact]
    public void Non_finite_points_are_ignored()
    {
        RulerInk clean = Tester.Synth.Clean(Tester.Word("at"), 4);
        RulerStroke first = clean.Strokes[0];
        RulerStroke dirty = first with
        {
            Points = [.. first.Points.Select((p, i) => i % 50 == 7 ? p with { U = double.NaN } : (i % 50 == 9 ? p with { V = double.PositiveInfinity } : p))],
        };
        RulerInk ink = clean with { Strokes = clean.Strokes.SetItem(0, dirty) };

        EvaluationResult result = Tester.EvaluateWord(ink, "at");

        Assert.Equal(Decision.Target, result.Summary.Decision);
        Assert.DoesNotContain(result.Summary.Findings, f => f.Code is ErrorCode.J1 or ErrorCode.D1);
    }

    [Fact]
    public void Many_short_strokes_do_not_break_the_alignment()
    {
        RulerInk clean = Tester.Synth.Clean(Tester.Word("at"), 6);
        ImmutableArray<RulerStroke>.Builder strokes = ImmutableArray.CreateBuilder<RulerStroke>();
        int index = 0;
        foreach (RulerStroke s in clean.Strokes.Where(s => s.Points.Length > 80))
        {
            // Штрих рубится на куски по 120 точек (около 0,6 x-height): перо «дрожит» и отрывается много раз.
            for (int from = 0; from < s.Points.Length; from += 120)
            {
                strokes.Add(new RulerStroke(index++, [.. s.Points.Skip(from).Take(121)], false));
            }
        }

        EvaluationResult result = Tester.EvaluateWord(clean with { Strokes = strokes.ToImmutable() }, "at");

        Assert.True(result.Summary.Decision is Decision.Target or Decision.Unrecognized);
        if (result.Summary.Decision == Decision.Target)
        {
            Assert.True(result.Detail.Word!.Lifts.Length >= 4, "the many lifts are recorded");
        }
    }

    [Fact]
    public void A_word_written_far_from_the_origin_and_huge_or_tiny_is_still_evaluated()
    {
        SynthWordSample sample = Tester.Synth.Mutate(Tester.Word("at"), SynthWordMutation.SkipJoin, 2)!;

        foreach (double scale in new[] { 0.6, 1.0, 1.6 })
        {
            RulerInk scaled = sample.Ink with
            {
                Strokes = [.. sample.Ink.Strokes.Select(s => s with { Points = [.. s.Points.Select(p => p with { U = (p.U * scale) + 4.0, V = p.V * scale })] })],
            };

            EvaluationResult result = Tester.EvaluateWord(scaled, "at");

            Assert.Equal(Decision.Target, result.Summary.Decision);
            Assert.Single(result.Summary.Of(ErrorCode.J1));
        }
    }

    [Fact]
    public void A_stroke_order_that_goes_backwards_along_the_word_does_not_crash()
    {
        RulerInk clean = Tester.Synth.Clean(Tester.Word("at"), 2);
        RulerInk reversedOrder = clean with { Strokes = [.. clean.Strokes.Reverse()] };

        EvaluationResult result = Tester.EvaluateWord(reversedOrder, "at");

        Assert.True(result.Summary.Decision is Decision.Target or Decision.Unrecognized);
    }
}
