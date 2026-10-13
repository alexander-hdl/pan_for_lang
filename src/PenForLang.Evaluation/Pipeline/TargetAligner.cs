using System.Collections.Concurrent;
using PenForLang.Core.Geometry;
using PenForLang.Evaluation.Config;

namespace PenForLang.Evaluation.Pipeline;

/// <summary>Клетка пути выравнивания: точка рукописи <see cref="I"/> соответствует точке эталона <see cref="J"/>.</summary>
internal readonly record struct PathCell(int I, int J);

/// <summary>Отрыв пера на пути: рукопись перешла к новому штриху, эталон — от <see cref="FromJ"/> к <see cref="ToJ"/>.</summary>
/// <param name="I">Первая точка рукописи после отрыва.</param>
/// <param name="FromJ">Индекс эталона последней точки до отрыва.</param>
/// <param name="ToJ">Индекс эталона первой точки после отрыва.</param>
/// <param name="Interval">Индекс диапазона соединения (<see cref="WordReference.Intervals"/>), в который уложен отрыв, или −1 (отрыв внутри тела).</param>
internal readonly record struct LiftEvent(int I, int FromJ, int ToJ, int Interval);

/// <summary>
/// Диапазон отрыва в задаче выравнивания: ядро <c>[Lo, Hi]</c> (конец тела левой — начало тела правой буквы) и допуск
/// <c>[SlackLo, SlackHi]</c> внутрь тел (промежуточный штраф: растёт от ядра к краю допуска).
/// </summary>
internal readonly record struct AlignInterval(int Lo, int Hi, bool Expected, int SlackLo, int SlackHi)
{
    /// <summary>Диапазон без допуска.</summary>
    public static AlignInterval Core(int lo, int hi, bool expected) => new(lo, hi, expected, lo, hi);
}

/// <summary>Условие задачи выравнивания: рукопись, эталон и его разметка (зоны, диапазоны отрыва, допустимые старт и конец).</summary>
internal sealed class AlignProblem
{
    public required Vec2[] InkPos { get; init; }

    public required Vec2[] InkDir { get; init; }

    /// <summary>Перед точкой <c>i</c> перо было оторвано (первая точка штриха, кроме самой первой).</summary>
    public required bool[] LiftBefore { get; init; }

    public required Vec2[] TplPos { get; init; }

    public required Vec2[] TplDir { get; init; }

    public required ZoneKind[] Zone { get; init; }

    public required double[] SkipPrefix { get; init; }

    public required AlignInterval[] Intervals { get; init; }

    public required int OpenBeginMax { get; init; }

    public required int OpenEndMin { get; init; }

    public int M => InkPos.Length;

    public int N => TplPos.Length;

    /// <summary>Та же задача при обходе в обратном порядке (для стоимости «от конца»; уверенность границ).</summary>
    public AlignProblem Reversed()
    {
        int m = M;
        int n = N;
        var inkPos = new Vec2[m];
        var inkDir = new Vec2[m];
        var lift = new bool[m];
        for (int i = 0; i < m; i++)
        {
            inkPos[i] = InkPos[m - 1 - i];
            inkDir[i] = -InkDir[m - 1 - i];
        }

        for (int i = 1; i < m; i++)
        {
            // Отрыв между старыми i − 1 и i — теперь перед старой точкой i − 1 (индекс m − i).
            if (LiftBefore[i])
            {
                lift[m - i] = true;
            }
        }

        var tplPos = new Vec2[n];
        var tplDir = new Vec2[n];
        var zone = new ZoneKind[n];
        var skip = new double[n + 1];
        for (int j = 0; j < n; j++)
        {
            tplPos[j] = TplPos[n - 1 - j];
            tplDir[j] = -TplDir[n - 1 - j];
            zone[j] = Zone[n - 1 - j];
            skip[j + 1] = skip[j] + (zone[j] == ZoneKind.Body ? 1.0 : 0.0);
        }

        var intervals = new AlignInterval[Intervals.Length];
        for (int k = 0; k < intervals.Length; k++)
        {
            AlignInterval a = Intervals[k];
            intervals[k] = new AlignInterval(n - 1 - a.Hi, n - 1 - a.Lo, a.Expected, n - 1 - a.SlackHi, n - 1 - a.SlackLo);
        }

        return new AlignProblem
        {
            InkPos = inkPos,
            InkDir = inkDir,
            LiftBefore = lift,
            TplPos = tplPos,
            TplDir = tplDir,
            Zone = zone,
            SkipPrefix = skip,
            Intervals = intervals,
            OpenBeginMax = n - 1 - OpenEndMin,
            OpenEndMin = n - 1 - OpenBeginMax,
        };
    }
}

/// <summary>Буферы DP, переиспользуемые между вызовами (без выделения больших массивов на каждую оценку).</summary>
internal sealed class AlignWorkspace
{
    private static readonly ConcurrentBag<AlignWorkspace> Pool = [];

    public double[] Accumulated = [];
    public byte[] Back = [];
    public int[] RowLo = [];
    public int[] RowHi = [];
    public int[] RowOffset = [];
    public double[] LiftBest = [];
    public double[] PrefixMaxX = [];
    public double[] SuffixMinX = [];

    public static AlignWorkspace Rent() => Pool.TryTake(out AlignWorkspace? w) ? w : new AlignWorkspace();

    public void Return() => Pool.Add(this);

    public void EnsureRows(int rows)
    {
        if (RowLo.Length < rows)
        {
            int size = Math.Max(rows, RowLo.Length * 3 / 2);
            RowLo = new int[size];
            RowHi = new int[size];
            RowOffset = new int[size];
        }
    }

    public void EnsureCells(long cells)
    {
        if (cells > Array.MaxLength)
        {
            throw new InvalidOperationException("The alignment table is too large.");
        }

        if (Accumulated.Length < cells)
        {
            int size = (int)Math.Min(Array.MaxLength, Math.Max(cells, (long)Accumulated.Length * 3 / 2));
            Accumulated = new double[size];
            Back = new byte[size];
        }
    }

    public void EnsureTemplate(int n)
    {
        if (LiftBest.Length < n)
        {
            LiftBest = new double[n];
            PrefixMaxX = new double[n];
            SuffixMinX = new double[n];
        }
    }
}

/// <summary>
/// Накопленная таблица DP выравнивания (по полосе: в каждой строке только клетки с <c>|Δu| ≤ bandU + bandMarginU</c>) и всё, что
/// нужно для восстановления пути. Буферы берутся из пула; <see cref="Dispose"/> возвращает их.
/// </summary>
internal sealed class DpTable : IDisposable
{
    private const byte Start = 255;
    private const byte Diagonal = 0;
    private const byte Up = 1;
    private const byte Left = 2;
    private const byte Lift = 3;

    private readonly AlignWorkspace _w;
    private readonly AlignerConfig _config;
    private readonly DtwConfig _weights;
    private readonly Dictionary<int, (int[] Arg, short[] Interval)> _liftRows = [];
    private readonly int _rowLimit;
    private bool _disposed;

    public DpTable(AlignProblem problem, AlignerConfig config, DtwConfig weights, bool restrictToBand = true, int rowLimit = int.MaxValue)
    {
        _rowLimit = Math.Min(rowLimit, problem.M);
        Problem = problem;
        _config = config;
        _weights = weights;
        _w = AlignWorkspace.Rent();
        BuildRows(restrictToBand);
        Fill();
        if (BestEnd < 0 && restrictToBand && _rowLimit >= problem.M)
        {
            // Рукопись вне полосы (нет пути): считаем без ограничения.
            _liftRows.Clear();
            BestCost = double.PositiveInfinity;
            BuildRows(false);
            Fill();
        }
    }

    public AlignProblem Problem { get; }

    public double BestCost { get; private set; } = double.PositiveInfinity;

    public int BestEnd { get; private set; } = -1;

    /// <summary>Число клеток таблицы (для оценки работы).</summary>
    public long Cells { get; private set; }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _w.Return();
        }
    }

    /// <summary>Накопленная стоимость <c>D[i][j]</c>; +∞ вне полосы.</summary>
    public double Cumulative(int i, int j)
    {
        if (i < 0 || i >= _rowLimit || j < _w.RowLo[i] || j > _w.RowHi[i])
        {
            return double.PositiveInfinity;
        }

        return _w.Accumulated[_w.RowOffset[i] + j - _w.RowLo[i]];
    }

    /// <summary>Локальная стоимость клетки <c>c(i, j)</c>: расстояние и направление; у «мостика» эталона — плоская цена в коридоре.</summary>
    public double Local(int i, int j)
    {
        AlignProblem p = Problem;
        Vec2 d = p.InkPos[i] - p.TplPos[j];
        if (p.Zone[j] == ZoneKind.Connector)
        {
            double excess = Math.Max(0.0, d.Length - _config.BreakCorridorU);
            return _config.ContinuousBreakCostPerPoint + (_weights.PositionWeight * excess * excess);
        }

        double c = (_weights.PositionWeight * d.LengthSquared) + (_weights.DirectionWeight * Math.Max(0.0, 1.0 - Vec2.Dot(p.InkDir[i], p.TplDir[j])));
        return Math.Abs(d.X) > _config.BandU ? c + _config.OutOfBandCost : c;
    }

    private void BuildRows(bool restrictToBand)
    {
        AlignProblem p = Problem;
        int m = p.M;
        int n = p.N;
        _w.EnsureRows(m);
        _w.EnsureTemplate(n);
        double window = _config.BandU + _config.BandMarginU;
        double[] prefixMax = _w.PrefixMaxX;
        double[] suffixMin = _w.SuffixMinX;
        double run = double.NegativeInfinity;
        for (int j = 0; j < n; j++)
        {
            run = Math.Max(run, p.TplPos[j].X);
            prefixMax[j] = run;
        }

        run = double.PositiveInfinity;
        for (int j = n - 1; j >= 0; j--)
        {
            run = Math.Min(run, p.TplPos[j].X);
            suffixMin[j] = run;
        }

        long total = 0;
        for (int i = 0; i < _rowLimit; i++)
        {
            int lo = 0;
            int hi = n - 1;
            if (restrictToBand)
            {
                double x = p.InkPos[i].X;

                // Первый j, у которого ломаная эталона до него уже дошла до x − window (prefixMax не убывает).
                int a = 0;
                int b = n;
                while (a < b)
                {
                    int mid = (a + b) / 2;
                    if (prefixMax[mid] >= x - window)
                    {
                        b = mid;
                    }
                    else
                    {
                        a = mid + 1;
                    }
                }

                lo = a;

                // Последний j, после которого ломаная эталона ещё не уходит правее x + window (suffixMin не убывает).
                a = 0;
                b = n;
                while (a < b)
                {
                    int mid = (a + b) / 2;
                    if (suffixMin[mid] > x + window)
                    {
                        b = mid;
                    }
                    else
                    {
                        a = mid + 1;
                    }
                }

                hi = a - 1;
                if (lo > hi)
                {
                    lo = 0;
                    hi = -1;
                }
            }

            _w.RowLo[i] = lo;
            _w.RowHi[i] = hi;
            _w.RowOffset[i] = (int)total;
            total += Math.Max(0, hi - lo + 1);
        }

        Cells = total;
        _w.EnsureCells(total);
    }

    private void Fill()
    {
        AlignProblem p = Problem;
        int m = p.M;
        int n = p.N;
        double[] acc = _w.Accumulated;
        byte[] back = _w.Back;
        BestCost = double.PositiveInfinity;
        BestEnd = -1;

        for (int i = 0; i < _rowLimit; i++)
        {
            int lo = _w.RowLo[i];
            int hi = _w.RowHi[i];
            int off = _w.RowOffset[i];
            bool lifted = i > 0 && p.LiftBefore[i];
            int[]? liftArg = null;
            short[]? liftInterval = null;
            double[]? liftBest = null;
            if (lifted)
            {
                liftBest = _w.LiftBest;
                liftArg = new int[n];
                liftInterval = new short[n];
                ComputeLiftRow(i, liftBest, liftArg, liftInterval);
                _liftRows[i] = (liftArg, liftInterval);
            }

            int plo = i > 0 ? _w.RowLo[i - 1] : 0;
            int phi = i > 0 ? _w.RowHi[i - 1] : -1;
            int poff = i > 0 ? _w.RowOffset[i - 1] : 0;
            for (int j = lo; j <= hi; j++)
            {
                double c = Local(i, j);
                double best = double.PositiveInfinity;
                byte how = Start;
                if (i == 0)
                {
                    if (j <= p.OpenBeginMax)
                    {
                        best = 0.0;
                    }
                }
                else if (lifted)
                {
                    best = liftBest![j];
                    how = Lift;
                }
                else
                {
                    if (j >= plo && j <= phi)
                    {
                        best = acc[poff + j - plo];
                        how = Up;
                    }

                    if (j - 1 >= plo && j - 1 <= phi)
                    {
                        double diagonal = acc[poff + j - 1 - plo];
                        if (diagonal < best)
                        {
                            best = diagonal;
                            how = Diagonal;
                        }
                    }
                }

                if (j > lo)
                {
                    double left = acc[off + j - 1 - lo];
                    if (left < best)
                    {
                        best = left;
                        how = Left;
                    }
                }

                acc[off + j - lo] = best + c;
                back[off + j - lo] = best == double.PositiveInfinity ? Start : how;
            }
        }

        if (_rowLimit < m)
        {
            // Усечённая таблица (обратный проход для границ): нужны только накопленные стоимости строк, конца пути нет.
            return;
        }

        int last = m - 1;
        for (int j = Math.Max(p.OpenEndMin, _w.RowLo[last]); j <= _w.RowHi[last]; j++)
        {
            double value = acc[_w.RowOffset[last] + j - _w.RowLo[last]];
            if (value < BestCost)
            {
                BestCost = value;
                BestEnd = j;
            }
        }
    }

    /// <summary>
    /// Переходы через отрыв пера в строке <paramref name="i"/> (из строки <c>i − 1</c>): отрыв внутри диапазона соединения
    /// (пропуск геометрии бесплатен; цена — <c>liftJoinPenalty</c>, растущая к <c>liftInLetterPenalty</c> по мере ухода вглубь тел
    /// соседних букв в пределах допуска; 0 для ожидаемого отрыва) или отрыв в другом месте (цена <c>liftInLetterPenalty</c> и
    /// <c>skipBodyCostPerPoint</c> за каждую пропущенную точку тела).
    /// </summary>
    private void ComputeLiftRow(int i, double[] best, int[] arg, short[] interval)
    {
        AlignProblem p = Problem;
        int n = p.N;
        double skip = _config.SkipBodyCostPerPoint;
        double[] w = p.SkipPrefix;
        int plo = _w.RowLo[i - 1];
        int phi = _w.RowHi[i - 1];
        int poff = _w.RowOffset[i - 1];
        double[] acc = _w.Accumulated;
        double Prev(int j) => j >= plo && j <= phi ? acc[poff + j - plo] : double.PositiveInfinity;

        // Общий случай: j0 == j без пропуска; j0 < j — с ценой пропущенных точек тела между ними.
        double runMin = double.PositiveInfinity;
        int runArg = -1;
        for (int j = 0; j < n; j++)
        {
            double prev = Prev(j);
            double same = prev + _config.LiftInLetterPenalty;
            best[j] = same;
            arg[j] = j;
            interval[j] = -1;
            if (runArg >= 0)
            {
                double viaSkip = runMin + (skip * w[j]) + _config.LiftInLetterPenalty;
                if (viaSkip < best[j])
                {
                    best[j] = viaSkip;
                    arg[j] = runArg;
                }
            }

            double candidate = prev - (skip * w[j + 1]);
            if (candidate < runMin)
            {
                runMin = candidate;
                runArg = j;
            }
        }

        // Отрыв внутри диапазона соединения с допуском.
        for (int k = 0; k < p.Intervals.Length; k++)
        {
            AlignInterval a = p.Intervals[k];
            double basePenalty = a.Expected ? 0.0 : _config.LiftJoinPenalty;
            int slack = Math.Max(1, Math.Max(a.Lo - a.SlackLo, a.SlackHi - a.Hi));
            double ramp = Math.Max(0.0, _config.LiftInLetterPenalty - basePenalty) / slack;
            double rangeMin = double.PositiveInfinity;
            int rangeArg = -1;
            for (int j = a.SlackLo; j <= a.SlackHi; j++)
            {
                double prev = Prev(j);

                // Отрыв начат правее ядра (j0 > Hi): штраф за глубину; приземление левее ядра (j < Lo) — то же.
                double depthStart = ramp * Math.Max(0, j - a.Hi);
                double value = prev + depthStart;
                int source = j;
                if (rangeArg >= 0)
                {
                    double viaSkip = rangeMin + (skip * w[j]);
                    if (viaSkip < value)
                    {
                        value = viaSkip;
                        source = rangeArg;
                    }
                }

                value += basePenalty + (ramp * Math.Max(0, a.Lo - j));
                if (value < best[j])
                {
                    best[j] = value;
                    arg[j] = source;
                    interval[j] = (short)k;
                }

                double candidate = prev + (ramp * Math.Max(0, j - a.Hi)) - (skip * w[j + 1]);
                if (candidate < rangeMin)
                {
                    rangeMin = candidate;
                    rangeArg = j;
                }
            }
        }
    }

    /// <summary>Восстанавливает путь с конца <paramref name="endJ"/> последней строки.</summary>
    public (List<PathCell> Path, List<LiftEvent> Lifts) Backtrack(int endJ)
    {
        var path = new List<PathCell>();
        var lifts = new List<LiftEvent>();
        int i = Problem.M - 1;
        int j = endJ;
        while (true)
        {
            path.Add(new PathCell(i, j));
            byte how = _w.Back[_w.RowOffset[i] + j - _w.RowLo[i]];
            if (how == Start)
            {
                break;
            }

            switch (how)
            {
                case Diagonal:
                    i--;
                    j--;
                    break;
                case Up:
                    i--;
                    break;
                case Left:
                    j--;
                    break;
                default:
                    {
                        (int[] arg, short[] interval) = _liftRows[i];
                        lifts.Add(new LiftEvent(i, arg[j], j, interval[j]));
                        i--;
                        j = arg[j];
                        break;
                    }
            }
        }

        path.Reverse();
        lifts.Reverse();
        return (path, lifts);
    }
}

/// <summary>Результат выравнивания рукописи с эталоном слова.</summary>
internal sealed class Alignment : IDisposable
{
    public required DpTable Forward { get; init; }

    public required List<PathCell> Path { get; init; }

    public required List<LiftEvent> Lifts { get; init; }

    public required double CostPerInkPoint { get; init; }

    /// <summary>Для каждой точки эталона — индекс точки рукописи (медиана по клеткам пути) или −1, если путь её не касается.</summary>
    public required int[] InkOfTemplate { get; init; }

    /// <summary>Для каждой точки рукописи — индекс точки эталона (медиана по клеткам пути).</summary>
    public required int[] TemplateOfInk { get; init; }

    public void Dispose() => Forward.Dispose();
}

/// <summary>
/// DTW по цели для слова (ARCHITECTURE.md §6.4.4, A03, A09): состояние <c>(i, j)</c> — точка рукописи и точка эталона;
/// шаги диагональный, по рукописи и по эталону; полоса по x (клетки вне <c>bandU + bandMarginU</c> не считаются; внутри
/// <c>bandU</c> — без штрафа, дальше — «мягкий» штраф); отрыв пера — переход через маркер отрыва с ценой по месту (внутри
/// диапазона соединения — альтернатива геометрии, пропуск соединения бесплатен); допустимый старт — подход или начало тела
/// первой буквы, допустимый конец — конец тела последней буквы или её выход. Прямой и обратный проходы дают стоимость пути
/// через любую клетку (уверенность границ).
/// </summary>
internal static class TargetAligner
{
    public static Alignment Align(AlignProblem problem, ScoringConfig config)
    {
        var table = new DpTable(problem, config.Word.Aligner, config.Dtw);
        if (table.BestEnd < 0)
        {
            table.Dispose();
            throw new InvalidOperationException("The alignment has no feasible path.");
        }

        (List<PathCell> path, List<LiftEvent> lifts) = table.Backtrack(table.BestEnd);
        var inkOfTemplate = new int[problem.N];
        Array.Fill(inkOfTemplate, -1);
        var templateOfInk = new int[problem.M];
        Array.Fill(templateOfInk, -1);
        MedianByKey(path, problem.N, inkOfTemplate, cell => cell.J, cell => cell.I);
        MedianByKey(path, problem.M, templateOfInk, cell => cell.I, cell => cell.J);
        return new Alignment
        {
            Forward = table,
            Path = path,
            Lifts = lifts,
            CostPerInkPoint = table.BestCost / problem.M,
            InkOfTemplate = inkOfTemplate,
            TemplateOfInk = templateOfInk,
        };
    }

    /// <summary>Рост полной стоимости пути, если пройти через клетку <c>(i, j)</c> вместо лучшей (≥ 0; +∞ вне полосы).</summary>
    public static double Rise(Alignment alignment, DpTable backward, int i, int j)
    {
        AlignProblem p = alignment.Forward.Problem;
        if (i < 0 || i >= p.M || j < 0 || j >= p.N)
        {
            return double.PositiveInfinity;
        }

        double through = alignment.Forward.Cumulative(i, j) + backward.Cumulative(p.M - 1 - i, p.N - 1 - j) - alignment.Forward.Local(i, j);
        return through - alignment.Forward.BestCost;
    }

    private static void MedianByKey(List<PathCell> path, int size, int[] target, Func<PathCell, int> key, Func<PathCell, int> value)
    {
        var groups = new List<int>?[size];
        foreach (PathCell cell in path)
        {
            (groups[key(cell)] ??= []).Add(value(cell));
        }

        for (int k = 0; k < size; k++)
        {
            if (groups[k] is { Count: > 0 } g)
            {
                g.Sort();
                target[k] = g[g.Count / 2];
            }
        }
    }
}
