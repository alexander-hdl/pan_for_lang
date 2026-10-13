using System.Windows;
using System.Windows.Media;

namespace PenForLang.Wpf.Controls;

/// <summary>
/// Слой линеек письма (заготовка V0.1; полная реализация — V0.6). Рисует горизонтальные линии
/// с шагом <see cref="LineSpacingMm"/> миллиметров.
/// </summary>
public sealed class GuidesLayer : FrameworkElement
{
    /// <summary>Шаг линейки, мм.</summary>
    public static readonly DependencyProperty LineSpacingMmProperty = DependencyProperty.Register(
        nameof(LineSpacingMm),
        typeof(double),
        typeof(GuidesLayer),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender),
        value => value is double d && d > 0);

    /// <summary>Шаг линейки в миллиметрах (строго больше нуля).</summary>
    public double LineSpacingMm
    {
        get => (double)GetValue(LineSpacingMmProperty);
        set => SetValue(LineSpacingMmProperty, value);
    }

    /// <summary>Количество линий, помещающихся в заданную высоту (DIP).</summary>
    public int CountLines(double heightDips) =>
        heightDips <= 0 ? 0 : (int)Math.Floor(heightDips / Units.MmToDip(LineSpacingMm)) + 1;

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var pen = new Pen(Brushes.LightSteelBlue, 1);
        pen.Freeze();
        double step = Units.MmToDip(LineSpacingMm);
        for (double y = 0; y <= ActualHeight; y += step)
        {
            drawingContext.DrawLine(pen, new Point(0, y), new Point(ActualWidth, y));
        }
    }
}
