using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PenForLang.Core;
using PenForLang.Glyphs.Loading;
using PenForLang.Ink;
using PenForLang.Platform.Windows;
using PenForLang.Tools.InkLab.Content;
using PenForLang.Tools.InkLab.ViewModels;
using PenForLang.Tools.InkLab.Views;

namespace PenForLang.Tools.InkLab;

/// <summary>
/// Окно InkLab: вкладки «Запись», «Повтор», «Разметка», «Сводка». Окно владеет источником пера (<see cref="Win32PointerPenSource"/>
/// или <see cref="MousePenSource"/> по <c>--mouse</c>), подавляет помехи Windows Ink и выбирает монитор пера; вся логика вкладок — в
/// ViewModel. Окно оконное (не киоск): тестовая кнопка и отчёт V0.5 в InkLab не нужны.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "The pen source is disposed in OnClosing; Window has its own lifetime.")]
public partial class MainWindow : Window, IPenHost
{
    private readonly LabOptions _options;
    private readonly QpcMonotonicClock _clock = new();
    private readonly MainViewModel _vm;
    private readonly LabContent? _content;
    private readonly string _contentProblem = string.Empty;

    private HwndSource? _hwndSource;
    private HwndPenSourceBase? _source;
    private IReadOnlyList<MonitorDescription> _monitors = [];
    private PenMonitorChoice _choice = new(null, PenMonitorChoiceSource.None, null, 0);

    /// <summary>Создаёт окно.</summary>
    public MainWindow(LabOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;

        string? root = LabContent.FindContentRoot(options.ContentDirectory, AppContext.BaseDirectory);
        if (root is null)
        {
            _contentProblem = "Контент не найден: нужна папка content/styles/zaner-bloser рядом с программой (или --content-dir). Экранного эталона не будет.";
        }
        else
        {
            try
            {
                _content = LabContent.Load(new DirectoryContentSource(root));
            }
            catch (Exception ex) when (ex is StyleLoadException or IOException or KeyNotFoundException or UnauthorizedAccessException)
            {
                _contentProblem = "Контент не загружен: " + ex.Message;
            }
        }

        _vm = new MainViewModel(options, _clock, new SystemClock(), _content?.SceneGuides);
        InitializeComponent();
        DataContext = _vm;
        ReplayView.Initialize(_vm.Replay);
        AnnotateView.Initialize(_vm.Annotate);
        SummaryView.Initialize(_vm.Summary);

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
        LocationChanged += (_, _) => _monitorCacheStale = true;
    }

    private bool _monitorCacheStale = true;
    private MonitorDescription? _windowMonitor;

    /// <inheritdoc />
    IPenInputSource IPenHost.Source => _source!;

    /// <inheritdoc />
    IMonotonicClock IPenHost.Clock => _clock;

    /// <inheritdoc />
    double IPenHost.DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <inheritdoc />
    MonitorDescription? IPenHost.Monitor => WindowMonitor();

    /// <inheritdoc />
    string IPenHost.OsVersion
    {
        get
        {
            Version os = Environment.OSVersion.Version;
            return $"{os.Major}.{os.Minor}.{os.Build}";
        }
    }

    /// <inheritdoc />
    string IPenHost.AppVersion => typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    /// <inheritdoc />
    void IPenHost.SetSurface(PenSurface surface) => _source?.SetSurface(surface);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwndSource = (HwndSource)PresentationSource.FromVisual(this)!;

        // Монитор пера (§7.4): принудительный номер из --monitor, иначе автоматически; в режиме мыши окно остаётся где было.
        _monitors = PenMonitorLocator.EnumerateMonitors();
        IReadOnlyList<PointerDeviceDescription> devices = PenMonitorLocator.EnumeratePointerDevices();
        if (_options.MonitorIndex is int forced)
        {
            _choice = PenMonitorSelector.SelectForced(_monitors, forced, devices);
        }
        else if (!_options.UseMouse)
        {
            _choice = PenMonitorSelector.Select(devices, _monitors);
        }

        if (_choice.Monitor is { } penMonitor && (_options.MonitorIndex is not null || !_options.UseMouse))
        {
            WindowPlacement.MoveTo(_hwndSource.Handle, penMonitor.BoundsPx.Left + 40, penMonitor.BoundsPx.Top + 40);
            WindowState = WindowState.Maximized;
        }

        // Помехи Windows Ink — только для этого окна (§7.2).
        WindowInkSuppressor.Apply(_hwndSource.Handle);
        WindowInkSuppressor.Attach(_hwndSource);

        HwndPenSourceBase source = _options.UseMouse ? new MousePenSource(_clock) : new Win32PointerPenSource(_clock);
        source.Attach(_hwndSource);
        _source = source;
        RecordView.Initialize(_vm.Record, _content, this, _options.XHeightMm, showTip: !_options.UseMouse);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_source is not null)
        {
            await _source.StartAsync(CancellationToken.None);
        }

        UpdateStatus();
        if (_options.ExitAfter is TimeSpan exitAfter)
        {
            var timer = new DispatcherTimer { Interval = exitAfter };
            timer.Tick += (_, _) => Close();
            timer.Start();
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Несохранённая разметка не должна пропасть: не удалось сохранить — спрашиваем, закрывать ли без неё.
        if (!_vm.Annotate.SaveIfDirty()
            && MessageBox.Show(
                _vm.Annotate.Status + Environment.NewLine + "Закрыть окно без сохранения разметки?",
                "InkLab",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        RecordView.Shutdown();
        _vm.Replay.Dispose();
        if (_source is not null)
        {
            _source.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _source = null;
        }
    }

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        // Первое Checked приходит из InitializeComponent, когда остальные элементы окна ещё не созданы.
        if (SummaryView is null || StatusText is null)
        {
            return;
        }

        if (sender is RadioButton { Tag: string tag } && int.TryParse(tag, NumberStyles.None, CultureInfo.InvariantCulture, out int index))
        {
            SelectTab(index);
        }
    }

    private void SelectTab(int index)
    {
        RecordView.Visibility = index == MainViewModel.Tabs.Record ? Visibility.Visible : Visibility.Collapsed;
        ReplayView.Visibility = index == MainViewModel.Tabs.Replay ? Visibility.Visible : Visibility.Collapsed;
        AnnotateView.Visibility = index == MainViewModel.Tabs.Annotate ? Visibility.Visible : Visibility.Collapsed;
        SummaryView.Visibility = index == MainViewModel.Tabs.Summary ? Visibility.Visible : Visibility.Collapsed;
        _vm.SelectedTab = index;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        string source = _source?.Kind.ToString() ?? "-";
        string monitor = _choice.Monitor is { } m ? $"монитор {m.DeviceName} {m.BoundsPx.Width}×{m.BoundsPx.Height}" : "монитор не выбран";
        string extra = _options.UnknownArguments.Count > 0 ? "  Неизвестные аргументы: " + string.Join(' ', _options.UnknownArguments) : string.Empty;
        string content = _contentProblem.Length > 0 ? "  " + _contentProblem : string.Empty;
        StatusText.Text = $"Источник пера: {source}; {monitor}; корпус: {_vm.CorpusRoot}{extra}{content}";
    }

    /// <summary>Монитор, на котором стоит окно (по центру окна); если не найден — монитор пера.</summary>
    private MonitorDescription? WindowMonitor()
    {
        if (!_monitorCacheStale && _windowMonitor is not null)
        {
            return _windowMonitor;
        }

        if (_hwndSource is not null && WindowPlacement.GetBounds(_hwndSource.Handle) is { } bounds)
        {
            int cx = bounds.Left + (bounds.Width / 2);
            int cy = bounds.Top + (bounds.Height / 2);
            _windowMonitor = _monitors.FirstOrDefault(
                m => cx >= m.BoundsPx.Left && cx < m.BoundsPx.Right && cy >= m.BoundsPx.Top && cy < m.BoundsPx.Bottom);
        }

        _windowMonitor ??= _choice.Monitor ?? _monitors.FirstOrDefault(m => m.IsPrimary);
        _monitorCacheStale = false;
        return _windowMonitor;
    }
}
