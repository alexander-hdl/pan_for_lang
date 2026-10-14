using PenForLang.Evaluation.Tests.HandFixtures;
using PenForLang.Evaluation.Tests.Support;
using PenForLang.Glyphs;
using PenForLang.Ink;

namespace PenForLang.Evaluation.Tests;

/// <summary>
/// Старт a с овала на 230–260° (8–9 часов: перо идёт вниз и вправо по низу овала, подхода нет) — P1 у любой руки и с запасом: измерено/порог
/// не меньше 1,2. Запас нужен, чтобы будущая калибровка <c>process.approach.overlapMax</c> (у порога P1 не ставится) не проглотила эту
/// ошибку. Руки: масштаб 0,8–1,3, дрожь до 0,03, наклон до ±20°.
/// </summary>
public class StartOnOvalP1Tests
{
    private const double RequiredMargin = 1.2;

    /// <summary>Старт 245–260° (перо идёт почти по низу овала вправо) геометрически почти не отличим от подхода: P1 только по доказательству перекрытия подхода, запас у него ниже (и граничит с «открытым овалом», у которого P1 запрещён).</summary>
    private const double RequiredOverlapMargin = 1.1;

    private static readonly (double Scale, double Tremor, double Slant, int Seed)[] Hands =
    [
        (1.0, 0.0, 0.0, 1), (0.8, 0.0, 0.0, 1), (1.3, 0.0, 0.0, 1), (1.0, 0.03, 0.0, 2), (0.8, 0.03, 0.0, 3), (1.3, 0.03, 0.0, 4),
        (1.0, 0.0, 20.0, 1), (1.0, 0.0, -20.0, 1), (1.0, 0.03, 20.0, 5), (0.8, 0.03, -20.0, 6), (1.3, 0.02, 20.0, 7),
    ];

    public static TheoryData<double, int> Cases
    {
        get
        {
            var data = new TheoryData<double, int>();
            foreach (double start in new[] { 230.0, 235.0, 240.0, 245.0, 250.0, 260.0 })
            {
                for (int h = 0; h < Hands.Length; h++)
                {
                    data.Add(start, h);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_start_on_the_lower_left_of_the_oval_is_p1_with_a_margin(double startDeg, int hand)
    {
        (double scale, double tremor, double slant, int seed) = Hands[hand];
        HandStroke stroke = Hand.A(leadIn: false, startDeg: startDeg, endDeg: startDeg + 340, scale: scale, tremor: tremor, slant: slant, seed: seed);

        EvaluationResult result = Tester.Evaluator.Evaluate(new LetterEvaluationRequest(HandInkBuilder.Build(stroke), Shipped.Glyph("a"), Shipped.Style));

        Finding p1 = Assert.Single(result.Summary.Findings, f => f.Code == ErrorCode.P1);
        Assert.True(p1.Measured / p1.Threshold >= (startDeg <= 240.0 ? RequiredMargin : RequiredOverlapMargin), $"start {startDeg}, hand {hand}: measured {p1.Measured:F3}, threshold {p1.Threshold:F3}");
        Assert.False(result.Summary.Letters[0].ProcessOk);
    }

    [Fact]
    public void A_real_approach_of_any_length_and_handwriting_is_not_p1()
    {
        foreach (double from in new[] { -1.6, -0.9, -0.3, 0.0 })
        {
            foreach ((double scale, double tremor, double slant, int seed) in Hands)
            {
                HandStroke stroke = Hand.A(leadInFromU: from, scale: scale, tremor: tremor, slant: slant, seed: seed);

                EvaluationResult result = Tester.Evaluator.Evaluate(new LetterEvaluationRequest(HandInkBuilder.Build(stroke), Shipped.Glyph("a"), Shipped.Style));

                Assert.DoesNotContain(result.Summary.Findings, f => f.Code == ErrorCode.P1);
            }
        }
    }
}
