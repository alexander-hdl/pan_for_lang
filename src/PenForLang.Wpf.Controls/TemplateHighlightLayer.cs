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
/// след пера оставались видны. Мигание идёт по <see cref="IMonotonicClock"/> и само заканчивается через заданное время (мягкий сигнал)
/// либо держится, пока слой не очищен (подсветка в обратной связи).
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

    private static readonly Color Amber = Color.FromRgb(0xE0, 0x8A, 0x00);
    private static readonly Color Green = Color.FromRgb(0x26, 0xA2, 0x69);

    private readonly IMonotonicClock _clock;
    private readonly TranslateTransform _snap = new();
    private IReadOnlyList<Vec2[]> _polylines = [];
    private Vec2? _pulsePoint;
    private HighlightColor _color = HighlightColor.Warm;
    private double _pulsePeriodMs = 800;
    private double _pulseDurationMs;
    private long _pulseStartTicks;
    private bool _pulseActive;
    private bool _subscribedToRendering;

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
        Loaded += (_, _) => SubscribeRendering();
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

    /// <summary>Что-то подсвечено или мигает.</summary>
    public bool HasContent => _polylines.Count > 0 || _pulsePoint is not null;

    /// <summary>Точка старта сейчас мигает.</summary>
    public bool IsPulsing => _pulseActive && _pulsePoint is not null && !PulseExpired();

    /// <summary>Подсвеченные ломаные.</summary>
    public IReadOnlyList<Vec2[]> Polylines => _polylines;

    /// <summary>Точка старта, которая мигает.</summary>
    public Vec2? PulsePoint => _pulsePoint;

    /// <summary>Цвет подсветки.</summary>
    public HighlightColor Tone => _color;

    /// <summary>
    /// Показывает подсветку.
    /// </summary>
    /// <param name="polylines">Ломаные участков эталона в координатах линейки (может быть пусто).</param>
    /// <param name="pulsePoint">Точка старта, которая мигает, или <c>null</c>.</param>
    /// <param name="color">Цвет.</param>
    /// <param name="pulsePeriodMs">Период мигания, мс.</param>
    /// <param name="pulseDurationMs">Сколько мигает, мс; 0 или меньше — пока слой не очищен.</param>
    public void Show(IReadOnlyList<Vec2[]> polylines, Vec2? pulsePoint, HighlightColor color, double pulsePeriodMs, double pulseDurationMs)
    {
        ArgumentNullException.ThrowIfNull(polylines);
        _polylines = polylines;
        _pulsePoint = pulsePoint;
        _color = color;
        _pulsePeriodMs = pulsePeriodMs > 0 ? pulsePeriodMs : 800;
        _pulseDurationMs = pulseDurationMs;
        _pulseActive = pulsePoint is not null;
        _pulseStartTicks = _clock.Ticks;
        InvalidateVisual();
    }

    /// <summary>Убирает подсветку и мигание.</summary>
    public void Clear()
    {
        _polylines = [];
        _pulsePoint = null;
        _pulseActive = false;
        InvalidateVisual();
    }

    /// <summary>
    /// Ход кадра (вызывается на <c>CompositionTarget.Rendering</c>): перерисовывает мигающую точку или снимает её по истечении времени.
    /// Публичен, чтобы тесты управляли временем без композитора.
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
            if (_polylines.Count == 0)
            {
                _pulsePoint = null;
            }
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
        Color baseColor = _color == HighlightColor.Positive ? Green : Amber;

        if (_polylines.Count > 0)
        {
            var brush = new SolidColorBrush(Color.FromArgb(0x70, baseColor.R, baseColor.G, baseColor.B));
            brush.Freeze();
            var pen = new Pen(brush, LineWidthMm * mm) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            pen.Freeze();
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

        if (_pulsePoint is { } point && _pulseActive && !PulseExpired())
        {
            double elapsed = ElapsedMs();
            double phase = PulseCurve.Phase(elapsed, _pulsePeriodMs);
            Point center = ToDip(frame, point, dpi);
            byte fill = (byte)(0x90 + (int)(0x6F * phase));
            var dotBrush = new SolidColorBrush(Color.FromArgb(fill, baseColor.R, baseColor.G, baseColor.B));
            dotBrush.Freeze();
            var ringBrush = new SolidColorBrush(Color.FromArgb((byte)(0xC0 * (1.0 - phase)), baseColor.R, baseColor.G, baseColor.B));
            ringBrush.Freeze();
            var ringPen = new Pen(ringBrush, 0.8 * mm);
            ringPen.Freeze();
            double dot = DotRadiusMm * mm;
            drawingContext.DrawEllipse(dotBrush, null, center, dot, dot);
            drawingContext.DrawEllipse(null, ringPen, center, dot + (RingGrowMm * mm * phase), dot + (RingGrowMm * mm * phase));
        }
    }

    private static Point ToDip(GuideFrame frame, Vec2 ruler, double dpi) =>
        new((frame.OriginXPx + (ruler.X * frame.XHeightPx)) / dpi, (frame.BaselineYPx - (ruler.Y * frame.XHeightPx)) / dpi);

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
