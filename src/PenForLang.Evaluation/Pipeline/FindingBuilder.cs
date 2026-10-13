using System.Collections.Immutable;
using PenForLang.Core.Geometry;
using PenForLang.Evaluation.Config;
using PenForLang.Glyphs;

namespace PenForLang.Evaluation.Pipeline;

/// <summary>
/// Находки по коду (ARCHITECTURE.md §6.4.14, таксономия методики §7.3): каждая несёт место, тяжесть, участок эталона и
/// измеренное значение с порогом — достаточно, чтобы объяснить ошибку без сохранения чернил.
/// </summary>
internal static class FindingBuilder
{
    private const int LetterIndex = 0;
    private const double StartSpanEnd = 0.15;

    public static ImmutableArray<Finding> Build(LetterAnalysis a)
    {
        var list = new List<Finding>();
        ScoringConfig config = a.Config;
        FindingsConfig f = config.Findings;
        double scale = a.ToleranceScale;

        foreach (PairMetrics pair in a.Pairs)
        {
            RefStroke stroke = pair.Ref;
            if (!pair.StartOk)
            {
                bool atEnd = pair.Reversed;
                list.Add(new Finding(
                    ErrorCode.P1,
                    new FindingLocation(FindingPlace.Stroke, LetterIndex, stroke.Id, pair.Child.InkIndex),
                    Severity(f, pair.StartErr, config.Process.StartTolerance),
                    new TemplateSpan(stroke.Id, pair.NearestStart.Part, atEnd ? 1.0 - StartSpanEnd : 0.0, atEnd ? 1.0 : StartSpanEnd),
                    FindingKind.Error,
                    pair.StartErr,
                    config.Process.StartTolerance));
            }

            if (!pair.DirectionOk)
            {
                list.Add(new Finding(
                    ErrorCode.P2,
                    new FindingLocation(FindingPlace.Stroke, LetterIndex, stroke.Id, pair.Child.InkIndex),
                    Fixed(f, ErrorCode.P2),
                    new TemplateSpan(stroke.Id, TemplatePart.Body, 0.0, 1.0),
                    FindingKind.Error,
                    pair.ReverseRatio,
                    config.Process.Direction.ReverseCostRatioMax));
            }

            if (pair.RetraceShare is { } share && share < config.Process.Retrace.MinShareOfReference)
            {
                (double from, double to) = pair.RetraceSpan ?? (0.0, 1.0);
                list.Add(new Finding(
                    ErrorCode.P6,
                    new FindingLocation(FindingPlace.Stroke, LetterIndex, stroke.Id, pair.Child.InkIndex),
                    Fixed(f, ErrorCode.P6),
                    new TemplateSpan(stroke.Id, TemplatePart.Body, from, to),
                    FindingKind.Error,
                    share,
                    config.Process.Retrace.MinShareOfReference));
            }
        }

        if (a.OrderViolation is { } late)
        {
            list.Add(new Finding(
                ErrorCode.P3,
                new FindingLocation(FindingPlace.Stroke, LetterIndex, late.Ref.Id, late.Child.InkIndex),
                Fixed(f, ErrorCode.P3),
                new TemplateSpan(late.Ref.Id, TemplatePart.Body, 0.0, 1.0),
                FindingKind.Error,
                late.Child.Chrono,
                late.Ref.Order));
        }

        AddStrokeCountFindings(a, list);
        AddDeferredFindings(a, list);
        AddFormFindings(a, list, scale);

        list.Sort(Compare);
        return [.. list];
    }

    private static void AddStrokeCountFindings(LetterAnalysis a, List<Finding> list)
    {
        ScoringConfig config = a.Config;
        FindingsConfig f = config.Findings;
        StrokeCountConfig count = config.Process.StrokeCount;
        int extra = a.Deferred.ExtraMain.Count;
        if (extra >= count.ExtraForP4)
        {
            double severity = Math.Min(1.0, (double)extra / count.ExtraForStop);
            foreach (ChildStroke stroke in a.Deferred.ExtraMain)
            {
                list.Add(new Finding(
                    ErrorCode.P4,
                    new FindingLocation(FindingPlace.Stroke, LetterIndex, null, stroke.InkIndex),
                    severity,
                    NearestSpan(a, stroke),
                    FindingKind.Error,
                    extra,
                    count.ExtraForP4));
            }
        }

        foreach (RefStroke missing in a.MissingStrokes)
        {
            list.Add(new Finding(
                ErrorCode.P5,
                new FindingLocation(FindingPlace.Stroke, LetterIndex, missing.Id, null),
                Fixed(f, ErrorCode.P5),
                new TemplateSpan(missing.Id, TemplatePart.Body, 0.0, 1.0),
                FindingKind.Error,
                a.MissingStrokes.Count,
                0));
        }
    }

    private static void AddDeferredFindings(LetterAnalysis a, List<Finding> list)
    {
        ScoringConfig config = a.Config;
        FindingsConfig f = config.Findings;
        DeferredConfig d = config.Deferred;
        foreach (DeferredAssignment slot in a.Deferred.Slots)
        {
            string id = slot.Slot.Id;
            var whole = new TemplateSpan(id, TemplatePart.Body, 0.0, 1.0);
            if (slot.Stroke is not { } stroke)
            {
                list.Add(new Finding(
                    ErrorCode.D1,
                    new FindingLocation(FindingPlace.Deferred, LetterIndex, id, null),
                    Fixed(f, ErrorCode.D1),
                    whole));
                continue;
            }

            var where = new FindingLocation(FindingPlace.Deferred, LetterIndex, id, stroke.InkIndex);
            if (!slot.Near)
            {
                list.Add(new Finding(
                    ErrorCode.D2,
                    where,
                    Severity(f, slot.Distance!.Value, d.NearDistance),
                    whole,
                    FindingKind.Error,
                    slot.Distance,
                    d.NearDistance));
            }

            if (slot.WrongDirection)
            {
                list.Add(new Finding(ErrorCode.P2, where, Fixed(f, ErrorCode.P2), whole));
            }

            if (slot.Timing == DeferredTiming.Immediate)
            {
                bool strict = a.AcceptPolicy == DeferredAcceptPolicy.AfterWord;
                list.Add(new Finding(
                    ErrorCode.D4,
                    where,
                    strict ? d.StrictTimingSeverity : 0.0,
                    whole,
                    strict ? FindingKind.Error : FindingKind.Info));
            }
        }

        foreach (ChildStroke mark in a.Deferred.ExtraMarks)
        {
            list.Add(new Finding(
                ErrorCode.D3,
                new FindingLocation(FindingPlace.Deferred, LetterIndex, null, mark.InkIndex),
                Fixed(f, ErrorCode.D3),
                NearestSpan(a, mark)));
        }
    }

    private static void AddFormFindings(LetterAnalysis a, List<Finding> list, double scale)
    {
        FindingsConfig f = a.Config.Findings;
        if (a.Pairs.Count == 0)
        {
            return;
        }

        double dtw = a.WeightedDtwMean;
        if (dtw > f.F1DtwMean * scale)
        {
            list.Add(LetterFinding(ErrorCode.F1, Severity(f, dtw, f.F1DtwMean * scale), TemplateSpan.Whole, dtw, f.F1DtwMean * scale));
        }

        double? worstClosed = null;
        PairMetrics? closedPair = null;
        foreach (PairMetrics pair in a.Pairs)
        {
            if (pair.ClosedGap is { } gap && (worstClosed is null || gap > worstClosed))
            {
                worstClosed = gap;
                closedPair = pair;
            }
        }

        if (worstClosed is { } closed && closedPair is not null && closed > f.F2ClosedGap * scale)
        {
            ClosedRef check = closedPair.Ref.Closed[0];
            list.Add(new Finding(
                ErrorCode.F2,
                new FindingLocation(FindingPlace.Stroke, LetterIndex, closedPair.Ref.Id, closedPair.Child.InkIndex),
                Severity(f, closed, f.F2ClosedGap * scale),
                new TemplateSpan(closedPair.Ref.Id, TemplatePart.Body, check.FromT, check.ToT),
                FindingKind.Error,
                closed,
                f.F2ClosedGap * scale));
        }

        PairMetrics? noLoop = a.Pairs.FirstOrDefault(p => p.LoopOk is false);
        if (noLoop is not null)
        {
            LoopRef loop = noLoop.Ref.Loops[0];
            list.Add(new Finding(
                ErrorCode.F3,
                new FindingLocation(FindingPlace.Stroke, LetterIndex, noLoop.Ref.Id, noLoop.Child.InkIndex),
                Fixed(f, ErrorCode.F3),
                new TemplateSpan(noLoop.Ref.Id, TemplatePart.Body, loop.FromT, loop.ToT),
                FindingKind.Error,
                0,
                1));
        }

        ReachError? worstReach = a.ReachErrors.Where(e => !e.Check.IsBaseline).OrderByDescending(e => Math.Abs(e.Error)).FirstOrDefault();
        if (worstReach is not null && Math.Abs(worstReach.Error) > f.F4ReachError * scale)
        {
            list.Add(LetterFinding(
                ErrorCode.F4,
                Severity(f, Math.Abs(worstReach.Error), f.F4ReachError * scale),
                TemplateSpan.Whole,
                worstReach.Error,
                f.F4ReachError * scale));
        }

        ReachError? worstBaseline = a.ReachErrors.Where(e => e.Check.IsBaseline).OrderByDescending(e => Math.Abs(e.Error)).FirstOrDefault();
        if (worstBaseline is not null && Math.Abs(worstBaseline.Error) > f.F5BaselineError * scale)
        {
            list.Add(LetterFinding(
                ErrorCode.F5,
                Severity(f, Math.Abs(worstBaseline.Error), f.F5BaselineError * scale),
                TemplateSpan.Whole,
                worstBaseline.Error,
                f.F5BaselineError * scale));
        }

        if (a.Style.SlantScoring == SlantScoring.Absolute
            && a.Style.SlantDeg is { } styleSlant
            && a.Normalization.InkSlantDeg is { } inkSlant
            && Math.Abs(inkSlant - styleSlant) > f.F6SlantDeviationDeg * scale)
        {
            double deviation = Math.Abs(inkSlant - styleSlant);
            list.Add(LetterFinding(ErrorCode.F6, Severity(f, deviation, f.F6SlantDeviationDeg * scale), TemplateSpan.Whole, deviation, f.F6SlantDeviationDeg * scale));
        }
    }

    private static Finding LetterFinding(ErrorCode code, double severity, TemplateSpan span, double measured, double threshold) =>
        new(code, new FindingLocation(FindingPlace.Letter, LetterIndex, null, null), severity, span, FindingKind.Error, measured, threshold);

    /// <summary>Тяжесть: <c>base</c> на пороге, 1 при превышении порога в <c>severityFullAtMultiple</c> раз.</summary>
    private static double Severity(FindingsConfig f, double value, double threshold)
    {
        if (!(threshold > 0.0))
        {
            return 1.0;
        }

        double over = ((value / threshold) - 1.0) / (f.SeverityFullAtMultiple - 1.0);
        return f.SeverityBase + ((1.0 - f.SeverityBase) * Math.Clamp(over, 0.0, 1.0));
    }

    private static double Fixed(FindingsConfig f, ErrorCode code) => f.FixedSeverity.TryGetValue(code, out double value) ? value : 1.0;

    /// <summary>Участок эталона рядом с лишним штрихом: ближайший основной штрих целиком.</summary>
    private static TemplateSpan NearestSpan(LetterAnalysis a, ChildStroke stroke)
    {
        Vec2 center = stroke.NCenter;
        string? best = null;
        double bestDistance = double.PositiveInfinity;
        foreach (RefStroke reference in a.Reference.Strokes)
        {
            double d = PolylineMath.DistanceToPolyline(center, reference.Pos);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = reference.Id;
            }
        }

        return best is null ? TemplateSpan.Whole : new TemplateSpan(best, TemplatePart.Body, 0.0, 1.0);
    }

    /// <summary>Порядок по приоритету методики §7.3 (P перед F и D), затем по штриху эталона и штриху рукописи.</summary>
    private static int Compare(Finding x, Finding y)
    {
        int c = Rank(x.Code).CompareTo(Rank(y.Code));
        if (c != 0)
        {
            return c;
        }

        c = string.CompareOrdinal(x.Where.StrokeId, y.Where.StrokeId);
        return c != 0 ? c : Nullable.Compare(x.Where.InkStrokeIndex, y.Where.InkStrokeIndex);
    }

    private static int Rank(ErrorCode code) => code switch
    {
        ErrorCode.P1 or ErrorCode.P2 => 30,
        ErrorCode.P3 => 40,
        ErrorCode.P4 => 41,
        ErrorCode.P5 => 42,
        ErrorCode.P6 => 43,
        ErrorCode.F2 or ErrorCode.F3 => 60,
        ErrorCode.D1 or ErrorCode.D2 or ErrorCode.D3 => 70,
        ErrorCode.F4 or ErrorCode.F5 => 80,
        ErrorCode.F1 => 90,
        ErrorCode.F6 or ErrorCode.F7 or ErrorCode.F8 => 100,
        ErrorCode.D4 => 200,
        _ => 150,
    };
}
