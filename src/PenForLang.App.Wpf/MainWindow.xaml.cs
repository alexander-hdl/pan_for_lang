using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PenForLang.Core;
using PenForLang.Glyphs;
using PenForLang.Glyphs.Loading;
using PenForLang.Ink;
using PenForLang.Platform.Windows;
using PenForLang.Wpf.Controls;

namespace PenForLang.App.Wpf;

/// <summary>
/// Окно V0.6: «урок-песочница» — линейки прописи (<see cref="GuidesLayer"/>), показ буквы a/i/t (<see cref="GlyphAnimatorLayer"/>),
/// живые чернила (<see cref="LiveInkLayer"/>), кнопки «Показать», «Заново», «Готово» в полосе вне холста, запись penlog каждой попытки
/// (<see cref="SandboxSession"/>) и dev-оверлей. Сохранены возможности V0.5: источник пера (<see cref="Win32PointerPenSource"/> или
/// <see cref="MousePenSource"/> по <c>--mouse</c>), тестовая кнопка вне холста, панель диагностики и отчёт сеанса
/// (<see cref="SessionReport"/>). Клавиши: Esc — выход, C — «Заново», T — 9 мишеней точности, S — снимок в отчёт, P — ширина линии от давления вкл/выкл, F12 — dev-оверлей,
/// F11 — подробности оверлея (диагностика V0.5), L — замер задержки «перо → пиксель» камерой.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "The session is disposed in OnClosing; Window has its own lifetime.")]
public partial class MainWindow : Window
{
    private const int MaxPlacementAttempts = 6;
    private const double TargetHitRadiusPx = 150;
    private const double LatencyBaseWidthMm = 3.0;

    private readonly AppOptions _options;
    private readonly QpcMonotonicClock _clock = new();
    private readonly PenStatsMeter _meter = new();
    private readonly DispatcherTimer _panelTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly GuidesLayer _guides = new();
    private readonly GlyphAnimatorLayer _animator;
    private readonly AccuracyTargetsLayer _targets = new();
    private readonly LiveInkLayer _ink;
    private readonly LatencyProbeLayer _probe;
    private readonly DateTimeOffset _started = DateTimeOffset.Now;
    private readonly List<ReportSnapshot> _snapshots = [];
    private readonly List<string> _errors = [];
    private readonly Dictionary<int, TargetResult> _targetResults = [];

    private SandboxContent? _content;
    private SandboxSession? _session;
    private string? _penLogDirectory;
    private string _letter = "a";

    private HwndSource? _hwndSource;
    private HwndPenSourceBase? _source;
    private IReadOnlyList<MonitorDescription> _monitors = [];
    private IReadOnlyList<PointerDeviceDescription> _devices = [];
    private PenMonitorChoice _monitorChoice = new(null, PenMonitorChoiceSource.None, null, 0);
    private InkSuppressionResult? _suppression;
    private PenSurface? _surface;
    private double _dpiScale = 1.0;
    private double _appliedDpiScale;
    private double _pxPerMm = SurfaceMapper.DefaultPxPerMm;
    private PxPerMmSource _pxPerMmSource = PxPerMmSource.Default;
    private GuideFrame? _writingFrame;
    private (double X, double Y)? _lastDown;
    private string? _reportPath;
    private string _notice = string.Empty;
    private string _status = string.Empty;
    private bool _reportWritten;
    private bool _overlayVisible;
    private bool _overlayDetails;
    private bool _latencyMode;
    private bool _overlayBeforeLatency;
    private double _savedInkWidthMm = 1.0;
    private double _appliedPxPerMm;
    private string? _savedFlash;
    private readonly DispatcherTimer _flashTimer = new() { Interval = TimeSpan.FromSeconds(2) };

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

        _animator = new GlyphAnimatorLayer(_clock) { Visibility = Visibility.Collapsed };
        _ink = new LiveInkLayer(_clock, null) { ShowTip = !options.UseMouse, PressureEnabled = options.PressureWidth };
        _probe = new LatencyProbeLayer(_clock);
        CanvasHost.Children.Add(_guides);
        CanvasHost.Children.Add(_animator);
        CanvasHost.Children.Add(_targets);
        CanvasHost.Children.Add(_ink);
        CanvasHost.Children.Add(_probe);
        _animator.PlaybackFinished += (_, _) => _session?.StimulusEnded();

        _overlayVisible = options.Dev;
        _overlayDetails = !options.UseMouse; // подробности V0.5 нужны протоколу на планшете; в режиме мыши они закрывали бы линейки
        LoadContent();
        CreateSession();

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
        _flashTimer.Tick += (_, _) =>
        {
            _flashTimer.Stop();
            _savedFlash = null;
            RefreshStatus();
        };
    }

    /// <summary>Записывает отчёт с пометкой о сбое (вызывается из обработчика необработанных исключений).</summary>
    public void WriteCrashReport(Exception exception)
    {
        _errors.Add("Необработанное исключение: " + exception.GetType().Name + ": " + exception.Message);
        _session?.Close();
        WriteReport("crash");
    }

    private void LoadContent()
    {
        string? root = SandboxContent.FindContentRoot(_options.ContentDirectory, AppContext.BaseDirectory);
        if (root is null)
        {
            _errors.Add("Контент не найден: нужна папка content/styles/zaner-bloser рядом с программой (или --content-dir).");
            _status = "Контент не найден";
            ShowButton.IsEnabled = false;
            return;
        }

        try
        {
            _content = SandboxContent.Load(new DirectoryContentSource(root));
            _guides.Manifest = _content.Style;
            _animator.Script = _content.GetScript(_letter);
        }
        catch (Exception ex) when (ex is StyleLoadException or IOException or KeyNotFoundException or UnauthorizedAccessException)
        {
            _errors.Add("Контент не загружен: " + ex.Message);
            _status = "Контент не загружен";
            ShowButton.IsEnabled = false;
        }
    }

    private void CreateSession()
    {
        _penLogDirectory = _options.Dev
            ? _options.PenLogDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PenForLang", "dev", "penlogs")
            : null;
        _session = new SandboxSession(_clock, _penLogDirectory, () => DateTimeOffset.Now, BuildHeader, _letter);

        // Пока идёт запись, это видно в полосе: dev-запись допустима только для взрослого (ребёнку в этом режиме не писать).
        RecordingBanner.Visibility = _session.IsRecording ? Visibility.Visible : Visibility.Collapsed;
        RefreshStatus();
    }

    private PenLogHeader? BuildHeader(string letter, int attempt)
    {
        if (_writingFrame is not { } frame)
        {
            return null;
        }

        PenDeviceInfo device = _source?.Device
            ?? new PenDeviceInfo("unknown", _options.UseMouse ? PenSourceKind.Mouse : PenSourceKind.Win32Pointer, null, null, null, false, PenTimestampKind.Qpc);
        Version os = Environment.OSVersion.Version;
        return SandboxLayout.Header(
            typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            $"{os.Major}.{os.Minor}.{os.Build}",
            device,
            _monitorChoice.Monitor,
            _dpiScale,
            _pxPerMm,
            _pxPerMmSource,
            frame,
            letter,
            attempt,
            _clock.Frequency);
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

        // Живые чернила подписываются первыми: точки рисуются раньше, чем статистика и запись.
        _ink.Attach(source);
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
        if (_options.StartInLatencyMode)
        {
            SetLatencyMode(true);
        }

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
        _flashTimer.Stop();
        _probe.Deactivate();
        _ink.EndAttempt();
        _session?.Close();
        if (!_reportWritten)
        {
            _reportWritten = true;
            WriteReport("exit");
        }

        _ink.Detach();
        if (_source is not null)
        {
            _source.SamplesReceived -= OnSamples;
            _source.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _source = null;
        }

        _session?.Dispose();
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

    private void OnShowClick(object sender, RoutedEventArgs e) => ShowDemo();

    private void OnLetterClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string letter })
        {
            SelectLetter(letter);
        }
    }

    private void OnRestartClick(object sender, RoutedEventArgs e) => Restart();

    private void OnDoneClick(object sender, RoutedEventArgs e) => Done();

    private void OnOverlayClick(object sender, RoutedEventArgs e) => SetOverlayVisible(!_overlayVisible);

    /// <summary>«Показать»: анимация буквы с начала; в penlog — события стимула.</summary>
    private void ShowDemo()
    {
        if (_content is null || _latencyMode)
        {
            return;
        }

        _animator.Script = _content.GetScript(_letter);
        _animator.Visibility = Visibility.Visible;
        _session?.StimulusStarted();
        _animator.Replay();
        RefreshStatus();
    }

    /// <summary>Выбор буквы: текущая попытка закрывается как отменённая, холст чистый, эталон скрыт.</summary>
    private void SelectLetter(string letter)
    {
        foreach (ToggleButton button in new[] { LetterA, LetterI, LetterT })
        {
            button.IsChecked = string.Equals((string)button.Tag, letter, StringComparison.Ordinal);
        }

        if (string.Equals(letter, _letter, StringComparison.Ordinal))
        {
            return;
        }

        _letter = letter;
        _ink.EndAttempt();
        _ink.ClearBeforeNextStroke = false;
        _ink.Clear();
        _session?.SelectLetter(letter);
        if (_content is not null)
        {
            _animator.Stop();
            _animator.Script = _content.GetScript(letter);
        }

        _animator.Visibility = Visibility.Collapsed;
        RefreshStatus();
    }

    /// <summary>«Заново» (A10, §7.6): очищает холст, попытка продолжается, <c>Restarts + 1</c>; неактивна, пока перо опущено.</summary>
    private void Restart()
    {
        if (_ink.IsPenDown)
        {
            return;
        }

        _ink.Clear();
        _session?.Restart();
        RefreshStatus();
    }

    /// <summary>
    /// «Готово»: попытка завершена кнопкой, penlog закрывается. Чернила не стираются мгновенно — они остаются до следующего касания
    /// (<see cref="LiveInkLayer.ClearBeforeNextStroke"/>), в полосе на пару секунд появляется нейтральное «Сохранено». Путь файла и
    /// число «Заново» видны только в dev-оверлее.
    /// </summary>
    private void Done()
    {
        _ink.EndAttempt();
        RecordedAttempt? recorded = _session?.Done();
        _ink.ClearBeforeNextStroke = true;
        _animator.Stop();
        _animator.Visibility = Visibility.Collapsed;
        _notice = recorded is null
            ? (_session is { IsRecording: true } ? "Попытка без касаний - файл не создан" : string.Empty)
            : $"Попытка записана: {recorded.Path} ({recorded.Samples} точек, Заново: {recorded.Restarts})";
        _savedFlash = "Сохранено";
        _flashTimer.Stop();
        _flashTimer.Start();
        RefreshStatus();
        RefreshPanel();
    }

    private void SetOverlayVisible(bool visible)
    {
        _overlayVisible = visible;
        OverlayBorder.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        RefreshPanel();
    }

    /// <summary>Режим замера задержки камерой: белый фон, крупная отметка под пером, счётчик кадров и QPC.</summary>
    private void SetLatencyMode(bool on)
    {
        if (_latencyMode == on)
        {
            return;
        }

        _latencyMode = on;
        if (on)
        {
            _savedInkWidthMm = _ink.BaseWidthMm;
            CanvasHost.Background = Brushes.White;
            _guides.Visibility = Visibility.Collapsed;
            _animator.Visibility = Visibility.Collapsed;
            _ink.BaseWidthMm = LatencyBaseWidthMm;
            _probe.Activate();
            _overlayBeforeLatency = _overlayVisible;
            _overlayVisible = false; // оверлей закрывал бы счётчик в кадре камеры; F12 вернёт его
        }
        else
        {
            _probe.Deactivate();
            _ink.BaseWidthMm = _savedInkWidthMm;
            CanvasHost.Background = null;
            _guides.Visibility = Visibility.Visible;
            _overlayVisible = _overlayBeforeLatency;
        }

        _notice = on ? "Latency mode: L - exit. Film the pen tip and the pink marker with a 240 fps camera." : string.Empty;
        RefreshStatus();
        RefreshPanel();
    }

    private void RefreshStatus()
    {
        // В полосе ребёнка — только буква и короткое «Сохранено»; номер попытки и счётчик «Заново» — в dev-оверлее.
        string status = "'" + _letter + "'" + (_savedFlash is null ? string.Empty : "  " + _savedFlash);
        StatusText.Text = _status.Length > 0 ? status + "  [" + _status + "]" : status;
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
                Restart();
                break;
            case Key.T:
                e.Handled = true;
                _targets.ShowTargets = !_targets.ShowTargets;
                break;
            case Key.S:
                e.Handled = true;
                TakeSnapshot();
                break;
            case Key.L:
                e.Handled = true;
                SetLatencyMode(!_latencyMode);
                break;
            case Key.P:
                e.Handled = true;
                _ink.PressureEnabled = !_ink.PressureEnabled; // ширина линии от давления (с ограничением) или постоянная
                RefreshPanel();
                break;
            case Key.F12:
                e.Handled = true;
                SetOverlayVisible(!_overlayVisible);
                break;
            case Key.F11:
                e.Handled = true;
                _overlayDetails = !_overlayDetails;
                RefreshPanel();
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
                RestartButton.IsEnabled = false;
                StopDemoOnFirstTouch();
            }
            else if (sample.Phase == PenPhase.Up)
            {
                RestartButton.IsEnabled = true;
            }
        }

        if (_latencyMode && !span.IsEmpty)
        {
            PenSample last = span[^1];
            _probe.MoveTo(last.X / _dpiScale, last.Y / _dpiScale);
        }

        _session?.OnSamples(span);
    }

    /// <summary>
    /// Первое касание в зоне письма во время показа: анимация ставится на паузу и показывает готовую букву (она не должна «рисовать»
    /// под рукой ребёнка); в penlog пишется <c>stimulus-end</c>.
    /// </summary>
    private void StopDemoOnFirstTouch()
    {
        if (!_animator.IsPlaying || _animator.Script is not { } script)
        {
            return;
        }

        _animator.Pause();
        _animator.Seek(script.DurationMs);
        _session?.StimulusEnded();
    }

    private void RecordTarget(double x, double y)
    {
        if (!_targets.ShowTargets || _surface is not PenSurface surface)
        {
            return;
        }

        (int number, double dx, double dy) = AccuracyTargets.Nearest(AccuracyTargets.Grid(surface.WidthPx, surface.HeightPx), x, y);
        if (Math.Sqrt((dx * dx) + (dy * dy)) > TargetHitRadiusPx)
        {
            return;
        }

        double mmPerPx = MmPerPx();
        int count = _targetResults.TryGetValue(number, out TargetResult? previous) ? previous.Count + 1 : 1;
        _targetResults[number] = new TargetResult(number, dx, dy, dx * mmPerPx, dy * mmPerPx, count);
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
        if (!IsLoaded || _source is null || _ink.ActualWidth <= 0 || _ink.ActualHeight <= 0)
        {
            return;
        }

        Point topLeft = _ink.PointToScreen(new Point(0, 0));
        Point bottomRight = _ink.PointToScreen(new Point(_ink.ActualWidth, _ink.ActualHeight));
        var surface = PenSurface.FromScreenCorners(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
        if (_surface == surface && _appliedDpiScale == _dpiScale)
        {
            return;
        }

        _surface = surface;
        _source.SetSurface(surface);
        ApplyLayout(surface);
    }

    /// <summary>Линейки показа и письма в мм → px → слои (DPI входит один раз, при переводе px → DIP внутри слоёв).</summary>
    private void ApplyLayout(PenSurface surface)
    {
        _appliedDpiScale = _dpiScale;
        (_pxPerMm, _pxPerMmSource) = SandboxLayout.ResolvePxPerMm(_monitorChoice.Monitor);
        _ink.PxPerMm = _pxPerMm;
        ApplyTargetSizes();

        // Сменился масштаб px/мм (другой монитор): линейка и чернила больше не соответствуют друг другу — попытка отменяется, холст чистый.
        if (_appliedPxPerMm > 0 && Math.Abs(_appliedPxPerMm - _pxPerMm) > 1e-6)
        {
            _ink.EndAttempt();
            _ink.ClearBeforeNextStroke = false;
            _ink.Clear();
            _session?.CancelAttempt();
        }

        _appliedPxPerMm = _pxPerMm;
        if (_content is null)
        {
            return;
        }

        var mapper = new SurfaceMapper(surface, _pxPerMm, _dpiScale);
        (GuideFrame demo, GuideFrame writing) = SandboxLayout.Frames(mapper, _content.Style, _options.XHeightMm);
        _writingFrame = writing;
        _guides.DpiScale = _dpiScale;
        _guides.Guides = writing;
        _animator.DpiScale = _dpiScale;
        _animator.Guides = demo;

        // Линейка известна — заголовок penlog можно составить: открытие файла уходит в фоновый поток записи (в UI ввода-вывода нет).
        _session?.Arm();
    }

    /// <summary>
    /// Цели для пера: не меньше 12 мм, отступы не меньше 4 мм (ARCHITECTURE.md §7.8, A34). Размеры берутся из миллиметров через
    /// <c>PxPerMm</c> и DPI-масштаб окна (<see cref="SandboxLayout.MmToDip"/>), а не из DIP в разметке.
    /// </summary>
    private void ApplyTargetSizes()
    {
        double target = SandboxLayout.MmToDip(SandboxLayout.TargetMinMm, _pxPerMm, _dpiScale);
        double gap = SandboxLayout.MmToDip(SandboxLayout.TargetGapMm, _pxPerMm, _dpiScale);
        foreach (Control control in new Control[] { ShowButton, LetterA, LetterI, LetterT, RestartButton, DoneButton, OverlayButton, TestButton })
        {
            control.MinHeight = target;
            control.MinWidth = target;
            control.Margin = new Thickness(0, 0, gap, gap);
        }

        StripHost.Padding = new Thickness(gap, gap, gap, 0);
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
            Targets = [.. _targetResults.Values],
            Snapshots = _snapshots,
            Errors = _errors,
            ReportPath = _reportPath,
            Latency = _ink.Latency,
            LiveInkStrokes = _ink.StrokeCount,
            LiveInkSegments = _ink.SegmentCount,
            GcLowLatencyActivations = _ink.GcGuard.ActivationCount,
            GcCollections = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)),
            GcPause = GC.GetTotalPauseDuration(),
            DevMode = _options.Dev,
            PenLogDirectory = _penLogDirectory,
            RecordedAttempts = _session?.Recorded ?? [],
            PenLogError = _session?.LastError,
        };
    }

    private DevOverlayInput BuildOverlayInput(PenStats s)
    {
        MonitorDescription? monitor = _monitorChoice.Monitor;
        int number = monitor is null ? 0 : _monitors.ToList().FindIndex(m => m.Handle == monitor.Handle) + 1;
        return new DevOverlayInput
        {
            SourceKind = _source?.Kind.ToString() ?? "-",
            RateHz = s.OverallMedianHz,
            RateP5Hz = s.OverallP5Hz,
            Latency = _ink.Latency,
            Strokes = _ink.StrokeCount,
            Segments = _ink.SegmentCount,
            PenDown = _ink.IsPenDown,
            PressureWidth = _ink.PressureEnabled,
            DpiScale = _dpiScale,
            MonitorNumber = number,
            MonitorName = monitor?.DeviceName,
            MonitorSizePx = monitor is null ? null : (monitor.BoundsPx.Width, monitor.BoundsPx.Height),
            MonitorSizeMm = monitor is { PhysicalWidthMm: double w, PhysicalHeightMm: double h } ? (w, h) : null,
            PxPerMm = _pxPerMm,
            PxPerMmSource = _pxPerMmSource.ToString().ToLowerInvariant(),
            GcLowLatencyActive = _ink.IsGcLatencyModeActive,
            GcCollections = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)),
            GcPause = GC.GetTotalPauseDuration(),
            Letter = _letter,
            Attempt = _session?.AttemptNumber ?? 0,
            Restarts = _session?.Restarts ?? 0,
            Recording = _session?.IsRecording ?? false,
            PenLogPath = _session?.CurrentPath,
            PenLogSamples = _session?.SamplesInAttempt ?? 0,
            Recorded = _session?.Recorded.Count ?? 0,
            PenLogError = _session?.LastError,
        };
    }

    private void RefreshPanel()
    {
        OverlayBorder.Visibility = _overlayVisible ? Visibility.Visible : Visibility.Collapsed;
        if (!_overlayVisible)
        {
            return;
        }

        PenStats s = _meter.Snapshot();
        var text = new StringBuilder();
        CultureInfo inv = CultureInfo.InvariantCulture;

        text.AppendLine(DevOverlayText.Build(BuildOverlayInput(s)));

        if (_overlayDetails)
        {
            text.AppendLine();
            AppendDetails(text, s, inv);
        }

        if (_notice.Length > 0)
        {
            text.AppendLine(_notice);
        }

        text.Append("Esc - exit (report is saved to the Desktop), C - redo, T - 9 targets, S - snapshot, L - latency, P - pressure width, F11/F12 - overlay");
        PanelText.Text = text.ToString();
    }

    /// <summary>Подробная диагностика V0.5 (протокол <c>docs/spikes/V0-hardware.md</c>): монитор, WM_POINTER, давление, мишени.</summary>
    private void AppendDetails(StringBuilder text, PenStats s, CultureInfo inv)
    {
        text.AppendLine(inv, $"Device: {_source?.Device?.Product ?? "-"}  tilt reported: {_source?.Device?.Tilt}");
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
            text.AppendLine(inv, $"Last down: x {downX:0.0} y {downY:0.0}; nearest target #{number}: dx {dx:0.0} dy {dy:0.0} px ({dx * mmPerPx:0.00}, {dy * mmPerPx:0.00} mm){(_targets.ShowTargets ? string.Empty : "  [T - show targets]")}");
        }

        if (_options.UnknownArguments.Count > 0)
        {
            text.AppendLine("Unknown arguments: " + string.Join(' ', _options.UnknownArguments));
        }

        foreach (string error in _errors.TakeLast(3))
        {
            text.AppendLine("! " + error);
        }
    }
}
