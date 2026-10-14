using PenForLang.Evaluation.Tests.HandFixtures;
using PenForLang.Evaluation.Tests.Support;
using static PenForLang.Evaluation.Tests.HandFixtures.ProbeWords;

namespace PenForLang.Evaluation.Tests;

/// <summary>
/// R2-01: верное слово с широким зазором между a и t не должно давать ложный U0 (раньше: зазор 1,0 → U0, 1,1 → цель, 1,2 → U0).
/// Две независимые модели букв: первая (<see cref="ProbeWords"/>) и вторая (<see cref="ProbeWords2"/>: наклонный эллипс a,
/// изогнутый ствол t). Широкий зазор — F8 (интервал), а не «чужое слово»: форма судится после подгонки каждой буквы и без
/// зоны соединения, а если положение букв всё же не прощается — эталон каждой буквы смещается на её сдвиг по x («упругий» эталон).
/// </summary>
public class WideGapTests
{
    private static readonly ErrorCode[] Stops = [ErrorCode.P1, ErrorCode.P2, ErrorCode.P3, ErrorCode.P4, ErrorCode.P5, ErrorCode.P6, ErrorCode.J1, ErrorCode.J2, ErrorCode.J3, ErrorCode.J4, ErrorCode.D1, ErrorCode.D2, ErrorCode.D3, ErrorCode.D4];

    private static readonly Dictionary<string, PCase> Cases = WideGaps().Concat(ProbeWords2.Gaps()).Concat(ProbeWords2.Correct()).ToDictionary(c => c.Id, StringComparer.Ordinal);

    public static TheoryData<string> GapIds { get; } = [.. WideGaps().Concat(ProbeWords2.Gaps()).Select(c => c.Id)];

    public static TheoryData<string> SecondModelIds { get; } = [.. ProbeWords2.Correct().Select(c => c.Id)];

    private static EvaluationResult Evaluate(string id) => Tester.EvaluateWord(Cases[id].Ink, Cases[id].Target);

    [Theory]
    [MemberData(nameof(GapIds))]
    public void A_wide_gap_between_the_letters_is_a_target_without_stop_findings(string id)
    {
        EvaluationSummary s = Evaluate(id).Summary;

        Assert.Equal(Decision.Target, s.Decision);
        Assert.DoesNotContain(s.Findings, f => Stops.Contains(f.Code));
        Assert.All(s.Letters, l => Assert.True(l.ProcessOk, $"{id}: letter {l.Index}"));
    }

    [Theory]
    [InlineData("G1-gap")]
    [InlineData("G2-gap")]
    public void The_decision_does_not_flip_with_the_gap_between_0_6_and_1_4(string prefix)
    {
        string[] decisions = [.. Cases.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).Select(k => $"{k}:{Evaluate(k).Summary.Decision}")];

        Assert.Equal(9, decisions.Length);
        Assert.All(decisions, d => Assert.EndsWith(":Target", d, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(SecondModelIds))]
    public void The_second_letter_model_is_a_target_without_stop_findings_in_every_handwriting(string id)
    {
        EvaluationSummary s = Evaluate(id).Summary;

        Assert.Equal(Decision.Target, s.Decision);
        Assert.DoesNotContain(s.Findings, f => Stops.Contains(f.Code));
    }

    [Fact]
    public void A_gap_far_beyond_a_letter_is_still_not_judged_by_the_position_of_the_letters()
    {
        // Крайний зазор (2,0 x-height) первой модели: положение букв не причина U0 (форма букв верна).
        PWord w = MakeAt(Low, gap: 2.0);
        EvaluationResult r = Tester.EvaluateWord(Build(S(w.Curve, H0), Bar(H0, StemAt(w, 1.0))), "at");

        Assert.Equal(Decision.Target, r.Summary.Decision);
    }
}
