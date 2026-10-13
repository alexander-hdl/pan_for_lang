namespace PenForLang.Core.Geometry;

/// <summary>Сводка углов наклона письма (градусы; положительный — наклон вправо, как в <see cref="Deslant"/>).</summary>
/// <param name="Count">Число нисходящих квазивертикальных участков.</param>
/// <param name="MedianDegrees">Медиана углов (устойчива к выбросам) — оценка наклона для деслантинга (F6: <c>slantDeg</c>).</param>
/// <param name="MeanDegrees">Среднее углов.</param>
/// <param name="StdDevDegrees">Стандартное отклонение углов (F6: <c>slantSd</c>, консистентность наклона).</param>
public readonly record struct SlantEstimate(int Count, double MedianDegrees, double MeanDegrees, double StdDevDegrees)
{
    /// <summary>Нет ни одного участка: оценка наклона невозможна.</summary>
    public static SlantEstimate None { get; } = new(0, 0.0, 0.0, 0.0);
}

/// <summary>
/// Оценка наклона письма по нисходящим квазивертикалям (ARCHITECTURE.md §6.2/§6.4.2/§6.4.9: F6, деслантинг для
/// выравнивания). Берутся участки ломаной, идущие вниз (ось y вверх) и отклоняющиеся от вертикали не более
/// <c>maxDeviationDegrees</c>; для каждого достаточно длинного участка угол — главная ось (метод наименьших квадратов,
/// ортогональная регрессия). Угол положителен, если верх участка правее низа; тогда
/// <see cref="Deslant.ApplyDegrees"/> с этим углом выпрямляет письмо.
/// </summary>
public static class SlantEstimator
{
    /// <summary>
    /// Дописывает углы (градусы) всех нисходящих квазивертикальных участков ломаной. Направление в вершине — хорда между
    /// вершинами на ±<paramref name="halfWindow"/> (концы укорачиваются). Подряд идущие подходящие вершины образуют участок;
    /// участок короче <paramref name="minLength"/> отбрасывается. Ломаная должна быть сглажена и приблизительно равномерна.
    /// </summary>
    /// <param name="points">Вершины штриха.</param>
    /// <param name="minLength">Минимальная длина участка по дуге, &gt; 0.</param>
    /// <param name="maxDeviationDegrees">Допустимое отклонение хорды от вертикали, (0, 90).</param>
    /// <param name="anglesDegrees">Список, в конец которого добавляются углы.</param>
    /// <param name="halfWindow">Полуширина окна хорды в вершинах, ≥ 1.</param>
    public static void CollectAngles(
        ReadOnlySpan<Vec2> points,
        double minLength,
        double maxDeviationDegrees,
        List<double> anglesDegrees,
        int halfWindow = 2)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDeviationDegrees);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(maxDeviationDegrees, 90.0);
        ArgumentOutOfRangeException.ThrowIfLessThan(halfWindow, 1);
        ArgumentNullException.ThrowIfNull(anglesDegrees);
        int n = points.Length;
        if (n < 2)
        {
            return;
        }

        double tanLimit = Math.Tan(maxDeviationDegrees * (Math.PI / 180.0));
        int runStart = -1;
        for (int i = 0; i <= n; i++)
        {
            bool fits = false;
            if (i < n)
            {
                Vec2 d = points[Math.Min(i + halfWindow, n - 1)] - points[Math.Max(i - halfWindow, 0)];
                fits = d.Y < 0.0 && Math.Abs(d.X) <= tanLimit * -d.Y;
            }

            if (fits)
            {
                if (runStart < 0)
                {
                    runStart = i;
                }

                continue;
            }

            if (runStart >= 0)
            {
                ReadOnlySpan<Vec2> run = points[runStart..i];
                if (run.Length >= 2 && PolylineMath.Length(run) >= minLength)
                {
                    anglesDegrees.Add(PrincipalSlantDegrees(run));
                }

                runStart = -1;
            }
        }
    }

    /// <summary>Сводка (медиана, среднее, стандартное отклонение) по набору углов в градусах.</summary>
    /// <param name="anglesDegrees">Углы, например собранные <see cref="CollectAngles"/> по всем штрихам попытки.</param>
    public static SlantEstimate Summarize(ReadOnlySpan<double> anglesDegrees)
    {
        int n = anglesDegrees.Length;
        if (n == 0)
        {
            return SlantEstimate.None;
        }

        double[] sorted = anglesDegrees.ToArray();
        Array.Sort(sorted);
        double median = (n % 2) == 1 ? sorted[n / 2] : (sorted[(n / 2) - 1] + sorted[n / 2]) * 0.5;
        double mean = 0.0;
        foreach (double a in sorted)
        {
            mean += a;
        }

        mean /= n;
        double variance = 0.0;
        foreach (double a in sorted)
        {
            variance += (a - mean) * (a - mean);
        }

        return new SlantEstimate(n, median, mean, Math.Sqrt(variance / n));
    }

    /// <summary>Оценка наклона по одному штриху: <see cref="CollectAngles"/> + <see cref="Summarize"/>.</summary>
    /// <param name="points">Вершины штриха.</param>
    /// <param name="minLength">Минимальная длина участка по дуге.</param>
    /// <param name="maxDeviationDegrees">Допустимое отклонение хорды от вертикали.</param>
    /// <param name="halfWindow">Полуширина окна хорды в вершинах.</param>
    public static SlantEstimate Estimate(
        ReadOnlySpan<Vec2> points,
        double minLength,
        double maxDeviationDegrees,
        int halfWindow = 2)
    {
        var angles = new List<double>();
        CollectAngles(points, minLength, maxDeviationDegrees, angles, halfWindow);
        return Summarize(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(angles));
    }

    // Главная ось участка по ортогональной регрессии; угол от вертикали, положителен при наклоне вправо (верх правее низа).
    private static double PrincipalSlantDegrees(ReadOnlySpan<Vec2> run)
    {
        double mx = 0.0;
        double my = 0.0;
        foreach (Vec2 p in run)
        {
            mx += p.X;
            my += p.Y;
        }

        mx /= run.Length;
        my /= run.Length;
        double sxx = 0.0;
        double sxy = 0.0;
        double syy = 0.0;
        foreach (Vec2 p in run)
        {
            double dx = p.X - mx;
            double dy = p.Y - my;
            sxx += dx * dx;
            sxy += dx * dy;
            syy += dy * dy;
        }

        double theta = 0.5 * Math.Atan2(2.0 * sxy, sxx - syy);
        double vx = Math.Cos(theta);
        double vy = Math.Sin(theta);
        if (vy < 0.0)
        {
            vx = -vx;
            vy = -vy;
        }

        return Math.Atan2(vx, vy) * (180.0 / Math.PI);
    }
}
