using System.Windows;
using System.Windows.Media;
using PenForLang.Glyphs;
using PenForLang.Ink;

namespace PenForLang.Wpf.Controls;

/// <summary>
/// Слой линеек прописи (ARCHITECTURE.md §7.3): базовая линия, x-height, асцендеры, десцендеры и наклонные линии стиля.
/// Размер строки задаётся <see cref="Guides"/> (<see cref="GuideFrame"/>, физические px поверхности — строится в мм через
/// <c>GuideFrame.FromMm</c>); слой переводит px в DIP одним делением на <see cref="EffectiveDpiScale"/>, поэтому физический размер
/// строки не зависит от масштаба Windows (A12). Слой не перехватывает ввод.
/// </summary>
public sealed class GuidesLayer : FrameworkElement
{
    /// <summary>Линейка попытки (px поверхности).</summary>
    public static readonly DependencyProperty GuidesProperty = DependencyProperty.Register(
        nameof(Guides),
        typeof(GuideFrame),
        typeof(GuidesLayer),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Стиль письма (линии и наклон).</summary>
    public static readonly DependencyProperty ManifestProperty = DependencyProperty.Register(
        nameof(Manifest),
        typeof(StyleManifest),
        typeof(GuidesLayer),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>DPI-масштаб; 0 или меньше — брать у окна автоматически.</summary>
    public static readonly DependencyProperty DpiScaleProperty = DependencyProperty.Register(
        nameof(DpiScale),
        typeof(double),
        typeof(GuidesLayer),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Повторять строки вверх и вниз по <c>LineSpacing</c>.</summary>
    public static readonly DependencyProperty RepeatRowsProperty = DependencyProperty.Register(
        nameof(RepeatRows),
        typeof(bool),
        typeof(GuidesLayer),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Pen BaselinePen = MakePen(Color.FromRgb(0x3F, 0x5F, 0x9A), 1.0, null);
    private static readonly Pen XHeightPen = MakePen(Color.FromRgb(0x8F, 0xA6, 0xCF), 1.0, new DashStyle([4, 3], 0));
    private static readonly Pen OuterPen = MakePen(Color.FromRgb(0xB9, 0xC6, 0xE0), 1.0, new DashStyle([2, 4], 0));
    private static readonly Pen SlantPen = MakePen(Color.FromArgb(0x60, 0xB9, 0xC6, 0xE0), 1.0, null);

    private readonly TranslateTransform _snap = new();

    /// <summary>Создаёт слой; ввод слой не принимает.</summary>
    public GuidesLayer()
    {
        IsHitTestVisible = false;
        RenderTransform = _snap;
        LayoutUpdated += (_, _) => SnapToPixelGrid();
    }

    /// <summary>Сдвиг, прижимающий слой к сетке физических пикселей (<see cref="PixelSnap"/>): линии в 1 px иначе расплываются.</summary>
    public Vector SnapOffset => new(_snap.X, _snap.Y);

    /// <summary>Прижимает слой к сетке физических пикселей.</summary>
    public void SnapToPixelGrid() => PixelSnap.Apply(this, _snap, EffectiveDpiScale);

    /// <summary>Линейка попытки.</summary>
    public GuideFrame? Guides
    {
        get => (GuideFrame?)GetValue(GuidesProperty);
        set => SetValue(GuidesProperty, value);
    }

    /// <summary>Стиль письма.</summary>
    public StyleManifest? Manifest
    {
        get => (StyleManifest?)GetValue(ManifestProperty);
        set => SetValue(ManifestProperty, value);
    }

    /// <summary>DPI-масштаб монитора; 0 — определять автоматически (<see cref="VisualTreeHelper.GetDpi"/>).</summary>
    public double DpiScale
    {
        get => (double)GetValue(DpiScaleProperty);
        set => SetValue(DpiScaleProperty, value);
    }

    /// <summary>Повторять строки через <c>LineSpacing</c> x-height.</summary>
    public bool RepeatRows
    {
        get => (bool)GetValue(RepeatRowsProperty);
        set => SetValue(RepeatRowsProperty, value);
    }

    /// <summary>Масштаб, которым слой переводит px в DIP: <see cref="DpiScale"/> или DPI окна.</summary>
    public double EffectiveDpiScale => DpiScale > 0 ? DpiScale : VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>Отрезки линейки для текущих свойств и размера слоя (то, что рисует <see cref="OnRender"/>, без привязки к пикселям).</summary>
    public IReadOnlyList<GuideSegment> Segments =>
        Guides is { } frame && Manifest is { } style
            ? GuidesGeometry.Compute(frame, style, EffectiveDpiScale, ActualWidth, ActualHeight, RepeatRows)
            : [];

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        double dpi = EffectiveDpiScale;
        foreach (GuideSegment s in Segments)
        {
            Pen pen = PenFor(s.Kind);
            if (s.Kind == GuideKind.Slant)
            {
                drawingContext.DrawLine(pen, new Point(s.X1, s.Y1), new Point(s.X2, s.Y2));
                continue;
            }

            // Горизонтали прижимаем к физическим пикселям: линия в 1 px при дробном масштабе (125%, 150%) иначе расплывается.
            double thicknessPx = s.Kind == GuideKind.Baseline ? 2 : 1;
            double y = Snap(s.Y1, dpi, thicknessPx);
            var snapped = new Pen(pen.Brush, thicknessPx / dpi) { DashStyle = pen.DashStyle, DashCap = PenLineCap.Flat };
            drawingContext.DrawLine(snapped, new Point(s.X1, y), new Point(s.X2, y));
        }
    }

    private static Pen PenFor(GuideKind kind) => kind switch
    {
        GuideKind.Baseline => BaselinePen,
        GuideKind.XHeight => XHeightPen,
        GuideKind.Slant => SlantPen,
        _ => OuterPen,
    };

    private static double Snap(double dip, double dpi, double thicknessPx)
    {
        double px = dip * dpi;
        double snapped = thicknessPx % 2 == 0 ? Math.Round(px) : Math.Floor(px) + 0.5;
        return snapped / dpi;
    }

    private static Pen MakePen(Color color, double thickness, DashStyle? dash)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var pen = new Pen(brush, thickness);
        if (dash is not null)
        {
            dash.Freeze();
            pen.DashStyle = dash;
        }

        pen.Freeze();
        return pen;
    }
}
