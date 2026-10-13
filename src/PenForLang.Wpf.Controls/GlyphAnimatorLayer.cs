using System.Globalization;
using System.Windows;
using System.Windows.Media;
using PenForLang.Core;
using PenForLang.Core.Geometry;
using PenForLang.Glyphs.Animation;
using PenForLang.Ink;

namespace PenForLang.Wpf.Controls;

/// <summary>
/// Аниматор глифа (ARCHITECTURE.md §7.5): проигрывает <see cref="AnimationScript"/> поверх линеек — штрихи по мере прохождения пера,
/// «перо» (кончик), стрелки направления, номера штрихов и зелёную точку старта. Состояние кадра даёт чистая функция
/// <see cref="AnimationScript.StateAt"/>; слой только переводит координаты линейки <c>(u, v)</c> в DIP через
/// <see cref="Guides"/> (<c>x = (x0 + u·h) / dpi</c>, <c>y = (baseline − v·h) / dpi</c>, где <c>h</c> — x-height в px) и рисует.
/// </summary>
/// <remarks>
/// Время идёт от инжектируемых <see cref="IMonotonicClock"/> (в тестах — ручные часы), ход берётся на каждом
/// <c>CompositionTarget.Rendering</c> (<see cref="FrameTick"/>). Управление: <see cref="Play"/>, <see cref="Pause"/>,
/// <see cref="Seek"/>, <see cref="Replay"/>, <see cref="Stop"/>. Слой не принимает ввод.
/// </remarks>
public sealed class GlyphAnimatorLayer : FrameworkElement
{
    /// <summary>Сценарий анимации.</summary>
    public static readonly DependencyProperty ScriptProperty = DependencyProperty.Register(
        nameof(Script),
        typeof(AnimationScript),
        typeof(GlyphAnimatorLayer),
        new FrameworkPropertyMetadata(null, OnScriptChanged));

    /// <summary>Линейка, в координатах которой стоит глиф (<c>u = 0</c> — её <c>OriginXPx</c>).</summary>
    public static readonly DependencyProperty GuidesProperty = DependencyProperty.Register(
        nameof(Guides),
        typeof(GuideFrame),
        typeof(GlyphAnimatorLayer),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>DPI-масштаб; 0 или меньше — брать у окна автоматически.</summary>
    public static readonly DependencyProperty DpiScaleProperty = DependencyProperty.Register(
        nameof(DpiScale),
        typeof(double),
        typeof(GlyphAnimatorLayer),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double FlattenToleranceXHeights = 0.003;
    private const double ArrowSizeXHeights = 0.075;
    private const double BadgeRadiusXHeights = 0.085;
    private const double StartDotRadiusXHeights = 0.05;
    private const double PenRadiusXHeights = 0.07;

    private static readonly SolidColorBrush InkBrush = Frozen(Color.FromRgb(0x1A, 0x5F, 0xB4));
    private static readonly SolidColorBrush DeferredBrush = Frozen(Color.FromRgb(0xC0, 0x1C, 0x28));
    private static readonly SolidColorBrush LeadBrush = Frozen(Color.FromRgb(0x9A, 0x99, 0x96));
    private static readonly SolidColorBrush StartBrush = Frozen(Color.FromRgb(0x26, 0xA2, 0x69));
    private static readonly SolidColorBrush BadgeBrush = Frozen(Color.FromRgb(0x61, 0x35, 0x83));
    private static readonly SolidColorBrush PenBrush = Frozen(Color.FromRgb(0x24, 0x1F, 0x31));
    private static readonly SolidColorBrush WhiteBrush = Frozen(Colors.White);

    private readonly IMonotonicClock _clock;
    private StrokeCache[] _cache = [];
    private AnimationState? _state;
    private double _positionMs;
    private long _lastTicks;
    private bool _isPlaying;
    private bool _finishedRaised;
    private bool _subscribedToRendering;

    /// <summary>Создаёт аниматор с системными часами.</summary>
    public GlyphAnimatorLayer()
        : this(null)
    {
    }

    /// <summary>Создаёт аниматор.</summary>
    /// <param name="clock">Часы воспроизведения; <c>null</c> — <see cref="StopwatchMonotonicClock"/>.</param>
    public GlyphAnimatorLayer(IMonotonicClock? clock)
    {
        _clock = clock ?? new StopwatchMonotonicClock();
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        Loaded += (_, _) => SubscribeRendering();
        Unloaded += (_, _) => UnsubscribeRendering();
    }

    /// <summary>Воспроизведение дошло до конца сценария (вызывается один раз за проход).</summary>
    public event EventHandler? PlaybackFinished;

    /// <summary>Сценарий; при смене позиция сбрасывается в начало, воспроизведение останавливается.</summary>
    public AnimationScript? Script
    {
        get => (AnimationScript?)GetValue(ScriptProperty);
        set => SetValue(ScriptProperty, value);
    }

    /// <summary>Линейка анимации (px поверхности).</summary>
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

    /// <summary>Толщина линии, мм.</summary>
    public double StrokeWidthMm { get; set; } = 1.4;

    /// <summary>Множитель скорости воспроизведения (1 — как в сценарии); &gt; 0.</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>Текущая позиция на шкале сценария, мс.</summary>
    public double PositionMs => _positionMs;

    /// <summary>Идёт воспроизведение.</summary>
    public bool IsPlaying => _isPlaying;

    /// <summary>Позиция в конце сценария.</summary>
    public bool IsFinished => Script is { } script && _positionMs >= script.DurationMs;

    /// <summary>Состояние кадра в текущей позиции (<c>null</c> без сценария).</summary>
    public AnimationState? State => _state;

    /// <summary>Положение «пера» в DIP слоя (<c>null</c> без сценария или линейки).</summary>
    public Point? PenPositionDip => _state is { } state && Guides is { } frame ? ToDip(frame, state.PenPosition) : null;

    /// <summary>Начинает или продолжает воспроизведение; в конце сценария — с начала.</summary>
    public void Play()
    {
        if (Script is null)
        {
            return;
        }

        if (IsFinished)
        {
            SeekCore(0);
        }

        _isPlaying = true;
        _finishedRaised = false;
        _lastTicks = _clock.Ticks;
    }

    /// <summary>Ставит воспроизведение на паузу.</summary>
    public void Pause()
    {
        if (_isPlaying)
        {
            Advance();
            _isPlaying = false;
        }
    }

    /// <summary>Останавливает и возвращает в начало (чистое поле: видна только точка старта).</summary>
    public void Stop()
    {
        _isPlaying = false;
        SeekCore(0);
    }

    /// <summary>Переходит к моменту <paramref name="timeMs"/> (ограничивается шкалой сценария); ход воспроизведения не меняется.</summary>
    public void Seek(double timeMs)
    {
        if (_isPlaying)
        {
            _lastTicks = _clock.Ticks;
        }

        SeekCore(timeMs);
    }

    /// <summary>Воспроизводит с начала.</summary>
    public void Replay()
    {
        SeekCore(0);
        Play();
    }

    /// <summary>
    /// Ход кадра (вызывается на <c>CompositionTarget.Rendering</c>): сдвигает позицию по часам и перерисовывает слой.
    /// Публичен, чтобы тесты управляли временем без композитора.
    /// </summary>
    public void FrameTick()
    {
        if (_isPlaying)
        {
            Advance();
        }
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        if (Script is not { } script || _state is not { } state || Guides is not { } frame)
        {
            return;
        }

        double dpi = EffectiveDpiScale;
        double xhDip = frame.XHeightPx / dpi;
        double width = Math.Max(StrokeWidthMm * frame.PxPerMm / dpi, 1.0);
        EnsureCache(script);

        for (int i = 0; i < script.Strokes.Length; i++)
        {
            DrawStroke(drawingContext, script.Strokes[i], _cache[i], state.StrokeProgress[i], frame, width);
        }

        foreach (int index in state.VisibleStartDots)
        {
            ScriptStartDot dot = script.StartDots[index];
            drawingContext.DrawEllipse(StartBrush, null, ToDip(frame, dot.Position), Math.Max(StartDotRadiusXHeights * xhDip, 3.5), Math.Max(StartDotRadiusXHeights * xhDip, 3.5));
        }

        foreach (int index in state.VisibleArrows)
        {
            DrawArrow(drawingContext, frame, script.Arrows[index], xhDip);
        }

        foreach (int index in state.VisibleNumbers)
        {
            DrawBadge(drawingContext, frame, script.Numbers[index], xhDip, dpi);
        }

        if (!state.IsFinished)
        {
            DrawPen(drawingContext, ToDip(frame, state.PenPosition), state.PenDown, xhDip);
        }
    }

    private static void OnScriptChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var layer = (GlyphAnimatorLayer)d;
        layer._cache = [];
        layer._isPlaying = false;
        layer.SeekCore(0);
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private Point ToDip(GuideFrame frame, Vec2 ruler)
    {
        double dpi = EffectiveDpiScale;
        return new Point(
            (frame.OriginXPx + (ruler.X * frame.XHeightPx)) / dpi,
            (frame.BaselineYPx - (ruler.Y * frame.XHeightPx)) / dpi);
    }

    private void SeekCore(double timeMs)
    {
        if (Script is not { } script)
        {
            _positionMs = 0;
            _state = null;
            InvalidateVisual();
            return;
        }

        _positionMs = double.IsNaN(timeMs) ? 0 : Math.Clamp(timeMs, 0, script.DurationMs);
        _state = script.StateAt(_positionMs);
        _finishedRaised = _positionMs >= script.DurationMs && !_isPlaying;
        InvalidateVisual();
    }

    private void Advance()
    {
        if (Script is not { } script)
        {
            _isPlaying = false;
            return;
        }

        long now = _clock.Ticks;
        double elapsedMs = (now - _lastTicks) * 1000.0 / _clock.Frequency * (Speed > 0 ? Speed : 1.0);
        _lastTicks = now;
        double target = _positionMs + Math.Max(elapsedMs, 0);
        SeekCore(target);

        if (_positionMs >= script.DurationMs)
        {
            _isPlaying = false;
            if (!_finishedRaised)
            {
                _finishedRaised = true;
                PlaybackFinished?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void EnsureCache(AnimationScript script)
    {
        if (_cache.Length == script.Strokes.Length)
        {
            return;
        }

        _cache = new StrokeCache[script.Strokes.Length];
        for (int i = 0; i < script.Strokes.Length; i++)
        {
            _cache[i] = StrokeCache.Build(script.Strokes[i].Path.Flatten(FlattenToleranceXHeights));
        }
    }

    private void DrawStroke(DrawingContext dc, AnimatedStroke stroke, StrokeCache cache, double progress, GuideFrame frame, double widthDip)
    {
        if (progress <= 0 || cache.Total <= 0 || stroke.Path.Length <= 0)
        {
            return;
        }

        double length = stroke.Path.Length;
        double drawn = Math.Min(progress, 1.0) * length;

        // Участки пути: входящий штрих, тело, выходящий штрих. Тело — цветом штриха, ведущие участки — серым и тоньше.
        double bodyEnd = stroke.BodyStart + stroke.BodyLength;
        Brush bodyBrush = stroke.IsDeferred ? DeferredBrush : InkBrush;
        DrawRange(dc, cache, frame, length, 0, Math.Min(drawn, stroke.BodyStart), LeadBrush, widthDip * 0.6);
        DrawRange(dc, cache, frame, length, stroke.BodyStart, Math.Min(drawn, bodyEnd), bodyBrush, widthDip);
        DrawRange(dc, cache, frame, length, bodyEnd, drawn, LeadBrush, widthDip * 0.6);
    }

    private void DrawRange(DrawingContext dc, StrokeCache cache, GuideFrame frame, double pathLength, double from, double to, Brush brush, double widthDip)
    {
        if (!(to > from))
        {
            return;
        }

        double scale = cache.Total / pathLength;
        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(ToDip(frame, cache.PointAt(from * scale)), false, false);
            var points = new List<Point>();
            cache.AppendInterior(from * scale, to * scale, p => points.Add(ToDip(frame, p)));
            points.Add(ToDip(frame, cache.PointAt(to * scale)));
            ctx.PolyLineTo(points, true, true);
        }

        geometry.Freeze();
        var pen = new Pen(brush, widthDip) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        dc.DrawGeometry(null, pen, geometry);
    }

    private void DrawArrow(DrawingContext dc, GuideFrame frame, ScriptArrow arrow, double xhDip)
    {
        double size = ArrowSizeXHeights * xhDip;
        Point p = ToDip(frame, arrow.Position);

        // Направление глифа (v вверх) → экран (y вниз).
        var d = new Vector(arrow.Direction.X, -arrow.Direction.Y);
        var n = new Vector(-d.Y, d.X);
        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(p + (d * size), true, true);
            ctx.LineTo(p - (d * (size * 0.7)) + (n * (size * 0.75)), true, false);
            ctx.LineTo(p - (d * (size * 0.7)) - (n * (size * 0.75)), true, false);
        }

        geometry.Freeze();
        dc.DrawGeometry(InkBrush, new Pen(WhiteBrush, 1), geometry);
    }

    private void DrawBadge(DrawingContext dc, GuideFrame frame, ScriptNumber number, double xhDip, double dpi)
    {
        double radius = Math.Max(BadgeRadiusXHeights * xhDip, 8);
        Point center = ToDip(frame, number.Position);
        dc.DrawEllipse(WhiteBrush, new Pen(BadgeBrush, 1.5), center, radius, radius);
        var text = new FormattedText(
            number.Number.ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            radius * 1.2,
            BadgeBrush,
            dpi);
        dc.DrawText(text, new Point(center.X - (text.Width / 2), center.Y - (text.Height / 2)));
    }

    private static void DrawPen(DrawingContext dc, Point at, bool down, double xhDip)
    {
        double radius = Math.Max(PenRadiusXHeights * xhDip, 5);
        dc.DrawEllipse(down ? PenBrush : null, new Pen(PenBrush, 2), at, radius, radius);
    }

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

    /// <summary>Полилиния пути с накопленными длинами — для частичной отрисовки штриха по пройденному расстоянию.</summary>
    private sealed class StrokeCache
    {
        private readonly Vec2[] _points;
        private readonly double[] _cumulative;

        private StrokeCache(Vec2[] points, double[] cumulative)
        {
            _points = points;
            _cumulative = cumulative;
        }

        public double Total => _cumulative.Length == 0 ? 0 : _cumulative[^1];

        public static StrokeCache Build(Vec2[] points)
        {
            var cumulative = new double[points.Length];
            for (int i = 1; i < points.Length; i++)
            {
                cumulative[i] = cumulative[i - 1] + Vec2.Distance(points[i - 1], points[i]);
            }

            return new StrokeCache(points, cumulative);
        }

        public Vec2 PointAt(double distance)
        {
            if (_points.Length == 0)
            {
                return Vec2.Zero;
            }

            if (distance <= 0)
            {
                return _points[0];
            }

            if (distance >= Total)
            {
                return _points[^1];
            }

            int index = Array.BinarySearch(_cumulative, distance);
            if (index >= 0)
            {
                return _points[index];
            }

            int next = ~index;
            double span = _cumulative[next] - _cumulative[next - 1];
            double t = span > 0 ? (distance - _cumulative[next - 1]) / span : 0;
            return Vec2.Lerp(_points[next - 1], _points[next], t);
        }

        /// <summary>Вершины полилинии строго внутри (from, to).</summary>
        public void AppendInterior(double from, double to, Action<Vec2> add)
        {
            for (int i = 1; i < _points.Length - 1; i++)
            {
                if (_cumulative[i] > from && _cumulative[i] < to)
                {
                    add(_points[i]);
                }
            }
        }
    }
}
