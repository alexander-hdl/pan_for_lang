using System.Windows;
using System.Windows.Media;
using PenForLang.Core;
using PenForLang.Core.Geometry;
using PenForLang.Ink;

namespace PenForLang.Wpf.Controls;

/// <summary>Цвет подсветки эталона: без красных тонов (METHODOLOGY.md §7.6, никаких красных крестов).</summary>
public enum HighlightColor
{
    /// <summary>Подсказка: тёплый янтарный.</summary>
    Warm,

    /// <summary>Исправленная часть: зелёный.</summary>
    Positive,
}

/// <summary>Кривая мигания точки старта (чистая функция, проверяется без окна).</summary>
public static class PulseCurve
{
    /// <summary>Фаза мигания, [0, 1]: плавно 0 → 1 → 0 с периодом <paramref name="periodMs"/> (косинус, без резких вспышек).</summary>
    /// <param name="elapsedMs">Время с начала мигания, мс.</param>
    /// <param name="periodMs">Период, мс; &gt; 0.</param>
    public static double Phase(double elapsedMs, double periodMs)
    {
        if (!(periodMs > 0) || !double.IsFinite(elapsedMs))
        {
            return 0.0;
        }

        return 0.5 - (0.5 * Math.Cos(2.0 * Math.PI * Math.Max(elapsedMs, 0.0) / periodMs));
    }
}

/// <summary>
/// Слой подсветки эталона (V0.8, ARCHITECTURE.md §4.1 «наложение по Detail.Alignment», §7.5): ломаные участков эталона в координатах
/// линейки полупрозрачной широкой линией и мигающая точка старта. Слой ничего не принимает: стоит под чернилами, чтобы рука и
/// след пера оставались видны. Точка старта всегда зелёная; янтарным мигает только кольцо вокруг неё (период по <see cref="IMonotonicClock"/>).
/// Мигание идёт заданное время и либо исчезает (мягкий сигнал по ходу письма), либо замирает статичным кольцом (подсветка в обратной связи).
/// На <c>CompositionTarget.Rendering</c> слой подписан только пока мигает.
/// </summary>
public sealed class TemplateHighlightLayer : FrameworkElement
{
    /// <summary>Линейка, в координатах которой заданы ломаные (<c>u = 0</c> — её <c>OriginXPx</c>).</summary>
    public static readonly DependencyProperty GuidesProperty = DependencyProperty.Register(
        nameof(Guides),
        typeof(GuideFrame),
        typeof(TemplateHighlightLayer),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>DPI-масштаб; 0 или меньше — брать у окна автоматически.</summary>
    public static readonly DependencyProperty DpiScaleProperty = DependencyProperty.Register(
        nameof(DpiScale),
        typeof(double),
        typeof(TemplateHighlightLayer),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double LineWidthMm = 3.4;
    private const double DotRadiusMm = 2.6;
    private const double RingGrowMm = 5.0;
    private const double RingWidthMm = 0.8;

    private static readonly SolidColorBrush DotBrush = Frozen(Color.FromRgb(0x26, 0xA2, 0x69));
    private static readonly SolidColorBrush AmberLine = Frozen(Color.FromArgb(0x70, 0xE0, 0x8A, 0x00));
    private static readonly SolidColorBrush GreenLine = Frozen(Color.FromArgb(0x70, 0x26, 0xA2, 0x69));
    private static readonly SolidColorBrush AmberRing = Frozen(Color.FromRgb(0xE0, 0x8A, 0x00));
    private static readonly SolidColorBrush GreenRing = Frozen(Color.FromRgb(0x1B, 0x7F, 0x52));

    private readonly IMonotonicClock _clock;
    private readonly TranslateTransform _snap = new();
    private IReadOnlyList<Vec2[]> _polylines = [];
    private Vec2? _pulsePoint;
    private HighlightColor _color = HighlightColor.Warm;
    private double _pulsePeriodMs = 800;
    private double _pulseDurationMs;
    private long _pulseStartTicks;
    private bool _pulseActive;
    private bool _keepStatic;
    private bool _ringEnabled;
    private bool _subscribedToRendering;
    private (double Mm, HighlightColor Color)? _linePenKey;
    private Pen? _linePen;
    private (double Mm, HighlightColor Color)? _ringPenKey;
    private Pen? _ringPen;

    /// <summary>Создаёт слой с системными часами.</summary>
    public TemplateHighlightLayer()
        : this(null)
    {
    }

    /// <summary>Создаёт слой.</summary>
    /// <param name="clock">Часы мигания; <c>null</c> — <see cref="StopwatchMonotonicClock"/>.</param>
    public TemplateHighlightLayer(IMonotonicClock? clock)
    {
        _clock = clock ?? new StopwatchMonotonicClock();
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        RenderTransform = _snap;
        LayoutUpdated += (_, _) => PixelSnap.Apply(this, _snap, EffectiveDpiScale);
        Unloaded += (_, _) => UnsubscribeRendering();
    }

    /// <summary>Линейка слоя (px поверхности).</summary>
    public GuideFrame? Guides
    {
        get => (GuideFrame?)GetValue(GuidesProperty);
        set => SetValue(GuidesProperty, value);
    }

    /// <summary>DPI-масштаб монитора; 0 — автоматически.</summary>
    public double DpiScale
    {
        get => (double)GetValue(DpiScaleProperty);
        set => SetValue(DpiScaleProperty, value);
    }

    /// <summary>Масштаб, которым слой переводит px в DIP.</summary>
    public double EffectiveDpiScale => DpiScale > 0 ? DpiScale : VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>Что-то подсвечено или показано.</summary>
    public bool HasContent => _polylines.Count > 0 || _pulsePoint is not null;

    /// <summary>Кольцо точки старта сейчас мигает.</summary>
    public bool IsPulsing => _pulseActive && _pulsePoint is not null && !PulseExpired();

    /// <summary>Слой подписан на кадры композитора (только пока кольцо мигает).</summary>
    public bool IsListeningToFrames => _subscribedToRendering;

    /// <summary>Подсвеченные ломаные.</summary>
    public IReadOnlyList<Vec2[]> Polylines => _polylines;

    /// <summary>Точка старта.</summary>
    public Vec2? PulsePoint => _pulsePoint;

    /// <summary>Цвет подсветки.</summary>
    public HighlightColor Tone => _color;

    /// <summary>
    /// Показывает подсветку: ломаные, зелёную точку старта и янтарное мигающее кольцо вокруг неё.
    /// </summary>
    /// <param name="polylines">Ломаные участков эталона в координатах линейки (может быть пусто).</param>
    /// <param name="pulsePoint">Точка старта или <c>null</c>.</param>
    /// <param name="color">Цвет подсветки и кольца.</param>
    /// <param name="pulsePeriodMs">Период мигания, мс.</param>
    /// <param name="pulseDurationMs">Сколько мигает кольцо, мс; после этого оно замирает (<paramref name="keepStatic"/>) либо точка исчезает.</param>
    /// <param name="keepStatic">После мигания оставить точку и статичное кольцо (обратная связь); иначе убрать (сигнал по ходу письма).</param>
    public void Show(IReadOnlyList<Vec2[]> polylines, Vec2? pulsePoint, HighlightColor color, double pulsePeriodMs, double pulseDurationMs, bool keepStatic = false)
    {
        ArgumentNullException.ThrowIfNull(polylines);
        _polylines = polylines;
        _pulsePoint = pulsePoint;
        _color = color;
        _pulsePeriodMs = pulsePeriodMs > 0 ? pulsePeriodMs : 800;
        _pulseDurationMs = pulseDurationMs;
        _keepStatic = keepStatic;
        _ringEnabled = pulsePoint is not null;
        _pulseActive = pulsePoint is not null;
        _pulseStartTicks = _clock.Ticks;
        if (_pulseActive)
        {
            SubscribeRendering();
        }
        else
        {
            UnsubscribeRendering();
        }

        InvalidateVisual();
    }

    /// <summary>Показывает только зелёную точку старта, без кольца и мигания (точка старта на строке письма, уровень L2).</summary>
    public void ShowDot(Vec2 point)
    {
        _polylines = [];
        _pulsePoint = point;
        _ringEnabled = false;
        _pulseActive = false;
        _keepStatic = true;
        UnsubscribeRendering();
        InvalidateVisual();
    }

    /// <summary>Убирает подсветку и мигание.</summary>
    public void Clear()
    {
        _polylines = [];
        _pulsePoint = null;
        _pulseActive = false;
        UnsubscribeRendering();
        InvalidateVisual();
    }

    /// <summary>
    /// Ход кадра (вызывается на <c>CompositionTarget.Rendering</c>, пока кольцо мигает): перерисовывает слой и по истечении времени
    /// замирает или снимает точку. Публичен, чтобы тесты управляли временем без композитора.
    /// </summary>
    public void FrameTick()
    {
        if (!_pulseActive)
        {
            return;
        }

        if (PulseExpired())
        {
            _pulseActive = false;
            if (!_keepStatic && _polylines.Count == 0)
            {
                _pulsePoint = null;
            }

            if (!_keepStatic)
            {
                _ringEnabled = false;
            }

            UnsubscribeRendering();
        }

        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        if (Guides is not { } frame)
        {
            return;
        }

        double dpi = EffectiveDpiScale;
        double mm = frame.PxPerMm / dpi; // DIP в миллиметре

        if (_polylines.Count > 0)
        {
            Pen pen = LinePen(mm);
            SolidColorBrush brush = _color == HighlightColor.Positive ? GreenLine : AmberLine;
            foreach (Vec2[] line in _polylines)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                if (line.Length == 1)
                {
                    drawingContext.DrawEllipse(brush, null, ToDip(frame, line[0], dpi), LineWidthMm * mm / 2, LineWidthMm * mm / 2);
                    continue;
                }

                var geometry = new StreamGeometry();
                using (StreamGeometryContext ctx = geometry.Open())
                {
                    ctx.BeginFigure(ToDip(frame, line[0], dpi), false, false);
                    ctx.PolyLineTo([.. line.Skip(1).Select(p => ToDip(frame, p, dpi))], true, true);
                }

                geometry.Freeze();
                drawingContext.DrawGeometry(null, pen, geometry);
            }
        }

        if (_pulsePoint is { } point && (_pulseActive || _keepStatic || _ringEnabled))
        {
            if (_pulseActive && PulseExpired() && !_keepStatic)
            {
                return;
            }

            Point center = ToDip(frame, point, dpi);
            double dot = DotRadiusMm * mm;
            drawingContext.DrawEllipse(DotBrush, null, center, dot, dot);
            if (_ringEnabled)
            {
                // Янтарное кольцо мигает; после мигания остаётся тонким статичным кольцом вокруг зелёной точки.
                double phase = _pulseActive ? PulseCurve.Phase(ElapsedMs(), _pulsePeriodMs) : 0.0;
                double radius = dot + (RingGrowMm * mm * phase) + (0.8 * mm);
                drawingContext.PushOpacity(_pulseActive ? 1.0 - (0.75 * phase) : 0.85);
                drawingContext.DrawEllipse(null, RingPen(mm), center, radius, radius);
                drawingContext.Pop();
            }
        }
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Point ToDip(GuideFrame frame, Vec2 ruler, double dpi) =>
        new((frame.OriginXPx + (ruler.X * frame.XHeightPx)) / dpi, (frame.BaselineYPx - (ruler.Y * frame.XHeightPx)) / dpi);

    private Pen LinePen(double mm)
    {
        if (_linePen is null || _linePenKey != (mm, _color))
        {
            _linePen = new Pen(_color == HighlightColor.Positive ? GreenLine : AmberLine, LineWidthMm * mm) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            _linePen.Freeze();
            _linePenKey = (mm, _color);
        }

        return _linePen;
    }

    private Pen RingPen(double mm)
    {
        if (_ringPen is null || _ringPenKey != (mm, _color))
        {
            _ringPen = new Pen(_color == HighlightColor.Positive ? GreenRing : AmberRing, RingWidthMm * mm);
            _ringPen.Freeze();
            _ringPenKey = (mm, _color);
        }

        return _ringPen;
    }

    private double ElapsedMs() => (_clock.Ticks - _pulseStartTicks) * 1000.0 / _clock.Frequency;

    private bool PulseExpired() => _pulseDurationMs > 0 && ElapsedMs() >= _pulseDurationMs;

    private void SubscribeRendering()
    {
        if (!_subscribedToRendering)
        {
            CompositionTarget.Rendering += OnRendering;
            _subscribedToRendering = true;
        }
    }

    private void UnsubscribeRendering()
    {
        if (_subscribedToRendering)
        {
            CompositionTarget.Rendering -= OnRendering;
            _subscribedToRendering = false;
        }
    }

    private void OnRendering(object? sender, EventArgs e) => FrameTick();
}
