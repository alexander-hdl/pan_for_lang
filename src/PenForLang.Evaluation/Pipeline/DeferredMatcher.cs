using PenForLang.Core.Geometry;
using PenForLang.Evaluation.Config;
using PenForLang.Glyphs;

namespace PenForLang.Evaluation.Pipeline;

/// <summary>Итог по слоту отложенного штриха.</summary>
internal sealed class DeferredAssignment
{
    public required RefDeferred Slot { get; init; }

    public ChildStroke? Stroke { get; init; }

    /// <summary>Расстояние середины штриха рукописи до ожидаемого положения по реальному стволу.</summary>
    public double? Distance { get; init; }

    /// <summary>
    /// Эллиптическое расстояние до ожидаемого положения: смещение по u делится на <c>deferred.nearU</c>, по v — на <c>deferred.nearV</c>;
    /// не больше 1 — на месте.
    /// </summary>
    public double? NormalizedDistance { get; init; }

    /// <summary>Наименьшее расстояние перекладины до ствола рукописи (только для перекладины с привязкой к стволу).</summary>
    public double? CrossGap { get; init; }

    /// <summary>Перекладина не пересекает ствол (<c>deferred.barMustCrossStem</c>).</summary>
    public bool CrossFailed { get; init; }

    public bool Near { get; init; }

    public DeferredTiming Timing { get; init; }

    /// <summary>Перекладина <c>ltr</c> написана справа налево.</summary>
    public bool WrongDirection { get; init; }
}

/// <summary>Результат назначения отложенных штрихов.</summary>
internal sealed class DeferredOutcome
{
    public required List<DeferredAssignment> Slots { get; init; }

    /// <summary>Штрихи рукописи, не ставшие ни основным, ни отложенным: лишние черты (D3).</summary>
    public required List<ChildStroke> ExtraMarks { get; init; }

    /// <summary>Лишние штрихи основного следа (P4).</summary>
    public required List<ChildStroke> ExtraMain { get; init; }
}

/// <summary>Привязка слота к реальному стволу рукописи: ожидаемая середина штриха и ломаная ствола (для проверки пересечения).</summary>
/// <param name="Expected">Ожидаемая середина отложенного штриха: точка на стволе в доле якоря плюс смещение (нормализованные координаты).</param>
/// <param name="Stem">Нормализованные точки ствола рукописи.</param>
internal sealed record DeferredAnchor(Vec2 Expected, IReadOnlyList<Vec2> Stem);

/// <summary>
/// Назначение отложенных штрихов (§6.4.8): слоты — отложенные штрихи глифа, якорь пересчитывается по реальному стволу
/// рукописи (через выравнивание DTW), назначение — Венгерский алгоритм с неназначением; время — по хронологии штрихов.
/// </summary>
internal static class DeferredMatcher
{
    public static DeferredOutcome Assign(
        IReadOnlyList<ChildStroke> leftover,
        LetterReference reference,
        IReadOnlyList<PairMetrics> pairs,
        ScoringConfig config)
    {
        int lastMainChrono = -1;
        foreach (PairMetrics pair in pairs)
        {
            lastMainChrono = Math.Max(lastMainChrono, pair.Child.Chrono);
        }

        return Assign(
            leftover,
            reference.Deferred,
            j =>
            {
                RefDeferred slot = reference.Deferred[j];
                PairMetrics? pair = pairs.FirstOrDefault(p => p.Ref.Id == slot.AnchorStrokeId);
                return pair is null ? null : new DeferredAnchor(ExpectedCenter(slot, pair), pair.Child.NPos);
            },
            lastMainChrono,
            config);
    }

    /// <summary>
    /// Общее ядро назначения для буквы и для слова. <paramref name="anchorOf"/> даёт привязку слота к реальному стволу или
    /// <c>null</c>, если ствола нет (P5): положение судить не по чему, назначается любой подходящий штрих.
    /// </summary>
    /// <param name="leftover">Штрихи рукописи, не вошедшие в основной след.</param>
    /// <param name="slots">Слоты отложенных штрихов.</param>
    /// <param name="anchorOf">Привязка слота (по индексу) к стволу рукописи.</param>
    /// <param name="lastMainChrono">Хронологический номер последнего штриха основного следа (−1, если его нет).</param>
    /// <param name="config">Конфигурация.</param>
    public static DeferredOutcome Assign(
        IReadOnlyList<ChildStroke> leftover,
        IReadOnlyList<RefDeferred> slots,
        Func<int, DeferredAnchor?> anchorOf,
        int lastMainChrono,
        ScoringConfig config)
    {
        DeferredConfig d = config.Deferred;
        int rows = leftover.Count;
        int cols = slots.Count;
        var costs = new double[rows * cols];
        var anchors = new DeferredAnchor?[cols];
        for (int j = 0; j < cols; j++)
        {
            RefDeferred slot = slots[j];
            anchors[j] = anchorOf(j);
            for (int i = 0; i < rows; i++)
            {
                // Нет ствола, к которому привязан слот (P5): положение судить не по чему, назначается любой подходящий штрих.
                costs[(i * cols) + j] = !Compatible(leftover[i], slot, d)
                    ? double.PositiveInfinity
                    : (anchors[j] is { } anchor ? Vec2.Distance(leftover[i].NCenter, anchor.Expected) : 0.0);
            }
        }

        var rowToCol = new int[rows];
        if (rows > 0 && cols > 0)
        {
            double half = d.AssignMaxDistance * 0.5;
            Hungarian.SolveWithUnassignedCost(costs, rows, cols, half, half, rowToCol);
        }
        else
        {
            Array.Fill(rowToCol, -1);
        }

        var result = new List<DeferredAssignment>(cols);
        var assigned = new HashSet<int>();
        for (int j = 0; j < cols; j++)
        {
            RefDeferred slot = slots[j];
            int row = Array.IndexOf(rowToCol, j);
            if (row < 0)
            {
                result.Add(new DeferredAssignment { Slot = slot, Timing = DeferredTiming.None });
                continue;
            }

            assigned.Add(row);
            ChildStroke stroke = leftover[row];
            DeferredAnchor? anchored = anchors[j];
            double distance = anchored is not null ? costs[(row * cols) + j] : 0.0;
            double? normalized = null;
            double? crossGap = null;
            bool crossFailed = false;
            bool near = true;
            if (anchored is not null)
            {
                Vec2 delta = stroke.NCenter - anchored.Expected;
                double nearV = slot.Kind == DeferredKind.Dot && delta.Y > 0.0 ? d.NearVUpDot : d.NearV;
                normalized = Math.Sqrt(Math.Pow(delta.X / d.NearU, 2) + Math.Pow(delta.Y / nearV, 2));
                near = normalized <= 1.0;
                if (d.BarMustCrossStem && slot.Kind != DeferredKind.Dot)
                {
                    Vec2[] stem = [.. anchored.Stem];
                    crossGap = stroke.NPos.Min(p => PolylineMath.DistanceToPolyline(p, stem));
                    crossFailed = crossGap > d.BarCrossTolerance;
                    near &= !crossFailed;
                }
            }

            DeferredTiming timing = lastMainChrono < 0 || stroke.Chrono > lastMainChrono ? DeferredTiming.AfterWord : DeferredTiming.Immediate;
            bool wrongDirection = false;
            if (slot.Direction == DeferredDirection.LeftToRight && stroke.Pos.Length >= 2)
            {
                double net = stroke.Pos[^1].X - stroke.Pos[0].X;
                wrongDirection = net < -config.Process.Direction.BarMinDirectionalShare * stroke.Length;
            }

            result.Add(new DeferredAssignment
            {
                Slot = slot,
                Stroke = stroke,
                Distance = anchored is not null ? distance : null,
                NormalizedDistance = normalized,
                CrossGap = crossGap,
                CrossFailed = crossFailed,
                Near = near,
                Timing = timing,
                WrongDirection = wrongDirection,
            });
        }

        var extraMarks = new List<ChildStroke>();
        var extraMain = new List<ChildStroke>();
        for (int i = 0; i < rows; i++)
        {
            if (assigned.Contains(i))
            {
                continue;
            }

            (IsMark(leftover[i], d) ? extraMarks : extraMain).Add(leftover[i]);
        }

        return new DeferredOutcome { Slots = result, ExtraMarks = extraMarks, ExtraMain = extraMain };
    }

    /// <summary>Лишняя черта (точка или короткая горизонтальная перекладина), а не лишний штрих основного следа.</summary>
    public static bool IsMark(ChildStroke stroke, DeferredConfig d)
    {
        if (stroke.NExtent <= d.DotMaxLength)
        {
            return true;
        }

        if (stroke.NLength > d.ExtraMarkMaxLength || stroke.Pos.Length < 2)
        {
            return false;
        }

        double dx = Math.Abs(stroke.Pos[^1].X - stroke.Pos[0].X);
        return dx >= d.ExtraMarkHorizontalShare * stroke.Length; // доли длины — безразмерные
    }

    private static bool Compatible(ChildStroke stroke, RefDeferred slot, DeferredConfig d)
    {
        if (slot.Kind == DeferredKind.Dot)
        {
            return stroke.NExtent <= d.DotMaxLength;
        }

        double ratio = stroke.NLength / slot.Length;
        return ratio >= d.BarMinLengthRatio && ratio <= d.BarMaxLengthRatio;
    }

    /// <summary>Ожидаемая середина штриха: точка на реальном стволе рукописи в доле якоря плюс смещение.</summary>
    private static Vec2 ExpectedCenter(RefDeferred slot, PairMetrics pair)
    {
        int j = pair.Ref.IndexOfBodyFraction(slot.AnchorT);
        int i = pair.ChildOfRef[j];
        return pair.Child.NPos[i] + slot.AnchorOffset;
    }
}
