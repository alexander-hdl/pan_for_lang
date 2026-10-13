using PenForLang.Core.Geometry;
using PenForLang.Evaluation.Config;
using PenForLang.Glyphs;

namespace PenForLang.Evaluation.Pipeline;

/// <summary>Ошибка достижения линии: фактический экстремум против цели.</summary>
internal sealed record ReachError(ReachRef Check, double Actual, double Error);

/// <summary>Промежуточные результаты оценки одной буквы — вход для подбаллов формы и находок.</summary>
internal sealed class LetterAnalysis
{
    public required ScoringConfig Config { get; init; }

    public required LetterReference Reference { get; init; }

    public required StyleManifest Style { get; init; }

    public required double ToleranceScale { get; init; }

    public required DeferredAcceptPolicy AcceptPolicy { get; init; }

    public required List<ChildStroke> Children { get; init; }

    public required NormalizationParams Normalization { get; init; }

    /// <summary>Сопоставленные основные штрихи в порядке эталона.</summary>
    public required List<PairMetrics> Pairs { get; init; }

    /// <summary>Основные штрихи эталона без пары (P5).</summary>
    public required List<RefStroke> MissingStrokes { get; init; }

    public required DeferredOutcome Deferred { get; init; }

    /// <summary>Штрих эталона, написанный раньше предыдущего по порядку эталона (P3), или <c>null</c>.</summary>
    public required PairMetrics? OrderViolation { get; init; }

    public required List<ReachError> ReachErrors { get; init; }

    /// <summary>Среднее отклонение, взвешенное длиной тела эталона.</summary>
    public double WeightedDtwMean => Weighted(p => p.DtwMean);

    public double MaxFrechet => Pairs.Count == 0 ? 0.0 : Pairs.Max(p => p.Frechet);

    public double WeightedCorridor => Weighted(p => p.CorridorShare);

    /// <summary>Покрытие по всем основным штрихам эталона; штрих без пары даёт 0.</summary>
    public double TotalCoverage
    {
        get
        {
            double total = 0.0;
            double covered = 0.0;
            foreach (RefStroke stroke in Reference.Strokes)
            {
                total += stroke.BodyLength;
                PairMetrics? pair = Pairs.Find(p => p.Ref == stroke);
                covered += pair is null ? 0.0 : pair.Coverage * stroke.BodyLength;
            }

            return total > 0.0 ? covered / total : 0.0;
        }
    }

    /// <summary>
    /// Доля чернил основного следа (сопоставленные и лишние основные штрихи), лежащая в коридоре эталона: у каракулей мала,
    /// у верной буквы с одним лишним штрихом остаётся высокой.
    /// </summary>
    public double InkPrecision
    {
        get
        {
            double tau = Config.Form.CorridorTau * ToleranceScale;
            int inside = 0;
            int total = 0;
            foreach (Vec2[] stroke in Pairs.Select(p => p.Child.NPos).Concat(Deferred.ExtraMain.Select(s => s.NPos)))
            {
                foreach (Vec2 point in stroke)
                {
                    total++;
                    foreach (RefStroke reference in Reference.Strokes)
                    {
                        if (PolylineMath.DistanceToPolyline(point, reference.Pos) <= tau)
                        {
                            inside++;
                            break;
                        }
                    }
                }
            }

            return total == 0 ? 1.0 : (double)inside / total;
        }
    }

    /// <summary>Лишние и пропущенные основные штрихи.</summary>
    public int StrokeCountDelta => Deferred.ExtraMain.Count + MissingStrokes.Count;

    public bool StartOk => Pairs.All(p => p.StartOk);

    public bool DirectionOk => Pairs.All(p => p.DirectionOk) && (!Config.Process.Direction.BarReverseIsError || Deferred.Slots.All(s => !s.WrongDirection));

    public bool RetraceOk => Pairs.All(p => p.RetraceShare is not { } share || share >= Config.Process.Retrace.MinShareOfReference);

    private double Weighted(Func<PairMetrics, double> selector)
    {
        double sum = 0.0;
        double weight = 0.0;
        foreach (PairMetrics pair in Pairs)
        {
            sum += selector(pair) * pair.Ref.BodyLength;
            weight += pair.Ref.BodyLength;
        }

        return weight > 0.0 ? sum / weight : 0.0;
    }
}
