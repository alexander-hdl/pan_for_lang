using PenForLang.Evaluation.Tests.Support;
using PenForLang.Glyphs;
using PenForLang.Testing.SynthInk;

namespace PenForLang.Evaluation.Tests;

/// <summary>
/// Порядок, число и старт нескольких основных штрихов (P3, P4, P5, P1) и петля (F3). У a, i, t по одному основному штриху,
/// поэтому проверки идут на глифах-фикстурах из кода (<see cref="TestGlyphs"/>).
/// </summary>
public class MultiStrokeGlyphTests
{
    private const int Seeds = 20;

    private static readonly Glyph Rings = TestGlyphs.Rings();

    [Fact]
    public void Ideal_rings_are_matched_stroke_for_stroke_with_form_one()
    {
        EvaluationResult result = Tester.Evaluate(Tester.Synth.Ideal(Rings), Rings);

        Assert.Empty(result.Summary.Findings);
        Assert.Equal(1.0, result.Summary.Form, 1e-9);
        Assert.Equal(["s1", "s2"], result.Detail.Strokes.Select(s => s.StrokeId));
        Assert.Equal([0, 1], result.Detail.Strokes.Select(s => s.InkStrokeIndex));
    }

    [Fact]
    public void Clean_noisy_rings_have_no_false_process_findings()
    {
        for (ulong seed = 1; seed <= 50; seed++)
        {
            EvaluationResult result = Tester.Evaluate(Tester.Synth.Clean(Rings, seed), Rings);

            Assert.True(!result.Summary.Findings.Any(f => Tester.ProcessAndDeferred.Contains(f.Code)), $"seed {seed}: {string.Join(", ", result.Summary.Findings.Select(f => f.Code))}");
        }
    }

    [Fact]
    public void Swapped_stroke_order_is_P3_on_the_stroke_written_too_early()
    {
        for (ulong seed = 1; seed <= Seeds; seed++)
        {
            SynthSample sample = Tester.Synth.Mutate(Rings, SynthMutation.SwappedStrokeOrder, seed)!;
            EvaluationResult result = Tester.Evaluate(sample.Ink, Rings);

            Finding p3 = Assert.Single(result.Summary.Of(ErrorCode.P3));
            Assert.Equal("s2", p3.Where.StrokeId);
            Assert.False(result.Summary.Process().OrderOk);
            Assert.False(result.Summary.Letters[0].ProcessOk);
            Assert.False(result.Summary.Has(ErrorCode.P4));
            Assert.False(result.Summary.Has(ErrorCode.P5));
            Assert.False(result.Summary.Has(ErrorCode.P1), "swapping the order must not look like a wrong start");
        }
    }

    [Fact]
    public void Missing_stroke_is_P5_and_the_remaining_stroke_is_still_matched()
    {
        // Одна полудуга без второй — мало для опознания в редких случаях: допускаем единичные U0.
        int found = 0;
        for (ulong seed = 1; seed <= 40; seed++)
        {
            SynthSample sample = Tester.Synth.Mutate(Rings, SynthMutation.MissingStroke, seed)!;
            EvaluationResult result = Tester.Evaluate(sample.Ink, Rings);
            if (result.Summary.Decision == Decision.Unrecognized)
            {
                continue;
            }

            Finding p5 = Assert.Single(result.Summary.Of(ErrorCode.P5));
            Assert.Equal("s2", p5.Where.StrokeId);
            Assert.Null(p5.Where.InkStrokeIndex);
            Assert.Equal("s1", Assert.Single(result.Detail.Strokes).StrokeId);
            Assert.Equal(1, result.Summary.Process().StrokeCountDelta);
            found++;
        }

        Assert.True(found >= 36, $"P5 found for {found} of 40");
    }

    [Fact]
    public void One_extra_stroke_is_P4_below_the_stop_threshold_two_reach_it()
    {
        Glyph glyph = Shipped.Glyph("a");
        SynthSample one = Tester.Synth.Mutate(glyph, SynthMutation.ExtraStroke, 4)!;
        EvaluationResult result = Tester.Evaluate(one.Ink, glyph);
        Finding p4 = Assert.Single(result.Summary.Of(ErrorCode.P4));
        Assert.Equal(1.0 / Shipped.Config.Process.StrokeCount.ExtraForStop, p4.Severity, 1e-9);
        Assert.Null(p4.Where.StrokeId);
        Assert.NotNull(p4.Where.InkStrokeIndex);
        Assert.True(result.Summary.Letters[0].ProcessOk, "one extra stroke is below the stop threshold");

        // Два лишних штриха: к образцу добавляется лишний штрих из другой попытки.
        SynthSample other = Tester.Synth.Mutate(glyph, SynthMutation.ExtraStroke, 9)!;
        var two = one.Ink with
        {
            Strokes = [.. one.Ink.Strokes, other.Ink.Strokes[^1] with { Index = one.Ink.Strokes.Length }],
        };
        EvaluationResult both = Tester.Evaluate(two, glyph);
        Assert.Equal(2, both.Summary.Of(ErrorCode.P4).Count());
        Assert.All(both.Summary.Of(ErrorCode.P4), f => Assert.Equal(1.0, f.Severity));
        Assert.Equal(2, both.Summary.Process().StrokeCountDelta);
        Assert.False(both.Summary.Letters[0].ProcessOk);
    }

    [Fact]
    public void Wrong_start_of_the_second_stroke_is_P1_on_that_stroke()
    {
        // «Подход» к старту длиной до 1 x-height занимает заметную долю следа двух штрихов и сдвигает подгонку масштаба и
        // положения: допускаем единичные промахи (≥ 95% по 60 зёрнам) и единичные ложные P1 на соседнем штрихе.
        int found = 0;
        int falseOnOther = 0;
        int onSecond = 0;
        for (ulong seed = 1; seed <= 60; seed++)
        {
            SynthSample sample = Tester.Synth.Mutate(Rings, SynthMutation.WrongStart, seed)!;
            string target = sample.Expected[0].StrokeId!;
            EvaluationResult result = Tester.Evaluate(sample.Ink, Rings);

            found += result.Summary.Findings.Any(f => f.Code == ErrorCode.P1 && f.Where.StrokeId == target) ? 1 : 0;
            onSecond += target == "s2" ? 1 : 0;
            falseOnOther += result.Summary.Findings.Any(f => f.Code == ErrorCode.P1 && f.Where.StrokeId != target) ? 1 : 0;
        }

        Assert.True(onSecond > 0, "the mutation never picked the second stroke");
        Assert.True(falseOnOther <= 3, $"false P1 on the untouched stroke in {falseOnOther} of 60");
        Assert.True(found >= 57, $"P1 found for {found} of 60 wrong starts");
    }

    [Fact]
    public void Reversed_stroke_is_P2_on_that_stroke_only()
    {
        int found = 0;
        for (ulong seed = 1; seed <= 40; seed++)
        {
            SynthSample sample = Tester.Synth.Mutate(Rings, SynthMutation.ReversedStroke, seed)!;
            string target = sample.Expected[0].StrokeId!;
            EvaluationResult result = Tester.Evaluate(sample.Ink, Rings);
            if (result.Summary.Decision == Decision.Unrecognized)
            {
                continue;
            }

            Assert.Equal([target], result.Summary.Of(ErrorCode.P2).Select(f => f.Where.StrokeId));
            Assert.False(result.Summary.Has(ErrorCode.P1));
            found++;
        }

        Assert.True(found >= 36, $"P2 found for {found} of 40");
    }

    [Fact]
    public void Loop_glyph_ideal_ink_has_the_loop_and_form_one()
    {
        Glyph loop = TestGlyphs.Loop();
        EvaluationResult result = Tester.Evaluate(Tester.Synth.Ideal(loop), loop);

        Assert.Empty(result.Summary.Findings);
        Assert.Equal(1.0, result.Summary.Form, 1e-9);
        Assert.Equal(true, result.Detail.Strokes[0].LoopOk);
    }

    [Fact]
    public void Missing_loop_is_F3_with_the_loop_range_as_template_span()
    {
        Glyph loop = TestGlyphs.Loop();
        Glyph noLoop = TestGlyphs.NoLoop();
        EvaluationResult result = Tester.Evaluate(Tester.Synth.Ideal(noLoop), loop);

        Finding f3 = Assert.Single(result.Summary.Of(ErrorCode.F3));
        Assert.Equal("s1", f3.Where.StrokeId);
        Assert.Equal(0.3, f3.Span.FromT);
        Assert.Equal(0.9, f3.Span.ToT);
        Assert.Equal(false, result.Detail.Strokes[0].LoopOk);
        Assert.Contains(result.Detail.FormComponents, c => c.Name == "loops" && c.Score == 0.0);
    }
}
