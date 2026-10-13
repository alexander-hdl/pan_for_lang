using System.Windows;
using System.Windows.Media;

namespace PenForLang.Wpf.Controls;

/// <summary>
/// Прижимает слой к сетке физических пикселей. Если выше слоя в разметке стоит полоса дробной высоты (в DIP), левый верхний угол слоя
/// при масштабе 125% или 150% попадает в долю физического пикселя, и растр или тонкие линии выводятся размытыми (замер ревью V0.6:
/// слой на y = 175,805 физ. px при 150%). Основное средство — <c>UseLayoutRounding</c> окна; это страховка: доля вычисляется по
/// положению слоя относительно корня и гасится сдвигом <see cref="TranslateTransform"/> слоя.
/// </summary>
public static class PixelSnap
{
    /// <summary>
    /// Пересчитывает сдвиг <paramref name="correction"/> так, чтобы положение слоя в корне визуального дерева (× <paramref name="dpiScale"/>)
    /// стало целым числом физических px.
    /// </summary>
    /// <param name="element">Слой.</param>
    /// <param name="correction">Сдвиг, назначенный слою как <c>RenderTransform</c>.</param>
    /// <param name="dpiScale">DPI-масштаб монитора.</param>
    /// <returns><c>true</c>, если сдвиг изменился.</returns>
    public static bool Apply(FrameworkElement element, TranslateTransform correction, double dpiScale)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(correction);
        if (!(dpiScale > 0) || RootOf(element) is not { } root)
        {
            return false;
        }

        Point origin;
        try
        {
            origin = element.TransformToAncestor(root).Transform(new Point(0, 0));
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        // В положение уже входит текущий сдвиг; «номинальное» положение — без него.
        double nominalX = origin.X - correction.X;
        double nominalY = origin.Y - correction.Y;
        double dx = Correction(nominalX, dpiScale);
        double dy = Correction(nominalY, dpiScale);
        if (Math.Abs(dx - correction.X) < 1e-9 && Math.Abs(dy - correction.Y) < 1e-9)
        {
            return false;
        }

        correction.X = dx;
        correction.Y = dy;
        return true;
    }

    /// <summary>Сдвиг в DIP, переводящий положение <paramref name="dip"/> на ближайший целый физический пиксель.</summary>
    public static double Correction(double dip, double dpiScale)
    {
        double physical = dip * dpiScale;
        return (Math.Round(physical) - physical) / dpiScale;
    }

    private static Visual? RootOf(Visual element)
    {
        Visual? current = element;
        while (VisualTreeHelper.GetParent(current) is Visual parent)
        {
            current = parent;
        }

        return ReferenceEquals(current, element) ? null : current;
    }
}
