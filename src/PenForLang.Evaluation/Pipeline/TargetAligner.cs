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

    public required (int Lo, int Hi, bool Expected)[] Intervals { get; init; }

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

        var intervals = new (int, int, bool)[Intervals.Length];
        for (int k = 0; k < intervals.Length; k++)
        {
            intervals[k] = (n - 1 - Intervals[k].Hi, n - 1 - Intervals[k].Lo, Intervals[k].Expected);
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

/// <summary>Накопленная таблица DP выравнивания и всё, что нужно для восстановления пути.</summary>
internal sealed class DpTable
{
    private const byte Start = 255;
    private const byte Diagonal = 0;
    private const byte Up = 1;
    private const byte Left = 2;
    private const byte Lift = 3;

    private readonly byte[] _back;
    private readonly Dictionary<int, (int[] Arg, short[] Interval)> _liftRows = [];

    public DpTable(AlignProblem problem, AlignerConfig config, DtwConfig weights)
    {
        Problem = problem;
        int m = problem.M;
        int n = problem.N;
        Accumulated = new double[(long)m * n];
        Local = new double[(long)m * n];
        _back = new byte[(long)m * n];
        Fill(config, weights);
    }

    public AlignProblem Problem { get; }

    /// <summary>Накопленная стоимость <c>D[i * N + j]</c>.</summary>
    public double[] Accumulated { get; }

    /// <summary>Локальная стоимость клетки <c>c(i, j)</c>.</summary>
    public double[] Local { get; }

    public double BestCost { get; private set; } = double.PositiveInfinity;

    public int BestEnd { get; private set; } = -1;

    public double Cumulative(int i, int j) => Accumulated[((long)i * Problem.N) + j];

    private void Fill(AlignerConfig config, DtwConfig weights)
    {
        AlignProblem p = Problem;
        int m = p.M;
        int n = p.N;
        double wPos = weights.PositionWeight;
        double wTheta = weights.DirectionWeight;
        var liftBest = new double[n];
        var liftArg = new int[n];
        var liftInterval = new short[n];

        for (int i = 0; i < m; i++)
        {
            long row = (long)i * n;
            bool lifted = i > 0 && p.LiftBefore[i];
            if (lifted)
            {
                ComputeLiftRow(i, config, liftBest, liftArg, liftInterval);
                _liftRows[i] = ((int[])liftArg.Clone(), (short[])liftInterval.Clone());
            }

            for (int j = 0; j < n; j++)
            {
                double c;
                if (p.Zone[j] == ZoneKind.Connector)
                {
                    c = config.ContinuousBreakCostPerPoint;
                }
                else
                {
                    Vec2 d = p.InkPos[i] - p.TplPos[j];
                    c = (wPos * d.LengthSquared) + (wTheta * Math.Max(0.0, 1.0 - Vec2.Dot(p.InkDir[i], p.TplDir[j])));
                    if (Math.Abs(d.X) > config.BandU)
                    {
                        c += config.OutOfBandCost;
                    }
                }

                Local[row + j] = c;
                double best = double.PositiveInfinity;
                byte how = Start;
                if (i == 0)
                {
                    if (j <= p.OpenBeginMax)
                    {
                        best = 0.0;
                    }
                    else if (j > 0)
                    {
                        best = Accumulated[row + j - 1];
                        how = Left;
                    }
                }
                else if (lifted)
                {
                    best = liftBest[j];
                    how = Lift;
                }
                else
                {
                    long previous = row - n;
                    best = Accumulated[previous + j];
                    how = Up;
                    if (j > 0)
                    {
                        double diagonal = Accumulated[previous + j - 1];
                        if (diagonal < best)
                        {
                            best = diagonal;
                            how = Diagonal;
                        }
                    }
                }

                if (i > 0 && j > 0)
                {
                    double left = Accumulated[row + j - 1];
                    if (left < best)
                    {
                        best = left;
                        how = Left;
                    }
                }

                Accumulated[row + j] = best + c;
                _back[row + j] = how;
            }
        }

        long last = (long)(m - 1) * n;
        for (int j = p.OpenEndMin; j < n; j++)
        {
            if (Accumulated[last + j] < BestCost)
            {
                BestCost = Accumulated[last + j];
                BestEnd = j;
            }
        }
    }

    /// <summary>
    /// Переходы через отрыв пера в строке <paramref name="i"/> (из строки <c>i − 1</c>): отрыв внутри диапазона соединения
    /// (пропуск геометрии бесплатен, цена — <c>liftJoinPenalty</c> либо 0 для ожидаемого) или отрыв в другом месте (цена
    /// <c>liftInLetterPenalty</c> и <c>skipBodyCostPerPoint</c> за каждую пропущенную точку тела).
    /// </summary>
    private void ComputeLiftRow(int i, AlignerConfig config, double[] best, int[] arg, short[] interval)
    {
        AlignProblem p = Problem;
        int n = p.N;
        long previous = (long)(i - 1) * n;
        double skip = config.SkipBodyCostPerPoint;
        double[] w = p.SkipPrefix;

        // Общий случай: j0 == j без пропуска; j0 < j — с ценой пропущенных точек тела между ними.
        double runMin = double.PositiveInfinity;
        int runArg = -1;
        for (int j = 0; j < n; j++)
        {
            double same = Accumulated[previous + j] + config.LiftInLetterPenalty;
            best[j] = same;
            arg[j] = j;
            interval[j] = -1;
            if (runArg >= 0)
            {
                double viaSkip = runMin + (skip * w[j]) + config.LiftInLetterPenalty;
                if (viaSkip < best[j])
                {
                    best[j] = viaSkip;
                    arg[j] = runArg;
                }
            }

            double candidate = Accumulated[previous + j] - (skip * w[j + 1]);
            if (candidate < runMin)
            {
                runMin = candidate;
                runArg = j;
            }
        }

        // Отрыв внутри диапазона соединения (диапазон расширен на допуск у границ тел: размер букв ребёнка отличается от
        // эталона, и конец его тела не совпадает с эталонным): цена — liftJoinPenalty (0 для ожидаемого отрыва) и пропущенные
        // точки тела внутри допуска; пропуск самой геометрии соединения бесплатен.
        for (int k = 0; k < p.Intervals.Length; k++)
        {
            (int lo, int hi, bool expected) = p.Intervals[k];
            double penalty = expected ? 0.0 : config.LiftJoinPenalty;
            double rangeMin = double.PositiveInfinity;
            int rangeArg = -1;
            for (int j = lo; j <= hi; j++)
            {
                double value = Accumulated[previous + j] + penalty;
                int source = j;
                if (rangeArg >= 0)
                {
                    double viaSkip = rangeMin + (skip * w[j]) + penalty;
                    if (viaSkip < value)
                    {
                        value = viaSkip;
                        source = rangeArg;
                    }
                }

                if (value < best[j])
                {
                    best[j] = value;
                    arg[j] = source;
                    interval[j] = (short)k;
                }

                double candidate = Accumulated[previous + j] - (skip * w[j + 1]);
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
        int n = Problem.N;
        var path = new List<PathCell>();
        var lifts = new List<LiftEvent>();
        int i = Problem.M - 1;
        int j = endJ;
        while (true)
        {
            path.Add(new PathCell(i, j));
            byte how = _back[((long)i * n) + j];
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
internal sealed class Alignment
{
    public required DpTable Forward { get; init; }

    public required List<PathCell> Path { get; init; }

    public required List<LiftEvent> Lifts { get; init; }

    public required double CostPerInkPoint { get; init; }

    /// <summary>Для каждой точки эталона — индекс точки рукописи (медиана по клеткам пути) или −1, если путь её не касается.</summary>
    public required int[] InkOfTemplate { get; init; }

    /// <summary>Для каждой точки рукописи — индекс точки эталона (медиана по клеткам пути).</summary>
    public required int[] TemplateOfInk { get; init; }
}

/// <summary>
/// DTW по цели для слова (ARCHITECTURE.md §6.4.4, A03, A09): состояние <c>(i, j)</c> — точка рукописи и точка эталона;
/// шаги диагональный, по рукописи и по эталону; полоса по x — «мягкая» (штраф вне полосы); отрыв пера — переход через
/// маркер отрыва с цены по месту (внутри диапазона соединения — альтернатива геометрии, пропуск соединения бесплатен);
/// допустимый старт — подход или начало тела первой буквы, допустимый конец — конец тела последней буквы или её выход.
/// Прямой и обратный проходы дают стоимость пути через любую клетку (уверенность границ).
/// </summary>
internal static class TargetAligner
{
    public static Alignment Align(AlignProblem problem, ScoringConfig config)
    {
        var table = new DpTable(problem, config.Word.Aligner, config.Dtw);
        if (table.BestEnd < 0)
        {
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

    /// <summary>Рост полной стоимости пути, если пройти через клетку <c>(i, j)</c> вместо лучшей (≥ 0).</summary>
    public static double Rise(Alignment alignment, DpTable backward, int i, int j)
    {
        AlignProblem p = alignment.Forward.Problem;
        if (i < 0 || i >= p.M || j < 0 || j >= p.N)
        {
            return double.PositiveInfinity;
        }

        double through = alignment.Forward.Cumulative(i, j) + backward.Cumulative(p.M - 1 - i, p.N - 1 - j) - alignment.Forward.Local[((long)i * p.N) + j];
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
