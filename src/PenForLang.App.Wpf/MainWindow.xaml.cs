using System.Globalization;
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
/// <see cref="MousePenSource"/> по <c>--mouse</c>), временной отрисовкой точек и панелью диагностики для протокола
/// <c>docs/spikes/V0-hardware.md</c>. Esc закрывает окно, C очищает холст.
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppOptions _options;
    private readonly QpcMonotonicClock _clock = new();
    private readonly PenStatsMeter _meter = new();
    private readonly DispatcherTimer _panelTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private readonly DebugInkCanvas _inkCanvas = new();
    private HwndSource? _hwndSource;
    private HwndPenSourceBase? _source;
    private PenMonitorChoice _monitorChoice = new(null, PenMonitorChoiceSource.None, null, 0);
    private InkSuppressionResult? _suppression;
    private PenSurface? _surface;
    private double _dpiScale = 1.0;
    private (double X, double Y)? _lastDown;

    private long _wpfMouseMoves;
    private long _wpfMouseDowns;
    private long _wpfStylusMoves;
    private long _wpfStylusDowns;

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
        LocationChanged += (_, _) => UpdateSurface();
        SizeChanged += (_, _) => UpdateSurface();
        LayoutUpdated += (_, _) => UpdateSurface();
        DpiChanged += OnDpiChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseMove += (_, _) => _wpfMouseMoves++;
        PreviewMouseDown += (_, _) => _wpfMouseDowns++;
        PreviewStylusMove += (_, _) => _wpfStylusMoves++;
        PreviewStylusDown += (_, _) => _wpfStylusDowns++;
        _panelTimer.Tick += (_, _) => RefreshPanel();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwndSource = (HwndSource)PresentationSource.FromVisual(this)!;
        _dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;

        // Монитор пера и размещение в физических px (§7.4). Выбор родителя при неоднозначности — позже.
        _monitorChoice = PenMonitorLocator.Locate();
        PlaceOnPenMonitor();

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
        PlaceOnPenMonitor();
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

        // WPF при смене DPI подгоняет размер под DIP; возвращаем окно на весь монитор пера в физических px.
        PlaceOnPenMonitor();
        UpdateSurface();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key == Key.C)
        {
            e.Handled = true;
            _inkCanvas.Clear();
        }
        else if (e.Key == Key.T)
        {
            e.Handled = true;
            _inkCanvas.ShowTargets = !_inkCanvas.ShowTargets;
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
            }
        }

        _inkCanvas.AddSamples(span, _dpiScale);
    }

    private void PlaceOnPenMonitor()
    {
        if (_hwndSource is null || _monitorChoice.Monitor is not MonitorDescription monitor)
        {
            return;
        }

        MonitorRect bounds = monitor.BoundsPx;
        if (_options.Windowed)
        {
            int insetX = bounds.Width / 8;
            int insetY = bounds.Height / 8;
            bounds = new MonitorRect(bounds.Left + insetX, bounds.Top + insetY, bounds.Right - insetX, bounds.Bottom - insetY);
        }

        WindowPlacement.Place(_hwndSource.Handle, bounds, topmost: !_options.Windowed);
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

    private void RefreshPanel()
    {
        PenStats s = _meter.Snapshot();
        var text = new StringBuilder();
        CultureInfo inv = CultureInfo.InvariantCulture;

        text.AppendLine("PenForLang V0.5 - pen in window (dev panel)");
        text.AppendLine(inv, $"Source: {(_source?.Kind.ToString() ?? "-")}  device: {_source?.Device?.Product ?? "-"}  tilt reported: {_source?.Device?.Tilt}");
        bool stylusOff = AppContext.TryGetSwitch(AppOptions.DisableStylusSwitch, out bool v) && v;
        text.AppendLine(inv, $"WPF stylus: {(stylusOff ? "DISABLED (DisableStylusAndTouchSupport=true)" : "enabled (WISP)")}");

        if (_monitorChoice.Monitor is MonitorDescription m)
        {
            string size = m.PhysicalWidthMm is double w && m.PhysicalHeightMm is double h ? $"{w:0}x{h:0} mm" : "mm unknown";
            text.AppendLine(inv, $"Monitor: {m.DeviceName} {m.BoundsPx.Width}x{m.BoundsPx.Height} at ({m.BoundsPx.Left},{m.BoundsPx.Top}) {size}, DPI scale {m.DpiScale:0.00}");
            text.AppendLine(inv, $"  chosen by: {_monitorChoice.Source}; candidates: {_monitorChoice.CandidateCount}; parent must confirm: {_monitorChoice.NeedsParentConfirmation}");
        }
        else
        {
            text.AppendLine("Monitor: none found");
        }

        text.AppendLine(inv, $"Window DPI scale: {_dpiScale:0.00} ({_dpiScale * 96:0} dpi); canvas: {(_surface is PenSurface sf ? $"{sf.WidthPx:0}x{sf.HeightPx:0} px at ({sf.OriginXPx:0},{sf.OriginYPx:0})" : "-")}");

        if (_suppression is InkSuppressionResult sup)
        {
            text.AppendLine(inv, $"Ink suppression: tablet property {(sup.TabletPropertySet ? "set" : "FAILED")}, feedback off {sup.FeedbackSettingsApplied}/{WindowInkSuppressor.PenFeedbackTypes.Count}{(sup.Errors.Count > 0 ? " (" + sup.Errors[0] + ")" : string.Empty)}");
        }

        if (_source is Win32PointerPenSource win32)
        {
            PointerDiagnostics d = win32.Diagnostics;
            text.AppendLine(inv, $"WM_POINTER: total {d.PointerMessages}, pen {d.PenMessages}, non-pen {d.NonPenMessages}, handled {d.HandledMessages}, history failures {d.HistoryFailures}, buffer growths {d.HistoryBufferGrowths}, time fallback {d.TimeFallbackSamples}");
        }

        text.AppendLine(inv, $"WPF saw: mouse move {_wpfMouseMoves}, mouse down {_wpfMouseDowns}, stylus move {_wpfStylusMoves}, stylus down {_wpfStylusDowns}");
        text.AppendLine(inv, $"Rate: {s.RateHz:0.0} Hz (last 1 s)  dt median {s.MedianDtMs:0.0} ms, max {s.MaxDtMs:0.0} ms, gaps > 12 ms: {s.GapsOverThreshold}");
        text.AppendLine(inv, $"Samples: {s.TotalSamples} (contact {s.ContactSamples}, coalesced {s.CoalescedSamples}, clipped {s.ClippedSamples}), strokes {s.Strokes}");
        text.AppendLine(inv, $"Last: pointerId {s.LastPointerId}  {s.LastPhase}  pressure {s.LastPressure:0.000} (max {s.MaxPressure:0.000})  tilt {s.LastTiltX:0}/{s.LastTiltY:0}  buttons {s.LastButtons}  x {s.LastX:0.0} y {s.LastY:0.0}");

        if (_lastDown is (double downX, double downY) && _surface is PenSurface surf)
        {
            (int number, double dx, double dy) = AccuracyTargets.Nearest(AccuracyTargets.Grid(surf.WidthPx, surf.HeightPx), downX, downY);
            double mmPerPx = _monitorChoice.Monitor is { PhysicalWidthMm: double pw, BoundsPx.Width: > 0 } mon ? pw / mon.BoundsPx.Width : 0;
            text.AppendLine(inv, $"Last down: x {downX:0.0} y {downY:0.0}; nearest target #{number}: dx {dx:0.0} dy {dy:0.0} px ({dx * mmPerPx:0.00}, {dy * mmPerPx:0.00} mm){(_inkCanvas.ShowTargets ? string.Empty : "  [T - show targets]")}");
        }

        if (_options.UnknownArguments.Count > 0)
        {
            text.AppendLine("Unknown arguments: " + string.Join(' ', _options.UnknownArguments));
        }

        text.Append("Esc - exit, C - clear, T - 9 accuracy targets");
        PanelText.Text = text.ToString();
    }
}
