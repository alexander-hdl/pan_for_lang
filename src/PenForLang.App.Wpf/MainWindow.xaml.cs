using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PenForLang.Ink;
using PenForLang.Platform.Windows;

namespace PenForLang.App.Wpf;

/// <summary>
/// Окно-холст V0.5: без рамки, на мониторе пера, с источником пера (<see cref="Win32PointerPenSource"/> или
/// <see cref="MousePenSource"/> по <c>--mouse</c>), временной отрисовкой точек, полосой с тестовой кнопкой вне холста и панелью
/// диагностики для протокола <c>docs/spikes/V0-hardware.md</c>. При выходе пишет отчёт сеанса (<see cref="SessionReport"/>).
/// Клавиши: Esc — выход, C — очистить, T — 9 мишеней точности, S — снимок в отчёт.
/// </summary>
public partial class MainWindow : Window
{
    private const int MaxPlacementAttempts = 6;
    private const double TargetHitRadiusPx = 150;

    private readonly AppOptions _options;
    private readonly QpcMonotonicClock _clock = new();
    private readonly PenStatsMeter _meter = new();
    private readonly DispatcherTimer _panelTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DebugInkCanvas _inkCanvas = new();
    private readonly DateTimeOffset _started = DateTimeOffset.Now;
    private readonly List<ReportSnapshot> _snapshots = [];
    private readonly List<string> _errors = [];
    private readonly Dictionary<int, TargetResult> _targets = [];

    private HwndSource? _hwndSource;
    private HwndPenSourceBase? _source;
    private IReadOnlyList<MonitorDescription> _monitors = [];
    private IReadOnlyList<PointerDeviceDescription> _devices = [];
    private PenMonitorChoice _monitorChoice = new(null, PenMonitorChoiceSource.None, null, 0);
    private InkSuppressionResult? _suppression;
    private PenSurface? _surface;
    private double _dpiScale = 1.0;
    private (double X, double Y)? _lastDown;
    private string? _reportPath;
    private string _notice = string.Empty;
    private bool _reportWritten;

    private bool _placementPending;
    private int _placementAttempts;

    private long _wpfMouseMoves;
    private long _wpfMouseDowns;
    private long _wpfStylusMoves;
    private long _wpfStylusDowns;
    private long _buttonClicks;

    /// <summary>Создаёт окно.</summary>
    public MainWindow(AppOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        InitializeComponent();
        CanvasHost.Children.Add(_inkCanvas);

        if (!options.UseMouse && !options.Windowed)
        {
            Cursor = Cursors.None; // §7.2: свой маркер кончика при hover
        }

        Topmost = !options.Windowed;
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
        LocationChanged += (_, _) => OnGeometryChanged();
        SizeChanged += (_, _) => OnGeometryChanged();
        LayoutUpdated += (_, _) => UpdateSurface();
        DpiChanged += OnDpiChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseMove += (_, _) => _wpfMouseMoves++;
        PreviewMouseDown += (_, _) => _wpfMouseDowns++;
        PreviewStylusMove += (_, _) => _wpfStylusMoves++;
        PreviewStylusDown += (_, _) => _wpfStylusDowns++;
        _panelTimer.Tick += (_, _) => RefreshPanel();
    }

    /// <summary>Записывает отчёт с пометкой о сбое (вызывается из обработчика необработанных исключений).</summary>
    public void WriteCrashReport(Exception exception)
    {
        _errors.Add("Необработанное исключение: " + exception.GetType().Name + ": " + exception.Message);
        WriteReport("crash");
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwndSource = (HwndSource)PresentationSource.FromVisual(this)!;
        _dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;

        // Монитор пера (§7.4). Принудительный номер из --monitor имеет приоритет. Выбор родителя при неоднозначности — позже.
        _monitors = PenMonitorLocator.EnumerateMonitors();
        _devices = PenMonitorLocator.EnumeratePointerDevices();
        _monitorChoice = _options.MonitorIndex is int index
            ? PenMonitorSelector.SelectForced(_monitors, index, _devices)
            : PenMonitorSelector.Select(_devices, _monitors);
        if (_options.MonitorIndex is int forced && _monitorChoice.Source != PenMonitorChoiceSource.CommandLine)
        {
            _errors.Add($"--monitor {forced}: такого монитора нет (всего {_monitors.Count}); выбран автоматически.");
        }

        // F2: сначала перенос на монитор пера без изменения размера, чтобы WPF определил DPI этого монитора;
        // точная постановка — отложенно (после того, как WPF обработает смену DPI).
        if (TargetBounds() is MonitorRect target)
        {
            WindowPlacement.MoveTo(_hwndSource.Handle, target.Left, target.Top);
        }

        // Помехи Windows Ink — только для этого окна (§7.2).
        _suppression = WindowInkSuppressor.Apply(_hwndSource.Handle);
        WindowInkSuppressor.Attach(_hwndSource);

        HwndPenSourceBase source = _options.UseMouse
            ? new MousePenSource(_clock)
            : new Win32PointerPenSource(_clock);
        source.Attach(_hwndSource);
        source.SamplesReceived += OnSamples;
        _source = source;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SchedulePlacement();
        UpdateSurface();
        if (_source is not null)
        {
            await _source.StartAsync(CancellationToken.None);
        }

        _panelTimer.Start();
        RefreshPanel();

        if (_options.ExitAfter is TimeSpan exitAfter)
        {
            var exitTimer = new DispatcherTimer { Interval = exitAfter };
            exitTimer.Tick += (_, _) => Close();
            exitTimer.Start();
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _panelTimer.Stop();
        if (!_reportWritten)
        {
            _reportWritten = true;
            WriteReport("exit");
        }

        if (_source is not null)
        {
            _source.SamplesReceived -= OnSamples;
            _source.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _source = null;
        }
    }

    private void OnDpiChanged(object sender, DpiChangedEventArgs e)
    {
        _dpiScale = e.NewDpi.DpiScaleX;

        // F2: WPF сам вызовет SetWindowPos с предложенным системой прямоугольником ПОСЛЕ этого события и перетрёт размер;
        // поэтому постановка откладывается до простоя диспетчера.
        SchedulePlacement();
        UpdateSurface();
    }

    private void OnGeometryChanged()
    {
        SchedulePlacement();
        UpdateSurface();
    }

    private void OnTestButtonClick(object sender, RoutedEventArgs e)
    {
        _buttonClicks++;
        ClickText.Text = "Clicks: " + _buttonClicks.ToString(CultureInfo.InvariantCulture);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                Close();
                break;
            case Key.C:
                e.Handled = true;
                _inkCanvas.Clear();
                break;
            case Key.T:
                e.Handled = true;
                _inkCanvas.ShowTargets = !_inkCanvas.ShowTargets;
                break;
            case Key.S:
                e.Handled = true;
                TakeSnapshot();
                break;
            default:
                break;
        }
    }

    private void OnSamples(ReadOnlyMemory<PenSample> samples)
    {
        ReadOnlySpan<PenSample> span = samples.Span;
        _meter.Add(span);
        foreach (PenSample sample in span)
        {
            if (sample.Phase == PenPhase.Down)
            {
                _lastDown = (sample.X, sample.Y);
                RecordTarget(sample.X, sample.Y);
            }
        }

        _inkCanvas.AddSamples(span, _dpiScale);
    }

    private void RecordTarget(double x, double y)
    {
        if (!_inkCanvas.ShowTargets || _surface is not PenSurface surface)
        {
            return;
        }

        (int number, double dx, double dy) = AccuracyTargets.Nearest(AccuracyTargets.Grid(surface.WidthPx, surface.HeightPx), x, y);
        if (Math.Sqrt((dx * dx) + (dy * dy)) > TargetHitRadiusPx)
        {
            return;
        }

        double mmPerPx = MmPerPx();
        int count = _targets.TryGetValue(number, out TargetResult? previous) ? previous.Count + 1 : 1;
        _targets[number] = new TargetResult(number, dx, dy, dx * mmPerPx, dy * mmPerPx, count);
    }

    private double MmPerPx() =>
        _monitorChoice.Monitor is { PhysicalWidthMm: double widthMm, BoundsPx.Width: > 0 } monitor ? widthMm / monitor.BoundsPx.Width : 0;

    /// <summary>Целевой прямоугольник окна в физических px: весь монитор пера или (в оконном режиме) его центральная часть.</summary>
    private MonitorRect? TargetBounds()
    {
        if (_monitorChoice.Monitor is not MonitorDescription monitor)
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
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            _placementPending = false;
            EnsurePlacement();
        });
    }

    /// <summary>
    /// F2: ставит окно в целевой прямоугольник и повторяет, пока WPF не перестанет его менять (до <see cref="MaxPlacementAttempts"/> раз).
    /// </summary>
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

    /// <summary>Экранный прямоугольник холста в ФИЗИЧЕСКИХ px из <c>PointToScreen</c> (A12): на DPI не умножается.</summary>
    private void UpdateSurface()
    {
        if (!IsLoaded || _source is null || _inkCanvas.ActualWidth <= 0 || _inkCanvas.ActualHeight <= 0)
        {
            return;
        }

        Point topLeft = _inkCanvas.PointToScreen(new Point(0, 0));
        Point bottomRight = _inkCanvas.PointToScreen(new Point(_inkCanvas.ActualWidth, _inkCanvas.ActualHeight));
        var surface = PenSurface.FromScreenCorners(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
        if (_surface == surface)
        {
            return;
        }

        _surface = surface;
        _source.SetSurface(surface);
    }

    private PointerDiagnostics? PointerDiagnosticsOrNull() => (_source as Win32PointerPenSource)?.Diagnostics;

    private bool? WindowFitsTarget() =>
        _hwndSource is not null && TargetBounds() is MonitorRect target
            ? WindowPlacement.GetBounds(_hwndSource.Handle) == target
            : null;

    private void TakeSnapshot()
    {
        PenStats stats = _meter.Snapshot();
        _snapshots.Add(new ReportSnapshot(_snapshots.Count + 1, DateTimeOffset.Now, stats, PointerDiagnosticsOrNull(), _buttonClicks, _wpfMouseDowns));
        WriteReport("snapshot");
        _notice = _reportPath is null
            ? "Snapshot FAILED to save - see report errors"
            : $"Snapshot {_snapshots.Count} saved to: {_reportPath}";
        RefreshPanel();
    }

    private void WriteReport(string reason)
    {
        IReadOnlyList<string> directories = ReportWriter.CandidateDirectories(_options.ReportDirectory);
        string fileName = ReportWriter.FileName(_started);
        string known = _reportPath ?? Path.Combine(directories[0], fileName);

        ReportInput input = BuildReportInput(reason) with { ReportPath = known };
        bool ok = ReportWriter.TryWrite(directories, fileName, SessionReport.Build(input), out string? path, out string? error);
        if (ok && path != known)
        {
            // Писали не туда, куда рассчитывали: повторить с настоящим путём внутри отчёта.
            ReportWriter.TryWrite([Path.GetDirectoryName(path)!], fileName, SessionReport.Build(input with { ReportPath = path }), out _, out _);
        }

        if (ok)
        {
            _reportPath = path;
        }

        if (error is not null)
        {
            string message = "Отчёт: " + error;
            if (!_errors.Contains(message, StringComparer.Ordinal))
            {
                _errors.Add(message);
            }
        }
    }

    private ReportInput BuildReportInput(string reason)
    {
        Version os = Environment.OSVersion.Version;
        bool stylusOff = AppContext.TryGetSwitch(AppOptions.DisableStylusSwitch, out bool disabled) && disabled;
        bool pointerSupport = AppContext.TryGetSwitch(AppOptions.EnablePointerSupportSwitch, out bool enabled) && enabled;
        return new ReportInput
        {
            Reason = reason,
            Now = DateTimeOffset.Now,
            Started = _started,
            Arguments = Environment.GetCommandLineArgs().Skip(1).ToArray(),
            OsDescription = RuntimeInformation.OSDescription,
            OsVersion = $"{os.Major}.{os.Minor}.{os.Build}",
            OsSupported = WindowsVersion.IsSupported(os.Major, os.Minor, os.Build),
            FrameworkDescription = RuntimeInformation.FrameworkDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            AppVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "?",
            StylusDisabledSwitch = stylusOff,
            PointerSupportSwitch = pointerSupport,
            SourceKind = _source?.Kind.ToString() ?? (_options.UseMouse ? "Mouse" : "Win32Pointer"),
            DeviceProduct = _source?.Device?.Product,
            TiltReported = _source?.Device?.Tilt ?? false,
            Monitors = _monitors,
            Choice = _monitorChoice,
            Devices = _devices,
            DpiScale = _dpiScale,
            Surface = _surface,
            WindowFitsTarget = WindowFitsTarget(),
            Suppression = _suppression,
            PointerStats = PointerDiagnosticsOrNull(),
            WpfMouseMoves = _wpfMouseMoves,
            WpfMouseDowns = _wpfMouseDowns,
            WpfStylusMoves = _wpfStylusMoves,
            WpfStylusDowns = _wpfStylusDowns,
            ButtonClicks = _buttonClicks,
            Stats = _meter.Snapshot(),
            Targets = [.. _targets.Values],
            Snapshots = _snapshots,
            Errors = _errors,
            ReportPath = _reportPath,
        };
    }

    private void RefreshPanel()
    {
        PenStats s = _meter.Snapshot();
        var text = new StringBuilder();
        CultureInfo inv = CultureInfo.InvariantCulture;

        text.AppendLine("PenForLang V0.5 - pen in window (dev panel)");
        text.AppendLine(inv, $"Source: {(_source?.Kind.ToString() ?? "-")}  device: {_source?.Device?.Product ?? "-"}  tilt reported: {_source?.Device?.Tilt}");
        bool stylusOff = AppContext.TryGetSwitch(AppOptions.DisableStylusSwitch, out bool v) && v;
        bool pointerSupport = AppContext.TryGetSwitch(AppOptions.EnablePointerSupportSwitch, out bool ps) && ps;
        text.AppendLine(inv, $"WPF stylus: {(stylusOff ? "DISABLED (DisableStylusAndTouchSupport=true)" : "enabled (WISP)")}{(pointerSupport ? " + EnablePointerSupport" : string.Empty)}");

        text.Append("Monitors: ");
        for (int i = 0; i < _monitors.Count; i++)
        {
            text.Append(inv, $"[{i + 1}] {_monitors[i].DeviceName} {_monitors[i].BoundsPx.Width}x{_monitors[i].BoundsPx.Height}{(_monitors[i].IsPrimary ? " primary" : string.Empty)}  ");
        }

        text.AppendLine();
        if (_monitorChoice.Monitor is MonitorDescription m)
        {
            string size = m.PhysicalWidthMm is double w && m.PhysicalHeightMm is double h ? $"{w:0}x{h:0} mm" : "mm unknown";
            text.AppendLine(inv, $"Pen monitor: {m.DeviceName} {m.BoundsPx.Width}x{m.BoundsPx.Height} at ({m.BoundsPx.Left},{m.BoundsPx.Top}) {size}, DPI scale {m.DpiScale:0.00}");
            text.AppendLine(inv, $"  chosen by: {_monitorChoice.Source}; candidates: {_monitorChoice.CandidateCount}; parent must confirm: {_monitorChoice.NeedsParentConfirmation}; window fits: {WindowFitsTarget()?.ToString() ?? "?"}");
        }
        else
        {
            text.AppendLine("Pen monitor: none found");
        }

        text.AppendLine(inv, $"Window DPI scale: {_dpiScale:0.00} ({_dpiScale * 96:0} dpi); canvas: {(_surface is PenSurface sf ? $"{sf.WidthPx:0}x{sf.HeightPx:0} px at ({sf.OriginXPx:0},{sf.OriginYPx:0})" : "-")}");

        if (_suppression is InkSuppressionResult sup)
        {
            text.AppendLine(inv, $"Ink suppression: tablet property {(sup.TabletPropertySet ? "set" : "FAILED")}, feedback off {sup.FeedbackSettingsApplied}/{WindowInkSuppressor.PenFeedbackTypes.Count}{(sup.Errors.Count > 0 ? " (" + sup.Errors[0] + ")" : string.Empty)}");
        }

        if (_source is Win32PointerPenSource win32)
        {
            PointerDiagnostics d = win32.Diagnostics;
            text.AppendLine(inv, $"WM_POINTER: total {d.PointerMessages}, pen {d.PenMessages}, non-pen {d.NonPenMessages}, handled {d.HandledMessages}, enter/leave {d.EnterLeaveMessages}, dup frames {d.DuplicateFrames}");
            text.AppendLine(inv, $"  history failures {d.HistoryFailures} (last Win32 error {d.LastHistoryError}, rescued {d.HistoryFallbacks}), buffer growths {d.HistoryBufferGrowths}, no PerformanceCount {d.TimeFallbackSamples}");
        }

        text.AppendLine(inv, $"WPF saw: mouse move {_wpfMouseMoves}, mouse down {_wpfMouseDowns}, stylus move {_wpfStylusMoves}, stylus down {_wpfStylusDowns}; test button clicks {_buttonClicks}");
        text.AppendLine(inv, $"Rate (contact only): {s.RateHz:0.0} Hz last 1 s; median {s.OverallMedianHz:0.0} Hz, p5 {s.OverallP5Hz:0.0} Hz; dt median {s.MedianDtMs:0.0} ms, max {s.MaxDtMs:0.0} ms, gaps > 12 ms: {s.GapsOverThreshold}");
        text.AppendLine(inv, $"Samples: {s.TotalSamples} (contact {s.ContactSamples}, coalesced {s.CoalescedSamples}, clipped {s.ClippedSamples}), strokes {s.Strokes}");
        text.AppendLine(inv, $"Last: pointerId {s.LastPointerId}  {s.LastPhase}  pressure {s.LastPressure:0.000} (min {s.MinPressure:0.000} max {s.MaxPressure:0.000}, {s.PressureLevels} levels)  tilt {s.LastTiltX:0}/{s.LastTiltY:0}  buttons {s.LastButtons}  x {s.LastX:0.0} y {s.LastY:0.0}");

        if (_lastDown is (double downX, double downY) && _surface is PenSurface surf)
        {
            (int number, double dx, double dy) = AccuracyTargets.Nearest(AccuracyTargets.Grid(surf.WidthPx, surf.HeightPx), downX, downY);
            double mmPerPx = MmPerPx();
            text.AppendLine(inv, $"Last down: x {downX:0.0} y {downY:0.0}; nearest target #{number}: dx {dx:0.0} dy {dy:0.0} px ({dx * mmPerPx:0.00}, {dy * mmPerPx:0.00} mm){(_inkCanvas.ShowTargets ? string.Empty : "  [T - show targets]")}");
        }

        if (_options.UnknownArguments.Count > 0)
        {
            text.AppendLine("Unknown arguments: " + string.Join(' ', _options.UnknownArguments));
        }

        if (_notice.Length > 0)
        {
            text.AppendLine(_notice);
        }

        text.Append("Esc - exit (report is saved to the Desktop), C - clear, T - 9 targets, S - snapshot");
        PanelText.Text = text.ToString();
    }
}
