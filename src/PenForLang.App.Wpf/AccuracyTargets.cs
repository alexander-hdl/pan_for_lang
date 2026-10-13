namespace PenForLang.App.Wpf;

/// <summary>
/// Девять мишеней для проверки точности пера (ARCHITECTURE.md §10.9, V0.5): сетка 3×3 на 10%, 50%, 90% размеров холста.
/// Чистые функции: координаты — физические пиксели поверхности (A12), так что результат не зависит от DPI-масштаба.
/// </summary>
public static class AccuracyTargets
{
    /// <summary>Доли ширины и высоты холста, на которых стоят мишени.</summary>
    public static readonly IReadOnlyList<double> Fractions = [0.1, 0.5, 0.9];

    /// <summary>Мишени по строкам (слева направо, сверху вниз): номера 1..9 в панели диагностики.</summary>
    public static IReadOnlyList<(double X, double Y)> Grid(double widthPx, double heightPx)
    {
        var points = new List<(double X, double Y)>(9);
        foreach (double fy in Fractions)
        {
            foreach (double fx in Fractions)
            {
                points.Add((widthPx * fx, heightPx * fy));
            }
        }

        return points;
    }

    /// <summary>Ближайшая мишень к точке: номер (1..9) и смещение точки от центра мишени, px.</summary>
    public static (int Number, double Dx, double Dy) Nearest(IReadOnlyList<(double X, double Y)> targets, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0)
        {
            throw new ArgumentException("There are no targets.", nameof(targets));
        }

        int best = 0;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < targets.Count; i++)
        {
            double dx = x - targets[i].X;
            double dy = y - targets[i].Y;
            double distance = (dx * dx) + (dy * dy);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return (best + 1, x - targets[best].X, y - targets[best].Y);
    }
}
