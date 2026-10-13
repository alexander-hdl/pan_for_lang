using PenForLang.Evaluation.Tests.HandFixtures;
using PenForLang.Evaluation.Tests.Support;
using PenForLang.Glyphs;
using PenForLang.Ink;

namespace PenForLang.Evaluation.Tests;

/// <summary>
/// Регрессия V0.10 (W10-09): ложный P1 у начала очень медленного дрожащего письма, когда у глифа нет выходящего штриха (кусок слова).
/// Подход засчитывался, только если первые 0,2 x-height не идут вниз больше 10°; дрожь (0,05 x-height при 0,15 x-h/с) отклоняла эти
/// 0,2 вниз, подход отбрасывался и старт сравнивался с началом тела (1,6 x-height). Теперь подъём меряется на <c>process.approach.elevationLength</c>.
/// </summary>
public class SlowStartRegressionTests
{
    private static RulerInk SlowA(int pointsOfStroke)
    {
        RulerStroke stroke = ProbeWords.Speeds().First(c => c.Id == "S-vslow").Ink.Strokes[0];
        return new RulerInk([stroke with { Points = [.. stroke.Points.Take(pointsOfStroke)] }], null, 10.0, false);
    }

    public static TheoryData<int> Cuts => [6500, 7000, 7406, 7800];

    [Theory]
    [MemberData(nameof(Cuts))]
    public void A_very_slow_trembling_start_is_not_p1_without_a_lead_out(int cut)
    {
        Glyph noLeadOut = Shipped.Glyph("a") with { LeadOut = null };

        foreach (LetterContext context in new[] { LetterContext.Isolated, LetterContext.WordFirst })
        {
            EvaluationResult result = Tester.Evaluator.Evaluate(new LetterEvaluationRequest(SlowA(cut), noLeadOut, Shipped.Style, Context: context));

            Assert.DoesNotContain(result.Summary.Findings, f => f.Code == ErrorCode.P1);
            Assert.True(result.Detail.Strokes[0].StartOk, $"cut {cut}, {context}: start error {result.Detail.Strokes[0].StartErr:F2}");
        }
    }
}
