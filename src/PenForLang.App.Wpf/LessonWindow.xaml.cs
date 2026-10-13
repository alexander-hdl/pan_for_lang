using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PenForLang.App.Wpf.Lesson;
using PenForLang.Core.Geometry;
using PenForLang.Glyphs.Loading;
using PenForLang.Ink;
using PenForLang.Platform.Windows;
using PenForLang.Wpf.Controls;

namespace PenForLang.App.Wpf;

/// <summary>
/// Основной экран V0.8: фиксированный урок для буквы a, i или t — показ (E01), обводка ×3 (E03), копия ×2 (E04). Эталон показывает
/// <see cref="GlyphAnimatorLayer"/>, письмо идёт в <see cref="LiveInkLayer"/>, после каждой попытки <see cref="LessonViewModel"/> оценивает
/// её в фоне и показывает одну подсказку с подсветкой эталона (<see cref="TemplateHighlightLayer"/>). Мягкий сигнал по ходу письма — мигание
/// точки старта при неверном начале обводки, без звука. Окно-песочница V0.6 доступно как <c>--sandbox</c> (<see cref="MainWindow"/>).
/// Клавиши: Esc — выход, Enter — «Дальше», C — «Заново». Запись penlog попыток — только с <c>--dev</c> и только для взрослого.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "The recorder and the host are disposed in OnClosing; Window has its own lifetime.")]
public partial class LessonWindow : Window
{
    private const double HighlightPulsePeriodMs = 900;
    private const int TickMilliseconds = 50;

    private readonly AppOptions _options;
    private readonly QpcMonotonicClock _clock = new();
    private readonly GuidesLayer _guides = new();
    private readonly GuidesLayer _guidesModel = new() { Opacity = 0.6 };
    private readonly GlyphAnimatorLayer _demo;
    private readonly GlyphAnimatorLayer _underPen;
    private readonly TemplateHighlightLayer _highlight;
    private readonly LiveInkLayer _ink;
    private readonly TemplateHighlightLayer _cue;
    private readonly TemplateHighlightLayer _startDot;
    private readonly PenWindowHost _host;
    private readonly List<string> _errors = [];
    private readonly DispatcherTimer _exitTimer = new();
    private readonly DispatcherTimer _tickTimer = new() { Interval = TimeSpan.FromMilliseconds(TickMilliseconds) };

    private LessonContent? _content;
    private LessonViewModel? _vm;
    private AttemptRecorder? _recorder;
    private ResolvedHighlight? _shownHighlight;
    private string? _demoLetter;
    private string? _underPenLetter;
    private bool _demoPending = true;
    private bool _stimulusOpen;
    private bool _startDotShown;
    private int _attemptSerial;

    /// <summary>Создаёт окно.</summary>
    public LessonWindow(AppOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        InitializeComponent();

        _demo = new GlyphAnimatorLayer(_clock) { Visibility = Visibility.Collapsed };
        _underPen = new GlyphAnimatorLayer(_clock) { Visibility = Visibility.Collapsed, Opacity = 0.42 };
        _highlight = new TemplateHighlightLayer(_clock);
        _ink = new LiveInkLayer(_clock, null) { ShowTip = !options.UseMouse, PressureEnabled = false };
        _cue = new TemplateHighlightLayer(_clock);
        _startDot = new TemplateHighlightLayer(_clock);
        CanvasHost.Children.Add(_guidesModel);
        CanvasHost.Children.Add(_guides);
        CanvasHost.Children.Add(_underPen);
        CanvasHost.Children.Add(_demo);
        CanvasHost.Children.Add(_startDot);
        CanvasHost.Children.Add(_highlight);
        CanvasHost.Children.Add(_ink);
        CanvasHost.Children.Add(_cue);
        _host = new PenWindowHost(this, options, _clock, _ink);

        LoadContent();
        if (!options.UseMouse && !options.Windowed)
        {
            Cursor = Cursors.None; // §7.2: свой маркер кончика при hover
        }

        Topmost = !options.Windowed;
        _host.SamplesReceived += OnSamples;
        _host.SurfaceChanged += ApplyLayout;
        SourceInitialized += (_, _) =>
        {
            _host.PlacementEnabled = PlaceOnPenMonitor;
            _host.Initialize();
        };
        Loaded += OnLoaded;
        Closing += OnClosing;
        LocationChanged += (_, _) => _host.OnGeometryChanged();
        SizeChanged += (_, _) => _host.OnGeometryChanged();
        LayoutUpdated += (_, _) => _host.UpdateSurface();
        DpiChanged += (_, e) => _host.OnDpiChanged(e);
        PreviewKeyDown += OnPreviewKeyDown;
        _demo.PlaybackFinished += (_, _) =>
        {
            EndStimulus();
            _vm?.DemoPlaybackFinished();
        };
        _tickTimer.Tick += (_, _) => _vm?.Tick();
        RefreshUi();
    }

    // Шов для UI-тестов (InternalsVisibleTo): настоящее окно, но ввод подаётся без сообщений Windows (их разбор тестируется в Platform.Windows).
    internal LessonViewModel? ViewModel => _vm;

    internal LiveInkLayer InkLayer => _ink;

    internal TemplateHighlightLayer HighlightLayer => _highlight;

    internal TemplateHighlightLayer CueLayer => _cue;

    internal GlyphAnimatorLayer DemoLayer => _demo;

    internal GlyphAnimatorLayer UnderPenLayer => _underPen;

    internal TemplateHighlightLayer StartDotLayer => _startDot;

    /// <summary>Ставить окно на монитор пера; UI-тесты выключают до <c>Show</c>.</summary>
    internal bool PlaceOnPenMonitor { get; set; } = true;

    internal void FeedSamples(ReadOnlyMemory<PenSample> samples) => OnSamples(samples);

    private void LoadContent()
    {
        string? root = SandboxContent.FindContentRoot(_options.ContentDirectory, AppContext.BaseDirectory);
        if (root is null)
        {
            _errors.Add("Content not found: content/styles/zaner-bloser next to the program (or --content-dir) is required.");
            return;
        }

        try
        {
            _content = LessonContent.Load(new DirectoryContentSource(root));
        }
        catch (Exception ex) when (ex is StyleLoadException or IOException or InvalidDataException or KeyNotFoundException or UnauthorizedAccessException)
        {
            _errors.Add("Content not loaded: " + ex.Message);
            return;
        }

        _vm = new LessonViewModel(_content, LessonRuntime.Live(_clock, _host.Device), _options.Language, _options.Letter);
        _vm.PropertyChanged += (_, _) => RefreshUi();
        _vm.ReplayRequested += OnReplayRequested;
        _vm.InkClearRequested += ClearInk;
        _vm.LiveCueRaised += OnLiveCue;
        _vm.AttemptStarted += OnAttemptStarted;
        _vm.AttemptRestarted += () => _recorder?.Restart();
        _vm.AttemptFinished += OnAttemptFinished;
        _vm.StimulusChanged += OnStimulusChanged;
        _vm.AidShown += reason => _recorder?.RecordEvent("aid-shown", new Dictionary<string, string> { ["by"] = reason, ["mode"] = "copy-aided" });

        if (_options.Dev)
        {
            string directory = _options.PenLogDirectory
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PenForLang", "dev", "penlogs");
            _recorder = new AttemptRecorder(directory, _clock); // попытка открывается событием AttemptStarted (на показе письма нет)
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _host.StartAsync();
        _tickTimer.Start();
        if (_options.ExitAfter is TimeSpan exitAfter)
        {
            _exitTimer.Interval = exitAfter;
            _exitTimer.Tick += (_, _) => Close();
            _exitTimer.Start();
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _exitTimer.Stop();
        _tickTimer.Stop();
        _ink.EndAttempt();
        _recorder?.Complete("cancelled");
        _recorder?.Dispose();
        _host.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private void OnSamples(ReadOnlyMemory<PenSample> samples)
    {
        if (_vm is not { } vm)
        {
            return;
        }

        ReadOnlySpan<PenSample> span = samples.Span;

        // Касание пером строки в фазе обратной связи ведёт дальше, и первый штрих уже идёт в новой попытке.
        vm.BeginTouch(span);
        bool accepts = vm.AcceptsInk;

        // Живые чернила рисуются первыми (задержка); вне попытки допускается только наведение — маркер кончика пера.
        if (accepts || !ContainsContact(span))
        {
            _ink.AddSamples(span);
        }

        if (accepts)
        {
            foreach (PenSample sample in span)
            {
                if (sample.Phase == PenPhase.Down)
                {
                    RestartButton.IsEnabled = false;
                }
                else if (sample.Phase == PenPhase.Up)
                {
                    RestartButton.IsEnabled = vm.CanRestart;
                }
            }

            StopDemoOnFirstTouch(span);
            vm.OnSamples(span);
            _recorder?.AddSamples(span);
        }
    }

    private static bool ContainsContact(ReadOnlySpan<PenSample> span)
    {
        foreach (PenSample sample in span)
        {
            if (sample.IsContact)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Первое касание во время показа: анимация доигрывается до конца и замирает (она не должна «рисовать» под рукой ребёнка).</summary>
    private void StopDemoOnFirstTouch(ReadOnlySpan<PenSample> span)
    {
        if (!_demo.IsPlaying || _demo.Script is not { } script)
        {
            return;
        }

        foreach (PenSample sample in span)
        {
            if (sample.Phase == PenPhase.Down)
            {
                _demo.Pause();
                _demo.Seek(script.DurationMs);
                EndStimulus();
                return;
            }
        }
    }

    private void ApplyLayout(PenSurface surface)
    {
        double pxPerMm = _host.PxPerMm;
        double dpi = _host.DpiScale;
        _ink.PxPerMm = pxPerMm;
        ApplyTargetSizes(pxPerMm, dpi);
        if (_content is null || _vm is null)
        {
            return;
        }

        var mapper = new SurfaceMapper(surface, pxPerMm, dpi);
        double xHeightMm = LessonLayout.FitXHeightMm(_options.XHeightMm, surface.HeightPx / pxPerMm, _content.Style);
        (GuideFrame demo, GuideFrame writing) = LessonLayout.Frames(mapper, _content.Style, xHeightMm);
        _guidesModel.Manifest = _content.Style;
        _guidesModel.DpiScale = dpi;
        _guidesModel.Guides = demo;
        _guides.Manifest = _content.Style;
        _guides.DpiScale = dpi;
        _guides.Guides = writing;
        _startDot.DpiScale = dpi;
        _startDotShown = false;
        _demo.DpiScale = dpi;
        _demo.Guides = demo;
        _underPen.DpiScale = dpi;
        _underPen.Guides = writing;
        _highlight.DpiScale = dpi;
        _cue.DpiScale = dpi;
        _cue.Guides = writing;
        _vm.SetFrames(demo, writing);
        _shownHighlight = null;
        _recorder?.Arm();
        RefreshUi();
    }

    /// <summary>Цели для пера не меньше 12 мм, отступы не меньше 4 мм (A34); шрифты — в мм, чтобы полоса сообщений не зависела от масштаба Windows.</summary>
    private void ApplyTargetSizes(double pxPerMm, double dpi)
    {
        double target = SandboxLayout.MmToDip(SandboxLayout.TargetMinMm, pxPerMm, dpi);
        double gap = SandboxLayout.MmToDip(SandboxLayout.TargetGapMm, pxPerMm, dpi);
        double buttonFont = SandboxLayout.MmToDip(3.6, pxPerMm, dpi);
        foreach (Control control in new Control[] { ShowButton, RestartButton, DoneButton, NextButton, RestartLessonButton, LanguageButton, LetterA, LetterI, LetterT })
        {
            control.MinHeight = target;
            control.MinWidth = target;
            control.Margin = new Thickness(0, 0, gap, gap);
            control.FontSize = buttonFont;
        }

        StripHost.Padding = new Thickness(gap, gap, gap, 0);
        ProgressText.FontSize = SandboxLayout.MmToDip(3.2, pxPerMm, dpi);
        RecordingBanner.FontSize = SandboxLayout.MmToDip(2.4, pxPerMm, dpi);
        InstructionText.FontSize = SandboxLayout.MmToDip(3.0, pxPerMm, dpi);
        MessageText.FontSize = SandboxLayout.MmToDip(4.4, pxPerMm, dpi);
        BannerHost.Padding = new Thickness(gap * 1.5, gap / 2, gap * 1.5, gap / 2);
        BannerHost.Height = SandboxLayout.MmToDip(LessonLayout.BannerHeightMm, pxPerMm, dpi);
    }

    private void RefreshUi()
    {
        if (_vm is not { } vm)
        {
            InstructionText.Text = string.Join(' ', DiagnosticLines());
            MessageText.Text = string.Empty;
            foreach (Control control in new Control[] { ShowButton, RestartButton, DoneButton, NextButton, RestartLessonButton, LetterA, LetterI, LetterT })
            {
                control.Visibility = Visibility.Collapsed;
            }

            return;
        }

        LanguageButton.Content = vm.T("ui.lang.switch");
        ShowButton.Content = vm.T("ui.showAgain");
        RestartButton.Content = vm.T("ui.restart");
        DoneButton.Content = vm.T("ui.done");
        NextButton.Content = vm.T("ui.next");
        RestartLessonButton.Content = vm.T("ui.restartLesson");
        ShowButton.Visibility = vm.CanShowAgain ? Visibility.Visible : Visibility.Collapsed;
        RestartButton.Visibility = vm.CanRestart ? Visibility.Visible : Visibility.Collapsed;
        RestartButton.IsEnabled = vm.CanRestart && !_ink.IsPenDown;
        DoneButton.Visibility = vm.CanDone ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = vm.CanNext ? Visibility.Visible : Visibility.Collapsed;
        RestartLessonButton.Visibility = vm.Phase == LessonPhase.Finished ? Visibility.Visible : Visibility.Collapsed;
        foreach (ToggleButton letter in new[] { LetterA, LetterI, LetterT })
        {
            letter.IsChecked = string.Equals((string)letter.Tag, vm.Letter, StringComparison.Ordinal);
            letter.IsEnabled = vm.CanSelectLetter;
        }

        ProgressText.Text = vm.TotalAttempts > 0
            ? vm.CompletedAttempts.ToString(CultureInfo.InvariantCulture) + " / " + vm.TotalAttempts.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        List<string> diagnostics = DiagnosticLines();
        InstructionText.Text = diagnostics.Count == 0 ? vm.InstructionText : vm.InstructionText + "  [" + string.Join("; ", diagnostics) + "]";
        string praise = vm.PraiseText;
        MessageText.Text = praise.Length > 0 ? praise : vm.HintText;
        MessageText.Foreground = praise.Length > 0 ? PraiseBrush : HintBrush;
        RecordingBanner.Text = vm.T("ui.recording");
        RecordingBanner.Visibility = _recorder is null ? Visibility.Collapsed : Visibility.Visible;
        RefreshLayers(vm);
    }

    /// <summary>Ошибки загрузки контента и хоста пера, неизвестные аргументы: разработчик видит их в строке инструкции, а не в пустоте.</summary>
    private List<string> DiagnosticLines()
    {
        List<string> lines = [.. _errors, .. _host.Errors];
        if (_options.UnknownArguments.Count > 0)
        {
            lines.Add("Unknown arguments: " + string.Join(' ', _options.UnknownArguments));
        }

        return lines;
    }

    private static readonly SolidColorBrush PraiseBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x7A, 0x4C)));
    private static readonly SolidColorBrush HintBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3B, 0x3B, 0x3B)));

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    private void RefreshLayers(LessonViewModel vm)
    {
        if (_content is null)
        {
            return;
        }

        // Эталон бледно под пером: обводка и добавленная опора.
        if (vm.ModelUnderPen)
        {
            if (_underPenLetter != vm.Letter || _underPen.Visibility != Visibility.Visible)
            {
                _underPen.Script = _content.GetScript(vm.Letter);
                _underPen.Seek(_underPen.Script.DurationMs);
                _underPenLetter = vm.Letter;
            }

            _underPen.Visibility = Visibility.Visible;
        }
        else
        {
            _underPen.Visibility = Visibility.Collapsed;
        }

        // Точка старта на строке письма: письмо по памяти на L2 (на строке видна только она).
        if (vm.StartDotOnWritingRow && vm.WritingFrame is { } writingFrame)
        {
            if (!_startDotShown)
            {
                _startDot.Guides = writingFrame;
                _startDot.ShowDot(TemplateGeometry.StartPoint(_content.GetGlyph(vm.Letter)));
                _startDotShown = true;
            }
        }
        else if (_startDotShown)
        {
            _startDot.Clear();
            _startDotShown = false;
        }

        _demo.ShowArrows = vm.ArrowsVisible;

        // Анимация на линейке эталона (над строкой): показ, копия, вспышка по памяти и повторы по просьбе.
        if (vm.DemoVisible)
        {
            if (_demoLetter != vm.Letter || _demo.Visibility != Visibility.Visible)
            {
                _demo.Script = _content.GetScript(vm.Letter);
                _demoLetter = vm.Letter;
                _demo.Visibility = Visibility.Visible;
                if (vm.Phase == LessonPhase.Demo)
                {
                    PlayDemo();
                }
                else
                {
                    _demo.Seek(_demo.Script.DurationMs);
                }
            }
            else if (vm.Phase == LessonPhase.Demo && _demoPending)
            {
                PlayDemo();
            }
        }
        else if (_demo.Visibility == Visibility.Visible)
        {
            _demo.Stop();
            _demo.Visibility = Visibility.Collapsed;
            _demoLetter = null;
        }

        // Одна подсказка с подсветкой участка эталона.
        if (vm is { Phase: LessonPhase.Feedback, Highlight: { } highlight })
        {
            if (!ReferenceEquals(highlight, _shownHighlight) && vm.WritingFrame is not null && vm.DemoFrame is not null)
            {
                _highlight.Guides = vm.HighlightOnWritingFrame ? vm.WritingFrame : vm.DemoFrame;
                _highlight.Show(
                    highlight.Polylines,
                    highlight.StartPoint,
                    highlight.Tone == FeedbackTone.Positive ? HighlightColor.Positive : HighlightColor.Warm,
                    HighlightPulsePeriodMs,
                    HighlightPulsePeriodMs * _content.Config.Feedback.HighlightPulses,
                    keepStatic: true);
                _shownHighlight = highlight;
            }
        }
        else if (_shownHighlight is not null)
        {
            _highlight.Clear();
            _shownHighlight = null;
        }
    }

    private void PlayDemo()
    {
        if (_demo.Guides is null || _demo.Script is null)
        {
            _demoPending = true; // линейка ещё не известна: проиграем, когда будет раскладка
            return;
        }

        _demoPending = false;
        _demo.Visibility = Visibility.Visible;
        _demo.Replay();
    }

    private void OnReplayRequested(ReplayReason reason)
    {
        _ = reason;
        if (_content is null || _vm is null)
        {
            return;
        }

        if (_demoLetter != _vm.Letter || _demo.Script is null)
        {
            _demo.Script = _content.GetScript(_vm.Letter);
            _demoLetter = _vm.Letter;
        }

        if (_vm.Phase == LessonPhase.Writing && _recorder is not null)
        {
            EndStimulus();
            _stimulusOpen = true;
            _recorder.RecordEvent(InkEvent.Names.StimulusStart, new Dictionary<string, string> { ["key"] = "glyph/" + _vm.Letter });
        }

        PlayDemo();
    }

    private void OnStimulusChanged(bool shown)
    {
        if (_recorder is null || _vm is null)
        {
            return;
        }

        if (shown)
        {
            EndStimulus();
            _stimulusOpen = true;
            _recorder.RecordEvent(InkEvent.Names.StimulusStart, new Dictionary<string, string> { ["key"] = "glyph/" + _vm.Letter });
        }
        else
        {
            EndStimulus();
        }
    }

    private void EndStimulus()
    {
        if (_stimulusOpen)
        {
            _stimulusOpen = false;
            _recorder?.RecordEvent(InkEvent.Names.StimulusEnd);
        }
    }

    private void OnLiveCue(Vec2 point)
    {
        if (_content is null || _vm?.WritingFrame is not { } frame)
        {
            return;
        }

        LiveCueSettings cue = _content.Config.LiveCue;
        _cue.Guides = frame;
        _cue.Show([], point, HighlightColor.Warm, cue.PulsePeriodMs, cue.DurationMs);
    }

    private void ClearInk()
    {
        _ink.EndAttempt();
        _ink.ClearBeforeNextStroke = false;
        _ink.Clear();
        _cue.Clear();
    }

    private void OnAttemptStarted(LessonStep step, int attemptNo)
    {
        _ = attemptNo;
        _cue.Clear();
        if (_recorder is null || _vm is null)
        {
            return;
        }

        EndStimulus();
        _attemptSerial++;
        string letter = _vm.Letter;
        int serial = _attemptSerial;
        string stem = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
            + "-" + letter + "-" + step.Code + "-" + serial.ToString("000", CultureInfo.InvariantCulture);
        _recorder.Begin(() => BuildHeader(letter, serial, step), stem, "glyph:" + letter);
    }

    private void OnAttemptFinished()
    {
        _ink.EndAttempt();
        EndStimulus();
        _recorder?.Complete("done-button");
    }

    private PenLogHeader? BuildHeader(string letter, int serial, LessonStep step)
    {
        if (_vm?.WritingFrame is not { } frame)
        {
            return null;
        }

        Version os = Environment.OSVersion.Version;
        return LessonLayout.Header(
            typeof(LessonWindow).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            $"{os.Major}.{os.Minor}.{os.Build}",
            _host.Device,
            _host.Monitor,
            _host.DpiScale,
            _host.PxPerMm,
            _host.PxPerMmSource,
            frame,
            letter,
            serial,
            step,
            _clock.Frequency);
    }

    private void OnShowClick(object sender, RoutedEventArgs e) => _vm?.ShowAgain();

    private void OnRestartClick(object sender, RoutedEventArgs e) => _vm?.Restart();

    private async void OnDoneClick(object sender, RoutedEventArgs e)
    {
        if (_vm is not null)
        {
            await _vm.DoneAsync();
        }
    }

    private void OnNextClick(object sender, RoutedEventArgs e) => _vm?.Next();

    private void OnRestartLessonClick(object sender, RoutedEventArgs e) => _vm?.RestartLesson();

    private void OnLanguageClick(object sender, RoutedEventArgs e) => _vm?.ToggleLanguage();

    private void OnLetterClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string letter })
        {
            _vm?.SelectLetter(letter);
        }

        RefreshUi();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                Close();
                break;
            case Key.Enter:
                e.Handled = true;
                _vm?.Next();
                break;
            case Key.C:
                e.Handled = true;
                _vm?.Restart();
                break;
            default:
                break;
        }
    }
}
