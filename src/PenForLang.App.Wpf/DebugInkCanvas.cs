using System.Windows;
using System.Windows.Media;
using PenForLang.Ink;

namespace PenForLang.App.Wpf;

/// <summary>
/// ВРЕМЕННАЯ отладочная отрисовка точек пера (V0.5): полноценный <c>LiveInkLayer</c> с низкой задержкой — задача V0.6.
/// Рисует ломаные штрихов, точки (цвет — давление, красный контур — за краем холста, синий — наведение) и маркер кончика.
/// Не оптимизирована: перерисовка целиком на каждую пачку, лимит точек <see cref="MaxPoints"/>.
/// </summary>
public sealed class DebugInkCanvas : FrameworkElement
{
    /// <summary>Сколько точек хранится; старейшие отбрасываются.</summary>
    public const int MaxPoints = 8000;

    private static readonly Pen StrokePen = MakePen(Colors.Black, 1.5);
    private static readonly Pen ClippedPen = MakePen(Colors.Red, 1.0);
    private static readonly Pen TargetPen = MakePen(Color.FromRgb(0x20, 0x80, 0x20), 1.0);
    private static readonly Pen CrossPen = MakePen(Color.FromRgb(0x30, 0x90, 0xE0), 1.0);
    private static readonly Brush HoverBrush = MakeBrush(Color.FromRgb(0x30, 0x90, 0xE0));
    private static readonly Brush[] ShadeBrushes = Enumerable.Range(0, 256).Select(i => (Brush)MakeBrush(Color.FromRgb((byte)i, 0x40, 0x40))).ToArray();

    private readonly List<DebugPoint> _points = [];
    private DebugPoint? _tip;
    private bool _showTargets;

    /// <summary>Показывать девять мишеней для проверки точности (<see cref="AccuracyTargets"/>).</summary>
    public bool ShowTargets
    {
        get => _showTargets;
        set
        {
            _showTargets = value;
            InvalidateVisual();
        }
    }

    /// <summary>Сколько точек сейчас хранится.</summary>
    public int PointCount => _points.Count;

    /// <summary>Добавляет пачку точек; координаты поверхности (физические px) переводятся в DIP делением на масштаб.</summary>
    public void AddSamples(ReadOnlySpan<PenSample> samples, double dpiScale)
    {
        double scale = dpiScale > 0 ? dpiScale : 1.0;
        foreach (PenSample s in samples)
        {
            var point = new DebugPoint(s.X / scale, s.Y / scale, s.Phase, s.Pressure, s.IsClipped, s.IsAuxiliary);
            if (s.Phase == PenPhase.Hover)
            {
                _tip = point;
                continue;
            }

            _tip = s.Phase == PenPhase.Up ? null : point;
            _points.Add(point);
        }

        if (_points.Count > MaxPoints)
        {
            _points.RemoveRange(0, _points.Count - MaxPoints);
        }

        InvalidateVisual();
    }

    /// <summary>Очищает холст.</summary>
    public void Clear()
    {
        _points.Clear();
        _tip = null;
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        if (_showTargets)
        {
            foreach ((double fx, double fy) in AccuracyTargets.Grid(1.0, 1.0))
            {
                double tx = fx * RenderSize.Width;
                double ty = fy * RenderSize.Height;
                drawingContext.DrawLine(TargetPen, new Point(tx - 14, ty), new Point(tx + 14, ty));
                drawingContext.DrawLine(TargetPen, new Point(tx, ty - 14), new Point(tx, ty + 14));
                drawingContext.DrawEllipse(null, TargetPen, new Point(tx, ty), 5, 5);
            }
        }

        DebugPoint? previous = null;
        foreach (DebugPoint p in _points)
        {
            if (p.Phase == PenPhase.Down)
            {
                previous = null;
            }

            if (previous is DebugPoint prev)
            {
                drawingContext.DrawLine(p.Clipped ? ClippedPen : StrokePen, new Point(prev.X, prev.Y), new Point(p.X, p.Y));
            }

            double radius = 1.0 + (p.Pressure * 3.0);
            byte shade = (byte)(255 - (int)(Math.Clamp(p.Pressure, 0f, 1f) * 200));
            drawingContext.DrawEllipse(
                p.Auxiliary ? Brushes.Orange : ShadeBrushes[shade],
                null,
                new Point(p.X, p.Y),
                radius,
                radius);
            previous = p;
        }

        if (_tip is DebugPoint tip)
        {
            drawingContext.DrawEllipse(HoverBrush, null, new Point(tip.X, tip.Y), 3, 3);
            drawingContext.DrawLine(CrossPen, new Point(tip.X - 8, tip.Y), new Point(tip.X + 8, tip.Y));
            drawingContext.DrawLine(CrossPen, new Point(tip.X, tip.Y - 8), new Point(tip.X, tip.Y + 8));
        }
    }

    private static Pen MakePen(Color color, double thickness)
    {
        var pen = new Pen(MakeBrush(color), thickness);
        pen.Freeze();
        return pen;
    }

    private static SolidColorBrush MakeBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private readonly record struct DebugPoint(double X, double Y, PenPhase Phase, float Pressure, bool Clipped, bool Auxiliary);
}
