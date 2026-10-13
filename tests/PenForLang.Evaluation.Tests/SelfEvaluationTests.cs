using PenForLang.Evaluation.Tests.Support;
using PenForLang.Glyphs;
using PenForLang.Ink;

namespace PenForLang.Evaluation.Tests;

/// <summary>Самооценка эталона (ARCHITECTURE.md §3.2.7 п. 6, §10.4): идеальная траектория глифа даёт <c>form = 1</c> и ноль находок.</summary>
public class SelfEvaluationTests
{
    public static TheoryData<string> Letters => ["a", "i", "t"];

    [Theory]
    [MemberData(nameof(Letters))]
    public void Ideal_trajectory_gets_form_one_and_no_findings(string symbol)
    {
        Glyph glyph = Shipped.Glyph(symbol);
        EvaluationResult result = Tester.Evaluate(Tester.Synth.Ideal(glyph), glyph);

        Assert.Empty(result.Summary.Findings);
        Assert.Equal(Decision.Target, result.Summary.Decision);
        Assert.Equal(Confidence.High, result.Summary.Confidence);
        Assert.Equal(1.0, result.Summary.Form, 1e-9);
        Assert.Equal(true, result.Summary.Letters[0].ProcessOk);
        Assert.All(result.Detail.FormComponents, c => Assert.Equal(1.0, c.Score, 1e-9));
        Assert.All(result.Summary.Deferred, d =>
        {
            Assert.Equal(DeferredStatus.Present, d.Status);
            Assert.Equal(DeferredTiming.AfterWord, d.Timing);
        });
    }

    [Theory]
    [MemberData(nameof(Letters))]
    public void Ideal_trajectory_matches_every_stroke_without_normalization(string symbol)
    {
        Glyph glyph = Shipped.Glyph(symbol);
        EvaluationResult result = Tester.Evaluate(Tester.Synth.Ideal(glyph), glyph);

        StrokeMatch match = Assert.Single(result.Detail.Strokes);
        Assert.Equal("s1", match.StrokeId);
        Assert.False(match.Reversed);
        Assert.True(match.StartOk);
        Assert.True(match.DirectionOk);
        Assert.InRange(match.StartErr, 0.0, 0.02);
        Assert.InRange(match.DtwMean, 0.0, 0.02);
        Assert.InRange(match.Coverage, 0.99, 1.0);
        Assert.Equal(0.0, result.Detail.Normalization.ShearDeg);
        Assert.InRange(result.Detail.Normalization.Scale, 0.97, 1.03);
    }

    [Fact]
    public void Component_weights_are_normalized_and_form_is_their_weighted_sum()
    {
        Glyph glyph = Shipped.Glyph("t");
        EvaluationResult result = Tester.Evaluate(Tester.Synth.Clean(glyph, 3), glyph);

        Assert.Equal(1.0, result.Detail.FormComponents.Sum(c => c.Weight), 1e-9);
        Assert.Equal(result.Summary.Form, result.Detail.FormComponents.Sum(c => c.Weight * c.Score), 1e-9);
    }

    [Fact]
    public void Inapplicable_metrics_are_left_out_of_form()
    {
        // У i нет замкнутого овала, ретрейса и проверки baseline; у a нет отложенных штрихов.
        string[] forI = [.. Tester.Evaluate(Tester.Synth.Ideal(Shipped.Glyph("i")), "i").Detail.FormComponents.Select(c => c.Name)];
        string[] forA = [.. Tester.Evaluate(Tester.Synth.Ideal(Shipped.Glyph("a")), "a").Detail.FormComponents.Select(c => c.Name)];

        Assert.DoesNotContain("closed", forI);
        Assert.DoesNotContain("retrace", forI);
        Assert.DoesNotContain("baseline", forI);
        Assert.Contains("deferred", forI);
        Assert.Contains("closed", forA);
        Assert.Contains("retrace", forA);
        Assert.Contains("baseline", forA);
        Assert.DoesNotContain("deferred", forA);
    }
}
