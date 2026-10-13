using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PenForLang.Core;
using PenForLang.Glyphs;
using PenForLang.Ink;
using PenForLang.Platform.Windows;
using PenForLang.Tools.InkLab.Content;
using PenForLang.Tools.InkLab.Protocol;
using PenForLang.Tools.InkLab.Recording;
using PenForLang.Tools.InkLab.ViewModels;
using PenForLang.Wpf.Controls;

namespace PenForLang.Tools.InkLab.Views;

/// <summary>
/// Вкладка «Запись»: линейки письма, экранный эталон (аниматор глифов) слева и живые чернила справа. Всю логику ведёт
/// <see cref="RecordTabViewModel"/>; здесь — только слои, раскладка и связь с источником пера окна.
/// </summary>
public partial class RecordTab : UserControl
{
    private readonly GuidesLayer _guides = new();
    private readonly List<GlyphAnimatorLayer> _animators = [];

    private RecordTabViewModel? _vm;
    private IPenHost? _host;
    private LabContent? _content;
    private LiveInkLayer? _ink;
    private double _xHeightMm = RecordingLayout.DefaultXHeightMm;
    private ReferencePlan? _plan;
    private PenSurface? _surface;
    private double _appliedDpi;
    private GuideFrame? _demoFrame;

    /// <summary>Создаёт вкладку.</summary>
    public RecordTab()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateSurface();
        SizeChanged += (_, _) => UpdateSurface();
        LayoutUpdated += (_, _) => UpdateSurface();
        IsVisibleChanged += (_, _) => UpdateSurface();
    }

    /// <summary>Связывает вкладку с ViewModel, контентом и окном-хозяином пера.</summary>
    public void Initialize(RecordTabViewModel vm, LabContent? content, IPenHost host, double xHeightMm, bool showTip)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(host);
        _vm = vm;
        _content = content;
        _host = host;
        _xHeightMm = xHeightMm;
        DataContext = vm;

        if (content is not null)
        {
            _guides.Manifest = content.Style;
        }

        _ink = new LiveInkLayer(host.Clock, null) { ShowTip = showTip };
        CanvasHost.Children.Add(_guides);
        CanvasHost.Children.Add(_ink);

        // Живые чернила подписываются первыми: точки рисуются раньше, чем запись и разбор.
        _ink.Attach(host.Source);
        vm.Attach(host.Source);
        host.Source.SamplesReceived += OnSamples;
        vm.ClearInkRequested += ClearInk;
        vm.ReferenceRequested += ShowReference;
    }

    /// <summary>Отключается от источника (закрытие окна).</summary>
    public void Shutdown()
    {
        if (_vm is not null && _host is not null)
        {
            _host.Source.SamplesReceived -= OnSamples;
            _vm.Detach();
            _vm.ClearInkRequested -= ClearInk;
            _vm.ReferenceRequested -= ShowReference;
        }

        _ink?.EndAttempt();
        _ink?.Detach();
    }

    private void OnPromptDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm is not null && PromptList.SelectedItem is PromptItem item)
        {
            _vm.GoTo(item.Index);
        }
    }

    private void ClearInk()
    {
        if (_ink is null)
        {
            return;
        }

        _ink.EndAttempt();
        _ink.ClearBeforeNextStroke = false;
        _ink.Clear();
        if (_vm?.CurrentPrompt is null)
        {
            StopAnimators(finish: false);
        }
    }

    /// <summary>Показ эталона: буквы анимируются по очереди; начало и конец показа уходят в penlog событиями.</summary>
    private void ShowReference(RecordingPrompt prompt)
    {
        if (_content is null || _vm is null)
        {
            return;
        }

        try
        {
            _plan = _content.PlanFor(prompt);
        }
        catch (KeyNotFoundException)
        {
            _plan = null;
            return;
        }

        PrepareAnimators();
        UpdateSurface(force: true);
        if (_animators.Count == 0 || _demoFrame is null)
        {
            return;
        }

        foreach (GlyphAnimatorLayer layer in _animators)
        {
            layer.Stop();
        }

        _vm.ReferenceStarted();
        PlayFrom(0);
    }

    private void PlayFrom(int index)
    {
        if (index >= _animators.Count)
        {
            _vm?.ReferenceEnded();
            return;
        }

        for (int i = 0; i < _animators.Count; i++)
        {
            _animators[i].Visibility = i <= index ? Visibility.Visible : Visibility.Collapsed;
        }

        GlyphAnimatorLayer layer = _animators[index];
        layer.Replay();
    }

    private void PrepareAnimators()
    {
        int needed = _plan?.Steps.Length ?? 0;
        while (_animators.Count < needed)
        {
            var layer = new GlyphAnimatorLayer(_host!.Clock) { Visibility = Visibility.Collapsed };
            layer.PlaybackFinished += OnAnimatorFinished;
            _animators.Add(layer);
            CanvasHost.Children.Insert(CanvasHost.Children.IndexOf(_ink!), layer);
        }

        for (int i = 0; i < _animators.Count; i++)
        {
            if (i < needed)
            {
                _animators[i].Script = _plan!.Steps[i].Script;
            }
            else
            {
                _animators[i].Stop();
                _animators[i].Script = null;
                _animators[i].Visibility = Visibility.Collapsed;
            }
        }
    }

    private void OnAnimatorFinished(object? sender, EventArgs e)
    {
        if (sender is not GlyphAnimatorLayer layer)
        {
            return;
        }

        int index = _animators.IndexOf(layer);
        if (index >= 0 && index < (_plan?.Steps.Length ?? 0))
        {
            PlayFrom(index + 1);
        }
    }

    /// <summary>Первое касание во время показа: эталон замирает готовым (он не должен «рисовать» под рукой).</summary>
    private void OnSamples(ReadOnlyMemory<PenSample> samples)
    {
        if (!_animators.Any(a => a.IsPlaying))
        {
            return;
        }

        foreach (PenSample sample in samples.Span)
        {
            if (sample.Phase == PenPhase.Down)
            {
                StopAnimators(finish: true);
                _vm?.ReferenceEnded();
                return;
            }
        }
    }

    private void StopAnimators(bool finish)
    {
        int count = _plan?.Steps.Length ?? 0;
        for (int i = 0; i < _animators.Count; i++)
        {
            GlyphAnimatorLayer layer = _animators[i];
            if (finish && i < count && layer.Script is { } script)
            {
                layer.Pause();
                layer.Seek(script.DurationMs);
                layer.Visibility = Visibility.Visible;
            }
            else
            {
                layer.Stop();
                layer.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>Прямоугольник холста → источнику пера; линейки и эталон → в слои; окружение записи → ViewModel.</summary>
    private void UpdateSurface(bool force = false)
    {
        if (_host is null || _vm is null || _ink is null)
        {
            return;
        }

        if (!IsVisible || !_ink.IsLoaded || _ink.ActualWidth <= 0 || _ink.ActualHeight <= 0)
        {
            if (_surface != PenSurfaces.Hidden)
            {
                _surface = PenSurfaces.Hidden;
                _host.SetSurface(PenSurfaces.Hidden);
            }

            return;
        }

        Point topLeft = _ink.PointToScreen(new Point(0, 0));
        Point bottomRight = _ink.PointToScreen(new Point(_ink.ActualWidth, _ink.ActualHeight));
        var surface = PenSurface.FromScreenCorners(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
        double dpi = _host.DpiScale;
        if (!force && _surface == surface && _appliedDpi == dpi)
        {
            return;
        }

        _surface = surface;
        _appliedDpi = dpi;
        _host.SetSurface(surface);
        (double pxPerMm, PxPerMmSource source) = RecordingLayout.ResolvePxPerMm(_host.Monitor);
        _ink.PxPerMm = pxPerMm;
        _ink.DpiScale = dpi;

        if (_content is null)
        {
            return;
        }

        var mapper = new SurfaceMapper(surface, pxPerMm, dpi);
        (GuideFrame demo, GuideFrame writing) = RecordingLayout.Frames(mapper, _content.Style, _xHeightMm, _plan?.WidthXHeights ?? 0);
        _demoFrame = demo;
        _guides.DpiScale = dpi;
        _guides.Guides = writing;

        for (int i = 0; i < _animators.Count; i++)
        {
            GlyphAnimatorLayer layer = _animators[i];
            layer.DpiScale = dpi;
            double offset = _plan is { } plan && i < plan.Steps.Length ? plan.Steps[i].OffsetU : 0;
            layer.Guides = new GuideFrame(
                demo.OriginXPx + (offset * demo.XHeightPx),
                demo.BaselineYPx,
                demo.XHeightPx,
                demo.PxPerMm,
                demo.LineSpacing,
                demo.SlantDeg,
                demo.Style);
        }

        PenDeviceInfo device = _host.Source.Device
            ?? new PenDeviceInfo("unknown", _host.Source.Kind, null, null, null, false, PenTimestampKind.Qpc);
        PenScreenInfo screen = RecordingLayout.Screen(_host.Monitor, dpi, pxPerMm, source);
        _vm.Context = new RecordingContext(device, screen, writing, _host.OsVersion, _host.AppVersion, _host.Clock.Frequency);
    }
}
