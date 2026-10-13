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
        using var run = new WordRun(this, request, reference);
        return run.Execute();
    }

    /// <summary>Состояние одной оценки.</summary>
    private sealed class WordRun : IDisposable
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
        private DpTable? _backward;
        private double _toleranceScale;

        public WordRun(WordEvaluator owner, WordEvaluationRequest request, WordReference reference)
        {
            _owner = owner;
            _config = owner._config;
            _request = request;
            _reference = reference;
            _word = request.Word;
        }

        public void Dispose()
        {
            _alignment?.Dispose();
            _backward?.Dispose();
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
            SplitMainAndMarks();
            if (_main.Count == 0)
            {
                return Unrecognized(normalization, null);
            }

            Similarity centering = CenterOnTemplate();
            Similarity refinement = AlignWithRefinement();
            refinement = new Similarity(refinement.Scale * centering.Scale, (centering.Translation * refinement.Scale) + refinement.Translation);
            AlignmentStats stats = Measure();
            WordUnrecognizedConfig u = _config.Word.Unrecognized;
            if (stats.MeanDtw > u.MeanDtw
                || stats.Coverage < u.Coverage
                || stats.InkShare < u.InkShare
                || stats.CostPerPoint > u.CostPerPoint
                || stats.FarShare > u.FarShare
                || stats.MinLetterCoverage < u.LetterCoverage)
            {
                return Unrecognized(normalization, stats);
            }

            return Assemble(normalization, refinement, stats);
        }

        /// <summary>
        /// Основной след и кандидаты в отложенные (§6.4.3). Короткая черта, начатая у конца предыдущего основного штриха, —
        /// продолжение следа (хвост t после перекладины «сразу»), а не отложенный штрих; точки — всегда отложенные.
        /// </summary>
        private void SplitMainAndMarks()
        {
            foreach (ChildStroke stroke in _children)
            {
                bool mark = IsMark(stroke);
                if (mark && stroke.Extent > _config.Deferred.DotMaxLength && _main.Count > 0
                    && Vec2.Distance(stroke.NPos[0], _main[^1].NPos[^1]) <= _config.Word.MarkAdjacencyU)
                {
                    mark = false;
                }

                (mark ? _marks : _main).Add(stroke);
            }
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
        /// Диапазон отрыва с допуском <c>joinSlackU</c> внутрь тел соседних букв, но не больше четверти тела (отрыв посреди
        /// короткой буквы — это отрыв внутри буквы, а не на соединении). Внутри допуска штраф растёт к штрафу отрыва в букве.
        /// </summary>
        private AlignInterval ExtendedInterval(JoinInterval interval)
        {
            int slack = (int)Math.Round(_config.Word.Aligner.JoinSlackU / _config.ResampleStep);
            int leftLetter = interval.Letter;
            int rightLetter = Math.Min(_word.Letters.Length - 1, interval.Join >= 0 ? interval.Join + 1 : interval.Letter);
            int leftBody = _reference.BodyEnd[leftLetter] - _reference.BodyStart[leftLetter];
            int rightBody = _reference.BodyEnd[rightLetter] - _reference.BodyStart[rightLetter];
            int lo = Math.Max(0, interval.Lo - Math.Min(slack, leftBody / 4));
            int hi = Math.Min(_reference.Count - 1, interval.Hi + Math.Min(slack, rightBody / 4));
            return new AlignInterval(interval.Lo, interval.Hi, interval.Expected, lo, hi);
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
                _alignment.Dispose();
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

        private sealed record AlignmentStats(double MeanDtw, double Coverage, double InkShare, double CostPerPoint, double FarShare, double MinLetterCoverage, double[] LetterCoverage);

        /// <summary>Подгонка тела буквы ребёнка к эталону: <c>p' = tplMean + (p − inkMean)·scale</c>.</summary>
        private readonly record struct LetterFit(double Scale, Vec2 InkMean, Vec2 TplMean)
        {
            public Vec2 Apply(Vec2 p) => TplMean + ((p - InkMean) * Scale);
        }

        private LetterFit[] _fits = [];


        /// <summary>
        /// Подгонка (масштаб и сдвиг) тела каждой буквы к эталону по клеткам пути: форма буквы судится независимо от того, где
        /// и каким размером ребёнок её поставил (интервал — F8, размер — F4, не J3 и не «буква не та»).
        /// </summary>
        private LetterFit[] ComputeFits()
        {
            int letters = _word.Letters.Length;
            var ink = new List<Vec2>[letters];
            var tpl = new List<Vec2>[letters];
            for (int k = 0; k < letters; k++)
            {
                ink[k] = [];
                tpl[k] = [];
            }

            foreach (PathCell cell in _alignment.Path)
            {
                if (_reference.Zone[cell.J] != ZoneKind.Body)
                {
                    continue;
                }

                int k = _reference.OwnerLetter[cell.J];
                ink[k].Add(_ink.Pos[cell.I]);
                tpl[k].Add(_reference.Pos[cell.J]);
            }

            var fits = new LetterFit[letters];
            for (int k = 0; k < letters; k++)
            {
                if (ink[k].Count < 3)
                {
                    fits[k] = new LetterFit(1.0, Vec2.Zero, Vec2.Zero);
                    continue;
                }

                // Робастная подгонка (отсечение худших соответствий): лишние чернила, сваленные на конец эталона, подгонку не тянут.
                RefineConfig refine = _config.Normalization.Refine with { ScaleMin = 0.5, ScaleMax = 2.0, KeepShare = 0.7, Iterations = 4 };
                Similarity fit = SimilarityFit.Fit(ink[k], tpl[k], refine);
                fits[k] = new LetterFit(fit.Scale, Vec2.Zero, fit.Translation);
            }

            return fits;
        }

        /// <summary>
        /// Сводка выравнивания для решения «не распознано» (§6.4.7): среднее отклонение выровненных точек, доля «чужих» точек,
        /// покрытие тел эталона (в целом и по буквам) и доля чернил в коридоре. Отклонения для покрытия и «чужих» точек — после
        /// подгонки тела своей буквы (<see cref="ComputeFits"/>). Считается по клеткам пути, без перебора «точка × ломаная».
        /// </summary>
        private AlignmentStats Measure()
        {
            WordUnrecognizedConfig u = _config.Word.Unrecognized;
            double tau = _config.Form.CorridorTau * _toleranceScale * u.CoverageTauScale;
            int letters = _word.Letters.Length;
            _fits = ComputeFits();
            var nearest = new double[_reference.Count];
            Array.Fill(nearest, double.PositiveInfinity);
            double sum = 0.0;
            int counted = 0;
            int far = 0;
            int inside = 0;
            foreach (PathCell cell in _alignment.Path)
            {
                if (_reference.Zone[cell.J] == ZoneKind.Connector)
                {
                    continue;
                }

                sum += Vec2.Distance(_ink.Pos[cell.I], _reference.Pos[cell.J]);
                counted++;
                double d = Vec2.Distance(_fits[_reference.OwnerLetter[cell.J]].Apply(_ink.Pos[cell.I]), _reference.Pos[cell.J]);
                nearest[cell.J] = Math.Min(nearest[cell.J], d);
                far += d > u.FarDistance ? 1 : 0;
                inside += d <= tau ? 1 : 0;
            }

            int body = 0;
            int covered = 0;
            var letterCoverage = new double[letters];
            for (int k = 0; k < letters; k++)
            {
                int letterBody = 0;
                int letterCovered = 0;
                for (int j = _reference.BodyStart[k]; j <= _reference.BodyEnd[k]; j++)
                {
                    if (_reference.Zone[j] != ZoneKind.Body)
                    {
                        continue;
                    }

                    letterBody++;
                    letterCovered += nearest[j] <= tau ? 1 : 0;
                }

                body += letterBody;
                covered += letterCovered;
                letterCoverage[k] = letterBody == 0 ? 1.0 : (double)letterCovered / letterBody;
            }

            return new AlignmentStats(
                counted == 0 ? double.PositiveInfinity : sum / counted,
                body == 0 ? 0.0 : (double)covered / body,
                counted == 0 ? 0.0 : (double)inside / counted,
                _alignment.CostPerInkPoint,
                counted == 0 ? 1.0 : (double)far / counted,
                letterCoverage.Length == 0 ? 0.0 : letterCoverage.Min(),
                letterCoverage);
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

        /// <summary>Точки следа на расстоянии <c>shiftU</c> до и после границы (по длине дуги, через отрыв без приращения).</summary>
        private (int Before, int After) ShiftRange(int index)
        {
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

            return (before, after);
        }

        /// <summary>
        /// Обратный проход DP нужен только для неуверенных (не краёв штриха) границ и только по строкам от самой ранней из них:
        /// стоимость «до конца» нужна у границ, а не у начала слова.
        /// </summary>
        private void PrepareBackward((BoundaryPos End, BoundaryPos Start)[] boundaries)
        {
            int earliest = int.MaxValue;
            for (int k = 0; k < boundaries.Length; k++)
            {
                foreach (BoundaryPos b in new[] { boundaries[k].End, boundaries[k].Start })
                {
                    if (!b.Crisp)
                    {
                        earliest = Math.Min(earliest, ShiftRange(b.Index).Before);
                    }
                }
            }

            if (earliest != int.MaxValue)
            {
                AlignProblem reversed = _alignment.Forward.Problem.Reversed();
                _backward = new DpTable(reversed, _config.Word.Aligner, _config.Dtw, rowLimit: reversed.M - earliest);
            }
        }

        /// <summary>Рост стоимости пути при сдвиге границы на <c>shiftU</c> в обе стороны (меньшее из двух).</summary>
        private double BoundaryRise(int index, int templateIndex)
        {
            if (_backward is null)
            {
                return double.PositiveInfinity;
            }

            (int before, int after) = ShiftRange(index);
            double riseBefore = before == index ? double.PositiveInfinity : TargetAligner.Rise(_alignment, _backward, before, templateIndex);
            double riseAfter = after == index ? double.PositiveInfinity : TargetAligner.Rise(_alignment, _backward, after, templateIndex);
            return Math.Min(riseBefore, riseAfter);
        }

        // ---------- Сборка результата ----------

        private EvaluationResult Assemble(NormalizationParams normalization, Similarity refinement, AlignmentStats stats)
        {
            int letters = _word.Letters.Length;
            (BoundaryPos End, BoundaryPos Start)[] boundaries = FindBoundaries();

            // Диапазоны рукописи по буквам (глобальные индексы точек основного следа).
            var inkRange = new (int From, int To)[letters];
            for (int k = 0; k < letters; k++)
            {
                int from = k == 0 ? 0 : boundaries[k - 1].Start.Index;
                int to = k + 1 == letters ? _ink.Count - 1 : boundaries[k].End.Index;
                inkRange[k] = (from, Math.Max(from, to));
            }

            PrepareBackward(boundaries);
            var findings = new List<Finding>();

            // Отложенные штрихи и ожидаемые отрывы (§6.4.8).
            DeferredOutcome deferred = AssignDeferred(inkRange);
            DeferredAcceptPolicy policy = _request.DeferredAccept ?? _config.Deferred.AcceptTiming;
            FindingBuilder.AddDeferredFindings(
                deferred,
                _config,
                policy,
                slot => LetterOfSlot(slot),
                mark => NearestLetterSpan(mark, inkRange),
                findings);

            // Отрывы основного следа: ожидаем отрыв рядом с отложенным штрихом, поставленным сразу, у ЕГО буквы (A03).
            var immediate = deferred.Slots
                .Where(slot => slot.Stroke is not null && slot.Timing == DeferredTiming.Immediate)
                .Select(slot => (Stroke: slot.Stroke!, Letter: LetterOfSlot(slot)))
                .ToList();
            var lifts = ClassifyLifts(immediate, out int liftsInsideLetter);

            // Границы и уверенность.
            var boundaryDetail = ImmutableArray.CreateBuilder<WordBoundary>();
            double minRise = _config.Word.Boundaries.MinRiseCost;
            for (int k = 0; k + 1 < letters; k++)
            {
                (BoundaryPos end, BoundaryPos start) = boundaries[k];
                double endRise = end.Crisp ? double.PositiveInfinity : BoundaryRise(end.Index, _reference.BodyEnd[k]);
                double startRise = start.Crisp ? double.PositiveInfinity : BoundaryRise(start.Index, _reference.BodyStart[k + 1]);
                boundaryDetail.Add(new WordBoundary(
                    k,
                    _ink.RawPoint(end.Index),
                    _ink.RawPoint(start.Index),
                    lifts.Any(l => l.Join == k),
                    endRise >= minRise,
                    startRise >= minRise,
                    endRise,
                    startRise));
            }

            var letterConfident = new bool[letters];
            for (int k = 0; k < letters; k++)
            {
                letterConfident[k] = (k == 0 || boundaryDetail[k - 1].StartConfident) && (k + 1 == letters || boundaryDetail[k].EndConfident);
            }

            // Соединения.
            var joinMetrics = ImmutableArray.CreateBuilder<JoinMetrics>();
            var joinSummaries = ImmutableArray.CreateBuilder<JoinSummary>();
            for (int k = 0; k + 1 < letters; k++)
            {
                JoinMetrics metrics = MeasureJoin(k, boundaries[k].End.Index, boundaries[k].Start.Index, lifts);
                joinMetrics.Add(metrics);
                joinSummaries.Add(new JoinSummary(
                    k,
                    k,
                    k + 1,
                    _word.Joins[k].Family,
                    _word.Joins[k].Tall,
                    metrics.Present,
                    metrics.Lifted && metrics.LiftExpected,
                    JoinScore(metrics, _word.Joins[k])));
            }

            AddJoinFindings(joinMetrics, findings);
            HashSet<int> retraceFailed = AddRetraceFindings(letterConfident, findings);

            // Локальная оценка букв.
            var letterResults = ImmutableArray.CreateBuilder<EvaluationResult>(letters);
            var letterSummaries = ImmutableArray.CreateBuilder<LetterSummary>(letters);
            for (int k = 0; k < letters; k++)
            {
                EvaluationResult result = EvaluateLetter(k, inkRange[k]);
                letterResults.Add(result);
                LetterSummary local = result.Summary.Letters[0];
                bool confident = letterConfident[k];
                bool? processOk = local.ProcessOk;
                if (processOk is not null)
                {
                    // Внутри слова: направление и ретрейс (в том числе по соединению) и вход (J3) — только при уверенной сегментации.
                    processOk = k == 0
                        ? processOk.Value && !retraceFailed.Contains(k)
                        : (confident ? processOk.Value && !retraceFailed.Contains(k) && EntryOk(joinMetrics[k - 1]) : null);
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
                    // P2 и P6 внутренней буквы судятся только при уверенной сегментации: иначе участок мог быть вырезан неверно.
                    if (k > 0 && !confident && f.Code is ErrorCode.P2 or ErrorCode.P6)
                    {
                        continue;
                    }

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
            diagnostics["align.farShare"] = stats.FarShare;
            diagnostics["align.minLetterCoverage"] = stats.MinLetterCoverage;
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

            for (int k = 0; k < letters; k++)
            {
                diagnostics[FormattableString.Invariant($"fit{k}.scale")] = _fits[k].Scale;
                diagnostics[FormattableString.Invariant($"fit{k}.dx")] = _fits[k].TplMean.X;
                diagnostics[FormattableString.Invariant($"fit{k}.dy")] = _fits[k].TplMean.Y;
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

        private int LetterOfSlot(DeferredAssignment slot) => _reference.Slots.First(x => ReferenceEquals(x.Slot, slot.Slot)).Letter;

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

        private List<ClassifiedLift> ClassifyLifts(List<(ChildStroke Stroke, int Letter)> immediateMarks, out int insideLetter)
        {
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

                // Отложенный штрих «сразу» между штрихами основного следа ожидает отрыв у ЕГО буквы: на соединении до неё и после неё
                // и внутри неё; штрих другой буквы отрыв не объясняет.
                ChildStroke previous = _main[_ink.StrokeOf(lift.I - 1)];
                ChildStroke next = _main[_ink.StrokeOf(lift.I)];
                bool byMark = immediateMarks.Any(m =>
                    m.Stroke.StartUs >= previous.EndUs
                    && m.Stroke.EndUs <= next.StartUs
                    && (join >= 0 ? (join == m.Letter || join == m.Letter - 1) : letter == m.Letter));
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

            (double entryErr, double? entryAngle) = EntryFit(k + 1, startIndex);
            return new JoinMetrics(k, lifted, expected, drawn, coverage, minV, entryErr, cornerExcess, loops, entryAngle);
        }

        /// <summary>
        /// Вход в тело буквы <paramref name="letter"/> относительно буквы ребёнка (J3): тело буквы подгоняется к эталону (масштаб
        /// и сдвиг по клеткам пути), и мерится, где после подгонки оказалась точка начала тела; широкий интервал (F8) или низкая
        /// буква (F4) смещают всё тело вместе со входом и не дают ошибки входа. Угол — между направлением прихода и эталонным.
        /// </summary>
        private (double Error, double? AngleDeg) EntryFit(int letter, int startIndex)
        {
            int bs = _reference.BodyStart[letter];
            Vec2 entry = _reference.Pos[bs];
            double error = EntryOffset(letter, startIndex);

            double? angle = null;
            int w = Math.Max(2, (int)Math.Round(_config.Joins.Corner.WindowU / _config.ResampleStep));
            if (startIndex - w >= 0 && bs - w >= 0 && _ink.StrokeOf(startIndex - w) == _ink.StrokeOf(startIndex))
            {
                Vec2 inkDirection = _ink.Pos[startIndex] - _ink.Pos[startIndex - w];
                Vec2 tplDirection = entry - _reference.Pos[bs - w];
                if (inkDirection.Length > 1e-9 && tplDirection.Length > 1e-9)
                {
                    double cos = Math.Clamp(Vec2.Dot(inkDirection.Normalized(), tplDirection.Normalized()), -1.0, 1.0);
                    angle = Math.Acos(cos) * 180.0 / Math.PI;
                }
            }

            return (error, angle);
        }

        /// <summary>
        /// Смещение входа относительно тела буквы ребёнка. По клеткам тела (без начала, где соединение вливается в букву)
        /// подгоняется поправка «рукопись − эталон», линейная по высоте: <c>Δu = a + k·v</c> (сдвиг и свой наклон буквы),
        /// <c>Δv = b + c·v</c> (сдвиг и свой размер); смещение входа — остаток в точке входа после поправки (с отсечением худших
        /// 30% клеток). Сдвиг всей буквы (интервал, F8), другая высота (F4) и другой наклон буквы входа не смещают.
        /// </summary>
        private double EntryOffset(int letter, int startIndex)
        {
            int bs = _reference.BodyStart[letter];
            int be = _reference.BodyEnd[letter];
            int skipUntil = bs + (int)Math.Round(_config.Joins.Corner.WindowU / (2.0 * _config.ResampleStep));
            var v = new List<double>();
            var du = new List<double>();
            var dv = new List<double>();
            foreach (PathCell cell in _alignment.Path)
            {
                if (cell.J > skipUntil && cell.J <= be)
                {
                    Vec2 d = _ink.Pos[cell.I] - _reference.Pos[cell.J];
                    v.Add(_ink.Pos[cell.I].Y);
                    du.Add(d.X);
                    dv.Add(d.Y);
                }
            }

            Vec2 atEntry = _ink.Pos[startIndex] - _reference.Pos[bs];
            if (v.Count < 5)
            {
                return atEntry.Length;
            }

            var keep = new bool[v.Count];
            Array.Fill(keep, true);
            (double a, double k, double b, double c) = (0.0, 0.0, 0.0, 0.0);
            for (int pass = 0; pass < 2; pass++)
            {
                (a, k) = Regress(v, du, keep);
                (b, c) = Regress(v, dv, keep);
                if (pass == 0)
                {
                    double[] residual = [.. Enumerable.Range(0, v.Count).Select(i => Math.Sqrt(Math.Pow(du[i] - (a + (k * v[i])), 2) + Math.Pow(dv[i] - (b + (c * v[i])), 2)))];
                    double limit = residual.Order().ElementAt((int)(residual.Length * 0.7));
                    for (int i = 0; i < keep.Length; i++)
                    {
                        keep[i] = residual[i] <= limit;
                    }
                }
            }

            double y = _ink.Pos[startIndex].Y;
            return (atEntry - new Vec2(a + (k * y), b + (c * y))).Length;
        }

        /// <summary>Прямая <c>y = a + k·x</c> по методу наименьших квадратов (по отмеченным точкам).</summary>
        private static (double A, double K) Regress(List<double> x, List<double> y, bool[] keep)
        {
            double n = 0.0;
            double sx = 0.0;
            double sy = 0.0;
            double sxx = 0.0;
            double sxy = 0.0;
            for (int i = 0; i < x.Count; i++)
            {
                if (!keep[i])
                {
                    continue;
                }

                n++;
                sx += x[i];
                sy += y[i];
                sxx += x[i] * x[i];
                sxy += x[i] * y[i];
            }

            double denominator = (n * sxx) - (sx * sx);
            if (n < 2 || Math.Abs(denominator) < 1e-9)
            {
                return (n > 0 ? sy / n : 0.0, 0.0);
            }

            double slope = ((n * sxy) - (sx * sy)) / denominator;
            return ((sy - (slope * sx)) / n, slope);
        }

        /// <summary>Вход в тело в допуске: одна формула для находки J3, оценки соединения и <c>process_ok</c> следующей буквы.</summary>
        private bool EntryOk(JoinMetrics m) =>
            m.EntryErr <= _config.Joins.EntryErr * _toleranceScale && (m.EntryAngleDeg ?? 0.0) <= _config.Joins.EntryAngleDeg;

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

        private double JoinScore(JoinMetrics m, WordJoin join)
        {
            if (!m.Present)
            {
                return 0.0;
            }

            bool bad = (join.High && m.Drawn && m.MinV is { } v && v < _config.Joins.HighMinHeight)
                || !EntryOk(m)
                || m.CornerExcessDeg > _config.Joins.Corner.ExcessDeg
                || m.Loops > 0;
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

                if (!EntryOk(m))
                {
                    // Измеренное и порог — по той метрике, что вышла из допуска (позиция входа в долях допуска, иначе угол прихода).
                    double threshold = j.EntryErr * _toleranceScale;
                    bool position = m.EntryErr > threshold;
                    double measured = position ? m.EntryErr : m.EntryAngleDeg ?? 0.0;
                    double limit = position ? threshold : j.EntryAngleDeg;
                    findings.Add(new Finding(ErrorCode.J3, where, FindingBuilder.Severity(f, measured, limit), span, FindingKind.Error, measured, limit));
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
        private HashSet<int> AddRetraceFindings(bool[] letterConfident, List<Finding> findings)
        {
            var failed = new HashSet<int>();
            RetraceConfig retrace = _config.Process.Retrace;
            Vec2[] all = _ink.Pos;
            foreach (WordRetrace r in _reference.Retrace)
            {
                // Только внутренняя буква, только при уверенной сегментации (иначе участок мог быть вырезан неверно).
                if (r.Letter == 0 || !r.StartsBody || r.RefShare is not { } refShare || !letterConfident[r.Letter])
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

                // Участок отобразился в слишком малое число точек (низкая t, слишком короткий ретрейс): не измерено, а не «ретрейса нет».
                if (to - from + 1 < retrace.MinRangePoints)
                {
                    continue;
                }

                double relative = refShare > 1e-9 ? Math.Min(1.0, PolylineTools.ShareOnPriorInk(all, from, to, retrace.Tolerance) / refShare) : 1.0;
                if (relative < retrace.MinShareOfReference)
                {
                    failed.Add(r.Letter);
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

            return failed;
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
                diagnostics["align.costPerPoint"] = stats.CostPerPoint;
                diagnostics["align.farShare"] = stats.FarShare;
                diagnostics["align.minLetterCoverage"] = stats.MinLetterCoverage;
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
