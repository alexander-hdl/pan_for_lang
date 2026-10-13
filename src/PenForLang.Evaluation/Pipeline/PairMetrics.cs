using PenForLang.Core.Geometry;
using PenForLang.Evaluation.Config;

namespace PenForLang.Evaluation.Pipeline;

/// <summary>Метрики одного сопоставленного штриха (§6.4.9): процесс (P1, P2, P6) и форма (F1, F2, F3).</summary>
internal sealed class PairMetrics
{
    public required PairAlignment Alignment { get; init; }

    public ChildStroke Child => Alignment.Child;

    public RefStroke Ref => Alignment.Ref;

    /// <summary>Штрих написан в обратную сторону (P2): метрики формы посчитаны по обращённому выравниванию.</summary>
    public required bool Reversed { get; init; }

    public required IReadOnlyList<DtwCell> Path { get; init; }

    /// <summary>Индекс точки рукописи для каждой точки эталона (по выбранному пути).</summary>
    public required int[] ChildOfRef { get; init; }

    public required double MatchCost { get; init; }

    public required double StartErr { get; init; }

    public required EndpointCandidate NearestStart { get; init; }

    public required double ReverseRatio { get; init; }

    public required double FirstSegmentAngleDeg { get; init; }

    public required double DtwMean { get; init; }

    public required double DtwMax { get; init; }

    public required double Frechet { get; init; }

    public required double CorridorShare { get; init; }

    /// <summary>Покрытие тела эталона рукописью; уточняется после назначения лишних штрихов (<see cref="PairMetricsCalculator.WithCoverage"/>).</summary>
    public double Coverage { get; set; }

    public required double? ClosedGap { get; init; }

    public required bool? LoopOk { get; init; }

    /// <summary>Доля ретрейса по написанному относительно эталонной, [0, 1], или <c>null</c> (неприменимо).</summary>
    public required double? RetraceShare { get; init; }

    /// <summary>Участок тела эталона (доли длины дуги) наихудшей проверки ретрейса или <c>null</c>.</summary>
    public (double FromT, double ToT)? RetraceSpan { get; init; }

    public required bool StartOk { get; init; }

    public required bool DirectionOk { get; init; }
}

internal static class PairMetricsCalculator
{
    /// <summary>Считает метрики пары; покрытие — по одному штриху (уточняется <see cref="WithCoverage"/>).</summary>
    public static PairMetrics Compute(PairAlignment alignment, ScoringConfig config, double toleranceScale)
    {
        ChildStroke child = alignment.Child;
        RefStroke reference = alignment.Ref;
        ProcessConfig process = config.Process;
        Vec2[] pos = child.NPos;
        int n = pos.Length;
        int m = reference.Pos.Length;

        // --- Направление (P2): обращённый эталон лучше И первый участок смотрит не туда ---
        int firstTarget = PolylineTools.IndexAtArc(pos, 0, +1, process.Direction.FirstSegmentLength);
        Vec2 childDirection = pos[firstTarget] - pos[0];
        double forwardAngle = 180.0;
        foreach (EndpointCandidate start in reference.Starts)
        {
            forwardAngle = Math.Min(forwardAngle, PolylineTools.AngleDegrees(childDirection, start.Direction));
        }

        double ratio = alignment.ReverseRatio;
        bool longEnough = child.Length >= process.Direction.MinStrokeLength;
        bool reversed = longEnough
            && ratio <= process.Direction.ReverseCostRatioMax
            && (ratio <= process.Direction.StrongReverseCostRatio || forwardAngle >= process.Direction.FirstSegmentAngleMaxDeg);
        IReadOnlyList<DtwCell> path = reversed ? alignment.ReversedPath : alignment.ForwardPath;
        DtwResult result = reversed ? alignment.Comparison.Reversed : alignment.Comparison.Forward;

        // --- Старт (P1): для обращённого штриха «старт» — конец эталона ---
        double bestStart = double.PositiveInfinity;
        EndpointCandidate nearest = reference.Starts[0];
        foreach (EndpointCandidate candidate in reversed ? reference.Ends : reference.Starts)
        {
            double d = Vec2.Distance(pos[0], candidate.Position);
            if (d < bestStart)
            {
                bestStart = d;
                nearest = candidate;
            }
        }

        // --- Отклонение вдоль пути выравнивания ---
        var sums = new double[n];
        var counts = new int[n];
        int jMin = m;
        int jMax = -1;
        foreach (DtwCell cell in path)
        {
            sums[cell.I] += Vec2.Distance(pos[cell.I], reference.Pos[cell.J]);
            counts[cell.I]++;
            jMin = Math.Min(jMin, cell.J);
            jMax = Math.Max(jMax, cell.J);
        }

        double total = 0.0;
        double max = 0.0;
        for (int i = 0; i < n; i++)
        {
            double d = counts[i] > 0 ? sums[i] / counts[i] : 0.0;
            total += d;
            max = Math.Max(max, d);
        }

        double dtwMean = total / n;
        ReadOnlySpan<Vec2> referenceSpan = reversed
            ? reference.PosRev.AsSpan(m - 1 - jMax, jMax - jMin + 1)
            : reference.Pos.AsSpan(jMin, jMax - jMin + 1);
        double frechet = Frechet.Distance(pos, referenceSpan);

        // --- Коридор ---
        double tau = config.Form.CorridorTau * toleranceScale;
        double share = Corridor.Share(pos, reference.Pos, tau);
        double coverage = CoverageUnion(reference.Body, pos, [], tau, config.ResampleStep);

        int[] childOfRef = StrokeMatcher.ChildIndexOfRef(path, m);

        // --- Проверки глифа: замкнутость, петли, ретрейс ---
        double? closedGap = null;
        foreach (ClosedRef check in reference.Closed)
        {
            double gap = PolylineTools.GapToWindow(pos, childOfRef[check.ToIndex], childOfRef[check.FromIndex]);
            double excess = Math.Max(0.0, gap - check.RefGap);
            closedGap = closedGap is null ? excess : Math.Max(closedGap.Value, excess);
        }

        // Петля участка: самопересечение всего штриха, чья петля (середина её дуги) лежит на отмеченном участке.
        bool? loopOk = null;
        if (reference.Loops.Length > 0)
        {
            var loops = new List<PolylineLoop>();
            SelfIntersections.FindLoops(pos, loops);
            foreach (LoopRef check in reference.Loops)
            {
                int a = Math.Min(childOfRef[check.FromIndex], childOfRef[check.ToIndex]);
                int b = Math.Max(childOfRef[check.FromIndex], childOfRef[check.ToIndex]);
                bool ok = loops.Any(l => l.Area >= config.Form.MinLoopArea && (l.Intersection.SegmentA + l.Intersection.SegmentB) / 2 is var mid && mid >= a && mid <= b);
                loopOk = (loopOk ?? true) && ok;
            }
        }

        double? retraceShare = null;
        (double, double)? retraceSpan = null;
        if (!reversed)
        {
            foreach (RetraceRef check in reference.Retrace)
            {
                if (check.RefShare is not { } refShare)
                {
                    continue;
                }

                int from = Math.Min(childOfRef[check.FromIndex], childOfRef[check.ToIndex]);
                int to = Math.Max(childOfRef[check.FromIndex], childOfRef[check.ToIndex]);
                if (from < process.Retrace.MinPriorPoints)
                {
                    continue;
                }

                double childShare = PolylineTools.ShareOnPriorInk(pos, from, to, process.Retrace.Tolerance);
                double relative = refShare > 1e-9 ? Math.Min(1.0, childShare / refShare) : 1.0;
                if (retraceShare is null || relative < retraceShare.Value)
                {
                    retraceShare = relative;
                    retraceSpan = (check.FromT, check.ToT);
                }
            }
        }

        return new PairMetrics
        {
            Alignment = alignment,
            Reversed = reversed,
            Path = path,
            ChildOfRef = childOfRef,
            MatchCost = result.CostPerFirstElement,
            StartErr = bestStart,
            NearestStart = nearest,
            ReverseRatio = ratio,
            FirstSegmentAngleDeg = forwardAngle,
            DtwMean = dtwMean,
            DtwMax = max,
            Frechet = frechet,
            CorridorShare = share,
            Coverage = coverage,
            ClosedGap = closedGap,
            LoopOk = loopOk,
            RetraceShare = retraceShare,
            RetraceSpan = retraceSpan,
            StartOk = bestStart <= process.StartTolerance,
            DirectionOk = !reversed,
        };
    }

    /// <summary>
    /// Пересчитывает покрытие с учётом других основных штрихов рукописи без пары: вместе они покрывают эталон («буква
    /// кусками» не обнуляет покрытие, её наказывает P4).
    /// </summary>
    public static void WithCoverage(PairMetrics pair, IReadOnlyList<Vec2[]> otherInk, ScoringConfig config, double toleranceScale)
    {
        double tau = config.Form.CorridorTau * toleranceScale;
        pair.Coverage = CoverageUnion(pair.Ref.Body, pair.Child.NPos, otherInk, tau, config.ResampleStep);
    }

    /// <summary>Доля длины тела эталона, лежащая в коридоре <paramref name="tau"/> хотя бы одной из ломаных рукописи.</summary>
    private static double CoverageUnion(ReadOnlySpan<Vec2> body, Vec2[] first, IReadOnlyList<Vec2[]> others, double tau, double step)
    {
        double total = PolylineMath.Length(body);
        if (!(total > 0.0))
        {
            return PolylineMath.DistanceToPolyline(body[0], first) <= tau ? 1.0 : 0.0;
        }

        double covered = 0.0;
        for (int i = 1; i < body.Length; i++)
        {
            Vec2 a = body[i - 1];
            Vec2 b = body[i];
            double len = Vec2.Distance(a, b);
            if (len <= 0.0)
            {
                continue;
            }

            int pieces = Math.Max(1, (int)Math.Ceiling(len / step));
            double pieceLength = len / pieces;
            for (int k = 0; k < pieces; k++)
            {
                Vec2 mid = Vec2.Lerp(a, b, (k + 0.5) / pieces);
                bool inside = PolylineMath.DistanceToPolyline(mid, first) <= tau;
                for (int o = 0; !inside && o < others.Count; o++)
                {
                    inside = PolylineMath.DistanceToPolyline(mid, others[o]) <= tau;
                }

                if (inside)
                {
                    covered += pieceLength;
                }
            }
        }

        return Math.Min(1.0, covered / total);
    }
}
