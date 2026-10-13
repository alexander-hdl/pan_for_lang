using System.Windows;
using System.Windows.Media;

namespace PenForLang.App.Wpf;

/// <summary>
/// Слой девяти мишеней точности (клавиша T, <see cref="AccuracyTargets"/>; ARCHITECTURE.md §10.9). От временной отрисовки точек
/// V0.5 (<c>DebugInkCanvas</c>) остались только мишени: чернила теперь рисует <c>LiveInkLayer</c>. Ввод слой не принимает.
/// </summary>
public sealed class AccuracyTargetsLayer : FrameworkElement
{
    private static readonly Pen TargetPen = MakePen(Color.FromRgb(0x20, 0x80, 0x20), 1.0);

    private bool _showTargets;

    /// <summary>Создаёт слой.</summary>
    public AccuracyTargetsLayer()
    {
        IsHitTestVisible = false;
    }

    /// <summary>Показывать девять мишеней для проверки точности.</summary>
    public bool ShowTargets
    {
        get => _showTargets;
        set
        {
            _showTargets = value;
            InvalidateVisual();
        }
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        if (!_showTargets)
        {
            return;
        }

        foreach ((double fx, double fy) in AccuracyTargets.Grid(1.0, 1.0))
        {
            double tx = fx * RenderSize.Width;
            double ty = fy * RenderSize.Height;
            drawingContext.DrawLine(TargetPen, new Point(tx - 14, ty), new Point(tx + 14, ty));
            drawingContext.DrawLine(TargetPen, new Point(tx, ty - 14), new Point(tx, ty + 14));
            drawingContext.DrawEllipse(null, TargetPen, new Point(tx, ty), 5, 5);
        }
    }

    private static Pen MakePen(Color color, double thickness)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }
}
