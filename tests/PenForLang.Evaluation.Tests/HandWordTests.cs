using PenForLang.Evaluation.Tests.HandFixtures;
using PenForLang.Evaluation.Tests.Support;
using PenForLang.Ink;

namespace PenForLang.Evaluation.Tests;

/// <summary>
/// Слова на ручных фикстурах (V0.10): траектории — параметрические кривые (<see cref="HandWords"/>), а не глифы и не
/// SynthInk, соединение — своя форма. Защита от «теста на собственной модели»: слитно с другой формой соединения, отрыв в
/// разных местах соединения, точка сразу (<c>it</c>), ретрейс не по соединению.
/// </summary>
public class HandWordTests
{
    private const double HandBoundaryTolerance = 0.3;

    private static readonly HandWords.HandStyle Plain = new(Slant: 0);
    private static readonly HandWords.HandStyle Tilted = new(Slant: 12, Scale: 1.15, Dv: 0.04, Rotation: 2);
    private static readonly HandWords.HandStyle Small = new(Slant: -8, Scale: 0.85, Dv: -0.05);

    public static TheoryData<int> Hands => [0, 1, 2];

    private static HandWords.HandStyle HandOf(int index) => index switch { 0 => Plain, 1 => Tilted, _ => Small };

    private static ErrorCode[] Unwanted => [.. Tester.ProcessAndDeferred, ErrorCode.J1, ErrorCode.J2, ErrorCode.J3, ErrorCode.J4, ErrorCode.F1, ErrorCode.F2, ErrorCode.F3];

    private static double Err(InkPoint p, HandVec truth) => Math.Sqrt(Math.Pow(p.U - truth.X, 2) + Math.Pow(p.V - truth.Y, 2));

    /// <summary>
    /// Границы тел против истинных. У ручной буквы своя форма и размер, а выход тела на гладком продолжении соединением
    /// неоднозначен (особенно при отрыве в глубине соединения), поэтому допуск конца тела шире, чем у начала (вход — излом).
    /// </summary>
    private static void AssertBoundaries(EvaluationResult result, HandWords.Cuts cuts, HandWords.HandStyle hand, double endTolerance = HandBoundaryTolerance)
    {
        (HandVec exit, HandVec entry) = HandWords.Truth(cuts, hand);
        WordBoundary b = result.Detail.Word!.Boundaries[0];
        Assert.True(Err(b.BodyEnd, exit) <= endTolerance, $"BodyEnd error {Err(b.BodyEnd, exit):F2}");
        Assert.True(Err(b.BodyStart, entry) <= HandBoundaryTolerance, $"BodyStart error {Err(b.BodyStart, entry):F2}");
    }

    // ---------------------------------- слитно, другая форма соединения ----------------------------------

    [Theory]
    [MemberData(nameof(Hands))]
    public void At_connected_with_other_join_shapes_has_no_findings_about_process_joins_or_deferred(int hand)
    {
        HandWords.HandStyle h = HandOf(hand);
        foreach (double reach in new[] { 0.3, 0.6, 0.9 })
        {
            EvaluationResult result = Tester.EvaluateWord(HandWords.AtConnected(h, reach), "at");

            Assert.Equal(Decision.Target, result.Summary.Decision);
            Assert.DoesNotContain(result.Summary.Findings, f => Unwanted.Contains(f.Code));
            Assert.All(result.Summary.Joins, j => Assert.True(j.Present && !j.LiftExpected));
            Assert.All(result.Summary.Letters, l => Assert.True(l.Form >= 0.85, $"reach {reach}: letter {l.Index} form {l.Form:F2}"));
            AssertBoundaries(result, HandWords.At(reach).Cuts, h);
        }
    }

    [Theory]
    [MemberData(nameof(Hands))]
    public void It_connected_has_no_findings_about_process_joins_or_deferred(int hand)
    {
        HandWords.HandStyle h = HandOf(hand);
        foreach (double reach in new[] { 0.3, 0.6, 0.9 })
        {
            EvaluationResult result = Tester.EvaluateWord(HandWords.ItConnected(h, reach), "it");

            Assert.Equal(Decision.Target, result.Summary.Decision);
            Assert.DoesNotContain(result.Summary.Findings, f => Unwanted.Contains(f.Code));
            Assert.DoesNotContain(result.Summary.Findings, f => f.Code == ErrorCode.D4);
            Assert.All(result.Summary.Deferred, d => Assert.Equal(DeferredTiming.AfterWord, d.Timing));
            AssertBoundaries(result, HandWords.It(reach).Cuts, h);
        }
    }

    // ---------------------------------- отрыв в разных местах соединения ----------------------------------

    [Theory]
    [MemberData(nameof(Hands))]
    public void At_lifted_at_different_places_of_the_join_is_j1_every_time(int hand)
    {
        HandWords.HandStyle h = HandOf(hand);
        HandWords.Cuts cuts = HandWords.At().Cuts;
        foreach (double share in new[] { 0.15, 0.35, 0.55, 0.75, 0.9 })
        {
            double at = cuts.BodyEnd + (share * (cuts.JoinEnd - cuts.BodyEnd));
            EvaluationResult result = Tester.EvaluateWord(HandWords.AtLiftedAt(h, at), "at");

            Assert.Equal(Decision.Target, result.Summary.Decision);
            Finding j1 = Assert.Single(result.Summary.Of(ErrorCode.J1));
            Assert.Equal(0, j1.Where.LetterIndex);
            Assert.Equal(FindingPlace.Join, j1.Where.Place);
            Assert.DoesNotContain(result.Summary.Findings, f => f.Code is ErrorCode.D1 or ErrorCode.D2 or ErrorCode.D3 or ErrorCode.D4);
            Assert.False(result.Summary.Joins[0].Present);
            Assert.Equal(0, result.Detail.Word!.LiftsInsideLetter);
            AssertBoundaries(result, cuts, h, endTolerance: 0.4);
        }
    }

    [Theory]
    [MemberData(nameof(Hands))]
    public void The_skipped_join_is_j1_in_both_words(int hand)
    {
        HandWords.HandStyle h = HandOf(hand);

        EvaluationSummary at = Tester.EvaluateWord(HandWords.AtSkippedJoin(h), "at").Summary;
        EvaluationSummary it = Tester.EvaluateWord(HandWords.ItSkippedJoin(h), "it").Summary;

        Assert.Single(at.Of(ErrorCode.J1));
        Assert.Single(it.Of(ErrorCode.J1));
        Assert.DoesNotContain(at.Findings, f => f.Code is ErrorCode.D1 or ErrorCode.D2 or ErrorCode.D3 or ErrorCode.D4);
        Assert.DoesNotContain(it.Findings, f => f.Code is ErrorCode.D1 or ErrorCode.D2 or ErrorCode.D3 or ErrorCode.D4);
    }

    // ---------------------------------- it с точкой сразу ----------------------------------

    [Theory]
    [MemberData(nameof(Hands))]
    public void It_with_the_dot_put_immediately_is_d4_info_and_no_j1(int hand)
    {
        HandWords.HandStyle h = HandOf(hand);

        EvaluationResult result = Tester.EvaluateWord(HandWords.ItDotImmediately(h), "it");
        EvaluationSummary s = result.Summary;

        Assert.Equal(Decision.Target, s.Decision);
        Finding d4 = Assert.Single(s.Of(ErrorCode.D4));
        Assert.Equal(FindingKind.Info, d4.Kind);
        Assert.Equal("dot", d4.Where.StrokeId);
        Assert.False(s.Has(ErrorCode.J1));
        Assert.True(s.Joins[0].Present);
        Assert.True(s.Joins[0].LiftExpected);
        Assert.Equal(DeferredTiming.Immediate, s.Deferred.Single(d => d.StrokeId == "dot").Timing);
        Assert.Equal(DeferredTiming.AfterWord, s.Deferred.Single(d => d.StrokeId == "bar").Timing);
        Assert.DoesNotContain(s.Findings, f => f.Code is ErrorCode.D1 or ErrorCode.D2 or ErrorCode.D3 or ErrorCode.P6);
        Assert.Contains(result.Detail.Word!.Lifts, l => l.Join == 0 && l.Expected);
    }

    [Fact]
    public void The_dot_after_the_word_with_a_lift_on_the_join_is_j1_but_not_d4()
    {
        EvaluationSummary s = Tester.EvaluateWord(HandWords.ItSkippedJoin(Plain), "it").Summary;

        Assert.True(s.Has(ErrorCode.J1));
        Assert.False(s.Has(ErrorCode.D4));
        Assert.False(s.Joins[0].LiftExpected);
    }

    // ---------------------------------- ретрейс по соединению ----------------------------------

    [Fact]
    public void A_stem_that_does_not_retrace_the_join_is_p6_on_the_inner_letter()
    {
        EvaluationResult off = Tester.EvaluateWord(HandWords.AtParallelStem(Plain), "at");
        EvaluationResult on = Tester.EvaluateWord(HandWords.AtConnected(Plain), "at");

        Finding p6 = Assert.Single(off.Summary.Of(ErrorCode.P6));
        Assert.Equal(1, p6.Where.LetterIndex);
        Assert.True(p6.Measured < Config.Process.Retrace.MinShareOfReference);
        Assert.False(on.Summary.Has(ErrorCode.P6));
    }

    private static readonly PenForLang.Evaluation.Config.ScoringConfig Config = Shipped.Config;
}
