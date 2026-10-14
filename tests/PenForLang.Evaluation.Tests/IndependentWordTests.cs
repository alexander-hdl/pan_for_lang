using PenForLang.Evaluation.Tests.HandFixtures;
using PenForLang.Evaluation.Tests.Support;
using static PenForLang.Evaluation.Tests.HandFixtures.ProbeWords;

namespace PenForLang.Evaluation.Tests;

/// <summary>
/// Оценка слов на независимых фикстурах (<see cref="ProbeWords"/>: свои модели букв, соединений и «неверных слов», не глифы
/// пакета и не SynthInk; портированы из пробы ревью V0.10). Защита от «теста на собственной модели» (A08): верные почерки без U0 и
/// ложных J*/P*/D*, неверные слова — U0, отрывы — J1 только на соединении, точка и перекладина «сразу» — D4 без J1.
/// </summary>
public class IndependentWordTests
{
    private const double BoundaryTolerance = 0.25;

    private static readonly double[] Belows = [0.2, 0.35, 0.5, 0.7];

    private static readonly string[] WrongByCost = ["W-ot", "W-ru-at", "W-a-only", "W-t-only", "W-ta", "W-it-for-at", "W-scribble"];

    private static readonly string[] LiftedIds =
    [
        "L-aend-0", "L-join20-0", "L-mid-0", "L-ttop-0", "L-attop-0", "L-skip-0",
        "L-aend-s20", "L-join20-s20", "L-mid-s20", "L-ttop-s20", "L-attop-s20", "L-skip-s20",
    ];

    public static TheoryData<string> CorrectIds { get; } = [.. Correct().Concat(GapAndHeight()).Concat(Ends()).Concat(Speeds()).Where(c => c.Id != "S-it-slow-dot").Select(c => c.Id)];

    private static readonly Dictionary<string, PCase> Cases = BuildCases();

    private static Dictionary<string, PCase> BuildCases()
    {
        IEnumerable<PCase> all = Correct().Concat(GapAndHeight()).Concat(Ends()).Concat(Speeds()).Concat(Lifts()).Concat(InsideLifts()).Concat(Dots()).Concat(Bars()).Concat(Wrong())
            .Append(LoopJoin()).Append(TopLoop()).Append(ALoop()).Append(ParallelStem()).Append(ShortT())
            .Concat(Belows.Select(TStemLift));
        return all.ToDictionary(c => c.Id, StringComparer.Ordinal);
    }

    private static EvaluationResult Evaluate(string id) => Tester.EvaluateWord(Cases[id].Ink, Cases[id].Target);

    private static EvaluationSummary Summary(string id) => Evaluate(id).Summary;

    private static ErrorCode[] Stops => [ErrorCode.P1, ErrorCode.P2, ErrorCode.P3, ErrorCode.P4, ErrorCode.P5, ErrorCode.P6, ErrorCode.J1, ErrorCode.J2, ErrorCode.J3, ErrorCode.J4, ErrorCode.D1, ErrorCode.D2, ErrorCode.D3, ErrorCode.D4];

    private static double Distance(InkPoint p, PVec truth) => Math.Sqrt(Math.Pow(p.U - truth.X, 2) + Math.Pow(p.V - truth.Y, 2));

    // ---------------------------------- верные почерки ----------------------------------

    [Theory]
    [MemberData(nameof(CorrectIds))]
    public void Correct_handwriting_is_a_target_without_process_join_or_deferred_findings(string id)
    {
        EvaluationResult result = Evaluate(id);
        EvaluationSummary s = result.Summary;

        Assert.Equal(Decision.Target, s.Decision);
        Assert.DoesNotContain(s.Findings, f => Stops.Contains(f.Code));
        Assert.All(s.Joins, j => Assert.True(j.Present && !j.LiftExpected));
        Assert.All(s.Letters, l =>
        {
            Assert.True(l.ProcessOk, $"{id}: letter {l.Index}");
            Assert.True(l.SegmentationConfident);
        });
        Assert.All(s.Deferred, d => Assert.Equal(DeferredTiming.AfterWord, d.Timing));
    }

    [Fact]
    public void Correct_handwriting_has_boundaries_within_the_tolerance()
    {
        // W10-18: допуск границ 0,2–0,25 (у ручной a — свой крюк-выход, не глиф пакета).
        foreach (PCase c in Correct().Concat(GapAndHeight()).Concat(Speeds()).Where(c => c.Id != "S-it-slow-dot"))
        {
            if (c.Truth is not { } truth)
            {
                continue;
            }

            WordBoundary b = Tester.EvaluateWord(c.Ink, c.Target).Detail.Word!.Boundaries[0];
            Assert.True(Distance(b.BodyEnd, truth.Exit) <= BoundaryTolerance, $"{c.Id}: end {Distance(b.BodyEnd, truth.Exit):F2}");
            Assert.True(Distance(b.BodyStart, truth.Entry) <= BoundaryTolerance, $"{c.Id}: start {Distance(b.BodyStart, truth.Entry):F2}");
        }
    }

    [Fact]
    public void A_very_slow_trembling_hand_does_not_get_a_false_p1_on_the_first_letter()
    {
        // W10-09: дрожь у медленного письма отклоняла первые 0,2 x-h начала вниз; подход не засчитывался и ставился P1.
        EvaluationResult result = Evaluate("S-vslow");

        Assert.DoesNotContain(result.Summary.Findings, f => f.Code == ErrorCode.P1);
        Assert.True(result.Summary.Letters[0].ProcessOk);
        Assert.True(result.Detail.Word!.LetterResults[0].Summary.Letters[0].ProcessOk);
    }

    // ---------------------------------- W10-03: интервал и высота t — F8 и F4, не J3 ----------------------------------

    [Theory]
    [InlineData("H-gap0.45")]
    [InlineData("H-gap0.60")]
    [InlineData("H-gap0.70")]
    [InlineData("H-gap0.80")]
    public void A_wider_gap_between_the_letters_is_not_j3_and_does_not_fail_the_process(string id)
    {
        EvaluationResult result = Evaluate(id);

        Assert.DoesNotContain(result.Summary.Findings, f => f.Code is ErrorCode.J3 or ErrorCode.P6 or ErrorCode.J1);
        Assert.True(result.Summary.Letters[1].ProcessOk);
        Assert.True(result.Detail.Word!.Joins[0].EntryErr <= Tester.Words.Config.Joins.EntryErr);
        Assert.Equal(Decision.Target, result.Summary.Decision);
    }

    [Theory]
    [InlineData("H-t1.35")]
    [InlineData("H-t1.25")]
    [InlineData("H-t1.15")]
    [InlineData("H-t1.05")]
    public void A_lower_t_is_f4_not_j3_and_not_p6(string id)
    {
        EvaluationResult result = Evaluate(id);

        Assert.DoesNotContain(result.Summary.Findings, f => f.Code is ErrorCode.J3 or ErrorCode.P6 or ErrorCode.J1);
        Assert.True(result.Summary.Letters[1].ProcessOk);
        Assert.Equal(Decision.Target, result.Summary.Decision);
    }

    [Fact]
    public void The_lowest_t_still_gets_its_f4_finding()
    {
        Assert.Contains(Summary("H-t1.05").Findings, f => f.Code == ErrorCode.F4 && f.Where.LetterIndex == 1);
    }

    [Fact]
    public void A_correct_hand_with_a_high_join_and_a_backward_slant_is_not_j3()
    {
        // Ошибка оценки наклона (−10°) смещает верх t относительно тела; вход меряется относительно буквы ребёнка (с её наклоном).
        EvaluationSummary s = Summary("A-high-s-10");

        Assert.DoesNotContain(s.Findings, f => f.Code is ErrorCode.J3);
        Assert.True(s.Letters[1].ProcessOk);
    }

    [Fact]
    public void The_entry_threshold_is_one_formula_for_the_finding_the_score_and_the_process_of_the_next_letter()
    {
        // W10-13: порог входа (с профилем допусков) один и тот же в находке J3, оценке соединения и process_ok.
        string json = Shipped.ConfigText.Replace("\"entryErr\": 0.4", "\"entryErr\": 0.01", StringComparison.Ordinal);
        var strict = new WordEvaluator(PenForLang.Evaluation.Config.ScoringConfigLoader.Parse(System.Text.Encoding.UTF8.GetBytes(json)));
        PCase c = Cases["A-low-0"];

        EvaluationSummary s = strict.Evaluate(new WordEvaluationRequest(c.Ink, Tester.Word("at"))).Summary;

        Assert.Contains(s.Findings, f => f.Code == ErrorCode.J3);
        Assert.Equal(0.5, s.Joins[0].Score);
        Assert.False(s.Letters[1].ProcessOk);
        Assert.True(Summary("A-low-0").Joins[0].Score == 1.0);
    }

    // ---------------------------------- неверные слова: U0 ----------------------------------

    [Theory]
    [InlineData("W-t-only")]
    [InlineData("W-a-only")]
    [InlineData("W-ot")]
    [InlineData("W-ta")]
    [InlineData("W-it-for-at")]
    [InlineData("W-att")]
    [InlineData("W-ru-at")]
    [InlineData("W-scribble")]
    [InlineData("W-at-for-it")]
    public void A_wrong_word_is_u0_without_findings(string id)
    {
        EvaluationResult result = Evaluate(id);

        Assert.Equal(Decision.Unrecognized, result.Summary.Decision);
        Assert.Equal(Confidence.Low, result.Summary.Confidence);
        Assert.Empty(result.Summary.Findings);
        Assert.All(result.Summary.Letters, l => Assert.Null(l.ProcessOk));
    }

    [Fact]
    public void Al_instead_of_at_is_a_target_with_findings_not_a_clean_word()
    {
        EvaluationSummary s = Summary("W-al");

        Assert.Equal(Decision.Target, s.Decision);
        Assert.NotEmpty(s.Findings);
        Assert.Contains(s.Findings, f => f.Where.LetterIndex == 1);
        Assert.False(s.Letters[1].ProcessOk);
    }

    [Fact]
    public void U0_has_a_margin_between_the_correct_and_the_wrong_words()
    {
        // Порог word.unrecognized.costPerPoint между самым дорогим верным почерком и самым дешёвым неверным словом, которое режет эта метрика.
        double threshold = Shipped.Config.Word.Unrecognized.CostPerPoint;
        double worstCorrect = Correct().Concat(GapAndHeight()).Concat(Speeds()).Concat(ProbeWords2.Correct()).Concat(ProbeWords2.Gaps())
            .Where(c => c.Id != "S-it-slow-dot")
            .Max(c => Tester.EvaluateWord(c.Ink, c.Target).Detail.Diagnostics["align.bodyCostPerPoint"]);
        double[] cut = [.. WrongByCost.Select(id => Evaluate(id).Detail.Diagnostics["align.bodyCostPerPoint"])];

        Assert.True(worstCorrect < threshold * 0.65, $"the costliest correct handwriting ({worstCorrect:F3}) is within 65% of the threshold {threshold}");
        Assert.True(cut.Min() > threshold, $"the cheapest wrong word cut by cost ({cut.Min():F3}) is above the threshold {threshold}");
    }

    // ---------------------------------- отрывы ----------------------------------

    [Theory]
    [InlineData("L-aend-0")]
    [InlineData("L-join20-0")]
    [InlineData("L-mid-0")]
    [InlineData("L-ttop-0")]
    [InlineData("L-attop-0")]
    [InlineData("L-skip-0")]
    [InlineData("L-aend-s20")]
    [InlineData("L-join20-s20")]
    [InlineData("L-mid-s20")]
    [InlineData("L-ttop-s20")]
    [InlineData("L-attop-s20")]
    [InlineData("L-skip-s20")]
    [InlineData("L-offset")]
    [InlineData("L-two-join")]
    [InlineData("L2-tstem-0.20")]
    public void A_pen_lift_on_the_join_is_one_j1_and_not_inside_a_letter(string id)
    {
        EvaluationResult result = Evaluate(id);

        Finding j1 = Assert.Single(result.Summary.Of(ErrorCode.J1));
        Assert.Equal(0, j1.Where.LetterIndex);
        Assert.Equal(0, result.Detail.Word!.LiftsInsideLetter);
        Assert.DoesNotContain(result.Summary.Findings, f => f.Code is ErrorCode.D1 or ErrorCode.D2 or ErrorCode.D3 or ErrorCode.D4);
        Assert.False(result.Summary.Joins[0].Present);
    }

    [Theory]
    [InlineData("L-inA")]
    [InlineData("L-inAstem")]
    [InlineData("L-inT")]
    [InlineData("L2-tstem-0.35")]
    [InlineData("L2-tstem-0.50")]
    [InlineData("L2-tstem-0.70")]
    public void A_pen_lift_inside_a_letter_is_counted_there_and_is_neither_j1_nor_p4(string id)
    {
        // W10-06: отрыв в стволе t на 0,35–0,7 ниже верха — внутри буквы, а не на соединении.
        EvaluationResult result = Evaluate(id);

        Assert.False(result.Summary.Has(ErrorCode.J1), id);
        Assert.Equal(1, result.Detail.Word!.LiftsInsideLetter);
        Assert.DoesNotContain(result.Summary.Findings, f => f.Code is ErrorCode.P3 or ErrorCode.P4 or ErrorCode.P5 or ErrorCode.D2 or ErrorCode.D3);
        Assert.True(result.Summary.Joins[0].Present);
    }

    [Theory]
    [InlineData("L-two-a-join")]
    [InlineData("L-join-inT")]
    public void A_lift_on_the_join_and_another_inside_a_letter_are_j1_plus_inside_and_no_false_d2(string id)
    {
        EvaluationResult result = Evaluate(id);

        Assert.Single(result.Summary.Of(ErrorCode.J1));
        Assert.Equal(1, result.Detail.Word!.LiftsInsideLetter);
        Assert.DoesNotContain(result.Summary.Findings, f => f.Code is ErrorCode.D1 or ErrorCode.D2 or ErrorCode.D3 or ErrorCode.D4);
    }

    [Fact]
    public void The_boundaries_of_lifted_words_stay_within_the_tolerance()
    {
        foreach (string id in LiftedIds)
        {
            PTruth truth = Cases[id].Truth!.Value;
            WordBoundary b = Evaluate(id).Detail.Word!.Boundaries[0];
            Assert.True(Distance(b.BodyEnd, truth.Exit) <= BoundaryTolerance, $"{id}: end {Distance(b.BodyEnd, truth.Exit):F2}");
            Assert.True(Distance(b.BodyStart, truth.Entry) <= BoundaryTolerance, $"{id}: start {Distance(b.BodyStart, truth.Entry):F2}");
            Assert.True(b.Lifted);
        }
    }

    // ---------------------------------- ретрейс ----------------------------------

    [Theory]
    [InlineData("R-toploop")]
    [InlineData("R-parallel")]
    public void A_stem_that_does_not_retrace_the_join_is_p6_and_fails_the_letter_process_consistently(string id)
    {
        EvaluationSummary s = Summary(id);

        Finding p6 = Assert.Single(s.Of(ErrorCode.P6));
        Assert.Equal(1, p6.Where.LetterIndex);
        Assert.False(s.Letters[1].ProcessOk);
        Assert.True(s.Letters[0].ProcessOk);
    }

    [Theory]
    [InlineData("A-shortT")]
    [InlineData("H-t1.15")]
    [InlineData("H-t1.05")]
    public void A_retrace_with_too_few_points_is_not_measured_rather_than_zero(string id)
    {
        // W10-04: низкая t отображает ретрейс в слишком малое число точек: не измерено (нет P6), а не «ретрейса нет».
        EvaluationSummary s = Summary(id);

        Assert.DoesNotContain(s.Findings, f => f.Code == ErrorCode.P6);
        Assert.True(s.Letters[1].ProcessOk);
    }

    // ---------------------------------- точка и перекладина сразу ----------------------------------

    [Theory]
    [InlineData("D-dot-end-0")]
    [InlineData("D-dot-near-0")]
    [InlineData("D-dot-gap-0")]
    [InlineData("D-dot-skip-0")]
    [InlineData("D-dot-end-s20")]
    [InlineData("D-dot-near-s20")]
    [InlineData("D-dot-gap-s20")]
    [InlineData("D-dot-skip-s20")]
    [InlineData("D-dot-end-small")]
    [InlineData("D-dot-near-small")]
    [InlineData("D-dot-gap-small")]
    [InlineData("D-dot-skip-small")]
    [InlineData("D-dot-beforehook")]
    [InlineData("S-it-slow-dot")]
    public void A_dot_put_immediately_is_d4_info_and_no_j1_and_the_lift_is_expected(string id)
    {
        EvaluationResult result = id == "S-it-slow-dot" ? Tester.EvaluateWord(Speeds().First(c => c.Id == id).Ink, "it") : Evaluate(id);
        EvaluationSummary s = result.Summary;

        Finding d4 = Assert.Single(s.Of(ErrorCode.D4));
        Assert.Equal(FindingKind.Info, d4.Kind);
        Assert.Equal("dot", d4.Where.StrokeId);
        Assert.False(s.Has(ErrorCode.J1));
        Assert.DoesNotContain(s.Findings, f => f.Code is ErrorCode.D1 or ErrorCode.D2 or ErrorCode.D3 or ErrorCode.P6);
        Assert.All(result.Detail.Word!.Lifts, l => Assert.True(l.Expected));
        Assert.Equal(0, result.Detail.Word.LiftsInsideLetter);
    }

    [Fact]
    public void A_dot_put_immediately_plus_another_lift_on_the_join_is_d4_and_j1()
    {
        EvaluationSummary s = Summary("D-dot-plus-lift");

        Assert.True(s.Has(ErrorCode.D4));
        Assert.Single(s.Of(ErrorCode.J1));
    }

    [Fact]
    public void A_lift_without_a_dot_between_is_j1_and_a_dot_after_the_word_is_not_an_exemption()
    {
        EvaluationSummary noDot = Summary("D-nodot-lift");
        EvaluationSummary afterWord = Summary("D-dot-afterword-lift");

        Assert.Single(noDot.Of(ErrorCode.J1));
        Assert.Contains(noDot.Findings, f => f.Code == ErrorCode.D1);
        Assert.Single(afterWord.Of(ErrorCode.J1));
        Assert.False(afterWord.Has(ErrorCode.D4));
    }

    [Fact]
    public void A_bar_put_before_the_tail_of_t_in_it_is_d4_and_the_tail_is_not_a_stray_mark()
    {
        // W10-15: хвост t после перекладины «сразу» продолжает основной след (смежен с концом ствола), а не лишняя черта D3.
        EvaluationSummary s = Summary("D-bar-immediate-it");

        Finding d4 = Assert.Single(s.Of(ErrorCode.D4));
        Assert.Equal("bar", d4.Where.StrokeId);
        Assert.Equal(1, d4.Where.LetterIndex);
        Assert.DoesNotContain(s.Findings, f => f.Code is ErrorCode.D3 or ErrorCode.J1 or ErrorCode.D1 or ErrorCode.D2);
    }

    [Fact]
    public void At_a_bar_before_the_tail_of_t_is_d4_info_and_no_j1()
    {
        // Отступление 1 (V0.10): перекладина до хвоста t — «сразу», D4 Info, J1 нет (A03).
        EvaluationResult result = Evaluate("C-bar-beforetail");

        Finding d4 = Assert.Single(result.Summary.Of(ErrorCode.D4));
        Assert.Equal(FindingKind.Info, d4.Kind);
        Assert.Equal("bar", d4.Where.StrokeId);
        Assert.False(result.Summary.Has(ErrorCode.J1));
        Assert.DoesNotContain(result.Summary.Findings, f => f.Code is ErrorCode.D3 or ErrorCode.D1 or ErrorCode.D2);
        Assert.All(result.Detail.Word!.Lifts, l => Assert.True(l.Expected));
        Assert.Equal(0, result.Detail.Word.LiftsInsideLetter);
    }

    [Fact]
    public void At_a_bar_on_the_top_of_t_before_the_stem_is_d4_and_no_j1()
    {
        EvaluationSummary s = Summary("C-bar-attop");

        Assert.Single(s.Of(ErrorCode.D4));
        Assert.False(s.Has(ErrorCode.J1));
        Assert.True(s.Joins[0].LiftExpected);
    }

    [Fact]
    public void A_bar_after_the_tail_of_t_is_after_the_word_and_a_reversed_or_missing_bar_is_reported()
    {
        EvaluationSummary end = Summary("C-bar-end");
        EvaluationSummary rtl = Summary("C-bar-rtl");
        EvaluationSummary none = Summary("C-nobar");

        Assert.DoesNotContain(end.Findings, f => f.Code is ErrorCode.D4 or ErrorCode.J1);
        Finding p2 = Assert.Single(rtl.Of(ErrorCode.P2));
        Assert.Equal(FindingKind.Info, p2.Kind);
        Assert.Single(none.Of(ErrorCode.D1));
    }

    // ---------------------------------- J4 ----------------------------------

    [Fact]
    public void A_small_loop_in_the_join_is_j4()
    {
        EvaluationSummary s = Summary("A-loopjoin");

        Assert.Single(s.Of(ErrorCode.J4));
        Assert.False(s.Has(ErrorCode.J1));
    }
}
