using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PenForLang.Core;
using PenForLang.Core.Geometry;
using PenForLang.Evaluation.Config;
using PenForLang.Evaluation.Pipeline;
using PenForLang.Glyphs;
using PenForLang.Ink;

namespace PenForLang.Evaluation;

/// <summary>
/// Оценщик изолированной буквы v0 (ARCHITECTURE.md §6.4.1–6.4.2, §6.4.8–6.4.10). Конвейер:
/// <list type="number">
/// <item>предобработка: выбросы, сглаживание по времени и прореживание дрожи отсчётов, микроштрихи, ресэмплинг по длине дуги (число точек ограничено), сглаживание Савицкого–Голея;</item>
/// <item>нормализация для выравнивания по линейкам (деслантинг, масштаб и сдвиг baseline по процентилям v, сдвиг по u; затем робастная подгонка по соответствиям DTW), не по bounding box;</item>
/// <item>сопоставление штрихов: DTW (позиция + направление, открытые концы, прямой и обращённый эталон) и Венгерский алгоритм с неназначением;</item>
/// <item>процесс: старт (P1; допустимы подход и начало тела), направление (P2; штрих целиком и обход овалов и петель), порядок (P3), лишние и пропущенные штрихи (P4, P5), ретрейс (P6);</item>
/// <item>отложенные штрихи: назначение по реальному стволу, D1, D2, D3, время (D4, информационно);</item>
/// <item>форма: коридор, покрытие, Фреше, замкнутость, петли, достижение линий, подбаллы и <c>form</c>.</item>
/// </list>
/// Числа берутся из <see cref="ScoringConfig"/>. Идентичность буквы (L*) не поддержана (гипотезы — позже).
/// <para>
/// <b>Контекст слова (V0.10, §6.4.9).</b> <see cref="LetterContext.WordFirst"/> и <see cref="LetterContext.WordInner"/> применяются
/// к подвыборке чернил одной буквы, вырезанной по выравниванию слова (<see cref="WordEvaluator"/>), и к эталону буквы без
/// отложенных штрихов: допуски формы умножаются на <c>word.contextToleranceScale</c>; порядок и число штрихов (P3–P5) не
/// выдаются (отрывы внутри слова — <c>LiftsInsideLetter</c>); у внутренней буквы нет и P1 (вход задаёт соединение, J3);
/// <c>process_ok</c> первой буквы: старт, направление, ретрейс; внутренней: направление, ретрейс.
/// </para>
/// <para>
/// <b>Старт изолированной буквы (P1).</b> Допустимы начало тела и (при <c>animation.startDot = leadIn</c>) любое место подхода:
/// вся ломаная входящего штриха и её продолжение вдоль линии начала подхода (подход бывает длиннее или короче эталонного),
/// при ходе вперёд. Кроме того, рукопись не должна «начинаться» с середины тела: место, с которого DTW с открытым началом
/// начинает рукопись, не дальше <c>process.startArcTolerance</c> по телу от его начала. Для штриха, написанного в обратную
/// сторону (P2) или с обратным обходом овала (P2 по знаку площади), P1 не выдаётся: одна ошибка — одна находка.
/// </para>
/// <para>
/// <b>Направление (P2).</b> Штрих в целом: обращённый эталон заметно лучше прямого. Овалы и петли (<c>checks.closed</c>,
/// <c>checks.loops</c>): знак ориентированной площади участка рукописи против эталона — ловит овал по часовой при верных
/// подходе и стволе; <c>TemplateSpan</c> указывает участок. Перекладина справа налево — информационная находка P2
/// (флаг <c>process.direction.barReverseIsError</c>).
/// </para>
/// <para>
/// <b>Время отложенных штрихов (D4).</b> «После слова» — позже последнего сопоставленного основного штриха, иначе «сразу».
/// У буквы с единственным основным штрихом (i, t) «сразу» возможно только как постановка точки или перекладины раньше ствола;
/// постановка после ствола — всегда «после слова» (различие появится в слове, V0.10: отрыв основного следа и <c>LiftExpected</c>).
/// «Сразу» даёт информационную находку D4 (<see cref="FindingKind.Info"/>), не влияет на <c>form</c>, процесс и исход
/// (решение заказчика, ARCHITECTURE.md §3.2.3); в строгом режиме (<see cref="DeferredAcceptPolicy.AfterWord"/>) D4 — ошибка.
/// </para>
/// <para>Потокобезопасен; кеш подготовленных эталонов ключуется экземпляром глифа и живёт, пока жив глиф.</para>
/// </summary>
public sealed class LetterEvaluator : ILetterEvaluator
{
    private readonly ScoringConfig _config;
    private readonly SavitzkyGolayFilter _filter;
    private readonly ConditionalWeakTable<Glyph, LetterReference> _references = new();

    /// <summary>Создаёт оценщик со снимком конфигурации.</summary>
    /// <param name="config">Конфигурация (<see cref="ScoringConfigLoader.Load"/>).</param>
    public LetterEvaluator(ScoringConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _filter = new SavitzkyGolayFilter(config.Smoothing.Window, config.Smoothing.Order);
        Info = new EvaluatorInfo(EvaluatorVersion.Current, config.ConfigHash);
    }

    /// <inheritdoc />
    public EvaluatorInfo Info { get; }

    /// <summary>Конфигурация, с которой создан оценщик.</summary>
    public ScoringConfig Config => _config;

    /// <summary>Загружает <c>config/scoring.json</c> из источника контента и создаёт оценщик.</summary>
    /// <param name="source">Источник контента.</param>
    /// <param name="path">Относительный путь файла конфигурации.</param>
    public static LetterEvaluator Create(IContentSource source, string path = ScoringConfig.DefaultPath) =>
        new(ScoringConfigLoader.Load(source, path));

    /// <inheritdoc />
    public EvaluationResult Evaluate(LetterEvaluationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Ink);
        ArgumentNullException.ThrowIfNull(request.Glyph);
        ArgumentNullException.ThrowIfNull(request.Style);
        Glyph glyph = request.Glyph;
        LetterReference reference = _references.GetValue(glyph, g => LetterReference.Build(g, request.Style.Lines, _config, _filter));
        List<ChildStroke> children = ChildInk.Prepare(request.Ink, reference, _config, _filter);
        if (children.Count == 0)
        {
            return Unrecognized(request, new NormalizationParams(0.0, 1.0, 0.0, 0.0, null, reference.SlantDeg), null);
        }

        NormalizationParams normalization = ChildInk.Normalize(children, reference, _config);
        var workspace = new DtwWorkspace();
        int[] assignment = StrokeMatcher.Assign(children, reference, _config, workspace, out PairAlignment?[,] alignments);

        // Пары в порядке эталона; штрихи рукописи без пары — кандидаты в отложенные и лишние.
        var matched = new List<(int Child, int Ref)>();
        for (int i = 0; i < assignment.Length; i++)
        {
            if (assignment[i] >= 0)
            {
                matched.Add((i, assignment[i]));
            }
        }

        matched.Sort(static (x, y) => x.Ref.CompareTo(y.Ref));

        // Раунды «подгонка масштаба и сдвига — повторное выравнивание»: после большой поправки соответствия первого прохода неточны.
        Similarity refinement = Similarity.Identity;
        for (int round = 0; round < _config.Normalization.Refine.Rounds; round++)
        {
            (Similarity fit, double change) = RefineTransform(children, reference, matched, alignments);
            if (change == 0.0)
            {
                break;
            }

            refinement = new Similarity(fit.Scale * refinement.Scale, (refinement.Translation * fit.Scale) + fit.Translation);
            foreach ((int childIndex, int refIndex) in matched)
            {
                alignments[childIndex, refIndex] = StrokeMatcher.Align(children[childIndex], reference.Strokes[refIndex], _config, workspace);
            }

            if (change <= _config.Normalization.Refine.RerunAbove)
            {
                break;
            }
        }

        double toleranceScale = _config.ToleranceScaleOf(request.Tolerance) * (request.Context == LetterContext.Isolated ? 1.0 : _config.Word.ContextToleranceScale);
        var pairs = new List<PairMetrics>(matched.Count);
        foreach ((int childIndex, int refIndex) in matched)
        {
            pairs.Add(PairMetricsCalculator.Compute(alignments[childIndex, refIndex]!, _config, toleranceScale));
        }

        var matchedChildren = new HashSet<int>(matched.Select(m => m.Child));
        var leftover = new List<ChildStroke>();
        for (int i = 0; i < children.Count; i++)
        {
            if (!matchedChildren.Contains(i))
            {
                leftover.Add(children[i]);
            }
        }

        DeferredOutcome deferred = DeferredMatcher.Assign(leftover, reference, pairs, _config);
        if (deferred.ExtraMain.Count > 0)
        {
            List<Vec2[]> others = [.. deferred.ExtraMain.Select(s => s.NPos)];
            foreach (PairMetrics pair in pairs)
            {
                PairMetricsCalculator.WithCoverage(pair, others, _config, toleranceScale);
                SuppressRetraceUnderExtra(pair, others);
            }
        }

        var matchedRefs = new HashSet<int>(matched.Select(m => m.Ref));
        var missing = new List<RefStroke>();
        for (int j = 0; j < reference.Strokes.Length; j++)
        {
            if (!matchedRefs.Contains(j))
            {
                missing.Add(reference.Strokes[j]);
            }
        }

        PairMetrics? orderViolation = null;
        int runningMax = -1;
        foreach (PairMetrics pair in pairs)
        {
            if (pair.Child.Chrono < runningMax && orderViolation is null)
            {
                orderViolation = pair;
            }

            runningMax = Math.Max(runningMax, pair.Child.Chrono);
        }

        var reachErrors = new List<ReachError>();
        if (pairs.Count > 0)
        {
            foreach (ReachRef check in reference.Reach)
            {
                double actual = check.Extreme == ExtremeKind.Max ? double.NegativeInfinity : double.PositiveInfinity;
                foreach (PairMetrics pair in pairs)
                {
                    foreach (Vec2 p in BodyRawPoints(pair))
                    {
                        actual = check.Extreme == ExtremeKind.Max ? Math.Max(actual, p.Y) : Math.Min(actual, p.Y);
                    }
                }

                reachErrors.Add(new ReachError(check, actual, actual - check.Target));
            }

            // Посадка на строку у любой буквы: нижние точки у касаний baseline (keypoints), а не только reach-проверки baseline.
            int touchHalf = PolylineTools.PointsFor(_config.Form.BaselineTouchWindow, _config.ResampleStep);
            foreach (TouchRef touch in reference.Touches)
            {
                PairMetrics? pair = pairs.Find(p => p.Ref.Id == touch.StrokeId);
                if (pair is null)
                {
                    continue;
                }

                int center = pair.ChildOfRef[touch.Index];
                RefStroke touched = pair.Ref;
                if (Vec2.Distance(pair.Child.NPos[center], touched.Pos[touch.Index]) > _config.Form.BaselineTouchMaxMismatch)
                {
                    continue; // нужной части буквы в этом штрихе нет (например, ствол написан отдельным штрихом)
                }

                double lowest = double.PositiveInfinity;
                for (int i = Math.Max(0, center - touchHalf); i <= Math.Min(pair.Child.Pos.Length - 1, center + touchHalf); i++)
                {
                    lowest = Math.Min(lowest, pair.Child.Pos[i].Y);
                }

                reachErrors.Add(new ReachError(new ReachRef(ExtremeKind.Min, touch.RefMin, true), lowest, lowest - touch.RefMin));
            }
        }

        var analysis = new LetterAnalysis
        {
            Config = _config,
            Reference = reference,
            Style = request.Style,
            ToleranceScale = toleranceScale,
            AcceptPolicy = request.DeferredAccept ?? _config.Deferred.AcceptTiming,
            Children = children,
            Normalization = normalization,
            Pairs = pairs,
            MissingStrokes = missing,
            Deferred = deferred,
            OrderViolation = orderViolation,
            ReachErrors = reachErrors,
        };

        if (IsUnrecognized(analysis))
        {
            return Unrecognized(request, normalization, analysis);
        }

        (double form, ImmutableArray<FormComponent> components) = FormScorer.Score(analysis);
        ImmutableArray<Finding> findings = ContextFindings(request.Context, FindingBuilder.Build(analysis));
        return Assemble(request, analysis, form, components, findings, refinement);
    }

    /// <summary>
    /// Уточнение масштаба и сдвига по соответствиям выравнивания первого прохода (§6.4.2: робастная оценка масштаба и сдвига):
    /// метод наименьших квадратов с отсечением худших соответствий (<see cref="SimilarityFit"/>). Применяется ко всем штрихам
    /// рукописи; возвращает применённое преобразование и величину поправки (0, если поправка мала и ничего не применено).
    /// </summary>
    private (Similarity Fit, double Change) RefineTransform(
        List<ChildStroke> children,
        LetterReference reference,
        List<(int Child, int Ref)> matched,
        PairAlignment?[,] alignments)
    {
        RefineConfig refine = _config.Normalization.Refine;
        if (!refine.Enabled || matched.Count == 0)
        {
            return (Similarity.Identity, 0.0);
        }

        var ink = new List<Vec2>();
        var template = new List<Vec2>();
        foreach ((int childIndex, int refIndex) in matched)
        {
            PairAlignment alignment = alignments[childIndex, refIndex]!;
            IReadOnlyList<DtwCell> path = alignment.Comparison.ReversedIsBetter ? alignment.ReversedPath : alignment.ForwardPath;
            ChildStroke child = children[childIndex];
            RefStroke stroke = reference.Strokes[refIndex];
            foreach (DtwCell cell in path)
            {
                ink.Add(child.NPos[cell.I]);
                template.Add(stroke.Pos[cell.J]);
            }
        }

        Similarity fit = SimilarityFit.Fit(ink, template, refine);
        Vec2 mean = new(ink.Average(p => p.X), ink.Average(p => p.Y));
        Vec2 effect = fit.Apply(mean) - mean;
        double excess = effect.Y - Math.Clamp(effect.Y, -refine.MaxShiftV, refine.MaxShiftV);
        fit = fit with { Translation = new Vec2(fit.Translation.X, fit.Translation.Y - excess) };
        effect = fit.Apply(mean) - mean;
        double change = Math.Max(Math.Abs(fit.Scale - 1.0), effect.Length);
        if (change <= refine.MinChange)
        {
            return (Similarity.Identity, 0.0);
        }

        ChildInk.Transform(children, fit);
        return (fit, change);
    }

    /// <summary>
    /// Исходные точки (после удаления выбросов, без сглаживания) той части штриха, что выровнена с телом эталона: подход
    /// (начатый выше или ниже строки) не считается высотой и посадкой буквы (F4, F5).
    /// </summary>
    private static IEnumerable<Vec2> BodyRawPoints(PairMetrics pair)
    {
        ChildStroke child = pair.Child;
        int n = child.NPos.Length;
        int low = int.MaxValue;
        int high = int.MinValue;
        foreach (DtwCell cell in pair.Path)
        {
            if (cell.J >= pair.Ref.BodyStart && cell.J <= pair.Ref.BodyEnd)
            {
                low = Math.Min(low, cell.I);
                high = Math.Max(high, cell.I);
            }
        }

        if (high < low || n < 2)
        {
            return child.Raw;
        }

        double from = (double)low / (n - 1);
        double to = (double)high / (n - 1);
        return child.Raw.Where((_, i) => child.RawArcFraction[i] >= from && child.RawArcFraction[i] <= to);
    }

    /// <summary>
    /// Буква кусками: если участок ретрейса лежит под лишним штрихом (ствол написан отдельным штрихом, P4), в основном штрихе
    /// ретрейса нет по построению — P6 не выдаётся, остаётся P4.
    /// </summary>
    private void SuppressRetraceUnderExtra(PairMetrics pair, List<Vec2[]> extra)
    {
        if (pair.RetraceShare is null || pair.RetraceRefRange is not { } range)
        {
            return;
        }

        Vec2[] reference = pair.Ref.Pos;
        int covered = 0;
        int total = 0;
        for (int j = range.FromIndex; j <= range.ToIndex && j < reference.Length; j++)
        {
            total++;
            if (extra.Any(e => PolylineMath.DistanceToPolyline(reference[j], e) <= _config.Process.Retrace.Tolerance))
            {
                covered++;
            }
        }

        if (total > 0 && (double)covered / total >= _config.Process.Retrace.CoveredByExtraShare)
        {
            pair.RetraceShare = null;
            pair.RetraceSpan = null;
        }
    }

    private bool IsUnrecognized(LetterAnalysis a)
    {
        ConfidenceConfig c = _config.Confidence;
        if (a.Pairs.Count == 0)
        {
            // Нечего сопоставить: если остались «длинные» штрихи — написано что-то другое, иначе просто нет основного штриха (P5).
            return a.Deferred.ExtraMain.Count > 0;
        }

        return a.WeightedDtwMean > c.UnrecognizedMeanDtw
            || a.TotalCoverage < c.UnrecognizedCoverage
            || a.InkPrecision < c.UnrecognizedInkShare;
    }

    /// <summary>
    /// Находки, применимые в контексте буквы (§6.4.9): внутри слова порядок и число штрихов не судятся (P3–P5), а старт (P1)
    /// не судится у внутренней буквы — её вход задаёт соединение (J3).
    /// </summary>
    private static ImmutableArray<Finding> ContextFindings(LetterContext context, ImmutableArray<Finding> findings)
    {
        if (context == LetterContext.Isolated)
        {
            return findings;
        }

        return
        [
            .. findings.Where(f => f.Code is not (ErrorCode.P3 or ErrorCode.P4 or ErrorCode.P5)
                && !(context == LetterContext.WordInner && f.Code == ErrorCode.P1)),
        ];
    }

    /// <summary>
    /// Результат «не распознано» (U0, §6.4.7): решение без находок и формы, с низкой уверенностью. Числа, по которым принято
    /// решение, остаются в диагностике — по ним видно, почему буква не опознана.
    /// </summary>
    private EvaluationResult Unrecognized(LetterEvaluationRequest request, NormalizationParams normalization, LetterAnalysis? analysis)
    {
        string symbol = request.Glyph.Symbol;
        var summary = new EvaluationSummary(
            Info,
            TargetKind.Letter,
            symbol,
            Decision.Unrecognized,
            symbol,
            Confidence.Low,
            [new LetterSummary(0, symbol, symbol, request.Context, null, 0.0, null, false, ProcessSummary.NotEvaluated)],
            [],
            []);
        ImmutableSortedDictionary<string, double>.Builder diagnostics = ImmutableSortedDictionary.CreateBuilder<string, double>(StringComparer.Ordinal);
        if (analysis is not null)
        {
            AddDiagnostics(diagnostics, analysis);
        }

        var detail = new EvaluationDetail(
            [],
            [],
            [],
            new NormalizationInfo(normalization.ShearDeg, normalization.Scale, normalization.ShiftU, normalization.ShiftV, normalization.InkSlantDeg, normalization.TemplateSlantDeg),
            diagnostics.ToImmutable());
        return new EvaluationResult(summary, detail);
    }

    private static void AddDiagnostics(ImmutableSortedDictionary<string, double>.Builder diagnostics, LetterAnalysis a)
    {
        diagnostics["ink.strokes"] = a.Children.Count;
        diagnostics["ink.precision"] = a.InkPrecision;
        diagnostics["match.pairs"] = a.Pairs.Count;
        diagnostics["match.extraMain"] = a.Deferred.ExtraMain.Count;
        diagnostics["match.extraMarks"] = a.Deferred.ExtraMarks.Count;
        diagnostics["match.missing"] = a.MissingStrokes.Count;
        diagnostics["shape.dtwMean"] = a.WeightedDtwMean;
        diagnostics["shape.frechet"] = a.MaxFrechet;
        diagnostics["shape.corridor"] = a.WeightedCorridor;
        diagnostics["shape.coverage"] = a.TotalCoverage;
        diagnostics["tolerance.scale"] = a.ToleranceScale;
        foreach (ReachError reach in a.ReachErrors)
        {
            diagnostics[$"reach.{(reach.Check.Extreme == ExtremeKind.Max ? "max" : "min")}.{reach.Check.Target.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}"] = reach.Error;
        }
    }

    private EvaluationResult Assemble(
        LetterEvaluationRequest request,
        LetterAnalysis a,
        double form,
        ImmutableArray<FormComponent> components,
        ImmutableArray<Finding> findings,
        Similarity refinement)
    {
        string symbol = request.Glyph.Symbol;
        bool orderOk = a.OrderViolation is null;

        // Нет ни одного сопоставленного основного штриха (например, только точка): процесс не верен (P5).
        bool processOk;
        ProcessSummary process;
        switch (request.Context)
        {
            case LetterContext.WordFirst:
                processOk = a.Pairs.Count > 0 && a.StartOk && a.DirectionOk && a.RetraceOk;
                process = new ProcessSummary(a.StartOk, a.DirectionOk, null, null, a.RetraceOk);
                break;
            case LetterContext.WordInner:
                processOk = a.Pairs.Count > 0 && a.DirectionOk && a.RetraceOk;
                process = new ProcessSummary(null, a.DirectionOk, null, null, a.RetraceOk);
                break;
            default:
                processOk = a.Pairs.Count > 0 && a.StartOk && a.DirectionOk && orderOk && a.StrokeCountDelta < _config.Process.StrokeCount.ExtraForStop;
                process = new ProcessSummary(a.StartOk, a.DirectionOk, orderOk, a.StrokeCountDelta, a.RetraceOk);
                break;
        }

        var deferredSummary = ImmutableArray.CreateBuilder<DeferredSummary>(a.Deferred.Slots.Count);
        var deferredDetail = ImmutableArray.CreateBuilder<DeferredMatch>(a.Deferred.Slots.Count);
        foreach (DeferredAssignment slot in a.Deferred.Slots)
        {
            DeferredStatus status = slot.Stroke is null ? DeferredStatus.Missing : (slot.Near ? DeferredStatus.Present : DeferredStatus.Misplaced);
            deferredSummary.Add(new DeferredSummary(0, slot.Slot.Id, slot.Slot.Kind, status, slot.Timing));
            deferredDetail.Add(new DeferredMatch(slot.Slot.Id, slot.Stroke?.InkIndex, slot.Distance, slot.Timing));
        }

        Confidence confidence = Confidence.High;
        if (a.Pairs.Count == 0)
        {
            confidence = Confidence.Low;
        }
        else if (a.WeightedDtwMean > _config.Confidence.MediumMeanDtw
            || a.Pairs.Any(p => p.Child.Clipped)
            || request.Ink.InterpolatedTime)
        {
            confidence = Confidence.Medium;
        }

        var strokes = ImmutableArray.CreateBuilder<StrokeMatch>(a.Pairs.Count);
        foreach (PairMetrics p in a.Pairs)
        {
            strokes.Add(new StrokeMatch(
                p.Ref.Id,
                p.Child.InkIndex,
                p.Reversed,
                p.MatchCost,
                p.StartErr,
                p.StartArcOffset,
                p.ReverseRatio,
                p.FirstSegmentAngleDeg,
                p.DtwMean,
                p.DtwMax,
                p.Frechet,
                p.CorridorShare,
                p.Coverage,
                p.ClosedGap,
                p.LoopOk,
                p.RetraceShare,
                p.StartOk,
                p.DirectionOk,
                p.RangeAreaRatio));
        }

        ImmutableSortedDictionary<string, double>.Builder diagnostics = ImmutableSortedDictionary.CreateBuilder<string, double>(StringComparer.Ordinal);
        AddDiagnostics(diagnostics, a);

        NormalizationParams n = a.Normalization;
        var summary = new EvaluationSummary(
            Info,
            TargetKind.Letter,
            symbol,
            Decision.Target,
            symbol,
            confidence,
            [new LetterSummary(0, symbol, symbol, request.Context, null, form, processOk, true, process)],
            deferredSummary.MoveToImmutable(),
            findings);
        var detail = new EvaluationDetail(
            strokes.MoveToImmutable(),
            deferredDetail.MoveToImmutable(),
            components,
            new NormalizationInfo(n.ShearDeg, n.Scale / refinement.Scale, n.ShiftU + refinement.Translation.X, n.ShiftV + refinement.Translation.Y, n.InkSlantDeg, n.TemplateSlantDeg),
            diagnostics.ToImmutable());
        return new EvaluationResult(summary, detail);
    }
}
