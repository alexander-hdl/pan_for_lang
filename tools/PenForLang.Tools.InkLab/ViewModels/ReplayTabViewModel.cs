using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using PenForLang.Core;
using PenForLang.Ink;
using PenForLang.Tools.InkLab.Corpus;
using PenForLang.Tools.InkLab.Mvvm;
using PenForLang.Tools.InkLab.Scene;

namespace PenForLang.Tools.InkLab.ViewModels;

/// <summary>
/// Часы с ускорением поверх <see cref="IClock"/>: виртуальное время идёт в <see cref="Speed"/> раз быстрее реального,
/// при <c>Speed = 0</c> стоит (пауза). Меняется скорость — время не прыгает.
/// </summary>
public sealed class ScaledClock : IClock
{
    private readonly IClock _inner;
    private DateTimeOffset _virtualAnchor;
    private DateTimeOffset _realAnchor;
    private double _speed = 1.0;

    /// <summary>Создаёт часы со скоростью 1.</summary>
    public ScaledClock(IClock inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _realAnchor = inner.UtcNow;
        _virtualAnchor = _realAnchor;
    }

    /// <summary>Скорость: 1 — реальное время, 0 — пауза.</summary>
    public double Speed
    {
        get => _speed;
        set
        {
            if (!double.IsFinite(value) || value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Speed must be a finite non-negative number.");
            }

            _virtualAnchor = UtcNow;
            _realAnchor = _inner.UtcNow;
            _speed = value;
        }
    }

    /// <inheritdoc />
    public DateTimeOffset UtcNow => _virtualAnchor + ((_inner.UtcNow - _realAnchor) * _speed);
}

/// <summary>Сведения о записи для панели «Метаданные» (чистая функция, без окна).</summary>
public static class ReplayMetadata
{
    /// <summary>Строки метаданных записи: субъект, задание, устройство, экран, линейка, число штрихов и точек, «Заново», завершение.</summary>
    public static IReadOnlyList<string> Describe(string fileName, PenLog log, InkAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(attempt);
        CultureInfo inv = CultureInfo.InvariantCulture;
        PenLogHeader h = log.Header;
        var lines = new List<string> { "Файл: " + fileName };

        if (h.Subject is { } subject)
        {
            string age = subject.AgeYears is { } a ? $", {a} лет" : string.Empty;
            lines.Add(string.Create(inv, $"Субъект: {subject.Anon} ({(subject.Kind == PenSubjectKind.Adult ? "взрослый" : "ребёнок")}{age})"));
        }

        if (h.Consent is { } consent)
        {
            lines.Add(string.Create(inv, $"Согласие: №{consent.Id}, {consent.Kind}, версия {consent.Version}"));
        }

        if (h.Task is { } task)
        {
            lines.Add($"Задание: {task.Target} ({task.TaskId}), упражнение {task.Exercise}, режим {task.Mode}, рука: {(task.Handedness == "left" ? "левая" : "правая")}");
        }

        PenDeviceInfo d = h.Device;
        string rate = d.RateHzMeasured is { } measured ? $", {measured:0} Гц" : (d.RateHzNominal is { } nominal ? $", {nominal:0} Гц (паспорт)" : string.Empty);
        lines.Add(string.Create(inv, $"Устройство: {d.Product}, источник {d.Source}{rate}"));
        lines.Add(string.Create(inv, $"Экран: {h.Screen.WidthPx}×{h.Screen.HeightPx} px, {h.Screen.PxPerMm:0.00} px/мм ({h.Screen.PxPerMmSource}), DPI {h.Screen.DpiScale:0.00}"));
        lines.Add(string.Create(inv, $"Линейка: x-height {h.Guides.XHeightMm:0.0} мм, стиль {h.Guides.Style.Value}"));

        int samples = attempt.Strokes.Sum(s => s.Samples.Length);
        long durationUs = log.Entries.Length == 0 ? 0 : log.Entries[^1].TimeUs;
        lines.Add(string.Create(inv, $"Штрихов: {attempt.Strokes.Length}, точек: {samples}, длительность: {durationUs / 1_000_000.0:0.0} с"));
        lines.Add(string.Create(inv, $"«Заново»: {attempt.Restarts}; завершение: {attempt.Completion}"));
        return lines;
    }
}

/// <summary>
/// Вкладка «Повтор» без окна: список записей папки, воспроизведение попытки через <see cref="ReplayPenSource"/> в реальном
/// времени или ускоренно, показ штрихов поверх линеек и метаданных. Время идёт от <see cref="IClock"/> (в тестах — <c>FakeClock</c>),
/// такт задаёт представление вызовом <see cref="Tick"/>.
/// </summary>
public sealed class ReplayTabViewModel : ViewModelBase, IDisposable
{
    /// <summary>Допустимые скорости воспроизведения.</summary>
    public static IReadOnlyList<double> Speeds { get; } = [0.5, 1, 2, 4, 8];

    private readonly ScaledClock _clock;
    private readonly SceneGuides _guides;
    private readonly RelayCommand _playCommand;
    private readonly RelayCommand _pauseCommand;
    private readonly RelayCommand _restartCommand;
    private readonly RelayCommand _showAllCommand;

    private PenLog? _log;
    private InkAttempt? _attempt;
    private ReplayPenSource? _source;
    private StrokeAssembler _played = new();
    private long _totalUs;
    private long _lastUs;
    private bool _isPlaying;
    private double _speed = 1.0;
    private string _folder = string.Empty;
    private string _status = "Откройте папку с записями или файл .pfl.jsonl.gz.";
    private CorpusEntry? _selected;
    private InkScene _scene;
    private IReadOnlyList<string> _metadata = [];

    /// <summary>Создаёт вкладку.</summary>
    /// <param name="clock">Часы воспроизведения (реальные в приложении, <c>FakeClock</c> в тестах).</param>
    /// <param name="guides">Линии линейки для рисования; <c>null</c> — по умолчанию.</param>
    public ReplayTabViewModel(IClock clock, SceneGuides? guides = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = new ScaledClock(clock);
        _guides = guides ?? SceneGuides.Default;
        _scene = InkScene.Empty with { Guides = _guides };
        _playCommand = new RelayCommand(Play, () => _log is not null && !_isPlaying);
        _pauseCommand = new RelayCommand(Pause, () => _isPlaying);
        _restartCommand = new RelayCommand(Restart, () => _log is not null);
        _showAllCommand = new RelayCommand(ShowAll, () => _log is not null);
    }

    /// <summary>Записи открытой папки.</summary>
    public ObservableCollection<CorpusEntry> Files { get; } = [];

    /// <summary>Команда «Играть / продолжить».</summary>
    public RelayCommand PlayCommand => _playCommand;

    /// <summary>Команда «Пауза».</summary>
    public RelayCommand PauseCommand => _pauseCommand;

    /// <summary>Команда «С начала».</summary>
    public RelayCommand RestartCommand => _restartCommand;

    /// <summary>Команда «Показать всё сразу».</summary>
    public RelayCommand ShowAllCommand => _showAllCommand;

    /// <summary>Открытая папка.</summary>
    public string Folder
    {
        get => _folder;
        private set => Set(ref _folder, value);
    }

    /// <summary>Сообщение.</summary>
    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>Выбранная в списке запись (загружается при выборе).</summary>
    public CorpusEntry? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value) && value is not null)
            {
                Load(value.Path);
            }
        }
    }

    /// <summary>Загруженный лог или <c>null</c>.</summary>
    public PenLog? Log => _log;

    /// <summary>Попытка загруженной записи (штрихи после последнего «Заново»).</summary>
    public InkAttempt? Attempt => _attempt;

    /// <summary>Идёт воспроизведение.</summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (Set(ref _isPlaying, value))
            {
                _playCommand.RaiseCanExecuteChanged();
                _pauseCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Скорость воспроизведения (1 — реальное время).</summary>
    public double Speed
    {
        get => _speed;
        set
        {
            if (Set(ref _speed, value) && _isPlaying)
            {
                _clock.Speed = value;
            }
        }
    }

    /// <summary>Доля проигранного, 0…1.</summary>
    public double Progress => _totalUs <= 0 ? 0 : Math.Clamp((double)_lastUs / _totalUs, 0, 1);

    /// <summary>Положение текстом: «1.2 / 3.4 с».</summary>
    public string PositionText => string.Create(CultureInfo.InvariantCulture, $"{_lastUs / 1_000_000.0:0.0} / {_totalUs / 1_000_000.0:0.0} с");

    /// <summary>Сцена для рисования: бледная тень всей попытки и проигранная часть.</summary>
    public InkScene Scene
    {
        get => _scene;
        private set => Set(ref _scene, value);
    }

    /// <summary>Метаданные загруженной записи.</summary>
    public IReadOnlyList<string> Metadata
    {
        get => _metadata;
        private set
        {
            if (Set(ref _metadata, value))
            {
                Raise(nameof(MetadataText));
            }
        }
    }

    /// <summary>Метаданные одним текстом.</summary>
    public string MetadataText => string.Join(Environment.NewLine, _metadata);

    /// <summary>Открывает папку и показывает её записи.</summary>
    public void OpenFolder(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        Files.Clear();
        Folder = folder;
        foreach (CorpusEntry entry in CorpusIndex.Scan(folder))
        {
            Files.Add(entry);
        }

        Status = Files.Count == 0 ? "В папке нет записей (*.pfl.jsonl.gz)." : $"Записей: {Files.Count}. Выберите запись.";
    }

    /// <summary>Загружает запись и показывает её целиком (тенью); воспроизведение — по «Играть».</summary>
    /// <returns><c>false</c>, если запись не читается (причина — в <see cref="Status"/>).</returns>
    public bool Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        StopSource();
        try
        {
            _log = CorpusStore.ReadPenLog(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PenLogFormatException or InvalidDataException)
        {
            _log = null;
            _attempt = null;
            Metadata = [];
            Scene = InkScene.Empty with { Guides = _guides };
            Status = "Запись не читается: " + ex.Message;
            RaiseCommands();
            return false;
        }

        _attempt = _log.ToInkAttempt();
        _totalUs = _log.Entries.Length == 0 ? 0 : _log.Entries[^1].TimeUs;
        _lastUs = 0;
        _played = new StrokeAssembler();
        Metadata = ReplayMetadata.Describe(Path.GetFileName(path), _log, _attempt);
        Status = "Запись загружена: " + Path.GetFileName(path);
        RebuildScene();
        RaiseProgress();
        RaiseCommands();
        return true;
    }

    /// <summary>Воспроизведение с места паузы или с начала (если закончено / не начато).</summary>
    public void Play()
    {
        if (_log is null)
        {
            return;
        }

        if (_source is null || _source.IsCompleted)
        {
            StartSource();
        }

        _clock.Speed = _speed;
        IsPlaying = true;
        Status = "Воспроизведение…";
    }

    /// <summary>Пауза: время воспроизведения стоит.</summary>
    public void Pause()
    {
        if (!_isPlaying)
        {
            return;
        }

        _clock.Speed = 0;
        IsPlaying = false;
        Status = "Пауза.";
    }

    /// <summary>С начала: чернила стираются, воспроизведение идёт заново.</summary>
    public void Restart()
    {
        if (_log is null)
        {
            return;
        }

        StartSource();
        _clock.Speed = _speed;
        IsPlaying = true;
        Status = "Воспроизведение с начала…";
    }

    /// <summary>Показывает попытку целиком без воспроизведения.</summary>
    public void ShowAll()
    {
        if (_log is null || _attempt is null)
        {
            return;
        }

        StopSource();
        _played = new StrokeAssembler();
        foreach (PenSample sample in _attempt.Strokes.SelectMany(s => s.Samples))
        {
            _played.Add(sample);
        }

        _lastUs = _totalUs;
        RebuildScene();
        RaiseProgress();
        Status = "Показана вся попытка.";
    }

    /// <summary>Один такт воспроизведения: выдать наступившие точки, обновить сцену. Вызывает таймер представления.</summary>
    public void Tick()
    {
        if (_source is null || !_isPlaying)
        {
            return;
        }

        if (_source.Poll() > 0)
        {
            RebuildScene();
            RaiseProgress();
        }

        if (_source.IsCompleted)
        {
            IsPlaying = false;
            _lastUs = _totalUs;
            RaiseProgress();
            Status = "Воспроизведение закончено.";
        }
    }

    /// <summary>Останавливает воспроизведение и освобождает источник.</summary>
    public void Dispose() => StopSource();

    private void StartSource()
    {
        StopSource();
        if (_log is null)
        {
            return;
        }

        _played = new StrokeAssembler();
        _lastUs = 0;
        _clock.Speed = 0;
        _source = new ReplayPenSource(_log, _clock);
        _source.SamplesReceived += OnSamples;
        _source.LogEventRaised += OnLogEvent;
        _source.StartAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        RebuildScene();
        RaiseProgress();
    }

    private void StopSource()
    {
        if (_source is not null)
        {
            _source.SamplesReceived -= OnSamples;
            _source.LogEventRaised -= OnLogEvent;
            _source.StopAsync().AsTask().GetAwaiter().GetResult();
            _source = null;
        }

        IsPlaying = false;
    }

    private void OnSamples(ReadOnlyMemory<PenSample> samples)
    {
        _played.Add(samples.Span);
        if (!samples.IsEmpty)
        {
            _lastUs = Math.Max(_lastUs, samples.Span[^1].TimeUs);
        }
    }

    private void OnLogEvent(InkEvent ev)
    {
        _lastUs = Math.Max(_lastUs, ev.TimeUs);
        if (ev.Name == InkEvent.Names.RestartButton)
        {
            // «Заново»: холст очищается, как у пишущего.
            _played = new StrokeAssembler();
        }
    }

    private void RebuildScene()
    {
        if (_attempt is null)
        {
            return;
        }

        GuideFrame frame = _attempt.Guides;
        InkScene ghost = SceneBuilder.FromStrokes(_attempt.Strokes, frame, _guides, SceneStyle.Ghost);
        InkScene played = SceneBuilder.FromStrokes(_played.ToStrokes(), frame, _guides);
        Scene = new InkScene(_guides, [.. ghost.Polylines, .. played.Polylines], []);
    }

    private void RaiseProgress()
    {
        Raise(nameof(Progress));
        Raise(nameof(PositionText));
    }

    private void RaiseCommands()
    {
        _playCommand.RaiseCanExecuteChanged();
        _pauseCommand.RaiseCanExecuteChanged();
        _restartCommand.RaiseCanExecuteChanged();
        _showAllCommand.RaiseCanExecuteChanged();
    }
}
