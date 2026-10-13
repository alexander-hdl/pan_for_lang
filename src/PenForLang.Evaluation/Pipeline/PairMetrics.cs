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
    public double? RetraceShare { get; set; }

    /// <summary>Участок тела эталона (доли длины дуги) наихудшей проверки ретрейса или <c>null</c>.</summary>
    public (double FromT, double ToT)? RetraceSpan { get; set; }

    /// <summary>Индексы участка ретрейса в эталоне (для проверки, не лежит ли он под лишним штрихом).</summary>
    public (int FromIndex, int ToIndex)? RetraceRefRange { get; init; }

    public required bool StartOk { get; init; }

    /// <summary>Длина дуги вдоль тела эталона от его начала до места, с которого DTW «начинает» рукопись (0, если на подходе или в начале).</summary>
    public required double StartArcOffset { get; init; }

    /// <summary>Причина P1 (для объяснения): измеренное значение, порог.</summary>
    public required (double Measured, double Threshold) StartEvidence { get; init; }

    public required bool DirectionOk { get; init; }

    /// <summary>
    /// Отношение ориентированной площади участка рукописи (овал, петля) к эталонной, если направление обхода участка обратное
    /// (отрицательное число), иначе <c>null</c>.
    /// </summary>
    public double? RangeAreaRatio { get; init; }

    /// <summary>Участок тела эталона (доли длины дуги), обойдённый в обратную сторону, или <c>null</c>.</summary>
    public (double FromT, double ToT)? RangeSpan { get; init; }
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
        double overlap = 0.0;
        double entryAngle = 0.0;
        bool approachRejected = false;
        bool reversed = longEnough
            && ratio <= process.Direction.ReverseCostRatioMax
            && (ratio <= process.Direction.StrongReverseCostRatio || forwardAngle >= process.Direction.FirstSegmentAngleMaxDeg);
        List<DtwCell> path = reversed ? alignment.ReversedPath : alignment.ForwardPath;
        DtwResult result = reversed ? alignment.Comparison.Reversed : alignment.Comparison.Forward;

        // --- Старт (P1): подход или начало тела; для обращённого штриха «старт» — конец эталона (P1 для него не выдаётся) ---
        double bestStart = double.PositiveInfinity;
        EndpointCandidate nearest = reference.Starts[0];
        if (reversed)
        {
            foreach (EndpointCandidate candidate in reference.Ends)
            {
                double d = Vec2.Distance(pos[0], candidate.Position);
                if (d < bestStart)
                {
                    bestStart = d;
                    nearest = candidate;
                }
            }
        }
        else
        {
            (bestStart, nearest, bool viaApproach) = StartError(reference, pos[0], childDirection, process);
            if (viaApproach)
            {
                // Подход засчитывается, только если путь до входа в тело не перекрыт последующим следом ИЛИ приходит в тело
                // в нужном направлении (старт снизу овала похож на подход геометрически, но это уже сам овал). У порога P1 не ставится.
                (overlap, entryAngle) = EntryEvidence(reference, pos, path, config);
                ApproachConfig approach = process.Approach;
                if (overlap > approach.OverlapMax + approach.AmbiguousShare && entryAngle > approach.EntryAngleMaxDeg + approach.AmbiguousAngleDeg)
                {
                    approachRejected = true;
                    nearest = reference.Starts[^1];
                    bestStart = Vec2.Distance(pos[0], nearest.Position);
                }
            }
        }

        // Место на эталоне, с которого DTW с открытым началом «начинает» рукопись: старт с середины тела — P1, даже если точка
        // случайно близка к подходу (например, старт снизу овала). Старт на самом эталоне в пределах допуска по дуге
        // (около ±50° по овалу: 12 и 3 часа у a) — малое отклонение от эталонного старта, не P1.
        double startArc = reversed || path.Count == 0 ? 0.0 : Math.Max(0.0, path[0].J - reference.BodyStart) * config.ResampleStep;
        bool onReference = !reversed && !approachRejected && path.Count > 0
            && Vec2.Distance(pos[0], reference.Pos[path[0].J]) <= process.StartOnPathTolerance
            && path[0].J >= reference.BodyStart;
        bool geometryOk = bestStart <= process.StartTolerance || onReference;
        (double, double) startEvidence = approachRejected
            ? (overlap, process.Approach.OverlapMax)
            : (!geometryOk ? (bestStart, process.StartTolerance) : (startArc, process.StartArcTolerance));

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

        // --- Проверки глифа: замкнутость, петли, направление обхода участков, ретрейс ---
        int closureHalf = PolylineTools.PointsFor(config.Form.ClosureWindow, config.ResampleStep);
        double? closedGap = null;
        foreach (ClosedRef check in reference.Closed)
        {
            double gap = PolylineTools.ClosureGap(pos, childOfRef[check.ToIndex], childOfRef[check.FromIndex], closureHalf);
            double excess = Math.Max(0.0, gap - check.RefGap);
            closedGap = closedGap is null ? excess : Math.Max(closedGap.Value, excess);
        }

        // Направление обхода овала или петли (главная P2-ошибка методики: «овалы против часовой»): знак ориентированной площади
        // участка рукописи против эталона. Не зависит от направления штриха в целом — ловит овал по часовой при верных подходе и стволе.
        double? rangeRatio = null;
        (double, double)? rangeSpan = null;
        if (!reversed)
        {
            foreach ((int fromIndex, int toIndex, double fromT, double toT) in reference.OrientedRanges)
            {
                int a = Math.Min(childOfRef[fromIndex], childOfRef[toIndex]);
                int b = Math.Max(childOfRef[fromIndex], childOfRef[toIndex]);
                if (b - a + 1 < 3)
                {
                    continue;
                }

                double refArea = PolylineTools.SignedArea(reference.Pos.AsSpan(fromIndex, toIndex - fromIndex + 1));
                double childArea = PolylineTools.SignedArea(pos.AsSpan(a, b - a + 1));
                if (Math.Abs(refArea) > 1e-6
                    && Math.Abs(childArea) >= process.Direction.RangeAreaMinShare * Math.Abs(refArea)
                    && Math.Sign(childArea) != Math.Sign(refArea))
                {
                    rangeRatio = childArea / refArea;
                    rangeSpan = (fromT, toT);
                    break;
                }
            }
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
        (int, int)? retraceRange = null;
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

                // Участок, отображённый в слишком малое число точек, — ретрейса в рукописи нет (например, нет ствола: «o» вместо a).
                double relative;
                if (to - from + 1 < process.Retrace.MinRangePoints)
                {
                    relative = 0.0;
                }
                else
                {
                    double childShare = PolylineTools.ShareOnPriorInk(pos, from, to, process.Retrace.Tolerance);
                    relative = refShare > 1e-9 ? Math.Min(1.0, childShare / refShare) : 1.0;
                }

                if (retraceShare is null || relative < retraceShare.Value)
                {
                    retraceShare = relative;
                    retraceSpan = (check.FromT, check.ToT);
                    retraceRange = (check.FromIndex, check.ToIndex);
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
            RetraceRefRange = retraceRange,
            StartArcOffset = startArc,
            StartEvidence = startEvidence,
            StartOk = reversed || rangeRatio is not null || (geometryOk && startArc <= process.StartArcTolerance),
            DirectionOk = !reversed && rangeRatio is null,
            RangeAreaRatio = rangeRatio,
            RangeSpan = rangeSpan,
        };
    }

    /// <summary>
    /// Ошибка старта: расстояние до ближайшего допустимого старта. Допустимы начало тела и (при <c>startDot = leadIn</c>) любое
    /// место подхода — ломаной входящего штриха и её продолжения влево вдоль линии начала — при ходе вперёд, то есть первый
    /// участок рукописи не отклонён от хода подхода больше <c>process.approach.maxAngleDeg</c>.
    /// </summary>
    private static (double Error, EndpointCandidate Nearest, bool ViaApproach) StartError(RefStroke reference, Vec2 start, Vec2 firstDirection, ProcessConfig process)
    {
        EndpointCandidate body = reference.Starts[^1];
        double best = Vec2.Distance(start, body.Position);
        EndpointCandidate nearest = body;
        if (reference.Starts.Length < 2)
        {
            return (best, nearest, false);
        }

        ReadOnlySpan<Vec2> approach = reference.Pos.AsSpan(0, reference.BodyStart + 1);
        int vertex = 0;
        double onApproach = double.PositiveInfinity;
        for (int i = 0; i < approach.Length; i++)
        {
            double d = Vec2.Distance(start, approach[i]);
            if (d < onApproach)
            {
                onApproach = d;
                vertex = i;
            }
        }

        onApproach = Math.Min(onApproach, PolylineMath.DistanceToPolyline(start, approach));
        Vec2 direction = reference.Dir[vertex];
        Vec2 origin = approach[0];
        bool onLine = start.X < origin.X ? process.Approach.ExtendLeft : start.X <= origin.X + process.Approach.ExtendRight;
        if (onLine)
        {
            double along = Math.Abs(start.Y - origin.Y);
            if (along < onApproach)
            {
                onApproach = along;
                direction = new Vec2(1.0, 0.0);
            }
        }

        // Ошибка выражается в единицах допуска старта: подход жёстче (process.approach.tolerance), чем начало тела.
        double scaled = onApproach * process.StartTolerance / process.Approach.Tolerance;
        double elevation = Math.Atan2(firstDirection.Y, Math.Abs(firstDirection.X)) * (180.0 / Math.PI);
        if (scaled < best
            && PolylineTools.AngleDegrees(firstDirection, direction) <= process.Approach.MaxAngleDeg
            && elevation >= process.Approach.MinElevationDeg)
        {
            return (scaled, reference.Starts[0], true);
        }

        return (best, nearest, false);
    }

    /// <summary>
    /// Признаки подхода: доля пути до входа в тело, перекрытая последующим следом (кроме прилегающего к телу хвоста), и угол между
    /// направлением прихода в точку входа и <c>entry.tangent</c> эталона. Верный подход почти не перекрыт; старт снизу овала
    /// проходит четверть овала, которую затем проходит и сам овал.
    /// </summary>
    private static (double Overlap, double EntryAngle) EntryEvidence(RefStroke reference, Vec2[] pos, List<DtwCell> path, ScoringConfig config)
    {
        ApproachConfig approach = config.Process.Approach;
        int entry = -1;
        foreach (DtwCell cell in path)
        {
            if (cell.J >= reference.BodyStart)
            {
                entry = cell.I;
                break;
            }
        }

        if (entry <= 0)
        {
            return (0.0, 0.0);
        }

        int tail = PolylineTools.PointsFor(approach.OverlapSkipTail, config.ResampleStep);
        int prefix = entry + 1 - tail;
        double overlap = 0.0;
        if (prefix >= 2)
        {
            ReadOnlySpan<Vec2> after = pos.AsSpan(entry);
            int inside = 0;
            for (int i = 0; i < prefix; i++)
            {
                if (PolylineMath.DistanceToPolyline(pos[i], after) <= approach.OverlapTolerance)
                {
                    inside++;
                }
            }

            overlap = (double)inside / prefix;
        }

        double angle = 0.0;
        if (reference.EntryTangent.LengthSquared > 0.0)
        {
            int back = Math.Max(0, entry - PolylineTools.PointsFor(0.2, config.ResampleStep));
            angle = PolylineTools.AngleDegrees(pos[entry] - pos[back], reference.EntryTangent);
        }

        return (overlap, angle);
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
