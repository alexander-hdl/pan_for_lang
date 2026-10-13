using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PenForLang.Core;
using PenForLang.Ink;
using PenForLang.Platform.Windows;
using PenForLang.Wpf.Controls;

namespace PenForLang.App.Wpf;

/// <summary>
/// Общая часть окон с пером для режима урока (V0.8): выбор монитора пера (§7.4), подавление помех Windows Ink только для своего окна
/// (§7.2), постановка окна на монитор, источник пера (<see cref="Win32PointerPenSource"/> или <see cref="MousePenSource"/> по
/// <c>--mouse</c>), экранный прямоугольник холста в физических px (A12) и масштаб px/мм (§7.3). Логика та же, что в окне-песочнице
/// <see cref="MainWindow"/>, которое остаётся нетронутым как режим протокола железа (<c>--sandbox</c>).
/// </summary>
internal sealed class PenWindowHost : IAsyncDisposable
{
    private const int MaxPlacementAttempts = 6;

    private readonly Window _window;
    private readonly AppOptions _options;
    private readonly IMonotonicClock _clock;
    private readonly FrameworkElement _canvas;
    private readonly List<string> _errors = [];

    private HwndSource? _hwndSource;
    private HwndPenSourceBase? _source;
    private IReadOnlyList<MonitorDescription> _monitors = [];
    private PenMonitorChoice _choice = new(null, PenMonitorChoiceSource.None, null, 0);
    private PenSurface? _surface;
    private double _dpiScale = 1.0;
    private double _appliedDpiScale;
    private bool _placementPending;
    private int _placementAttempts;

    /// <summary>Создаёт узел.</summary>
    /// <param name="window">Окно.</param>
    /// <param name="options">Параметры командной строки.</param>
    /// <param name="clock">Монотонные часы.</param>
    /// <param name="canvas">Элемент, занимающий область письма.</param>
    public PenWindowHost(Window window, AppOptions options, IMonotonicClock clock, FrameworkElement canvas)
    {
        _window = window;
        _options = options;
        _clock = clock;
        _canvas = canvas;
    }

    /// <summary>Ставить окно на монитор пера (по умолчанию да). UI-тесты выключают: их окно не должно появляться на экране пера.</summary>
    public bool PlacementEnabled { get; set; } = true;

    /// <summary>Пачка точек пера (физические px холста). Поднимается в потоке UI.</summary>
    public event Action<ReadOnlyMemory<PenSample>>? SamplesReceived;

    /// <summary>Холст, масштаб или DPI изменились: раскладку нужно пересчитать.</summary>
    public event Action<PenSurface>? SurfaceChanged;

    /// <summary>Описание устройства: настоящее после старта источника, иначе заглушка по виду источника.</summary>
    public PenDeviceInfo Device =>
        _source?.Device
        ?? new PenDeviceInfo("unknown", _options.UseMouse ? PenSourceKind.Mouse : PenSourceKind.Win32Pointer, null, null, null, false, PenTimestampKind.Qpc);

    /// <summary>Монитор пера.</summary>
    public MonitorDescription? Monitor => _choice.Monitor;

    /// <summary>DPI-масштаб окна.</summary>
    public double DpiScale => _dpiScale;

    /// <summary>Физических пикселей в миллиметре.</summary>
    public double PxPerMm { get; private set; } = SurfaceMapper.DefaultPxPerMm;

    /// <summary>Происхождение <see cref="PxPerMm"/>.</summary>
    public PxPerMmSource PxPerMmSource { get; private set; } = PxPerMmSource.Default;

    /// <summary>Текущий экранный прямоугольник холста.</summary>
    public PenSurface? Surface => _surface;

    /// <summary>Сообщения об ошибках узла (показываются разработчику).</summary>
    public IReadOnlyList<string> Errors => _errors;

    /// <summary>Вызывать из <c>SourceInitialized</c> окна.</summary>
    public void Initialize()
    {
        _hwndSource = (HwndSource)PresentationSource.FromVisual(_window)!;
        _dpiScale = VisualTreeHelper.GetDpi(_window).DpiScaleX;

        IReadOnlyList<PointerDeviceDescription> devices = PenMonitorLocator.EnumeratePointerDevices();
        _monitors = PenMonitorLocator.EnumerateMonitors();
        _choice = _options.MonitorIndex is int index
            ? PenMonitorSelector.SelectForced(_monitors, index, devices)
            : PenMonitorSelector.Select(devices, _monitors);
        if (_options.MonitorIndex is int forced && _choice.Source != PenMonitorChoiceSource.CommandLine)
        {
            _errors.Add($"--monitor {forced}: monitor not found (total {_monitors.Count}); chosen automatically.");
        }

        // Сначала перенос на монитор пера без изменения размера, чтобы WPF определил DPI этого монитора; точная постановка — отложенно.
        if (TargetBounds() is MonitorRect target)
        {
            WindowPlacement.MoveTo(_hwndSource.Handle, target.Left, target.Top);
        }

        WindowInkSuppressor.Apply(_hwndSource.Handle);
        WindowInkSuppressor.Attach(_hwndSource);

        HwndPenSourceBase source = _options.UseMouse ? new MousePenSource(_clock) : new Win32PointerPenSource(_clock);
        source.Attach(_hwndSource);
        source.SamplesReceived += OnSamples;
        _source = source;
    }

    /// <summary>Запускает источник (из <c>Loaded</c>).</summary>
    public async Task StartAsync()
    {
        SchedulePlacement();
        UpdateSurface();
        if (_source is not null)
        {
            await _source.StartAsync(CancellationToken.None);
        }
    }

    /// <summary>Сменился DPI окна.</summary>
    public void OnDpiChanged(DpiChangedEventArgs e)
    {
        _dpiScale = e.NewDpi.DpiScaleX;
        SchedulePlacement();
        UpdateSurface();
    }

    /// <summary>Сменились положение или размер окна.</summary>
    public void OnGeometryChanged()
    {
        SchedulePlacement();
        UpdateSurface();
    }

    /// <summary>
    /// Экранный прямоугольник холста в ФИЗИЧЕСКИХ px из <c>PointToScreen</c> (A12): на DPI не умножается. Поднимает
    /// <see cref="SurfaceChanged"/> только при изменении прямоугольника или DPI.
    /// </summary>
    public void UpdateSurface()
    {
        if (!_window.IsLoaded || _source is null || _canvas.ActualWidth <= 0 || _canvas.ActualHeight <= 0)
        {
            return;
        }

        Point topLeft = _canvas.PointToScreen(new Point(0, 0));
        Point bottomRight = _canvas.PointToScreen(new Point(_canvas.ActualWidth, _canvas.ActualHeight));
        var surface = PenSurface.FromScreenCorners(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
        if (_surface == surface && _appliedDpiScale == _dpiScale)
        {
            return;
        }

        _surface = surface;
        _appliedDpiScale = _dpiScale;
        _source.SetSurface(surface);
        (double pxPerMm, PxPerMmSource origin) = SandboxLayout.ResolvePxPerMm(_choice.Monitor);
        PxPerMm = pxPerMm;
        PxPerMmSource = origin;
        SurfaceChanged?.Invoke(surface);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_source is not null)
        {
            _source.SamplesReceived -= OnSamples;
            await _source.DisposeAsync();
            _source = null;
        }
    }

    private void OnSamples(ReadOnlyMemory<PenSample> samples) => SamplesReceived?.Invoke(samples);

    /// <summary>Целевой прямоугольник окна в физических px: весь монитор пера или (в оконном режиме) его центральная часть.</summary>
    private MonitorRect? TargetBounds()
    {
        if (!PlacementEnabled || _choice.Monitor is not MonitorDescription monitor)
        {
            return null;
        }

        MonitorRect bounds = monitor.BoundsPx;
        if (_options.Windowed)
        {
            int insetX = bounds.Width / 8;
            int insetY = bounds.Height / 8;
            bounds = new MonitorRect(bounds.Left + insetX, bounds.Top + insetY, bounds.Right - insetX, bounds.Bottom - insetY);
        }

        return bounds;
    }

    private void SchedulePlacement()
    {
        if (_placementPending || _hwndSource is null)
        {
            return;
        }

        _placementPending = true;
        _window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            _placementPending = false;
            EnsurePlacement();
        });
    }

    /// <summary>Ставит окно в целевой прямоугольник и повторяет, пока WPF не перестанет его менять (до <see cref="MaxPlacementAttempts"/> раз).</summary>
    private void EnsurePlacement()
    {
        if (_hwndSource is null || TargetBounds() is not MonitorRect target)
        {
            return;
        }

        if (WindowPlacement.GetBounds(_hwndSource.Handle) == target)
        {
            _placementAttempts = 0;
            return;
        }

        if (_placementAttempts++ >= MaxPlacementAttempts)
        {
            return;
        }

        WindowPlacement.Place(_hwndSource.Handle, target, topmost: !_options.Windowed);
        SchedulePlacement();
    }
}
