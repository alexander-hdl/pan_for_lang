using System.Collections.Immutable;
using PenForLang.Evaluation.Config;
using PenForLang.Glyphs;

namespace PenForLang.Evaluation.Pipeline;

/// <summary>
/// <c>form = Σ wₖ·sₖ(mₖ) / Σ wₖ</c> по применимым метрикам (§6.4.13): <c>sₖ</c> — кусочно-линейные функции из
/// <c>scoring.json → form.scores</c>, веса — из <c>form.weights</c>. D4 и K* в форму не входят.
/// </summary>
internal static class FormScorer
{
    public static (double Form, ImmutableArray<FormComponent> Components) Score(LetterAnalysis a)
    {
        ScoringConfig config = a.Config;
        double scale = a.ToleranceScale;
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        if (a.Pairs.Count > 0)
        {
            values["dtwMean"] = a.WeightedDtwMean;
            values["frechet"] = a.MaxFrechet;
            values["corridor"] = a.WeightedCorridor;
            values["coverage"] = a.TotalCoverage;
        }

        double? closed = MaxOrNull(a.Pairs.Select(p => p.ClosedGap));
        if (closed is { } gap)
        {
            values["closed"] = gap;
        }

        if (a.Pairs.Any(p => p.LoopOk is not null))
        {
            values["loops"] = a.Pairs.All(p => p.LoopOk is not false) ? 1.0 : 0.0;
        }

        double? reach = MaxOrNull(a.ReachErrors.Where(e => !e.Check.IsBaseline).Select(e => (double?)Math.Abs(e.Error)));
        if (reach is { } r)
        {
            values["reach"] = r;
        }

        double? baseline = MaxOrNull(a.ReachErrors.Where(e => e.Check.IsBaseline).Select(e => (double?)Math.Abs(e.Error)));
        if (baseline is { } b)
        {
            values["baseline"] = b;
        }

        double? retrace = a.Pairs.Where(p => p.RetraceShare is not null).Select(p => p.RetraceShare!.Value).DefaultIfEmpty(-1.0).Min();
        if (retrace >= 0.0)
        {
            values["retrace"] = retrace.Value;
        }

        values["strokes"] = a.StrokeCountDelta + (a.OrderViolation is null ? 0 : 1);
        if (a.Reference.Deferred.Length > 0 || a.Deferred.ExtraMarks.Count > 0)
        {
            values["deferred"] = DeferredUnits(a);
        }

        if (a.Style.SlantScoring == SlantScoring.Absolute
            && a.Style.SlantDeg is { } styleSlant
            && a.Normalization.InkSlantDeg is { } inkSlant)
        {
            values["slant"] = Math.Abs(inkSlant - styleSlant);
        }

        if (a.Pairs.Count == 0)
        {
            // Нет ни одного сопоставленного штриха: формы нет.
            return (0.0, []);
        }

        double weightSum = 0.0;
        double weighted = 0.0;
        var scored = new List<(string Name, double Value, double Score, double Weight)>();
        foreach (KeyValuePair<string, double> weight in config.Form.Weights)
        {
            if (!values.TryGetValue(weight.Key, out double value))
            {
                continue;
            }

            double score = config.Form.Scores[weight.Key].Evaluate(value, scale);
            weightSum += weight.Value;
            weighted += weight.Value * score;
            scored.Add((weight.Key, value, score, weight.Value));
        }

        var components = ImmutableArray.CreateBuilder<FormComponent>(scored.Count);
        foreach ((string name, double value, double score, double weight) in scored)
        {
            components.Add(new FormComponent(name, value, score, weightSum > 0.0 ? weight / weightSum : 0.0));
        }

        double form = weightSum > 0.0 ? Math.Clamp(weighted / weightSum, 0.0, 1.0) : 0.0;
        return (form, components.MoveToImmutable());
    }

    /// <summary>Единицы штрафа отложенных штрихов (<c>form.deferredPenalty</c>): пустой слот, штрих не на месте, лишняя черта.</summary>
    private static double DeferredUnits(LetterAnalysis a)
    {
        DeferredPenaltyConfig penalty = a.Config.Form.DeferredPenalty;
        double units = 0.0;
        foreach (DeferredAssignment slot in a.Deferred.Slots)
        {
            if (slot.Stroke is null)
            {
                units += penalty.Missing;
            }
            else if (!slot.Near)
            {
                units += penalty.Misplaced;
            }
        }

        return units + (penalty.Extra * a.Deferred.ExtraMarks.Count);
    }

    private static double? MaxOrNull(IEnumerable<double?> values)
    {
        double? best = null;
        foreach (double? v in values)
        {
            if (v is { } value && (best is null || value > best))
            {
                best = value;
            }
        }

        return best;
    }
}
