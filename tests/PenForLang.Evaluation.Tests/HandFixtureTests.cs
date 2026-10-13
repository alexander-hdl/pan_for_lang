using System.Text.Json.Nodes;
using PenForLang.Evaluation.Config;
using PenForLang.Evaluation.Tests.HandFixtures;
using PenForLang.Evaluation.Tests.Support;
using PenForLang.Glyphs;
using PenForLang.Ink;

namespace PenForLang.Evaluation.Tests;

/// <summary>
/// Регрессии на РУЧНЫХ траекториях (формулы, не глифы и не SynthInk): SynthInk строит попытки из самого глифа и поэтому не видит
/// ошибок, которых нет в его модели. Источник — независимая проба ревью V0.7 (F-01…F-13, F-17).
/// </summary>
public class HandFixtureTests
{
    private static readonly ErrorCode[] Stops =
    [
        ErrorCode.P1, ErrorCode.P2, ErrorCode.P3, ErrorCode.P4, ErrorCode.P5, ErrorCode.P6,
        ErrorCode.D1, ErrorCode.D2, ErrorCode.D3,
    ];

    private static EvaluationResult Run(RulerInk ink, string letter, LetterEvaluator? evaluator = null) =>
        (evaluator ?? Tester.Evaluator).Evaluate(new LetterEvaluationRequest(ink, Shipped.Glyph(letter), Shipped.Style));

    private static string Codes(EvaluationResult r) => string.Join(" ", r.Summary.Findings.Select(f => f.Code + (f.Kind == FindingKind.Info ? "(i)" : string.Empty)));

    // ---------------------------------------------------------------- F-01: направление обхода овала

    [Fact]
    public void A_clockwise_oval_with_the_right_approach_and_stem_is_P2_on_the_oval_range()
    {
        EvaluationResult result = Run(HandInkBuilder.Build(Hand.AClockwise(withApproach: true)), "a");

        Finding p2 = Assert.Single(result.Summary.Of(ErrorCode.P2));
        Assert.Equal("s1", p2.Where.StrokeId);
        Assert.Equal(FindingKind.Error, p2.Kind);
        Assert.Equal(TemplatePart.Body, p2.Span.Part);
        Assert.Equal(0.0, p2.Span.FromT);
        Assert.Equal(0.679, p2.Span.ToT, 3); // участок checks.closed глифа a
        Assert.True(p2.Measured < 0.0, "the oriented area of the child's oval has the opposite sign");
        Assert.False(result.Summary.Letters[0].ProcessOk);
        Assert.False(result.Summary.Process().DirectionOk);
        Assert.False(result.Summary.Has(ErrorCode.P1), "one mistake, one finding");
        Assert.NotNull(result.Detail.Strokes[0].RangeAreaRatio);
        Assert.False(result.Detail.Strokes[0].Reversed, "the stroke as a whole is not reversed");
    }

    [Theory]
    [InlineData(false, 40)]
    [InlineData(false, 180)]
    [InlineData(true, 100)]
    public void A_clockwise_oval_without_the_approach_or_from_nine_oclock_is_P2(bool withApproach, double startDeg)
    {
        HandStroke stroke = startDeg == 180 ? Hand.AClockwiseFromNineOClock() : Hand.AClockwise(withApproach, startDeg);
        EvaluationResult result = Run(HandInkBuilder.Build(stroke), "a");

        Assert.True(result.Summary.Has(ErrorCode.P2), Codes(result));
        Assert.False(result.Summary.Has(ErrorCode.P1), "P2 explains the start: " + Codes(result));
        Assert.False(result.Summary.Letters[0].ProcessOk);
    }

    [Fact]
    public void A_counter_clockwise_oval_is_not_P2_even_when_it_overlaps_itself()
    {
        foreach (double endDeg in new[] { 380.0, 460.0 })
        {
            EvaluationResult result = Run(HandInkBuilder.Build(Hand.A(endDeg: endDeg)), "a");

            Assert.False(result.Summary.Has(ErrorCode.P2), $"end {endDeg}: {Codes(result)}");
            Assert.Equal(true, result.Summary.Letters[0].ProcessOk);
        }
    }

    [Fact]
    public void The_whole_reversed_stroke_stays_one_finding()
    {
        foreach (string letter in new[] { "a", "i", "t" })
        {
            RulerInk ink = letter switch
            {
                "a" => HandInkBuilder.Build(Hand.A().Reverse()),
                "i" => HandInkBuilder.Build(Hand.I(reversed: true), Hand.Dot(0.45, 1.45).After(400)),
                _ => HandInkBuilder.Build(Hand.T(reversed: true), Hand.Bar().After(400)),
            };
            EvaluationResult result = Run(ink, letter);

            Assert.Equal(["P2"], result.Summary.Findings.Where(f => f.Code is ErrorCode.P1 or ErrorCode.P2).Select(f => f.Code.ToString()));
            Assert.False(result.Summary.Letters[0].ProcessOk);
        }
    }

    // ---------------------------------------------------------------- F-02: подход как допустимый старт

    [Theory]
    [InlineData(-1.6)]
    [InlineData(-1.2)]
    [InlineData(-0.9)]
    [InlineData(-0.7)]
    [InlineData(-0.3)]
    [InlineData(0.0)]
    [InlineData(0.3)]
    public void An_approach_of_any_realistic_length_is_not_P1(double fromU)
    {
        EvaluationResult result = Run(HandInkBuilder.Build(Hand.AApproach(fromU)), "a");

        Assert.False(result.Summary.Has(ErrorCode.P1), $"approach from u={fromU}: {Codes(result)}");
        Assert.Equal(true, result.Summary.Letters[0].ProcessOk);
        Assert.Equal(Decision.Target, result.Summary.Decision);
    }

    [Fact]
    public void A_short_approach_from_the_middle_of_the_rise_is_not_P1_either()
    {
        var stroke = new HandStroke(
            HandCurves.Concat(
            [
                HandCurves.Quad(new HandVec(0.1, 0.35), new HandVec(0.5, 0.6), new HandVec(0.845, 0.82)),
                t => new HandVec(0.5 + (0.45 * Math.Cos((40 + (340 * t)) * Math.PI / 180)), 0.5 + (0.5 * Math.Sin((40 + (340 * t)) * Math.PI / 180))),
                HandCurves.Line(new HandVec(0.92, 0.67), new HandVec(1.0, 1.0)),
                HandCurves.Line(new HandVec(1.0, 1.0), new HandVec(0.95, 0.12)),
                HandCurves.Quad(new HandVec(0.95, 0.12), new HandVec(0.97, -0.05), new HandVec(1.45, 0.5)),
            ]),
            1.5,
            0,
            1).Transform(0, 1, 1.0, 0, 0);

        EvaluationResult result = Run(HandInkBuilder.Build(stroke), "a");

        Assert.False(result.Summary.Has(ErrorCode.P1), Codes(result));
        Assert.False(result.Summary.Has(ErrorCode.F2), "closure of the oval does not depend on the approach: " + Codes(result));
    }

    [Fact]
    public void Starting_at_the_body_without_an_approach_is_fine_but_starting_in_the_middle_of_the_letter_is_P1()
    {
        EvaluationResult oneOClock = Run(HandInkBuilder.Build(Hand.AWithoutApproach(40)), "a");
        EvaluationResult twelve = Run(HandInkBuilder.Build(Hand.AWithoutApproach(90)), "a");
        EvaluationResult fourOClock = Run(HandInkBuilder.Build(Hand.AWithoutApproach(300)), "a");

        Assert.False(oneOClock.Summary.Has(ErrorCode.P1), Codes(oneOClock));
        Assert.True(twelve.Summary.Has(ErrorCode.P1), Codes(twelve));
        Assert.True(fourOClock.Summary.Has(ErrorCode.P1), Codes(fourOClock));
        Assert.All(new[] { twelve, fourOClock }.SelectMany(r => r.Summary.Of(ErrorCode.P1)), f =>
        {
            Assert.Equal("s1", f.Where.StrokeId);
            Assert.True(f.Measured > f.Threshold);
        });
    }

    // ---------------------------------------------------------------- F-03: сдвиг по v меняет только F4 и F5

    [Theory]
    [InlineData("a", 0.4)]
    [InlineData("a", 0.8)]
    [InlineData("a", 1.0)]
    [InlineData("a", 1.2)]
    [InlineData("a", 1.5)]
    [InlineData("a", -0.6)]
    [InlineData("a", -1.0)]
    [InlineData("t", 0.4)]
    [InlineData("t", 0.8)]
    [InlineData("t", 1.0)]
    [InlineData("t", 1.2)]
    [InlineData("t", -0.5)]
    [InlineData("i", 0.8)]
    [InlineData("i", 1.2)]
    public void A_letter_above_or_below_the_line_changes_only_ruler_findings(string letter, double dv)
    {
        EvaluationResult result = Run(Hand.Lifted(letter, dv), letter);

        Assert.Equal(Decision.Target, result.Summary.Decision);
        Assert.All(result.Summary.Findings, f => Assert.True(f.Code is ErrorCode.F4 or ErrorCode.F5, $"{letter} dv={dv}: unexpected {Codes(result)}"));
        Assert.True(result.Summary.Has(ErrorCode.F4) || result.Summary.Has(ErrorCode.F5), $"{letter} dv={dv}: the lift must be reported");
        Assert.Equal(true, result.Summary.Letters[0].ProcessOk);
    }

    [Fact]
    public void A_small_lift_is_below_the_ruler_thresholds_and_a_big_one_is_not()
    {
        EvaluationResult small = Run(Hand.Lifted("a", 0.1), "a");
        EvaluationResult big = Run(Hand.Lifted("a", 0.4), "a");

        Assert.Empty(small.Summary.Findings);
        Assert.True(big.Summary.Has(ErrorCode.F4) && big.Summary.Has(ErrorCode.F5), Codes(big));
    }

    [Fact]
    public void The_baseline_error_F5_is_found_for_i_through_its_baseline_touch()
    {
        EvaluationResult result = Run(Hand.Lifted("i", 0.35), "i");

        Finding f5 = Assert.Single(result.Summary.Of(ErrorCode.F5));
        Assert.True(f5.Measured > 0.3);
    }

    // ---------------------------------------------------------------- F-06: «o» вместо a

    [Fact]
    public void An_o_without_the_stem_is_not_a_clean_a()
    {
        EvaluationResult result = Run(HandInkBuilder.Build(Hand.OInsteadOfA()), "a");

        Finding p6 = Assert.Single(result.Summary.Of(ErrorCode.P6));
        Assert.Equal(0.0, p6.Measured);
        Assert.False(result.Summary.Process().RetraceOk);
    }

    // ---------------------------------------------------------------- F-05: допуски отложенных штрихов

    [Theory]
    [InlineData(0.45, true)]
    [InlineData(0.6, true)]
    [InlineData(0.75, false)]
    [InlineData(1.0, false)]
    [InlineData(1.4, true)]
    [InlineData(1.6, true)]
    public void The_t_bar_is_judged_by_its_height_above_the_stem(double barV, bool misplaced)
    {
        EvaluationResult result = Run(HandInkBuilder.Build(Hand.T(), Hand.Bar(barV).After(400)), "t");

        Assert.Equal(misplaced, result.Summary.Has(ErrorCode.D2));
        if (misplaced)
        {
            Assert.Equal(DeferredStatus.Misplaced, Assert.Single(result.Summary.Deferred).Status);
        }

        Assert.False(result.Summary.Has(ErrorCode.D1) || result.Summary.Has(ErrorCode.D3));
    }

    [Fact]
    public void A_bar_beside_the_stem_that_does_not_cross_it_is_D2_even_at_the_right_height()
    {
        EvaluationResult result = Run(HandInkBuilder.Build(Hand.T(), Hand.Bar(1.0, -1.1, -0.55).After(400)), "t");

        Finding d2 = Assert.Single(result.Summary.Of(ErrorCode.D2));
        Assert.True(d2.Measured > d2.Threshold);
    }

    [Theory]
    [InlineData(1.05, true)]
    [InlineData(1.15, false)]
    [InlineData(1.8, true)]
    [InlineData(2.3, true)]
    public void The_dot_is_judged_by_its_height_above_the_stem(double dotV, bool misplaced)
    {
        EvaluationResult result = Run(HandInkBuilder.Build(Hand.I(), Hand.Dot(0.45, dotV).After(400)), "i");

        Assert.True(misplaced == result.Summary.Has(ErrorCode.D2), Codes(result));
    }

    [Fact]
    public void A_bar_written_right_to_left_is_information_only_and_keeps_process_ok()
    {
        EvaluationResult result = Run(HandInkBuilder.Build(Hand.T(), Hand.Bar(rtl: true).After(400)), "t");

        Finding p2 = Assert.Single(result.Summary.Of(ErrorCode.P2));
        Assert.Equal(FindingKind.Info, p2.Kind);
        Assert.Equal(0.0, p2.Severity);
        Assert.Equal("bar", p2.Where.StrokeId);
        Assert.Equal(true, result.Summary.Letters[0].ProcessOk);
        Assert.Equal(true, result.Summary.Process().DirectionOk);
    }

    [Fact]
    public void The_config_flag_turns_the_reversed_bar_into_a_process_error()
    {
        JsonNode node = JsonNode.Parse(Shipped.ConfigText)!;
        node["process"]!["direction"]!["barReverseIsError"] = true;
        var strict = new LetterEvaluator(ScoringConfigLoader.Parse(System.Text.Encoding.UTF8.GetBytes(node.ToJsonString())));

        EvaluationResult result = Run(HandInkBuilder.Build(Hand.T(), Hand.Bar(rtl: true).After(400)), "t", strict);

        Finding p2 = Assert.Single(result.Summary.Of(ErrorCode.P2));
        Assert.Equal(FindingKind.Error, p2.Kind);
        Assert.False(result.Summary.Letters[0].ProcessOk);
    }

    [Fact]
    public void A_deferred_stroke_before_the_stem_is_still_not_penalized()
    {
        EvaluationResult dotFirst = Run(HandInkBuilder.Build(Hand.Dot(0.45, 1.45), Hand.I().After(300)), "i");
        EvaluationResult barFirst = Run(HandInkBuilder.Build(Hand.Bar(), Hand.T().After(300)), "t");

        Assert.Equal("D4(i)", Codes(dotFirst));
        Assert.Equal("D4(i)", Codes(barFirst));
        Assert.Equal(true, dotFirst.Summary.Letters[0].ProcessOk);
    }

    // ---------------------------------------------------------------- F-04: нет основного штриха

    [Fact]
    public void An_i_made_of_one_dot_has_no_correct_process()
    {
        EvaluationResult result = Run(HandInkBuilder.Build(Hand.Dot(0.45, 1.45)), "i");

        Assert.True(result.Summary.Has(ErrorCode.P5));
        Assert.NotEqual(true, result.Summary.Letters[0].ProcessOk);
        Assert.Empty(result.Detail.Strokes);
    }

    // ---------------------------------------------------------------- F-07: медленное дрожащее письмо

    [Theory]
    [InlineData("a", 0.3, 0.03)]
    [InlineData("i", 0.3, 0.03)]
    [InlineData("t", 0.3, 0.03)]
    [InlineData("a", 0.6, 0.02)]
    [InlineData("a", 0.35, 0.03)]
    public void Slow_trembling_handwriting_is_recognized_without_false_findings(string letter, double speed, double tremor)
    {
        EvaluationResult result = Run(Hand.SlowTrembling(letter, speed, tremor), letter);

        Assert.Equal(Decision.Target, result.Summary.Decision);
        Assert.DoesNotContain(result.Summary.Findings, f => Stops.Contains(f.Code));
        Assert.DoesNotContain(result.Summary.Findings, f => f.Code == ErrorCode.F1);
        Assert.Equal(true, result.Summary.Letters[0].ProcessOk);
        Assert.True(result.Summary.Form >= 0.85, $"form {result.Summary.Form:F2}");
    }

    // ---------------------------------------------------------------- U0

    [Fact]
    public void A_russian_t_is_not_recognized_as_t()
    {
        EvaluationResult result = Run(HandInkBuilder.Build(Hand.RussianT()), "t");

        Assert.Equal(Decision.Unrecognized, result.Summary.Decision);
        Assert.Empty(result.Summary.Findings);
        Assert.Equal(ProcessSummary.NotEvaluated, result.Summary.Letters[0].Process);
        Assert.Null(result.Summary.Letters[0].Process.StartOk);
        Assert.Null(result.Summary.Letters[0].Process.StrokeCountDelta);
    }

    // ---------------------------------------------------------------- F-10, F-11: замкнутость и порядок находок

    [Fact]
    public void The_oval_closure_is_measured_geometrically()
    {
        EvaluationResult open = Run(HandInkBuilder.Build(Hand.AOpenOval()), "a");
        EvaluationResult overlapped = Run(HandInkBuilder.Build(Hand.A(endDeg: 460)), "a");
        EvaluationResult plain = Run(HandInkBuilder.Build(Hand.A()), "a");

        Assert.True(open.Summary.Has(ErrorCode.F2), Codes(open));
        Assert.False(overlapped.Summary.Has(ErrorCode.F2), Codes(overlapped));
        Assert.False(plain.Summary.Has(ErrorCode.F2), Codes(plain));
    }

    [Fact]
    public void Findings_have_a_total_order_and_repeated_evaluation_is_identical()
    {
        EvaluationResult first = Run(HandInkBuilder.Build(Hand.AClockwise(), Hand.Dot(1.2, 0.5).After(300)), "a");
        EvaluationResult second = Shipped.NewEvaluator().Evaluate(new LetterEvaluationRequest(HandInkBuilder.Build(Hand.AClockwise(), Hand.Dot(1.2, 0.5).After(300)), Shipped.Glyph("a"), Shipped.Style));

        Assert.True(first.Summary.Findings.Length >= 2, Codes(first));
        Assert.Equal(ResultText.Describe(first), ResultText.Describe(second));
    }
}
