using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PenForLang.Tools.InkLab.Scene;

namespace PenForLang.Tools.InkLab.Views;

/// <summary>
/// Рисует <see cref="InkScene"/>: линейки (асцендер, x-height, базовая линия, десцендер), ломаные по буквам, отложенные штрихи
/// и метки стыков. Клик левой кнопкой сообщает координаты линейки (<see cref="RulerClicked"/>). Масштаб вписывает сцену в
/// элемент (<see cref="ViewTransform.Fit"/>).
/// </summary>
public sealed class StrokeView : FrameworkElement
{
    /// <summary>Сцена.</summary>
    public static readonly DependencyProperty SceneProperty = DependencyProperty.Register(
        nameof(Scene),
        typeof(InkScene),
        typeof(StrokeView),
        new FrameworkPropertyMetadata(InkScene.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x25, 0x63, 0xEB),
        Color.FromRgb(0xEA, 0x58, 0x0C),
        Color.FromRgb(0x16, 0xA3, 0x4A),
        Color.FromRgb(0x93, 0x33, 0xEA),
        Color.FromRgb(0x0E, 0x9F, 0x9F),
        Color.FromRgb(0xDC, 0x26, 0x26),
    ];

    private static readonly Typeface LabelFace = new("Segoe UI");

    /// <summary>Создаёт вид.</summary>
    public StrokeView()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = false;
    }

    /// <summary>Пользователь кликнул левой кнопкой: координаты <c>u</c> и <c>v</c> линейки (x-height) и нажат ли Shift.</summary>
    public event Action<double, double, bool>? RulerClicked;

    /// <summary>Сцена.</summary>
    public InkScene Scene
    {
        get => (InkScene)GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    /// <summary>Цвет буквы с номером <paramref name="group"/> (палитра по кругу); служебные группы — серые.</summary>
    public static Color ColorOf(int group) => group switch
    {
        SceneBuilder.NeutralGroup => Color.FromRgb(0x1F, 0x29, 0x37),
        SceneBuilder.JoinGroup => Color.FromRgb(0x6B, 0x72, 0x80),
        _ => Palette[((group % Palette.Length) + Palette.Length) % Palette.Length],
    };

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        drawingContext.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xFB, 0xF8, 0xEF)), null, new Rect(0, 0, ActualWidth, ActualHeight));
        InkScene scene = Scene;
        ViewTransform t = ViewTransform.Fit(scene, ActualWidth, ActualHeight);
        DrawGuides(drawingContext, scene.Guides, t);

        foreach (ScenePolyline line in scene.Polylines)
        {
            DrawPolyline(drawingContext, line, t);
        }

        foreach (SceneMarker marker in scene.Markers)
        {
            DrawMarker(drawingContext, marker, t, scene.Guides);
        }
    }

    /// <inheritdoc />
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        ArgumentNullException.ThrowIfNull(e);
        Point p = e.GetPosition(this);
        ViewTransform t = ViewTransform.Fit(Scene, ActualWidth, ActualHeight);
        RulerClicked?.Invoke(t.U(p.X), t.V(p.Y), (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
        e.Handled = true;
    }

    private static void DrawGuides(DrawingContext dc, SceneGuides g, ViewTransform t)
    {
        var baseline = new Pen(new SolidColorBrush(Color.FromRgb(0x3F, 0x5F, 0x9A)), 1.6);
        var xHeight = new Pen(new SolidColorBrush(Color.FromRgb(0x8F, 0xA6, 0xCF)), 1) { DashStyle = new DashStyle([4, 3], 0) };
        var outer = new Pen(new SolidColorBrush(Color.FromRgb(0xB9, 0xC6, 0xE0)), 1) { DashStyle = new DashStyle([2, 4], 0) };
        double width = 100000;
        void Line(Pen pen, double v) => dc.DrawLine(pen, new Point(-width, t.Y(v)), new Point(width, t.Y(v)));
        Line(outer, g.Ascender);
        if (g.Cap != g.Ascender)
        {
            Line(outer, g.Cap);
        }

        Line(xHeight, g.XHeight);
        Line(baseline, g.Baseline);
        Line(outer, g.Descender);
    }

    private static void DrawPolyline(DrawingContext dc, ScenePolyline line, ViewTransform t)
    {
        if (line.Points.IsDefaultOrEmpty)
        {
            return;
        }

        Color color = line.Style switch
        {
            SceneStyle.Ghost => Color.FromArgb(0x55, 0x6B, 0x72, 0x80),
            SceneStyle.Deferred => Color.FromRgb(0xC0, 0x26, 0xD3),
            _ => ColorOf(line.Group),
        };
        double thickness = line.Style == SceneStyle.Ghost ? 2.0 : 2.8;
        var pen = new Pen(new SolidColorBrush(color), thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        if (line.Style == SceneStyle.Deferred || line.Group == SceneBuilder.JoinGroup)
        {
            pen.DashStyle = new DashStyle([2.5, 1.5], 0);
        }

        if (line.Points.Length == 1)
        {
            ScenePoint only = line.Points[0];
            dc.DrawEllipse(new SolidColorBrush(color), null, new Point(t.X(only.U), t.Y(only.V)), thickness, thickness);
            return;
        }

        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(t.X(line.Points[0].U), t.Y(line.Points[0].V)), false, false);
            for (int i = 1; i < line.Points.Length; i++)
            {
                ctx.LineTo(new Point(t.X(line.Points[i].U), t.Y(line.Points[i].V)), true, true);
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private void DrawMarker(DrawingContext dc, SceneMarker marker, ViewTransform t, SceneGuides guides)
    {
        double x = t.X(marker.U);
        if (marker.Kind == MarkerKind.Info)
        {
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)), 2.2), new Point(x, t.Y(marker.V)), 7, 7);
            return;
        }

        var pen = new Pen(Brushes.Black, 1.4);
        dc.DrawLine(pen, new Point(x, t.Y(guides.XHeight + 0.15)), new Point(x, t.Y(guides.Baseline - 0.15)));
        dc.DrawEllipse(Brushes.Black, null, new Point(x, t.Y(marker.V)), 3.2, 3.2);
        if (marker.Label.Length > 0)
        {
            var text = new FormattedText(
                marker.Label,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                LabelFace,
                13,
                Brushes.Black,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(x - (text.Width / 2), t.Y(guides.XHeight + 0.15) - text.Height - 1));
        }
    }
}
