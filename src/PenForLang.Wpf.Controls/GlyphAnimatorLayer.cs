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

    private static readonly SolidColorBrush InkBrush = Frozen(Color.FromRgb(0x1A, 0x5F, 0xB4));
    private static readonly SolidColorBrush DeferredBrush = Frozen(Color.FromRgb(0xC0, 0x1C, 0x28));
    private static readonly SolidColorBrush LeadBrush = Frozen(Color.FromRgb(0x9A, 0x99, 0x96));
    private static readonly SolidColorBrush StartBrush = Frozen(Color.FromRgb(0x26, 0xA2, 0x69));
    private static readonly SolidColorBrush BadgeBrush = Frozen(Color.FromRgb(0x61, 0x35, 0x83));
    private static readonly SolidColorBrush PenBrush = Frozen(Color.FromRgb(0x24, 0x1F, 0x31));
    private static readonly SolidColorBrush WhiteBrush = Frozen(Colors.White);

    private readonly IMonotonicClock _clock;
    private readonly TranslateTransform _snap = new();
    private readonly Dictionary<int, StreamGeometry?[]> _completed = [];
    private readonly Dictionary<(int Number, double Em, double Dpi), FormattedText> _texts = [];
    private (GuideFrame? Frame, double Dpi, double Width) _cacheKey;
    private Pen? _bodyPen;
    private Pen? _deferredPen;
    private Pen? _leadPen;
    private StrokeCache[] _cache = [];
    private AnimationState? _state;
    private double _positionMs;
    private long _lastTicks;
    private bool _isPlaying;
    private bool _finishedRaised;
    private bool _subscribedToRendering;
    private bool _showArrows = true;

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
        RenderTransform = _snap;
        LayoutUpdated += (_, _) => SnapToPixelGrid();
        Loaded += (_, _) => SubscribeRendering();
        Unloaded += (_, _) => UnsubscribeRendering();
    }

    /// <summary>Сдвиг, прижимающий слой к сетке физических пикселей (<see cref="PixelSnap"/>).</summary>
    public Vector SnapOffset => new(_snap.X, _snap.Y);

    /// <summary>Прижимает слой к сетке физических пикселей.</summary>
    public void SnapToPixelGrid() => PixelSnap.Apply(this, _snap, EffectiveDpiScale);

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

    /// <summary>Размеры меток (стрелки, номера, точки старта, «пера») с минимумами в миллиметрах.</summary>
    public AnimatorMarkStyle Marks { get; set; } = new();

    /// <summary>Множитель скорости воспроизведения (1 — как в сценарии); &gt; 0.</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>Показывать стрелки направления (в копии их скрывают после верных попыток, METHODOLOGY.md §4.2).</summary>
    public bool ShowArrows
    {
        get => _showArrows;
        set
        {
            if (_showArrows != value)
            {
                _showArrows = value;
                InvalidateVisual();
            }
        }
    }

    /// <summary>Текущая позиция на шкале сценария, мс.</summary>
    public double PositionMs => _positionMs;

    /// <summary>Идёт воспроизведение.</summary>
    public bool IsPlaying => _isPlaying;

    /// <summary>Позиция в конце сценария.</summary>
    public bool IsFinished => Script is { } script && _positionMs >= script.DurationMs;

    /// <summary>Состояние кадра в текущей позиции (<c>null</c> без сценария).</summary>
    public AnimationState? State => _state;

    /// <summary>Сколько готовых штрихов закешировано как замороженная геометрия (перерисовывается только активный).</summary>
    public int CachedStrokeCount => _completed.Count;

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
        double mm = frame.PxPerMm / dpi; // DIP в одном миллиметре
        double width = Math.Max(StrokeWidthMm * mm, 1.0);
        EnsureCache(script, frame, dpi, width);

        for (int i = 0; i < script.Strokes.Length; i++)
        {
            DrawStroke(drawingContext, script.Strokes[i], i, state.StrokeProgress[i], frame);
        }

        AnimatorMarkSizes sizes = Marks.Resolve(xhDip, mm);
        foreach (int index in state.VisibleStartDots)
        {
            drawingContext.DrawEllipse(StartBrush, null, ToDip(frame, script.StartDots[index].Position), sizes.StartDotRadius, sizes.StartDotRadius);
        }

        foreach (int index in ShowArrows ? state.VisibleArrows : [])
        {
            DrawArrow(drawingContext, frame, script.Arrows[index], sizes, width);
        }

        foreach (int index in state.VisibleNumbers)
        {
            DrawBadge(drawingContext, frame, script.Numbers[index], sizes, dpi);
        }

        if (!state.IsFinished)
        {
            DrawPen(drawingContext, ToDip(frame, state.PenPosition), state.PenDown, sizes);
        }
    }

    private static void OnScriptChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var layer = (GlyphAnimatorLayer)d;
        layer._cache = [];
        layer._completed.Clear();
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

    private void EnsureCache(AnimationScript script, GuideFrame frame, double dpi, double widthDip)
    {
        if (_cache.Length != script.Strokes.Length)
        {
            _cache = new StrokeCache[script.Strokes.Length];
            for (int i = 0; i < script.Strokes.Length; i++)
            {
                _cache[i] = StrokeCache.Build(script.Strokes[i].Path.Flatten(FlattenToleranceXHeights));
            }

            _completed.Clear();
        }

        // Геометрия готовых штрихов и кисти зависят от линейки, DPI и толщины; при их смене пересобираются.
        if (!ReferenceEquals(_cacheKey.Frame, frame) || _cacheKey.Dpi != dpi || _cacheKey.Width != widthDip || _bodyPen is null)
        {
            _cacheKey = (frame, dpi, widthDip);
            _completed.Clear();
            _bodyPen = MakePen(InkBrush, widthDip);
            _deferredPen = MakePen(DeferredBrush, widthDip);
            _leadPen = MakePen(LeadBrush, widthDip * 0.6);
        }
    }

    private static Pen MakePen(Brush brush, double width)
    {
        var pen = new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }

    private void DrawStroke(DrawingContext dc, AnimatedStroke stroke, int index, double progress, GuideFrame frame)
    {
        StrokeCache cache = _cache[index];
        if (progress <= 0 || cache.Total <= 0 || stroke.Path.Length <= 0)
        {
            return;
        }

        Pen bodyPen = stroke.IsDeferred ? _deferredPen! : _bodyPen!;

        // Готовый штрих: три замороженные геометрии (входящий, тело, выходящий), собираются один раз; перерисовывается только активный.
        if (progress >= 1.0)
        {
            if (!_completed.TryGetValue(index, out StreamGeometry?[]? parts))
            {
                parts = BuildParts(stroke, cache, frame, stroke.Path.Length);
                _completed[index] = parts;
            }

            DrawParts(dc, parts, bodyPen);
            return;
        }

        DrawParts(dc, BuildParts(stroke, cache, frame, Math.Min(progress, 1.0) * stroke.Path.Length), bodyPen);
    }

    private void DrawParts(DrawingContext dc, StreamGeometry?[] parts, Pen bodyPen)
    {
        // Участки пути: входящий штрих, тело, выходящий штрих. Тело — цветом штриха, ведущие участки — серым и тоньше.
        if (parts[0] is { } lead)
        {
            dc.DrawGeometry(null, _leadPen, lead);
        }

        if (parts[1] is { } body)
        {
            dc.DrawGeometry(null, bodyPen, body);
        }

        if (parts[2] is { } tail)
        {
            dc.DrawGeometry(null, _leadPen, tail);
        }
    }

    private StreamGeometry?[] BuildParts(AnimatedStroke stroke, StrokeCache cache, GuideFrame frame, double drawn)
    {
        double length = stroke.Path.Length;
        double bodyEnd = stroke.BodyStart + stroke.BodyLength;
        return
        [
            BuildRange(cache, frame, length, 0, Math.Min(drawn, stroke.BodyStart)),
            BuildRange(cache, frame, length, stroke.BodyStart, Math.Min(drawn, bodyEnd)),
            BuildRange(cache, frame, length, bodyEnd, drawn),
        ];
    }

    private StreamGeometry? BuildRange(StrokeCache cache, GuideFrame frame, double pathLength, double from, double to)
    {
        if (!(to > from))
        {
            return null;
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
        return geometry;
    }

    private void DrawArrow(DrawingContext dc, GuideFrame frame, ScriptArrow arrow, AnimatorMarkSizes sizes, double strokeWidthDip)
    {
        double length = sizes.ArrowLength;

        // Направление глифа (v вверх) → экран (y вниз). Стрелка стоит рядом со штрихом (слева по ходу пера), а не на нём.
        var d = new Vector(arrow.Direction.X, -arrow.Direction.Y);
        if (d.Length > 0)
        {
            d.Normalize();
        }

        var n = new Vector(d.Y, -d.X);
        Point p = ToDip(frame, arrow.Position) + (n * ((strokeWidthDip / 2) + sizes.ArrowSideOffset));
        double half = length * 0.4;
        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(p + (d * (length * 0.6)), true, true);
            ctx.LineTo(p - (d * (length * 0.4)) + (n * half), true, false);
            ctx.LineTo(p - (d * (length * 0.4)) - (n * half), true, false);
        }

        geometry.Freeze();
        dc.DrawGeometry(InkBrush, new Pen(WhiteBrush, 1), geometry);
    }

    private void DrawBadge(DrawingContext dc, GuideFrame frame, ScriptNumber number, AnimatorMarkSizes sizes, double dpi)
    {
        // Цифра не меньше DigitMinMm (высота цифры ≈ 0,7 кегля); кружок не меньше BadgeMinDiameterMm и охватывает цифру.
        double em = sizes.DigitEm;
        double radius = sizes.BadgeRadius;
        Point center = ToDip(frame, number.Position);
        dc.DrawEllipse(WhiteBrush, new Pen(BadgeBrush, 1.5), center, radius, radius);

        double emKey = Math.Round(em, 2);
        if (!_texts.TryGetValue((number.Number, emKey, dpi), out FormattedText? text))
        {
            text = new FormattedText(
                number.Number.ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                em,
                BadgeBrush,
                dpi);
            _texts[(number.Number, emKey, dpi)] = text;
        }

        dc.DrawText(text, new Point(center.X - (text.Width / 2), center.Y - (text.Height / 2)));
    }

    private static void DrawPen(DrawingContext dc, Point at, bool down, AnimatorMarkSizes sizes)
    {
        dc.DrawEllipse(down ? PenBrush : null, new Pen(PenBrush, 2), at, sizes.PenRadius, sizes.PenRadius);
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
