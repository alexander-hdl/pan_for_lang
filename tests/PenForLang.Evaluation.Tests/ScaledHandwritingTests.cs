using PenForLang.Evaluation.Tests.HandFixtures;
using PenForLang.Evaluation.Tests.Support;
using static PenForLang.Evaluation.Tests.HandFixtures.ProbeWords;

namespace PenForLang.Evaluation.Tests;

/// <summary>
/// R2-02: длины черт (перекладина, точка) сравниваются с эталоном в нормализованных единицах. Раньше письмо крупнее ×1,8 давало
/// «перекладина не отложенный штрих» (D1, лишний отрыв, форма t 0,55), потому что длина черты сравнивалась в абсолютных единицах.
/// </summary>
public class ScaledHandwritingTests
{
    private static readonly ErrorCode[] Stops = [ErrorCode.P1, ErrorCode.P2, ErrorCode.P3, ErrorCode.P4, ErrorCode.P5, ErrorCode.P6, ErrorCode.J1, ErrorCode.J2, ErrorCode.J3, ErrorCode.J4, ErrorCode.D1, ErrorCode.D2, ErrorCode.D3, ErrorCode.D4];

    public static TheoryData<double> Scales => [0.45, 0.6, 1.6, 1.8, 2.2];

    [Theory]
    [MemberData(nameof(Scales))]
    public void The_bar_of_t_is_a_deferred_stroke_at_any_handwriting_size_second_model(double scale)
    {
        PCase c = ProbeWords2.At($"x{scale}", H0 with { Scale = scale });

        EvaluationSummary s = Tester.EvaluateWord(c.Ink, c.Target).Summary;

        Assert.Equal(Decision.Target, s.Decision);
        Assert.DoesNotContain(s.Findings, f => Stops.Contains(f.Code));
        Assert.Equal(DeferredTiming.AfterWord, Assert.Single(s.Deferred).Timing);
        Assert.All(s.Joins, j => Assert.True(j.Present && !j.LiftExpected));
        Assert.True(s.Letters[1].ProcessOk);
    }

    [Theory]
    [MemberData(nameof(Scales))]
    public void The_bar_of_t_is_a_deferred_stroke_at_any_handwriting_size_first_model(double scale)
    {
        PHand h = H0 with { Scale = scale };
        PWord w = MakeAt(Low);
        EvaluationSummary s = Tester.EvaluateWord(Build(S(w.Curve, h), Bar(h, StemAt(w, 1.0))), "at").Summary;

        Assert.Equal(Decision.Target, s.Decision);
        Assert.DoesNotContain(s.Findings, f => Stops.Contains(f.Code));
        Assert.Equal(DeferredTiming.AfterWord, Assert.Single(s.Deferred).Timing);
        Assert.All(s.Joins, j => Assert.True(j.Present && !j.LiftExpected));
    }

    [Theory]
    [MemberData(nameof(Scales))]
    public void The_dot_and_the_bar_of_it_are_deferred_strokes_at_any_handwriting_size(double scale)
    {
        PHand h = H0 with { Scale = scale };
        PWord w = MakeIt(Low);
        EvaluationSummary s = Tester.EvaluateWord(Build(S(w.Curve, h, 2), Dot(h, w.ITop), Bar(h, StemAt(w, 1.0))), "it").Summary;

        Assert.Equal(Decision.Target, s.Decision);
        Assert.DoesNotContain(s.Findings, f => Stops.Contains(f.Code));
        Assert.Equal(2, s.Deferred.Length);
        Assert.All(s.Deferred, d => Assert.Equal(DeferredTiming.AfterWord, d.Timing));
    }
}
