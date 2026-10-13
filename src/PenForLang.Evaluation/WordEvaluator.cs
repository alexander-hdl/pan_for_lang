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
/// Запрос оценки слова (ARCHITECTURE.md §6.2, §6.4.4–6.4.8). Неизменяем. Числа оценки берутся из <see cref="ScoringConfig"/>,
/// с которой создан оценщик.
/// </summary>
/// <param name="Ink">Попытка в системе линейки (<see cref="RulerProjection.Project"/>).</param>
/// <param name="Word">Эталон слова (<see cref="WordTemplateBuilder"/>).</param>
/// <param name="Tolerance">Профиль допусков формы.</param>
/// <param name="DeferredAccept">Какое время отложенных штрихов принимается; <c>null</c> — значение из <c>scoring.json</c>.</param>
public sealed record WordEvaluationRequest(
    RulerInk Ink,
    WordTemplate Word,
    ToleranceProfile Tolerance = ToleranceProfile.Learning,
    DeferredAcceptPolicy? DeferredAccept = null);

/// <summary>
/// Оценщик слова (ARCHITECTURE.md §6.4.4–6.4.9). Чистая функция от (запрос, снимок конфигурации); потокобезопасна.
/// </summary>
public interface IWordEvaluator
{
    /// <summary>Версия оценщика и хеш конфигурации.</summary>
    EvaluatorInfo Info { get; }

    /// <summary>
    /// Оценивает попытку написать слово: выравнивание всех основных штрихов с эталоном слова (соединение — альтернатива
    /// «геометрия или отрыв»), границы букв и их уверенность, отложенные штрихи и ожидаемые отрывы, локальная оценка каждой
    /// буквы и соединения.
    /// </summary>
    /// <param name="request">Запрос.</param>
    /// <exception cref="ArgumentNullException">Запрос или его части не заданы.</exception>
    EvaluationResult Evaluate(WordEvaluationRequest request);
}

/// <summary>
/// Оценщик слова v0 (V0.10). Конвейер:
/// <list type="number">
/// <item>предобработка и нормализация рукописи — как у буквы (§6.4.1–6.4.2), к сводке эталона слова;</item>
/// <item>основной след: штрихи, не похожие на точку или короткую перекладину; остальное — кандидаты в отложенные (§6.4.3);</item>
/// <item>выравнивание <see cref="TargetAligner"/>: DTW по цели с мягкой полосой по x; отрыв пера в диапазоне соединения —
/// альтернатива геометрии (A03), штраф по месту; уточнение масштаба и сдвига по соответствиям;</item>
/// <item>сегментация: границы тел (<c>BodyEnd_k</c>, <c>BodyStart_{k+1}</c>) и их уверенность — рост стоимости пути при
/// сдвиге границы на <c>word.boundaries.shiftU</c> (прямой и обратный проход);</item>
/// <item>отложенные штрихи: назначение по реальному стволу, время (D4); отрыв, рядом с которым поставлен штрих «сразу»,
/// помечается <c>LiftExpected</c> — J1 на нём нет (§6.4.8);</item>
/// <item>соединения J1–J4, локальная оценка каждой буквы на её участке (<see cref="LetterEvaluator"/> в контексте слова).</item>
/// </list>
/// </summary>
public sealed class WordEvaluator : IWordEvaluator
{
    private readonly ScoringConfig _config;
    private readonly SavitzkyGolayFilter _filter;
    private readonly LetterEvaluator _letters;
    private readonly ConditionalWeakTable<WordTemplate, WordReference> _references = new();

    /// <summary>Создаёт оценщик со снимком конфигурации.</summary>
    /// <param name="config">Конфигурация (<see cref="ScoringConfigLoader.Load"/>).</param>
    public WordEvaluator(ScoringConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _filter = new SavitzkyGolayFilter(config.Smoothing.Window, config.Smoothing.Order);
        _letters = new LetterEvaluator(config);
        Info = new EvaluatorInfo(EvaluatorVersion.Current, config.ConfigHash);
    }

    /// <inheritdoc />
    public EvaluatorInfo Info { get; }

    /// <summary>Конфигурация, с которой создан оценщик.</summary>
    public ScoringConfig Config => _config;

    /// <summary>Загружает <c>config/scoring.json</c> из источника контента и создаёт оценщик.</summary>
    /// <param name="source">Источник контента.</param>
    /// <param name="path">Относительный путь файла конфигурации.</param>
    public static WordEvaluator Create(IContentSource source, string path = ScoringConfig.DefaultPath) =>
        new(ScoringConfigLoader.Load(source, path));

    /// <inheritdoc />
    public EvaluationResult Evaluate(WordEvaluationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Ink);
        ArgumentNullException.ThrowIfNull(request.Word);
        WordReference reference = _references.GetValue(request.Word, t => WordReference.Build(t, _config, _filter));
        return new WordRun(this, request, reference).Execute();
    }

    /// <summary>Состояние одной оценки.</summary>
    private sealed class WordRun
    {
        private readonly WordEvaluator _owner;
        private readonly ScoringConfig _config;
        private readonly WordEvaluationRequest _request;
        private readonly WordReference _reference;
        private readonly WordTemplate _word;
        private List<ChildStroke> _children = [];
        private List<ChildStroke> _main = [];
        private List<ChildStroke> _marks = [];
        private MainInk _ink = null!;
        private Alignment _alignment = null!;
        private double _toleranceScale;

        public WordRun(WordEvaluator owner, WordEvaluationRequest request, WordReference reference)
        {
            _owner = owner;
            _config = owner._config;
            _request = request;
            _reference = reference;
            _word = request.Word;
        }

        public EvaluationResult Execute()
        {
            _toleranceScale = _config.ToleranceScaleOf(_request.Tolerance);
            _children = ChildInk.Prepare(_request.Ink, _reference, _config, _owner._filter);
            if (_children.Count == 0)
            {
                return Unrecognized(new NormalizationParams(0.0, 1.0, 0.0, 0.0, null, _reference.SlantDeg), null);
            }

            NormalizationParams normalization = ChildInk.Normalize(_children, _reference, _config);
            foreach (ChildStroke stroke in _children)
            {
                (IsMark(stroke) ? _marks : _main).Add(stroke);
            }

            if (_main.Count == 0)
            {
                return Unrecognized(normalization, null);
            }

            Similarity centering = CenterOnTemplate();
            Similarity refinement = AlignWithRefinement();
            refinement = new Similarity(refinement.Scale * centering.Scale, (centering.Translation * refinement.Scale) + refinement.Translation);
            AlignmentStats stats = Measure();
            if (stats.MeanDtw > _config.Word.Unrecognized.MeanDtw
                || stats.Coverage < _config.Word.Unrecognized.Coverage
                || stats.InkShare < _config.Word.Unrecognized.InkShare)
            {
                return Unrecognized(normalization, stats);
            }

            return Assemble(normalization, refinement, stats);
        }

        /// <summary>
        /// Начальный сдвиг по u: середина размаха основного следа — на середину размаха эталона. В отличие от центра тяжести
        /// ведущих штрихов (нормализация буквы) не зависит от того, нарисованы ли соединения: рукопись с отрывами и пропущенными
        /// соединениями короче эталона, и сдвиг по центру тяжести выводил её из полосы по x.
        /// </summary>
        private Similarity CenterOnTemplate()
        {
            double min = _main.Min(s => s.NPos.Min(p => p.X));
            double max = _main.Max(s => s.NPos.Max(p => p.X));
            var shift = new Similarity(1.0, new Vec2(_reference.SpanCenterU - ((min + max) / 2.0), 0.0));
            ChildInk.Transform(_children, shift);
            return shift;
        }

        private bool IsMark(ChildStroke stroke) => DeferredMatcher.IsMark(stroke, _config.Deferred);

        // ---------- Выравнивание ----------

        private AlignProblem BuildProblem()
        {
            _ink = MainInk.Build(_main);
            return new AlignProblem
            {
                InkPos = _ink.Pos,
                InkDir = _ink.Dir,
                LiftBefore = _ink.LiftBefore,
                TplPos = _reference.Pos,
                TplDir = _reference.Dir,
                Zone = _reference.Zone,
                SkipPrefix = _reference.SkipPrefix,
                Intervals = [.. _reference.Intervals.Select(ExtendedInterval)],
                OpenBeginMax = _reference.OpenBeginMax,
                OpenEndMin = _reference.OpenEndMin,
            };
        }

        /// <summary>
        /// Диапазон отрыва, расширенный на допуск <c>joinSlackU</c> внутрь тел соседних букв, но не больше четверти тела
        /// (отрыв посреди короткой буквы — это отрыв внутри буквы, а не на соединении).
        /// </summary>
        private (int Lo, int Hi, bool Expected) ExtendedInterval(JoinInterval interval)
        {
            int slack = (int)Math.Round(_config.Word.Aligner.JoinSlackU / _config.ResampleStep);
            int leftLetter = interval.Letter;
            int rightLetter = Math.Min(_word.Letters.Length - 1, interval.Join >= 0 ? interval.Join + 1 : interval.Letter);
            int leftBody = _reference.BodyEnd[leftLetter] - _reference.BodyStart[leftLetter];
            int rightBody = _reference.BodyEnd[rightLetter] - _reference.BodyStart[rightLetter];
            int lo = interval.Lo - Math.Min(slack, leftBody / 4);
            int hi = interval.Hi + Math.Min(slack, rightBody / 4);
            return (Math.Max(0, lo), Math.Min(_reference.Count - 1, hi), interval.Expected);
        }

        /// <summary>Выравнивание и раунды «подгонка масштаба и сдвига — повторное выравнивание» (§6.4.2).</summary>
        private Similarity AlignWithRefinement()
        {
            Similarity total = Similarity.Identity;
            _alignment = TargetAligner.Align(BuildProblem(), _config);
            RefineConfig refine = _config.Normalization.Refine;
            for (int round = 0; refine.Enabled && round < refine.Rounds; round++)
            {
                (Similarity fit, double change) = RefineTransform(refine);
                if (change == 0.0)
                {
                    break;
                }

                total = new Similarity(fit.Scale * total.Scale, (total.Translation * fit.Scale) + fit.Translation);
                ChildInk.Transform(_children, fit);
                _alignment = TargetAligner.Align(BuildProblem(), _config);
                if (change <= refine.RerunAbove)
                {
                    break;
                }
            }

            return total;
        }

        private (Similarity Fit, double Change) RefineTransform(RefineConfig refine)
        {
            var ink = new List<Vec2>(_alignment.Path.Count);
            var template = new List<Vec2>(_alignment.Path.Count);
            foreach (PathCell cell in _alignment.Path)
            {
                if (_reference.Zone[cell.J] == ZoneKind.Connector)
                {
                    continue;
                }

                ink.Add(_ink.Pos[cell.I]);
                template.Add(_reference.Pos[cell.J]);
            }

            if (ink.Count == 0)
            {
                return (Similarity.Identity, 0.0);
            }

            Similarity fit = SimilarityFit.Fit(ink, template, refine);
            Vec2 mean = new(ink.Average(p => p.X), ink.Average(p => p.Y));
            Vec2 effect = fit.Apply(mean) - mean;
            double excess = effect.Y - Math.Clamp(effect.Y, -refine.MaxShiftV, refine.MaxShiftV);
            fit = fit with { Translation = new Vec2(fit.Translation.X, fit.Translation.Y - excess) };
            effect = fit.Apply(mean) - mean;
            double change = Math.Max(Math.Abs(fit.Scale - 1.0), effect.Length);
            return change <= refine.MinChange ? (Similarity.Identity, 0.0) : (fit, change);
        }

        private sealed record AlignmentStats(double MeanDtw, double Coverage, double InkShare);

        private AlignmentStats Measure()
        {
            double sum = 0.0;
            int counted = 0;
            for (int i = 0; i < _ink.Count; i++)
            {
                int j = _alignment.TemplateOfInk[i];
                if (j < 0 || _reference.Zone[j] == ZoneKind.Connector)
                {
                    continue;
                }

                sum += Vec2.Distance(_ink.Pos[i], _reference.Pos[j]);
                counted++;
            }

            double tau = _config.Form.CorridorTau * _toleranceScale * 2.0;
            var strokes = _main.Select(s => s.NPos).ToList();
            int body = 0;
            int covered = 0;
            for (int j = 0; j < _reference.Count; j++)
            {
                if (_reference.Zone[j] != ZoneKind.Body)
                {
                    continue;
                }

                body++;
                foreach (Vec2[] stroke in strokes)
                {
                    if (PolylineMath.DistanceToPolyline(_reference.Pos[j], stroke) <= tau)
                    {
                        covered++;
                        break;
                    }
                }
            }

            int inkTotal = 0;
            int inkInside = 0;
            foreach (Vec2 point in _ink.Pos)
            {
                inkTotal++;
                if (PolylineMath.DistanceToPolyline(point, _reference.Pos) <= tau)
                {
                    inkInside++;
                }
            }

            return new AlignmentStats(
                counted == 0 ? double.PositiveInfinity : sum / counted,
                body == 0 ? 0.0 : (double)covered / body,
                inkTotal == 0 ? 1.0 : (double)inkInside / inkTotal);
        }

        // ---------- Сегментация ----------

        private sealed record BoundaryPos(int Index, bool Crisp);

        private (BoundaryPos End, BoundaryPos Start)[] FindBoundaries()
        {
            int letters = _word.Letters.Length;
            var result = new (BoundaryPos, BoundaryPos)[Math.Max(0, letters - 1)];
            for (int k = 0; k + 1 < letters; k++)
            {
                int be = _reference.BodyEnd[k];
                int bs = _reference.BodyStart[k + 1];
                int endIndex = _alignment.InkOfTemplate[be];
                if (endIndex < 0)
                {
                    endIndex = -1;
                    foreach (PathCell cell in _alignment.Path)
                    {
                        if (cell.J <= be)
                        {
                            endIndex = Math.Max(endIndex, cell.I);
                        }
                    }

                    endIndex = Math.Max(endIndex, 0);
                }

                int startIndex = _alignment.InkOfTemplate[bs];
                if (startIndex < 0)
                {
                    startIndex = _ink.Count - 1;
                    foreach (PathCell cell in _alignment.Path)
                    {
                        if (cell.J >= bs)
                        {
                            startIndex = Math.Min(startIndex, cell.I);
                        }
                    }
                }

                // Отрыв пера у конца тела: тело кончается там, где оторвано перо — на гладком продолжении тела соединением
                // положение границы по форме неопределённо.
                int snap = (int)Math.Round(_config.Word.Boundaries.LiftSnapU / _config.ResampleStep);
                foreach (LiftEvent lift in _alignment.Lifts)
                {
                    if (lift.Interval < 0 || _reference.Intervals[lift.Interval].Join != k)
                    {
                        continue;
                    }

                    if (lift.FromJ <= be + snap)
                    {
                        endIndex = lift.I - 1;
                    }

                }

                startIndex = Math.Max(startIndex, endIndex);
                result[k] = (new BoundaryPos(endIndex, _ink.IsStrokeEnd(endIndex)), new BoundaryPos(startIndex, _ink.IsStrokeStart(startIndex)));
            }

            return result;
        }

        /// <summary>Рост стоимости пути при сдвиге границы на <c>shiftU</c> в обе стороны (меньшее из двух).</summary>
        private double BoundaryRise(DpTable? backward, int index, int templateIndex)
        {
            if (backward is null)
            {
                return double.PositiveInfinity;
            }

            double shift = _config.Word.Boundaries.ShiftU;
            int before = index;
            while (before > 0 && _ink.Arc[index] - _ink.Arc[before] < shift)
            {
                before--;
            }

            int after = index;
            while (after + 1 < _ink.Count && _ink.Arc[after] - _ink.Arc[index] < shift)
            {
                after++;
            }

            double riseBefore = before == index ? double.PositiveInfinity : TargetAligner.Rise(_alignment, backward, before, templateIndex);
            double riseAfter = after == index ? double.PositiveInfinity : TargetAligner.Rise(_alignment, backward, after, templateIndex);
            return Math.Min(riseBefore, riseAfter);
        }

        // ---------- Сборка результата ----------

        private EvaluationResult Assemble(NormalizationParams normalization, Similarity refinement, AlignmentStats stats)
        {
            int letters = _word.Letters.Length;
            (BoundaryPos End, BoundaryPos Start)[] boundaries = FindBoundaries();
            DpTable? backward = boundaries.Length == 0 ? null : new DpTable(_alignment.Forward.Problem.Reversed(), _config.Word.Aligner, _config.Dtw);

            // Диапазоны рукописи по буквам (глобальные индексы точек основного следа).
            var inkRange = new (int From, int To)[letters];
            for (int k = 0; k < letters; k++)
            {
                int from = k == 0 ? 0 : boundaries[k - 1].Start.Index;
                int to = k + 1 == letters ? _ink.Count - 1 : boundaries[k].End.Index;
                inkRange[k] = (from, Math.Max(from, to));
            }

            var findings = new List<Finding>();

            // Отложенные штрихи и ожидаемые отрывы (§6.4.8).
            DeferredOutcome deferred = AssignDeferred(inkRange);
            DeferredAcceptPolicy policy = _request.DeferredAccept ?? _config.Deferred.AcceptTiming;
            FindingBuilder.AddDeferredFindings(
                deferred,
                _config,
                policy,
                slot => _reference.Slots.First(s => ReferenceEquals(s.Slot, slot.Slot)).Letter,
                mark => NearestLetterSpan(mark, inkRange),
                findings);

            // Отрывы основного следа.
            var immediateMarks = deferred.Slots.Where(s => s.Stroke is not null && s.Timing == DeferredTiming.Immediate).Select(s => s.Stroke!).ToList();
            var lifts = ClassifyLifts(boundaries, immediateMarks, out int liftsInsideLetter);

            // Соединения.
            var joinMetrics = ImmutableArray.CreateBuilder<JoinMetrics>();
            var joinSummaries = ImmutableArray.CreateBuilder<JoinSummary>();
            var boundaryDetail = ImmutableArray.CreateBuilder<WordBoundary>();
            var entryErrors = new double[Math.Max(0, letters - 1)];
            for (int k = 0; k + 1 < letters; k++)
            {
                (BoundaryPos end, BoundaryPos start) = boundaries[k];
                double endRise = end.Crisp ? double.PositiveInfinity : BoundaryRise(backward, end.Index, _reference.BodyEnd[k]);
                double startRise = start.Crisp ? double.PositiveInfinity : BoundaryRise(backward, start.Index, _reference.BodyStart[k + 1]);
                double minRise = _config.Word.Boundaries.MinRiseCost;
                boundaryDetail.Add(new WordBoundary(
                    k,
                    _ink.RawPoint(end.Index),
                    _ink.RawPoint(start.Index),
                    lifts.Any(l => l.Join == k),
                    endRise >= minRise,
                    startRise >= minRise,
                    endRise,
                    startRise));
                JoinMetrics metrics = MeasureJoin(k, end.Index, start.Index, lifts);
                entryErrors[k] = metrics.EntryErr;
                joinMetrics.Add(metrics);
                joinSummaries.Add(new JoinSummary(
                    k,
                    k,
                    k + 1,
                    _word.Joins[k].Family,
                    _word.Joins[k].Tall,
                    metrics.Present,
                    metrics.Lifted && metrics.LiftExpected,
                    JoinScore(metrics, _word.Joins[k], findings, k)));
            }

            AddJoinFindings(joinMetrics, findings);
            AddRetraceFindings(inkRange, findings);

            // Локальная оценка букв.
            var letterResults = ImmutableArray.CreateBuilder<EvaluationResult>(letters);
            var letterSummaries = ImmutableArray.CreateBuilder<LetterSummary>(letters);
            for (int k = 0; k < letters; k++)
            {
                EvaluationResult result = EvaluateLetter(k, inkRange[k]);
                letterResults.Add(result);
                LetterSummary local = result.Summary.Letters[0];
                bool confident = (k == 0 || boundaryDetail[k - 1].StartConfident) && (k + 1 == letters || boundaryDetail[k].EndConfident);
                bool? processOk = local.ProcessOk;
                if (k > 0 && processOk is not null)
                {
                    processOk = confident ? processOk.Value && entryErrors[k - 1] <= _config.Joins.EntryErr : null;
                }

                letterSummaries.Add(local with
                {
                    Index = k,
                    Context = k == 0 ? LetterContext.WordFirst : LetterContext.WordInner,
                    ProcessOk = processOk,
                    SegmentationConfident = confident && result.Summary.Decision != Decision.Unrecognized,
                });
                foreach (Finding f in result.Summary.Findings)
                {
                    findings.Add(f with { Where = f.Where with { LetterIndex = k } });
                }
            }

            findings.Sort(FindingBuilder.Compare);

            var deferredSummary = ImmutableArray.CreateBuilder<DeferredSummary>(deferred.Slots.Count);
            var deferredDetail = ImmutableArray.CreateBuilder<DeferredMatch>(deferred.Slots.Count);
            foreach (DeferredAssignment slot in deferred.Slots)
            {
                int letter = _reference.Slots.First(s => ReferenceEquals(s.Slot, slot.Slot)).Letter;
                DeferredStatus status = slot.Stroke is null ? DeferredStatus.Missing : (slot.Near ? DeferredStatus.Present : DeferredStatus.Misplaced);
                deferredSummary.Add(new DeferredSummary(letter, slot.Slot.Id, slot.Slot.Kind, status, slot.Timing));
                deferredDetail.Add(new DeferredMatch(slot.Slot.Id, slot.Stroke?.InkIndex, slot.Distance, slot.Timing));
            }

            Confidence confidence = Confidence.High;
            if (stats.MeanDtw > _config.Word.Unrecognized.MediumMeanDtw
                || boundaryDetail.Any(b => !b.EndConfident || !b.StartConfident)
                || _main.Any(s => s.Clipped)
                || _request.Ink.InterpolatedTime)
            {
                confidence = Confidence.Medium;
            }

            if (letterResults.Any(r => r.Summary.Decision == Decision.Unrecognized))
            {
                confidence = Confidence.Low;
            }

            ImmutableSortedDictionary<string, double>.Builder diagnostics = ImmutableSortedDictionary.CreateBuilder<string, double>(StringComparer.Ordinal);
            diagnostics["align.costPerPoint"] = _alignment.CostPerInkPoint;
            diagnostics["align.meanDtw"] = stats.MeanDtw;
            diagnostics["align.coverage"] = stats.Coverage;
            diagnostics["align.inkShare"] = stats.InkShare;
            diagnostics["ink.main"] = _main.Count;
            diagnostics["ink.marks"] = _marks.Count;
            diagnostics["lifts.total"] = lifts.Count;
            diagnostics["lifts.insideLetter"] = liftsInsideLetter;
            diagnostics["tolerance.scale"] = _toleranceScale;
            diagnostics["ink.points"] = _ink.Count;
            diagnostics["tpl.points"] = _reference.Count;
            for (int k = 0; k < letters; k++)
            {
                diagnostics[FormattableString.Invariant($"tpl.body{k}.from")] = _reference.BodyStart[k];
                diagnostics[FormattableString.Invariant($"tpl.body{k}.to")] = _reference.BodyEnd[k];
            }

            for (int q = 0; q < _alignment.Lifts.Count; q++)
            {
                LiftEvent lift = _alignment.Lifts[q];
                diagnostics[FormattableString.Invariant($"lift{q}.ink")] = lift.I;
                diagnostics[FormattableString.Invariant($"lift{q}.fromJ")] = lift.FromJ;
                diagnostics[FormattableString.Invariant($"lift{q}.toJ")] = lift.ToJ;
                diagnostics[FormattableString.Invariant($"lift{q}.interval")] = lift.Interval;
            }

            var summary = new EvaluationSummary(
                _owner.Info,
                TargetKind.Word,
                _word.Text,
                Decision.Target,
                _word.Text,
                confidence,
                letterSummaries.MoveToImmutable(),
                deferredSummary.MoveToImmutable(),
                [.. findings])
            {
                Joins = joinSummaries.ToImmutable(),
            };
            NormalizationParams n = normalization;
            var detail = new EvaluationDetail(
                [],
                deferredDetail.MoveToImmutable(),
                [],
                new NormalizationInfo(n.ShearDeg, n.Scale / refinement.Scale, n.ShiftU + refinement.Translation.X, n.ShiftV + refinement.Translation.Y, n.InkSlantDeg, n.TemplateSlantDeg),
                diagnostics.ToImmutable())
            {
                Word = new WordDetail(
                    boundaryDetail.ToImmutable(),
                    joinMetrics.ToImmutable(),
                    [.. lifts.Select(l => new WordLift(_ink.InkIndexOf(l.Event.I), l.Join, l.Letter, l.Expected))],
                    letterResults.ToImmutable(),
                    _alignment.CostPerInkPoint,
                    liftsInsideLetter),
            };
            return new EvaluationResult(summary, detail);
        }

        // ---------- Отложенные штрихи ----------

        private DeferredOutcome AssignDeferred((int From, int To)[] inkRange)
        {
            int lastMainChrono = _main.Max(s => s.Chrono);
            ImmutableArray<WordSlot> slots = _reference.Slots;
            return DeferredMatcher.Assign(
                _marks,
                [.. slots.Select(s => s.Slot)],
                j =>
                {
                    WordSlot slot = slots[j];
                    int g = NearestInkOfTemplate(slot.AnchorIndex);
                    (int from, int to) = inkRange[slot.Letter];
                    var stem = new List<Vec2>(to - from + 1);
                    for (int i = from; i <= to; i++)
                    {
                        stem.Add(_ink.Pos[i]);
                    }

                    return g < 0 || stem.Count == 0 ? null : new DeferredAnchor(_ink.Pos[g] + slot.Slot.AnchorOffset, stem);
                },
                lastMainChrono,
                _config);
        }

        private int NearestInkOfTemplate(int j)
        {
            for (int d = 0; d < _reference.Count; d++)
            {
                if (j - d >= 0 && _alignment.InkOfTemplate[j - d] >= 0)
                {
                    return _alignment.InkOfTemplate[j - d];
                }

                if (j + d < _reference.Count && _alignment.InkOfTemplate[j + d] >= 0)
                {
                    return _alignment.InkOfTemplate[j + d];
                }
            }

            return -1;
        }

        private (int Letter, TemplateSpan Span) NearestLetterSpan(ChildStroke mark, (int From, int To)[] inkRange)
        {
            Vec2 center = mark.NCenter;
            int best = 0;
            double bestDistance = double.PositiveInfinity;
            for (int k = 0; k < inkRange.Length; k++)
            {
                Vec2[] points = [.. Enumerable.Range(inkRange[k].From, inkRange[k].To - inkRange[k].From + 1).Select(i => _ink.Pos[i])];
                double d = PolylineMath.DistanceToPolyline(center, points);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = k;
                }
            }

            string? stroke = _word.Letters[best].Glyph.Strokes.OrderBy(s => s.Order).FirstOrDefault()?.Id;
            return (best, stroke is null ? TemplateSpan.Whole : new TemplateSpan(stroke, TemplatePart.Body, 0.0, 1.0));
        }

        // ---------- Отрывы и соединения ----------

        private sealed record ClassifiedLift(LiftEvent Event, int Join, int Letter, bool Expected);

        private List<ClassifiedLift> ClassifyLifts(
            (BoundaryPos End, BoundaryPos Start)[] boundaries,
            List<ChildStroke> immediateMarks,
            out int insideLetter)
        {
            _ = boundaries;
            var result = new List<ClassifiedLift>(_alignment.Lifts.Count);
            insideLetter = 0;
            foreach (LiftEvent lift in _alignment.Lifts)
            {
                int join = -1;
                int letter = -1;
                bool templateExpected = false;
                if (lift.Interval >= 0)
                {
                    JoinInterval interval = _reference.Intervals[lift.Interval];
                    join = interval.Join;
                    letter = interval.Join < 0 ? interval.Letter : -1;
                    templateExpected = interval.Expected;
                }
                else
                {
                    foreach (JoinInterval interval in _reference.Intervals)
                    {
                        if (interval.Join >= 0 && lift.FromJ >= interval.Lo && lift.FromJ <= interval.Hi)
                        {
                            join = interval.Join;
                            templateExpected = interval.Expected;
                            break;
                        }
                    }

                    if (join < 0)
                    {
                        letter = LetterOfTemplateIndex(lift.FromJ);
                    }
                }

                ChildStroke previous = _main[_ink.StrokeOf(lift.I - 1)];
                ChildStroke next = _main[_ink.StrokeOf(lift.I)];
                bool byMark = immediateMarks.Any(m => m.StartUs >= previous.EndUs && m.EndUs <= next.StartUs);
                bool expected = templateExpected || byMark;
                if (join < 0 && !expected)
                {
                    insideLetter++;
                }

                result.Add(new ClassifiedLift(lift, join, letter, expected));
            }

            return result;
        }

        private int LetterOfTemplateIndex(int j)
        {
            int best = 0;
            int bestDistance = int.MaxValue;
            for (int k = 0; k < _word.Letters.Length; k++)
            {
                int d = j < _reference.BodyStart[k] ? _reference.BodyStart[k] - j : (j > _reference.BodyEnd[k] ? j - _reference.BodyEnd[k] : 0);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = k;
                }
            }

            return best;
        }

        private JoinMetrics MeasureJoin(int k, int endIndex, int startIndex, List<ClassifiedLift> lifts)
        {
            JoinInterval interval = _reference.IntervalOf(k);
            List<ClassifiedLift> own = [.. lifts.Where(l => l.Join == k)];
            bool lifted = own.Count > 0;
            bool expected = lifted && own.All(l => l.Expected);

            int inside = Math.Max(0, interval.Hi - interval.Lo - 1);
            int covered = 0;
            for (int j = interval.Lo + 1; j < interval.Hi; j++)
            {
                if (_alignment.InkOfTemplate[j] >= 0)
                {
                    covered++;
                }
            }

            double coverage = inside == 0 ? 1.0 : (double)covered / inside;
            bool drawn = coverage >= _config.Joins.DrawnShare;
            double? minV = null;
            double? cornerExcess = null;
            int loops = 0;
            if (drawn)
            {
                double min = double.PositiveInfinity;
                for (int i = endIndex; i <= startIndex; i++)
                {
                    min = Math.Min(min, _ink.Pos[i].Y);
                }

                minV = min;
                if (!lifted && interval.Join >= 0)
                {
                    (cornerExcess, loops) = CornersAndLoops(interval, endIndex, startIndex);
                }
            }

            double entryErr = Vec2.Distance(_ink.Pos[startIndex], _reference.Pos[_reference.BodyStart[k + 1]]);
            return new JoinMetrics(k, lifted, expected, drawn, coverage, minV, entryErr, cornerExcess, loops);
        }

        /// <summary>Наибольший поворот внутри соединения рукописи сверх эталонного и число петель (J4); концы соединения не судятся.</summary>
        private (double? Excess, int Loops) CornersAndLoops(JoinInterval interval, int endIndex, int startIndex)
        {
            JoinCornerConfig corner = _config.Joins.Corner;
            Vec2[] ink = [.. Enumerable.Range(endIndex, startIndex - endIndex + 1).Select(i => _ink.Pos[i])];
            Vec2[] tpl = _reference.Pos[interval.Lo..(interval.Hi + 1)];
            double inkStep = ink.Length < 2 ? _config.ResampleStep : Math.Max(1e-6, PolylineMath.Length(ink) / (ink.Length - 1));
            double? inkTurn = MaxTurn(ink, inkStep, corner);
            double? tplTurn = MaxTurn(tpl, _config.ResampleStep, corner);
            double? excess = inkTurn is null ? null : inkTurn.Value - (tplTurn ?? 0.0);

            var found = new List<PolylineLoop>();
            if (ink.Length >= 4)
            {
                SelfIntersections.FindLoops(ink, found);
            }

            int loops = found.Count(l => l.Area >= _config.Joins.MinLoopArea);
            return (excess, loops);
        }

        private static double? MaxTurn(Vec2[] points, double step, JoinCornerConfig corner)
        {
            int w = Math.Max(1, (int)Math.Round(corner.WindowU / step));
            int margin = (int)Math.Round(corner.EdgeMarginU / step);
            double best = 0.0;
            bool any = false;
            for (int i = margin + w; i + w < points.Length - margin; i++)
            {
                Vec2 before = points[i] - points[i - w];
                Vec2 after = points[i + w] - points[i];
                if (before.Length < 1e-9 || after.Length < 1e-9)
                {
                    continue;
                }

                double cos = Math.Clamp(Vec2.Dot(before.Normalized(), after.Normalized()), -1.0, 1.0);
                best = Math.Max(best, Math.Acos(cos) * 180.0 / Math.PI);
                any = true;
            }

            return any ? best : null;
        }

        private double JoinScore(JoinMetrics m, WordJoin join, List<Finding> findings, int k)
        {
            _ = findings;
            if (!m.Present)
            {
                return 0.0;
            }

            bool bad = (join.High && m.Drawn && m.MinV is { } v && v < _config.Joins.HighMinHeight)
                || m.EntryErr > _config.Joins.EntryErr
                || m.CornerExcessDeg > _config.Joins.Corner.ExcessDeg
                || m.Loops > 0;
            _ = k;
            return bad ? 0.5 : 1.0;
        }

        private void AddJoinFindings(ImmutableArray<JoinMetrics>.Builder joins, List<Finding> findings)
        {
            FindingsConfig f = _config.Findings;
            JoinsConfig j = _config.Joins;
            foreach (JoinMetrics m in joins)
            {
                WordJoin join = _word.Joins[m.Join];
                var where = new FindingLocation(FindingPlace.Join, m.Join, $"join{m.Join}", null);
                var span = new TemplateSpan($"join{m.Join}", TemplatePart.Join, 0.0, 1.0);
                if (!m.Present)
                {
                    findings.Add(new Finding(ErrorCode.J1, where, FindingBuilder.Fixed(f, ErrorCode.J1), span, FindingKind.Error, 1.0, 0.0));
                }

                if (join.High && m.Drawn && m.MinV is { } minV && minV < j.HighMinHeight)
                {
                    findings.Add(new Finding(ErrorCode.J2, where, FindingBuilder.Severity(f, j.HighMinHeight - minV + j.HighMinHeight, j.HighMinHeight), span, FindingKind.Error, minV, j.HighMinHeight));
                }

                if (m.EntryErr > j.EntryErr * _toleranceScale)
                {
                    double threshold = j.EntryErr * _toleranceScale;
                    findings.Add(new Finding(ErrorCode.J3, where, FindingBuilder.Severity(f, m.EntryErr, threshold), span, FindingKind.Error, m.EntryErr, threshold));
                }

                if (m.CornerExcessDeg > j.Corner.ExcessDeg || m.Loops > 0)
                {
                    double measured = m.Loops > 0 ? m.Loops : m.CornerExcessDeg ?? 0.0;
                    double threshold = m.Loops > 0 ? 0.0 : j.Corner.ExcessDeg;
                    findings.Add(new Finding(ErrorCode.J4, where, m.Loops > 0 ? 1.0 : FindingBuilder.Severity(f, measured, threshold), span, FindingKind.Error, measured, threshold));
                }
            }
        }

        /// <summary>
        /// Ретрейс по соединению (§3.2.4: «включая соединение»): начало тела внутренней буквы повторяет подход; проверяется,
        /// только если соединение нарисовано (иначе повторять нечего), так же, как P6 буквы.
        /// </summary>
        private void AddRetraceFindings((int From, int To)[] inkRange, List<Finding> findings)
        {
            _ = inkRange;
            RetraceConfig retrace = _config.Process.Retrace;
            Vec2[] all = _ink.Pos;
            foreach (WordRetrace r in _reference.Retrace)
            {
                if (r.Letter == 0 || !r.StartsBody || r.RefShare is not { } refShare)
                {
                    continue;
                }

                int length = r.ToIndex - r.FromIndex + 1;
                int priorFrom = Math.Max(0, r.FromIndex - length);
                int priorCovered = 0;
                for (int j = priorFrom; j < r.FromIndex; j++)
                {
                    if (_alignment.InkOfTemplate[j] >= 0)
                    {
                        priorCovered++;
                    }
                }

                int prior = r.FromIndex - priorFrom;
                if (prior == 0 || (double)priorCovered / prior < _config.Word.RetraceCoveredShare)
                {
                    continue;
                }

                int a = _alignment.InkOfTemplate[r.FromIndex];
                int b = _alignment.InkOfTemplate[r.ToIndex];
                if (a < 0 || b < 0)
                {
                    continue;
                }

                int from = Math.Min(a, b);
                int to = Math.Max(a, b);
                if (from < retrace.MinPriorPoints)
                {
                    continue;
                }

                double relative = to - from + 1 < retrace.MinRangePoints
                    ? 0.0
                    : (refShare > 1e-9 ? Math.Min(1.0, PolylineTools.ShareOnPriorInk(all, from, to, retrace.Tolerance) / refShare) : 1.0);
                if (relative < retrace.MinShareOfReference)
                {
                    string? stroke = _word.Letters[r.Letter].Glyph.Strokes.OrderBy(s => s.Order).FirstOrDefault()?.Id;
                    double span = Math.Max(1, _reference.BodyEnd[r.Letter] - _reference.BodyStart[r.Letter]);
                    findings.Add(new Finding(
                        ErrorCode.P6,
                        new FindingLocation(FindingPlace.Stroke, r.Letter, stroke, _ink.InkIndexOf(from)),
                        FindingBuilder.Fixed(_config.Findings, ErrorCode.P6),
                        new TemplateSpan(stroke, TemplatePart.Body, (r.FromIndex - _reference.BodyStart[r.Letter]) / span, (r.ToIndex - _reference.BodyStart[r.Letter]) / span),
                        FindingKind.Error,
                        relative,
                        retrace.MinShareOfReference));
                }
            }
        }

        // ---------- Буквы ----------

        private EvaluationResult EvaluateLetter(int k, (int From, int To) range)
        {
            Glyph glyph = _reference.LetterGlyphs[k];
            LetterContext context = k == 0 ? LetterContext.WordFirst : LetterContext.WordInner;
            var points = new List<RulerPoint>();
            int firstInkIndex = -1;
            for (int s = 0; s < _main.Count; s++)
            {
                int gs = _ink.StrokeStart[s];
                int ge = gs + _main[s].NPos.Length - 1;
                int from = Math.Max(range.From, gs);
                int to = Math.Min(range.To, ge);
                if (from > to)
                {
                    continue;
                }

                firstInkIndex = firstInkIndex < 0 ? _main[s].InkIndex : firstInkIndex;
                _main[s].AppendRaw(from - gs, to - gs, points);
            }

            string symbol = glyph.Symbol;
            if (points.Count < 2)
            {
                var empty = new EvaluationSummary(
                    _owner.Info,
                    TargetKind.Letter,
                    symbol,
                    Decision.Unrecognized,
                    symbol,
                    Confidence.Low,
                    [new LetterSummary(0, symbol, symbol, context, null, 0.0, null, false, ProcessSummary.NotEvaluated)],
                    [],
                    []);
                return new EvaluationResult(empty, new EvaluationDetail([], [], [], new NormalizationInfo(0.0, 1.0, 0.0, 0.0, null, null), ImmutableSortedDictionary<string, double>.Empty));
            }

            var piece = new RulerInk(
                [new RulerStroke(firstInkIndex, [.. points], _main.Any(s => s.Clipped))],
                _request.Ink.StimulusEndUs,
                _request.Ink.MmPerUnit,
                _request.Ink.InterpolatedTime);
            EvaluationResult result = _owner._letters.Evaluate(new LetterEvaluationRequest(piece, glyph, _word.Style, _request.Tolerance, _request.DeferredAccept, context));
            return result;
        }

        // ---------- «Не распознано» ----------

        private EvaluationResult Unrecognized(NormalizationParams normalization, AlignmentStats? stats)
        {
            var letters = ImmutableArray.CreateBuilder<LetterSummary>(_word.Letters.Length);
            foreach (WordLetter letter in _word.Letters)
            {
                letters.Add(new LetterSummary(
                    letter.Index,
                    letter.Glyph.Symbol,
                    letter.Glyph.Symbol,
                    letter.Index == 0 ? LetterContext.WordFirst : LetterContext.WordInner,
                    null,
                    0.0,
                    null,
                    false,
                    ProcessSummary.NotEvaluated));
            }

            ImmutableSortedDictionary<string, double>.Builder diagnostics = ImmutableSortedDictionary.CreateBuilder<string, double>(StringComparer.Ordinal);
            if (stats is not null)
            {
                diagnostics["align.meanDtw"] = stats.MeanDtw;
                diagnostics["align.coverage"] = stats.Coverage;
                diagnostics["align.inkShare"] = stats.InkShare;
            }

            diagnostics["ink.main"] = _main.Count;
            diagnostics["ink.marks"] = _marks.Count;
            var summary = new EvaluationSummary(_owner.Info, TargetKind.Word, _word.Text, Decision.Unrecognized, _word.Text, Confidence.Low, letters.MoveToImmutable(), [], [])
            {
                Joins = [],
            };
            var detail = new EvaluationDetail(
                [],
                [],
                [],
                new NormalizationInfo(normalization.ShearDeg, normalization.Scale, normalization.ShiftU, normalization.ShiftV, normalization.InkSlantDeg, normalization.TemplateSlantDeg),
                diagnostics.ToImmutable());
            return new EvaluationResult(summary, detail);
        }
    }

    /// <summary>Основной след рукописи одной ломаной: нормализованные точки всех основных штрихов подряд.</summary>
    private sealed class MainInk
    {
        private MainInk(List<ChildStroke> strokes)
        {
            Strokes = strokes;
        }

        public List<ChildStroke> Strokes { get; }

        public Vec2[] Pos { get; private set; } = [];

        public Vec2[] Dir { get; private set; } = [];

        public bool[] LiftBefore { get; private set; } = [];

        /// <summary>Расстояние вдоль следа от его начала (через отрыв — без приращения).</summary>
        public double[] Arc { get; private set; } = [];

        public int[] StrokeStart { get; private set; } = [];

        private int[] StrokeIndex { get; set; } = [];

        public int Count => Pos.Length;

        public static MainInk Build(List<ChildStroke> main)
        {
            int total = main.Sum(s => s.NPos.Length);
            var ink = new MainInk(main)
            {
                Pos = new Vec2[total],
                Dir = new Vec2[total],
                LiftBefore = new bool[total],
                Arc = new double[total],
                StrokeStart = new int[main.Count],
                StrokeIndex = new int[total],
            };
            int g = 0;
            for (int s = 0; s < main.Count; s++)
            {
                ink.StrokeStart[s] = g;
                ChildStroke stroke = main[s];
                for (int l = 0; l < stroke.NPos.Length; l++)
                {
                    ink.Pos[g] = stroke.NPos[l];
                    ink.Dir[g] = stroke.NDir[l];
                    ink.StrokeIndex[g] = s;
                    ink.LiftBefore[g] = s > 0 && l == 0;
                    ink.Arc[g] = g == 0 ? 0.0 : ink.Arc[g - 1] + (l == 0 ? 0.0 : Vec2.Distance(ink.Pos[g - 1], ink.Pos[g]));
                    g++;
                }
            }

            return ink;
        }

        public int StrokeOf(int index) => StrokeIndex[Math.Clamp(index, 0, Count - 1)];

        public bool IsStrokeStart(int index) => index == 0 || StrokeIndex[index] != StrokeIndex[index - 1];

        public bool IsStrokeEnd(int index) => index == Count - 1 || StrokeIndex[index] != StrokeIndex[index + 1];

        public int InkIndexOf(int index) => Strokes[StrokeOf(index)].InkIndex;

        /// <summary>Точка исходной рукописи, ближайшая к точке следа (по доле длины дуги штриха).</summary>
        public InkPoint RawPoint(int index)
        {
            int s = StrokeOf(index);
            ChildStroke stroke = Strokes[s];
            int local = index - StrokeStart[s];
            int raw = stroke.NearestKeptIndex(local);
            RulerPoint p = stroke.Kept[raw];
            return new InkPoint(stroke.InkIndex, stroke.KeptOriginalIndex[raw], p.U, p.V, p.TimeUs);
        }
    }
}
