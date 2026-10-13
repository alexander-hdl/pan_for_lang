using PenForLang.Core.Geometry;
using PenForLang.Evaluation.Config;

namespace PenForLang.Evaluation.Pipeline;

/// <summary>Выравнивание штриха рукописи с основным штрихом эталона в прямом и обращённом направлении (DTW с открытыми концами).</summary>
internal sealed class PairAlignment
{
    public required ChildStroke Child { get; init; }

    public required RefStroke Ref { get; init; }

    public required DtwDirectionComparison Comparison { get; init; }

    public required List<DtwCell> ForwardPath { get; init; }

    public required List<DtwCell> ReversedPath { get; init; }

    /// <summary>Стоимость на точку рукописи лучшего из направлений (для матрицы назначения).</summary>
    public double BestCost =>
        Math.Min(
            Comparison.Forward.IsFeasible ? Comparison.Forward.CostPerFirstElement : double.PositiveInfinity,
            Comparison.Reversed.IsFeasible ? Comparison.Reversed.CostPerFirstElement : double.PositiveInfinity);

    /// <summary>Отношение стоимости обращённого выравнивания к прямому.</summary>
    public double ReverseRatio => Comparison.ReversedToForwardRatio;
}

/// <summary>Сопоставление штрихов рукописи и основных штрихов эталона (§6.4.10, п. 2).</summary>
internal static class StrokeMatcher
{
    public static PairAlignment Align(ChildStroke child, RefStroke reference, ScoringConfig config, DtwWorkspace workspace)
    {
        DtwConfig w = config.Dtw;
        var forward = new PositionDirectionCost(child.NPos, reference.Pos, child.NDir, reference.Dir, w.PositionWeight, w.DirectionWeight);
        var reversed = new PositionDirectionCost(child.NPos, reference.PosRev, child.NDir, reference.DirRev, w.PositionWeight, w.DirectionWeight);
        var options = new DtwOptions { OpenBegin = true, OpenEnd = true };
        var forwardPath = new List<DtwCell>(child.NPos.Length + reference.Pos.Length);
        var reversedPath = new List<DtwCell>(child.NPos.Length + reference.Pos.Length);
        DtwDirectionComparison comparison = Dtw.CompareDirections(
            child.NPos.Length,
            reference.Pos.Length,
            in forward,
            in reversed,
            options,
            workspace,
            forwardPath,
            reversedPath);
        return new PairAlignment { Child = child, Ref = reference, Comparison = comparison, ForwardPath = forwardPath, ReversedPath = reversedPath };
    }

    /// <summary>
    /// Венгерский алгоритм с неназначением: строки — штрихи рукописи, столбцы — основные штрихи эталона. Пара допустима,
    /// если длина штриха рукописи в пределах <c>matching.min/maxLengthRatio</c> от эталонной; назначается, только если
    /// стоимость меньше суммы цен неназначения. Возвращает для каждого штриха рукописи номер штриха эталона или −1.
    /// </summary>
    public static int[] Assign(
        List<ChildStroke> children,
        LetterReference reference,
        ScoringConfig config,
        DtwWorkspace workspace,
        out PairAlignment?[,] alignments)
    {
        int rows = children.Count;
        int cols = reference.Strokes.Length;
        alignments = new PairAlignment?[rows, cols];
        var costs = new double[rows * cols];
        MatchingConfig m = config.Matching;
        for (int i = 0; i < rows; i++)
        {
            for (int j = 0; j < cols; j++)
            {
                double ratio = children[i].Length / reference.Strokes[j].Length;
                if (ratio < m.MinLengthRatio || ratio > m.MaxLengthRatio)
                {
                    costs[(i * cols) + j] = double.PositiveInfinity;
                    continue;
                }

                PairAlignment alignment = Align(children[i], reference.Strokes[j], config, workspace);
                alignments[i, j] = alignment;
                costs[(i * cols) + j] = alignment.BestCost;
            }
        }

        var rowToCol = new int[rows];
        if (rows > 0 && cols > 0)
        {
            Hungarian.SolveWithUnassignedCost(costs, rows, cols, m.ExtraStrokeCost, m.MissingStrokeCost, rowToCol);
        }
        else
        {
            Array.Fill(rowToCol, -1);
        }

        return rowToCol;
    }

    /// <summary>
    /// Индекс точки рукописи, выровненной с каждой точкой эталона (среднее по клеткам пути, пустые места заполняются ближайшим
    /// слева и справа). Путь — выбранного направления, индексы <c>J</c> — исходного эталона.
    /// </summary>
    public static int[] ChildIndexOfRef(IReadOnlyList<DtwCell> path, int referenceLength)
    {
        var sum = new long[referenceLength];
        var count = new int[referenceLength];
        foreach (DtwCell cell in path)
        {
            sum[cell.J] += cell.I;
            count[cell.J]++;
        }

        var result = new int[referenceLength];
        Array.Fill(result, -1);
        for (int j = 0; j < referenceLength; j++)
        {
            if (count[j] > 0)
            {
                result[j] = (int)Math.Round((double)sum[j] / count[j], MidpointRounding.AwayFromZero);
            }
        }

        // Заполнение пропусков: ближайшее определённое значение (сначала слева, потом справа).
        int lastKnown = -1;
        for (int j = 0; j < referenceLength; j++)
        {
            if (result[j] >= 0)
            {
                lastKnown = result[j];
            }
            else if (lastKnown >= 0)
            {
                result[j] = lastKnown;
            }
        }

        lastKnown = -1;
        for (int j = referenceLength - 1; j >= 0; j--)
        {
            if (result[j] >= 0)
            {
                lastKnown = result[j];
            }
            else if (lastKnown >= 0)
            {
                result[j] = lastKnown;
            }
        }

        return result;
    }
}
