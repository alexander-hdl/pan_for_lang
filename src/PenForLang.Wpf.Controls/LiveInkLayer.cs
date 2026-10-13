using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PenForLang.Core;
using PenForLang.Ink;

namespace PenForLang.Wpf.Controls;

/// <summary>
/// Живые чернила (ARCHITECTURE.md §7.5, ADR-004): принимает пачки <see cref="PenSample"/> от <see cref="IPenInputSource"/> и
/// рисует штрихи с минимальной задержкой. Растеризация идёт в программный буфер (<see cref="InkSurface"/>, горячий путь без
/// аллокаций), а в <see cref="WriteableBitmap"/> изменённая область переносится один раз за кадр — на
/// <c>CompositionTarget.Rendering</c> (<see cref="FrameTick"/>). Координаты точек — физические px поверхности (A12); слой выводит
/// буфер 1:1 в физических пикселях (DPI растра = DPI монитора), без передискретизации.
/// </summary>
/// <remarks>
/// <b>Выбор техники</b> (обоснование — в <see cref="InkSurface"/>): <c>DrawingVisual</c>/<c>StreamGeometry</c> по ≤ 32 точек из
/// §7.5 требуют <c>RenderOpen()</c>/<c>Open()</c> на каждое обновление — это аллокации внутри WPF, которых не убрать. Растр в
/// <c>WriteableBitmap</c> даёт нулевые аллокации на пачку, детерминированный результат (тестируется по пикселям) и тот же
/// самый такт показа (раз за кадр).
/// <para><b>Попытка и GC.</b> Первое касание включает <see cref="System.Runtime.GCLatencyMode.SustainedLowLatency"/>, <see cref="EndAttempt"/>
/// возвращает прежний режим (A32).</para>
/// <para><b>Задержка.</b> При заданных <see cref="Clock"/> и абсолютных метках времени источника слой измеряет «метка точки →
/// Rendering» (<see cref="Latency"/>) для dev-оверлея и отчёта.</para>
/// Слой не принимает ввод мыши и лишних layout-проходов не создаёт.
/// </remarks>
public sealed class LiveInkLayer : FrameworkElement
{
    private const double TipStaleSeconds = 0.5;
    private const double TipRadiusDip = 9;

    private static readonly Pen TipPen = MakeTipPen();

    private readonly Action<ReadOnlyMemory<PenSample>> _samplesHandler;
    private readonly VisualCollection _visuals;
    private readonly DrawingVisual _tip = new();
    private readonly RenderLatencyMeter _meter = new();
    private readonly AttemptGcLatencyGuard _gcGuard;
    private readonly TranslateTransform _snap = new();

    private IPenInputSource? _source;
    private InkSurface? _surface;
    private WriteableBitmap? _bitmap;
    private double _dpiScale = 1.0;
    private bool _timesAbsolute = true;
    private long _clockFrequency;
    private long _lastSampleUs;
    private Vector _tipOffset = new(double.NaN, double.NaN);
    private bool _tipShown;
    private double _pxPerMm = SurfaceMapper.DefaultPxPerMm;
    private double _baseWidthMm = 1.0;
    private bool _subscribedToRendering;

    /// <summary>Создаёт слой без измерения задержки и с настоящим режимом GC процесса.</summary>
    public LiveInkLayer()
        : this(null, null)
    {
    }

    /// <summary>Создаёт слой.</summary>
    /// <param name="clock">Монотонные часы источника (QPC) для измерения задержки; <c>null</c> — не измерять.</param>
    /// <param name="gcLatencyMode">Управление режимом GC (для тестов); <c>null</c> — <see cref="SystemGcLatencyMode"/>.</param>
    public LiveInkLayer(IMonotonicClock? clock, IGcLatencyMode? gcLatencyMode)
    {
        Clock = clock;
        _clockFrequency = clock?.Frequency ?? 0;
        _gcGuard = new AttemptGcLatencyGuard(gcLatencyMode ?? new SystemGcLatencyMode());
        _samplesHandler = OnSamples;
        _visuals = new VisualCollection(this) { _tip };
        _tip.Opacity = 0;
        DrawTip();
        IsHitTestVisible = false;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        // Растр выводится 1:1 в физических пикселях: без передискретизации и со страховкой от дробного положения слоя (PixelSnap).
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        RenderTransform = _snap;
        LayoutUpdated += (_, _) => SnapToPixelGrid();
        Loaded += (_, _) => SubscribeRendering();
        Unloaded += (_, _) =>
        {
            UnsubscribeRendering();
            EndAttempt();
        };
    }

    /// <summary>Часы для измерения задержки (QPC); задаются при создании.</summary>
    public IMonotonicClock? Clock { get; }

    /// <summary>Поверхность чернил (<c>null</c>, пока размер слоя не известен).</summary>
    public InkSurface? Surface => _surface;

    /// <summary>Показывать маркер кончика пера (наведение и контакт).</summary>
    public bool ShowTip { get; set; }

    /// <summary>Физических пикселей в миллиметре (для толщины линии).</summary>
    public double PxPerMm
    {
        get => _pxPerMm;
        set
        {
            _pxPerMm = value > 0 ? value : SurfaceMapper.DefaultPxPerMm;
            ApplyBrush();
        }
    }

    /// <summary>Толщина линии при среднем давлении, мм.</summary>
    public double BaseWidthMm
    {
        get => _baseWidthMm;
        set
        {
            _baseWidthMm = value > 0 ? value : 1.0;
            ApplyBrush();
        }
    }

    /// <summary>Толщина зависит от давления (в пределах, ограничивающих рывки).</summary>
    public bool PressureEnabled
    {
        get => _surface?.PressureEnabled ?? _pressureEnabled;
        set
        {
            _pressureEnabled = value;
            if (_surface is not null)
            {
                _surface.PressureEnabled = value;
            }
        }
    }

    /// <summary>Сколько штрихов начато с последней очистки.</summary>
    public int StrokeCount => _surface?.StrokeCount ?? 0;

    /// <summary>Сколько отрезков нарисовано.</summary>
    public int SegmentCount => _surface?.SegmentCount ?? 0;

    /// <summary>Перо касается поверхности.</summary>
    public bool IsPenDown => _surface?.IsPenDown ?? false;

    /// <summary>Включён режим <see cref="System.Runtime.GCLatencyMode.SustainedLowLatency"/> на время попытки.</summary>
    public bool IsGcLatencyModeActive => _gcGuard.IsActive;

    /// <summary>Сторож режима GC (счётчик включений и прежний режим).</summary>
    public AttemptGcLatencyGuard GcGuard => _gcGuard;

    /// <summary>Сводка задержки «метка точки → Rendering».</summary>
    public RenderLatency Latency => _meter.Snapshot();

    /// <summary>Сколько раз пересоздавался битмап растра (для диагностики и тестов: при том же размере и DPI не растёт).</summary>
    public int BitmapGeneration { get; private set; }

    /// <summary>Масштаб DPI, в котором создан растр.</summary>
    public double RasterDpiScale => _dpiScale;

    /// <summary>DPI-масштаб для растра; 0 или меньше — брать у окна (<see cref="VisualTreeHelper.GetDpi"/>). Для тестов и принудительной настройки.</summary>
    public double DpiScale { get; set; }

    private bool _pressureEnabled;
    private bool _clearBeforeNextStroke;

    /// <summary>Масштаб, которым слой переводит px в DIP: <see cref="DpiScale"/> или DPI окна.</summary>
    public double EffectiveDpiScale => DpiScale > 0 ? DpiScale : VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>Сдвиг, прижимающий слой к сетке физических пикселей (<see cref="PixelSnap"/>).</summary>
    public Vector SnapOffset => new(_snap.X, _snap.Y);

    /// <summary>Прижимает слой к сетке физических пикселей: гасит дробную часть его положения в окне.</summary>
    public void SnapToPixelGrid() => PixelSnap.Apply(this, _snap, EffectiveDpiScale);

    /// <summary>Очистить чернила перед следующим касанием («Готово» оставляет штрихи на экране до него).</summary>
    public bool ClearBeforeNextStroke
    {
        get => _surface?.ClearBeforeNextStroke ?? _clearBeforeNextStroke;
        set
        {
            _clearBeforeNextStroke = value;
            if (_surface is not null)
            {
                _surface.ClearBeforeNextStroke = value;
            }
        }
    }

    /// <summary>Подписывается на пачки точек источника; предыдущий источник отключается.</summary>
    public void Attach(IPenInputSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Detach();
        _source = source;
        _timesAbsolute = source.TimeBase == PenTimeBase.MonotonicAbsolute;
        source.SamplesReceived += _samplesHandler;
    }

    /// <summary>Отключается от источника.</summary>
    public void Detach()
    {
        if (_source is not null)
        {
            _source.SamplesReceived -= _samplesHandler;
            _source = null;
        }
    }

    /// <summary>
    /// Принимает пачку точек в физических px поверхности. Горячий путь: аллокаций нет (после прогрева). Должен вызываться в потоке UI.
    /// </summary>
    public void AddSamples(ReadOnlySpan<PenSample> samples)
    {
        if (_surface is not { } surface || samples.IsEmpty)
        {
            return;
        }

        if (!_gcGuard.IsActive)
        {
            foreach (ref readonly PenSample s in samples)
            {
                if (s.Phase == PenPhase.Down)
                {
                    _gcGuard.Begin();
                    break;
                }
            }
        }

        long arrivalUs = 0;
        if (Clock is { } clock && _timesAbsolute)
        {
            arrivalUs = MonotonicTime.ToMicroseconds(clock.Ticks, _clockFrequency);
            _lastSampleUs = arrivalUs;
        }

        surface.Add(samples, arrivalUs);
    }

    /// <summary>Стирает чернила (кнопка «Заново»); режим GC попытки не меняется.</summary>
    public void Clear()
    {
        _surface?.Clear();
    }

    /// <summary>Попытка закончена («Готово», выход): возвращает прежний режим GC.</summary>
    public void EndAttempt() => _gcGuard.End();

    /// <summary>Сбрасывает накопленную статистику задержки.</summary>
    public void ResetLatency() => _meter.Reset();

    /// <summary>
    /// Размер и DPI растра: <paramref name="widthPx"/>×<paramref name="heightPx"/> физических px при масштабе <paramref name="dpiScale"/>.
    /// Вызывается при изменении размера и DPI слоя; сохранённые штрихи перерисовываются.
    /// </summary>
    public void ConfigureSurface(int widthPx, int heightPx, double dpiScale)
    {
        if (widthPx <= 0 || heightPx <= 0 || !(dpiScale > 0))
        {
            return;
        }

        _dpiScale = dpiScale;
        if (_surface is null)
        {
            _surface = new InkSurface(widthPx, heightPx) { PressureEnabled = _pressureEnabled, ClearBeforeNextStroke = _clearBeforeNextStroke };
            ApplyBrush();
        }
        else if (_bitmap is not null && _surface.WidthPx == widthPx && _surface.HeightPx == heightPx
            && Math.Abs(_bitmap.DpiX - (96.0 * dpiScale)) < 1e-6)
        {
            return; // тот же размер и DPI: растр и битмап уже подходят (смена раскладки не должна пересоздавать буфер)
        }
        else
        {
            _surface.Resize(widthPx, heightPx);
        }

        _bitmap = new WriteableBitmap(widthPx, heightPx, 96.0 * dpiScale, 96.0 * dpiScale, PixelFormats.Pbgra32, null);
        BitmapGeneration++;
        UploadAll();
        InvalidateVisual();
    }

    /// <summary>
    /// Действия кадра (вызывается на <c>CompositionTarget.Rendering</c>): переносит изменённую область в растр WPF, записывает
    /// задержку и двигает маркер пера. Публичен, чтобы тесты и приложение могли вызвать его без композитора.
    /// </summary>
    public void FrameTick()
    {
        if (_surface is not { } surface)
        {
            return;
        }

        bool rasterChanged = surface.TryTakeDirty(out Int32Rect rect);
        if (rasterChanged)
        {
            Upload(rect);
        }

        if (surface.HasPending)
        {
            // Задержка — только по кадрам, где изменился растр (касание); кадры с одним наведением не в счёт.
            if (rasterChanged && Clock is { } clock && _timesAbsolute && _clockFrequency > 0)
            {
                long nowUs = MonotonicTime.ToMicroseconds(clock.Ticks, _clockFrequency);
                _meter.Record(nowUs, surface.PendingNewestPointUs, surface.PendingOldestPointUs, surface.PendingLatestArrivalUs, surface.PendingFirstArrivalUs);
            }

            surface.ClearPending();
        }

        UpdateTip(surface);
    }

    /// <inheritdoc />
    protected override int VisualChildrenCount => _visuals.Count;

    /// <inheritdoc />
    protected override Visual GetVisualChild(int index) => _visuals[index];

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        if (_bitmap is not null)
        {
            drawingContext.DrawImage(_bitmap, new Rect(0, 0, _bitmap.Width, _bitmap.Height));
        }
    }

    /// <inheritdoc />
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        ConfigureFromLayout();
    }

    /// <inheritdoc />
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ConfigureFromLayout();
    }

    private static Pen MakeTipPen()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0xD7));
        brush.Freeze();
        var pen = new Pen(brush, 1.5);
        pen.Freeze();
        return pen;
    }

    private void ConfigureFromLayout()
    {
        double dpi = EffectiveDpiScale;
        int width = (int)Math.Round(RenderSize.Width * dpi);
        int height = (int)Math.Round(RenderSize.Height * dpi);
        ConfigureSurface(width, height, dpi);
    }

    private void ApplyBrush()
    {
        if (_surface is not null)
        {
            _surface.BaseRadiusPx = (float)(_baseWidthMm * _pxPerMm / 2.0);
        }
    }

    private void OnSamples(ReadOnlyMemory<PenSample> samples) => AddSamples(samples.Span);

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

    private void UploadAll()
    {
        if (_surface is { } surface)
        {
            surface.TryTakeDirty(out _);
            Upload(new Int32Rect(0, 0, surface.WidthPx, surface.HeightPx));
        }
    }

    private void Upload(Int32Rect rect)
    {
        if (_surface is not { } surface || _bitmap is not { } bitmap
            || bitmap.PixelWidth != surface.WidthPx || bitmap.PixelHeight != surface.HeightPx)
        {
            return;
        }

        // Источник — весь закреплённый буфер поверхности, переносится только изменённая область. WritePixels внутри WPF выделяет
        // порядка 40 байт на вызов (не на пачку: вызов один за кадр).
        IntPtr start = Marshal.UnsafeAddrOfPinnedArrayElement(surface.PixelArray, 0);
        int stride = surface.WidthPx * sizeof(uint);
        bitmap.WritePixels(rect, start, stride * surface.HeightPx, stride, rect.X, rect.Y);
    }

    private void UpdateTip(InkSurface surface)
    {
        bool visible = ShowTip && surface.HasTip;
        if (visible && !surface.IsPenDown && Clock is { } clock && _timesAbsolute && _clockFrequency > 0)
        {
            long nowUs = MonotonicTime.ToMicroseconds(clock.Ticks, _clockFrequency);
            visible = (nowUs - _lastSampleUs) < (TipStaleSeconds * 1_000_000);
        }

        if (visible)
        {
            var offset = new Vector(surface.TipX / _dpiScale, surface.TipY / _dpiScale);
            if (offset != _tipOffset)
            {
                _tipOffset = offset;
                _tip.Offset = offset;
            }
        }

        if (visible != _tipShown)
        {
            _tipShown = visible;
            _tip.Opacity = visible ? 1 : 0;
        }
    }

    private void DrawTip()
    {
        using DrawingContext dc = _tip.RenderOpen();
        dc.DrawEllipse(null, TipPen, new Point(0, 0), TipRadiusDip, TipRadiusDip);
        dc.DrawLine(TipPen, new Point(-TipRadiusDip - 4, 0), new Point(-TipRadiusDip + 3, 0));
        dc.DrawLine(TipPen, new Point(TipRadiusDip - 3, 0), new Point(TipRadiusDip + 4, 0));
        dc.DrawLine(TipPen, new Point(0, -TipRadiusDip - 4), new Point(0, -TipRadiusDip + 3));
        dc.DrawLine(TipPen, new Point(0, TipRadiusDip - 3), new Point(0, TipRadiusDip + 4));
    }
}
