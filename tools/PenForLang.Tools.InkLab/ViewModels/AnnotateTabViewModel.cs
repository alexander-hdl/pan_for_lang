using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using PenForLang.Ink;
using PenForLang.Tools.InkLab.Annotation;
using PenForLang.Tools.InkLab.Corpus;
using PenForLang.Tools.InkLab.Mvvm;
using PenForLang.Tools.InkLab.Scene;

namespace PenForLang.Tools.InkLab.ViewModels;

/// <summary>Чем сейчас работает клик по следу.</summary>
public enum AnnotateTool
{
    /// <summary>Клик ставит выход левой буквы (первый клик) и вход правой (второй клик) выбранного стыка.</summary>
    Boundary,

    /// <summary>Клик по штриху отмечает (или снимает) его как отложенный штрих выбранной буквы.</summary>
    Deferred,
}

/// <summary>Какую точку стыка ставит следующий клик.</summary>
public enum PlacePhase
{
    /// <summary>Выход тела левой буквы.</summary>
    End,

    /// <summary>Вход тела правой буквы.</summary>
    Start,
}

/// <summary>Строка стыка между двумя буквами в редакторе.</summary>
public sealed class JunctionRow : ViewModelBase
{
    private StrokePoint? _end;
    private StrokePoint? _start;
    private bool _isSelected;
    private Confidence _confidence;
    private PlacePhase _phase;

    /// <summary>Создаёт строку.</summary>
    public JunctionRow(int after, string label)
    {
        After = after;
        Label = label;
    }

    /// <summary>Стык между буквами <c>After</c> и <c>After + 1</c>.</summary>
    public int After { get; }

    /// <summary>Подпись: «a|t».</summary>
    public string Label { get; }

    /// <summary>Вид стыка по положению точек: один штрих — «соединены», разные — «отрыв пера».</summary>
    public JunctionKind Kind => _end is { } e && _start is { } s ? Junction.KindOf(e, s) : JunctionKind.Connected;

    /// <summary>Выход тела левой буквы или <c>null</c>.</summary>
    public StrokePoint? End
    {
        get => _end;
        internal set
        {
            if (Set(ref _end, value))
            {
                RaiseState();
            }
        }
    }

    /// <summary>Вход тела правой буквы или <c>null</c>.</summary>
    public StrokePoint? Start
    {
        get => _start;
        internal set
        {
            if (Set(ref _start, value))
            {
                RaiseState();
            }
        }
    }

    /// <summary>Уверенность разметчика.</summary>
    public Confidence Confidence
    {
        get => _confidence;
        internal set
        {
            if (Set(ref _confidence, value))
            {
                Raise(nameof(ConfidenceText));
            }
        }
    }

    /// <summary>Какую точку ждёт следующий клик (для выбранного стыка).</summary>
    public PlacePhase Phase
    {
        get => _phase;
        internal set
        {
            if (Set(ref _phase, value))
            {
                Raise(nameof(PhaseText));
            }
        }
    }

    /// <summary>Стык выбран для установки клика.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        internal set
        {
            if (Set(ref _isSelected, value))
            {
                Raise(nameof(PhaseText));
            }
        }
    }

    /// <summary>Обе точки стыка поставлены.</summary>
    public bool IsPlaced => _end is not null && _start is not null;

    /// <summary>«соединены» или «отрыв пера».</summary>
    public string KindText => IsPlaced ? (Kind == JunctionKind.Connected ? "соединены" : "отрыв пера") : "—";

    /// <summary>«уверенность: высокая / средняя / низкая».</summary>
    public string ConfidenceText => _confidence switch
    {
        Confidence.High => "уверенность: высокая",
        Confidence.Medium => "уверенность: средняя",
        _ => "уверенность: низкая",
    };

    /// <summary>Подсказка, что делать дальше.</summary>
    public string PhaseText => !_isSelected
        ? string.Empty
        : (_phase == PlacePhase.End ? "кликните: выход левой буквы" : "кликните: вход правой буквы (Shift — как выход)");

    /// <summary>Где стоит: «выход 0:20 → вход 1:0» или «не поставлен».</summary>
    public string PointText => (_end, _start) switch
    {
        ({ } e, { } s) when s == e => string.Create(CultureInfo.InvariantCulture, $"одна точка {e.Stroke}:{e.Sample}"),
        ({ } e, { } s) => string.Create(CultureInfo.InvariantCulture, $"выход {e.Stroke}:{e.Sample} → вход {s.Stroke}:{s.Sample}"),
        ({ } e, null) => string.Create(CultureInfo.InvariantCulture, $"выход {e.Stroke}:{e.Sample}, вход не поставлен"),
        _ => "не поставлен",
    };

    private void RaiseState()
    {
        Raise(nameof(IsPlaced));
        Raise(nameof(Kind));
        Raise(nameof(KindText));
        Raise(nameof(PointText));
    }
}

/// <summary>Строка отложенного штриха в редакторе.</summary>
public sealed class DeferredRow : ViewModelBase
{
    private DeferredTiming _timing;
    private DeferredTiming? _opinion;

    /// <summary>Создаёт строку.</summary>
    public DeferredRow(int stroke, int letter, DeferredPartKind kind, DeferredTiming timing, DeferredTiming? opinion)
    {
        Stroke = stroke;
        Letter = letter;
        Kind = kind;
        _timing = timing;
        _opinion = opinion;
    }

    /// <summary>Номер штриха.</summary>
    public int Stroke { get; }

    /// <summary>Буква, которой принадлежит штрих.</summary>
    public int Letter { get; }

    /// <summary>Точка или перекладина.</summary>
    public DeferredPartKind Kind { get; }

    /// <summary>Время, выведенное из порядка штрихов.</summary>
    public DeferredTiming Timing
    {
        get => _timing;
        internal set
        {
            if (Set(ref _timing, value))
            {
                Raise(nameof(Text));
            }
        }
    }

    /// <summary>Мнение разметчика, если он не согласен с выведенным временем.</summary>
    public DeferredTiming? Opinion
    {
        get => _opinion;
        internal set
        {
            if (Set(ref _opinion, value))
            {
                Raise(nameof(Text));
            }
        }
    }

    /// <summary>Строка для списка.</summary>
    public string Text =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"штрих {Stroke} · буква {Letter + 1} · {(Kind == DeferredPartKind.Dot ? "точка" : Kind == DeferredPartKind.Crossbar ? "перекладина" : "другое")} · {TimingName(_timing)}{(_opinion is { } o ? $" (по мнению разметчика: {TimingName(o)})" : string.Empty)}");

    private static string TimingName(DeferredTiming timing) => timing == DeferredTiming.Immediate ? "сразу" : "после слова";
}

/// <summary>Флаг качества с отметкой.</summary>
public sealed class FlagItem : ViewModelBase
{
    private bool _isChecked;

    /// <summary>Создаёт флаг.</summary>
    public FlagItem(string name, string title)
    {
        Name = name;
        Title = title;
    }

    /// <summary>Имя в файле разметки.</summary>
    public string Name { get; }

    /// <summary>Название для разметчика.</summary>
    public string Title { get; }

    /// <summary>Флаг поставлен.</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set => Set(ref _isChecked, value);
    }
}

/// <summary>
/// Вкладка «Разметка» без окна. Разметчик (идентификатор задаётся сеансом) открывает запись и задаёт: что фактически написано,
/// отложенные штрихи (точка, перекладина), затем границы букв — для каждого стыка два клика по следу: выход левой буквы и вход
/// правой (вид стыка «соединены» / «отрыв» следует из штрихов). Результат — файл <c>&lt;запись&gt;.pfl.jsonl.gz.ann.&lt;разметчик&gt;.json</c>
/// (<see cref="AnnotationFile"/>), у каждого разметчика свой; чужие разметки только читаются. Проверка идёт на каждое изменение
/// (<see cref="Issues"/>).
/// </summary>
public sealed class AnnotateTabViewModel : ViewModelBase
{
    /// <summary>Дальше этого расстояния от следа (x-height) клик не засчитывается.</summary>
    public const double MaxHitDistance = 1.0;

    private readonly SceneGuides _guides;
    private readonly Func<DateTimeOffset> _now;
    private readonly string _toolName;
    private readonly RelayCommand _saveCommand;
    private readonly RelayCommand _nextCommand;
    private readonly RelayCommand _previousCommand;
    private readonly RelayCommand _nextUnannotatedCommand;
    private readonly RelayCommand _toggleToolCommand;
    private readonly RelayCommand<JunctionRow> _selectJunctionCommand;
    private readonly RelayCommand<JunctionRow> _clearJunctionCommand;
    private readonly RelayCommand<JunctionRow> _cycleConfidenceCommand;
    private readonly RelayCommand<DeferredRow> _removeDeferredCommand;
    private readonly RelayCommand<DeferredRow> _toggleTimingCommand;
    private readonly Dictionary<int, (StrokePoint? End, StrokePoint? Start, Confidence Confidence)> _junctionCache = [];

    private string? _path;
    private string _sha = string.Empty;
    private PenLog? _log;
    private InkAttempt? _attempt;
    private ImmutableArray<InkStroke> _strokes = [];
    private ImmutableArray<int> _counts = [];
    private IReadOnlyList<AnnotationIssue> _staleIssues = [];
    private string? _target;
    private string? _variant;
    private string _folder = string.Empty;
    private string _status = "Откройте папку с записями и выберите запись.";
    private string _text = string.Empty;
    private string _annotator;
    private bool _usable = true;
    private string _note = string.Empty;
    private AnnotateTool _tool = AnnotateTool.Boundary;
    private int _selectedLetter;
    private bool _isDirty;
    private bool _isLoading;
    private bool _isValid;
    private CorpusEntry? _selectedEntry;
    private (JunctionRow Row, PlacePhase Phase)? _active;
    private InkScene _scene;

    /// <summary>Создаёт вкладку.</summary>
    /// <param name="guides">Линии линейки для рисования; <c>null</c> — по умолчанию.</param>
    /// <param name="now">Календарное время (UTC) для поля <c>annotatedAt</c>.</param>
    /// <param name="annotator">Идентификатор разметчика сеанса (без имени); от него зависит имя файла разметки.</param>
    /// <param name="toolName">Название программы для поля <c>tool</c>.</param>
    public AnnotateTabViewModel(SceneGuides? guides, Func<DateTimeOffset> now, string annotator = "A", string toolName = "InkLab")
    {
        ArgumentNullException.ThrowIfNull(now);
        ArgumentException.ThrowIfNullOrWhiteSpace(annotator);
        _guides = guides ?? SceneGuides.Default;
        _now = now;
        _annotator = annotator.Trim();
        _toolName = toolName;
        _scene = InkScene.Empty with { Guides = _guides };

        foreach (string flag in QualityFlags.All)
        {
            var item = new FlagItem(flag, FlagTitle(flag));
            item.PropertyChanged += OnFlagChanged;
            Flags.Add(item);
        }

        _saveCommand = new RelayCommand(() => Save(), () => _path is not null);
        _nextCommand = new RelayCommand(() => Move(1), () => Files.Count > 0);
        _previousCommand = new RelayCommand(() => Move(-1), () => Files.Count > 0);
        _nextUnannotatedCommand = new RelayCommand(NextUnannotated, () => Files.Count > 0);
        _toggleToolCommand = new RelayCommand(() => Tool = _tool == AnnotateTool.Boundary ? AnnotateTool.Deferred : AnnotateTool.Boundary);
        _selectJunctionCommand = new RelayCommand<JunctionRow>(row => SelectJunction(row?.After ?? -1));
        _clearJunctionCommand = new RelayCommand<JunctionRow>(row =>
        {
            if (row is not null)
            {
                ClearJunction(row.After);
            }
        });
        _cycleConfidenceCommand = new RelayCommand<JunctionRow>(row =>
        {
            if (row is not null)
            {
                SetConfidence(row.After, (Confidence)(((int)row.Confidence + 1) % 3));
            }
        });
        _removeDeferredCommand = new RelayCommand<DeferredRow>(row =>
        {
            if (row is not null)
            {
                RemoveDeferred(row.Stroke);
            }
        });
        _toggleTimingCommand = new RelayCommand<DeferredRow>(row =>
        {
            if (row is not null)
            {
                ToggleTimingOpinion(row.Stroke);
            }
        });
    }

    /// <summary>Записи открытой папки.</summary>
    public ObservableCollection<CorpusEntry> Files { get; } = [];

    /// <summary>Стыки между буквами выбранной записи.</summary>
    public ObservableCollection<JunctionRow> Junctions { get; } = [];

    /// <summary>Отложенные штрихи.</summary>
    public ObservableCollection<DeferredRow> Deferred { get; } = [];

    /// <summary>Флаги качества.</summary>
    public ObservableCollection<FlagItem> Flags { get; } = [];

    /// <summary>Замечания проверки.</summary>
    public ObservableCollection<AnnotationIssue> Issues { get; } = [];

    /// <summary>Разметки этой записи, сделанные другими разметчиками (только чтение).</summary>
    public ObservableCollection<AnnotatorState> OtherAnnotations { get; } = [];

    /// <summary>Команда «Сохранить».</summary>
    public RelayCommand SaveCommand => _saveCommand;

    /// <summary>Команда «Следующая запись».</summary>
    public RelayCommand NextCommand => _nextCommand;

    /// <summary>Команда «Предыдущая запись».</summary>
    public RelayCommand PreviousCommand => _previousCommand;

    /// <summary>Команда «К следующей неразмеченной».</summary>
    public RelayCommand NextUnannotatedCommand => _nextUnannotatedCommand;

    /// <summary>Команда «Границы ⇄ Отложенные».</summary>
    public RelayCommand ToggleToolCommand => _toggleToolCommand;

    /// <summary>Команда выбора стыка.</summary>
    public RelayCommand<JunctionRow> SelectJunctionCommand => _selectJunctionCommand;

    /// <summary>Команда «сбросить стык».</summary>
    public RelayCommand<JunctionRow> ClearJunctionCommand => _clearJunctionCommand;

    /// <summary>Команда «уверенность» (по кругу: высокая, средняя, низкая).</summary>
    public RelayCommand<JunctionRow> CycleConfidenceCommand => _cycleConfidenceCommand;

    /// <summary>Команда «снять отметку отложенного штриха».</summary>
    public RelayCommand<DeferredRow> RemoveDeferredCommand => _removeDeferredCommand;

    /// <summary>Команда «моё мнение о времени штриха».</summary>
    public RelayCommand<DeferredRow> ToggleTimingCommand => _toggleTimingCommand;

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

    /// <summary>Путь открытой записи или <c>null</c>.</summary>
    public string? Path => _path;

    /// <summary>Файл разметки текущего разметчика для открытой записи или <c>null</c>.</summary>
    public string? AnnotationPath => _path is null || string.IsNullOrWhiteSpace(_annotator) ? null : CorpusStore.AnnotationPathFor(_path, _annotator);

    /// <summary>Штрихи открытой записи (после последнего «Заново»).</summary>
    public ImmutableArray<InkStroke> Strokes => _strokes;

    /// <summary>Линейка открытой записи или <c>null</c>.</summary>
    public GuideFrame? Frame => _attempt?.Guides;

    /// <summary>Выбранная в списке запись; смена открывает её. Несохранённая разметка сохраняется; если сохранить не удалось — переход отменяется.</summary>
    public CorpusEntry? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedEntry))
            {
                return;
            }

            if (!SaveIfDirty())
            {
                Raise(nameof(SelectedEntry)); // привязка вернёт прежний выбор
                return;
            }

            if (Set(ref _selectedEntry, value))
            {
                Load(value.Path);
            }
        }
    }

    /// <summary>Что фактически написано (буквы по порядку, без пробелов).</summary>
    public string Text
    {
        get => _text;
        set
        {
            string normalized = new([.. (value ?? string.Empty).Where(c => !char.IsWhiteSpace(c))]);
            if (Set(ref _text, normalized))
            {
                RebuildJunctionSlots();
                SelectedLetter = _selectedLetter;
                Changed();
            }
        }
    }

    /// <summary>
    /// Идентификатор разметчика сеанса. Задаётся при запуске (<c>--annotator</c>) или в поле; из загруженного файла не берётся.
    /// Менять можно, пока нет несохранённых правок: открытая запись перечитывается как разметка нового разметчика.
    /// </summary>
    public string Annotator
    {
        get => _annotator;
        set
        {
            string id = (value ?? string.Empty).Trim();
            if (id.Length == 0 || string.Equals(id, _annotator, StringComparison.Ordinal))
            {
                Raise();
                return;
            }

            if (_isDirty && !Save())
            {
                Raise();
                return;
            }

            _annotator = id;
            Raise();
            Raise(nameof(AnnotationPath));
            if (_path is not null)
            {
                Load(_path);
            }
        }
    }

    /// <summary>Запись годится (<c>true</c>) или брак (<c>false</c>).</summary>
    public bool IsUsable
    {
        get => _usable;
        set
        {
            if (Set(ref _usable, value))
            {
                Changed();
            }
        }
    }

    /// <summary>Заметка разметчика.</summary>
    public string Note
    {
        get => _note;
        set
        {
            if (Set(ref _note, value ?? string.Empty))
            {
                Changed();
            }
        }
    }

    /// <summary>Инструмент клика.</summary>
    public AnnotateTool Tool
    {
        get => _tool;
        set => Set(ref _tool, value);
    }

    /// <summary>Буква, к которой относится новый отложенный штрих (с 0).</summary>
    public int SelectedLetter
    {
        get => _selectedLetter;
        set => Set(ref _selectedLetter, Math.Clamp(value, 0, Math.Max(0, _text.Length - 1)));
    }

    /// <summary>Есть несохранённые изменения.</summary>
    public bool IsDirty
    {
        get => _isDirty;
        private set => Set(ref _isDirty, value);
    }

    /// <summary>В разметке нет ошибок проверки.</summary>
    public bool IsValid
    {
        get => _isValid;
        private set => Set(ref _isValid, value);
    }

    /// <summary>Сцена для рисования.</summary>
    public InkScene Scene
    {
        get => _scene;
        private set => Set(ref _scene, value);
    }

    /// <summary>Подсказка про цель записи (из заголовка), например <c>word:at</c>.</summary>
    public string? Target => _target;

    /// <summary>Вариант записи (из задания): <c>lift</c>, <c>joined</c>, <c>dot</c> или <c>null</c>.</summary>
    public string? Variant => _variant;

    /// <summary>Открывает папку и показывает её записи с состоянием разметки. Несохранённая разметка сохраняется; не удалось — папка не перечитывается.</summary>
    /// <returns><c>false</c>, если несохранённую разметку сохранить не удалось.</returns>
    public bool OpenFolder(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        if (!SaveIfDirty())
        {
            return false;
        }

        Folder = folder;
        ReloadFiles();
        Status = Files.Count == 0 ? "В папке нет записей (*.pfl.jsonl.gz)." : $"Записей: {Files.Count}. Выберите запись.";
        return true;
    }

    /// <summary>Перечитывает текущую папку («Обновить»); несохранённая разметка сохраняется сначала.</summary>
    /// <returns><c>false</c>, если несохранённую разметку сохранить не удалось.</returns>
    public bool Refresh() => _folder.Length == 0 || OpenFolder(_folder);

    /// <summary>Сохраняет, если есть несохранённые правки. <c>false</c> — сохранить не удалось (причина в <see cref="Status"/>).</summary>
    public bool SaveIfDirty() => !_isDirty || _path is null || Save();

    /// <summary>Загружает запись и разметку текущего разметчика (если файл есть) либо готовит чистую разметку.</summary>
    /// <returns><c>false</c>, если запись не читается.</returns>
    public bool Load(string penLogPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(penLogPath);
        PenLog log;
        string sha;
        try
        {
            log = CorpusStore.ReadPenLog(penLogPath);
            sha = CorpusStore.ComputeSha256(penLogPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PenLogFormatException or InvalidDataException)
        {
            Status = "Запись не читается: " + ex.Message;
            return false;
        }

        _isLoading = true;
        try
        {
            _path = penLogPath;
            _sha = sha;
            _log = log;
            _attempt = log.ToInkAttempt();
            _strokes = _attempt.Strokes;
            _counts = SceneBuilder.StrokeSampleCounts(_strokes);
            _target = log.Header.Task?.Target;
            _variant = VariantOf(log);
            _active = null;
            _junctionCache.Clear();
            _staleIssues = [];

            (AnnotationFile? existing, string loadNote) = ReadOwnAnnotation(penLogPath);
            if (existing is not null)
            {
                IReadOnlyList<AnnotationIssue> onLoad = AnnotationValidator.Validate(existing, _strokes, _sha);
                _staleIssues =
                [
                    .. onLoad.Where(i => i.Severity == IssueSeverity.Error
                        && i.Code is IssueCodes.StrokeMismatch or IssueCodes.HashMismatch or IssueCodes.PointTime),
                ];
                if (_staleIssues.Count > 0)
                {
                    loadNote += " Запись изменилась после разметки: проверьте границы и сохраните заново.";
                }
            }

            ApplyAnnotation(existing ?? NewAnnotation(log));
            RefreshOthers(penLogPath, log);
            IsDirty = false;
            Status = (existing is null ? "Новая разметка: " : "Разметка загружена: ") + System.IO.Path.GetFileName(penLogPath) + "." + loadNote;
        }
        finally
        {
            _isLoading = false;
        }

        Revalidate();
        RebuildScene();
        _saveCommand.RaiseCanExecuteChanged();
        Raise(nameof(Path));
        Raise(nameof(AnnotationPath));
        Raise(nameof(Target));
        Raise(nameof(Variant));
        Raise(nameof(Strokes));
        Raise(nameof(Frame));
        return true;
    }

    /// <summary>Текущая разметка как файл (частично поставленные стыки в него попадают, неполнота видна в проверке).</summary>
    public AnnotationFile BuildAnnotation()
    {
        string penlogName = _path is null ? string.Empty : System.IO.Path.GetFileName(_path);
        ImmutableArray<Junction> junctions =
        [
            .. Junctions.Where(j => j.IsPlaced).Select(j => new Junction(
                j.After,
                j.Kind,
                PointRef.Of(_strokes, j.End!.Value) ?? new PointRef(j.End.Value.Stroke, j.End.Value.Sample, -1),
                PointRef.Of(_strokes, j.Start!.Value) ?? new PointRef(j.Start.Value.Stroke, j.Start.Value.Sample, -1),
                j.Confidence)),
        ];
        ImmutableArray<DeferredMark> deferred =
        [
            .. Deferred.OrderBy(d => d.Stroke).Select(d => new DeferredMark(d.Stroke, d.Letter, d.Kind, d.Timing, d.Opinion)),
        ];
        ImmutableArray<string> flags = [.. Flags.Where(f => f.IsChecked).Select(f => f.Name)];
        return new AnnotationFile(
            AnnotationFormat.Schema,
            penlogName,
            _sha,
            AnnotationFormat.Indexing,
            _target,
            _variant,
            _annotator.Trim(),
            _now().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            _toolName,
            _text,
            new QualityMark(_usable, flags, string.IsNullOrWhiteSpace(_note) ? null : _note.Trim()),
            StrokeInfo.Of(_strokes),
            junctions,
            deferred);
    }

    /// <summary>Записывает файл разметки текущего разметчика рядом с записью. Черновик с ошибками тоже сохраняется.</summary>
    /// <returns><c>true</c>, если файл записан.</returns>
    public bool Save()
    {
        if (_path is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(_annotator))
        {
            Status = "Укажите идентификатор разметчика.";
            return false;
        }

        try
        {
            AnnotationJson.Save(CorpusStore.AnnotationPathFor(_path, _annotator), BuildAnnotation());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "Не удалось сохранить разметку: " + ex.Message;
            return false;
        }

        _staleIssues = [];
        IsDirty = false;
        Revalidate();
        RefreshSelectedEntry();
        Status = IsValid ? "Разметка сохранена." : "Разметка сохранена как черновик: есть ошибки (см. список).";
        return true;
    }

    /// <summary>
    /// Клик по следу в координатах линейки (<c>u</c> вправо, <c>v</c> вверх, в x-height). Инструмент «Границы»: первый клик —
    /// выход левой буквы, второй — вход правой; <paramref name="shift"/> — вход совпадает с выходом (граница одной точкой).
    /// Инструмент «Отложенные»: клик по штриху ставит или снимает отметку.
    /// </summary>
    public void HandleClick(double u, double v, bool shift = false)
    {
        if (_attempt is null)
        {
            return;
        }

        HashSet<int> deferred = [.. Deferred.Select(d => d.Stroke)];
        if (_tool == AnnotateTool.Deferred)
        {
            (StrokePoint Point, double Distance)? any = SceneBuilder.Nearest(_strokes, _attempt.Guides, u, v);
            if (any is null || any.Value.Distance > MaxHitDistance)
            {
                Status = "Клик мимо следа.";
                return;
            }

            ToggleDeferred(any.Value.Point.Stroke);
            return;
        }

        JunctionRow? row = Junctions.FirstOrDefault(j => j.IsSelected);
        if (row is null)
        {
            Status = _text.Length < 2 ? "Сначала введите, что написано (минимум две буквы для стыков)." : "Выберите стык в списке справа.";
            return;
        }

        Func<StrokePoint, bool> allowed = AllowedPoints(row, row.Phase);
        (StrokePoint Point, double Distance)? hit = SceneBuilder.Nearest(_strokes, _attempt.Guides, u, v, s => !deferred.Contains(s), allowed);
        if (hit is null || hit.Value.Distance > MaxHitDistance)
        {
            Status = "Клик мимо следа (или точка недопустима: границы идут по порядку вдоль следа).";
            return;
        }

        PlacePoint(row, hit.Value.Point, shift);
    }

    /// <summary>Выбирает стык для установки границы (-1 — снять выбор); следующий клик ставит ещё не поставленную точку.</summary>
    public void SelectJunction(int after)
    {
        foreach (JunctionRow row in Junctions)
        {
            row.IsSelected = row.After == after;
            if (row.IsSelected)
            {
                row.Phase = row.End is not null && row.Start is null ? PlacePhase.Start : PlacePhase.End;
            }
        }

        Tool = AnnotateTool.Boundary;
        RebuildScene();
    }

    /// <summary>
    /// Ставит ближайшую следующую точку выбранного стыка: выход, затем вход. <paramref name="onePoint"/> — вход равен выходу.
    /// </summary>
    public void PlacePoint(JunctionRow row, StrokePoint point, bool onePoint = false)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Phase == PlacePhase.End || row.End is null)
        {
            row.Phase = PlacePhase.End;
            SetRow(row, point, onePoint ? point : null);
            if (onePoint)
            {
                Complete(row);
            }
            else
            {
                row.Phase = PlacePhase.Start;
                _active = (row, PlacePhase.End);
                Changed();
            }

            return;
        }

        StrokePoint end = row.End.Value;
        StrokePoint start = onePoint ? end : point;
        if (start < end)
        {
            Status = "Вход правой буквы не может быть раньше выхода левой.";
            return;
        }

        SetRow(row, end, start);
        Complete(row);
    }

    /// <summary>Сдвигает последнюю поставленную точку (выход или вход) на <paramref name="delta"/> точек вдоль основного следа (стрелки).</summary>
    public void Nudge(int delta)
    {
        if (_active is not { } active || _attempt is null)
        {
            return;
        }

        JunctionRow row = active.Row;
        StrokePoint? current = active.Phase == PlacePhase.End ? row.End : row.Start;
        if (current is null)
        {
            return;
        }

        HashSet<int> deferred = [.. Deferred.Select(d => d.Stroke)];
        StrokePoint moved = Step(current.Value, delta, deferred);
        if (moved == current.Value)
        {
            return;
        }

        if (active.Phase == PlacePhase.End)
        {
            StrokePoint? start = row.Start;

            // Выход не может уйти за вход: тянем вход за ним (оставляя «одну точку»), если они совпадали.
            bool single = start is { } s && s == current.Value;
            SetRow(row, moved, start is null ? null : (single || moved > start.Value ? moved : start));
        }
        else
        {
            if (row.End is { } end && moved < end)
            {
                return;
            }

            SetRow(row, row.End, moved);
        }

        Changed();
    }

    /// <summary>Меняет уверенность разметчика в стыке.</summary>
    public void SetConfidence(int after, Confidence confidence)
    {
        JunctionRow? row = Junctions.FirstOrDefault(j => j.After == after);
        if (row is null || row.Confidence == confidence)
        {
            return;
        }

        row.Confidence = confidence;
        CacheRow(row);
        Changed();
    }

    /// <summary>Сбрасывает стык.</summary>
    public void ClearJunction(int after)
    {
        JunctionRow? row = Junctions.FirstOrDefault(j => j.After == after);
        if (row is null)
        {
            return;
        }

        row.End = null;
        row.Start = null;
        CacheRow(row);
        SelectJunction(after);
        Changed();
    }

    /// <summary>Отмечает штрих отложенным для <see cref="SelectedLetter"/> или снимает отметку.</summary>
    public void ToggleDeferred(int stroke)
    {
        DeferredRow? existing = Deferred.FirstOrDefault(d => d.Stroke == stroke);
        if (existing is not null)
        {
            Deferred.Remove(existing);
        }
        else if (stroke >= 0 && stroke < _strokes.Length)
        {
            int letter = Math.Clamp(_selectedLetter, 0, Math.Max(0, _text.Length - 1));
            DeferredPartKind kind = letter < _text.Length ? AnnotationValidator.ExpectedDeferredKind(_text[letter]) ?? DeferredPartKind.Other : DeferredPartKind.Other;
            Deferred.Add(new DeferredRow(stroke, letter, kind, DeferredTiming.AfterWord, null));
        }

        OnDeferredChanged();
        Changed();
    }

    /// <summary>Снимает отметку отложенного штриха.</summary>
    public void RemoveDeferred(int stroke)
    {
        DeferredRow? existing = Deferred.FirstOrDefault(d => d.Stroke == stroke);
        if (existing is not null)
        {
            Deferred.Remove(existing);
            OnDeferredChanged();
            Changed();
        }
    }

    /// <summary>
    /// Мнение разметчика о времени отложенного штриха: если не задано — «наоборот, чем выведено из порядка»; повторно — снимается.
    /// Выведенное время (<see cref="DeferredRow.Timing"/>) всегда считается по порядку штрихов.
    /// </summary>
    public void ToggleTimingOpinion(int stroke)
    {
        DeferredRow? row = Deferred.FirstOrDefault(d => d.Stroke == stroke);
        if (row is null)
        {
            return;
        }

        row.Opinion = row.Opinion is null ? (row.Timing == DeferredTiming.Immediate ? DeferredTiming.AfterWord : DeferredTiming.Immediate) : null;
        Changed();
    }

    /// <summary>Переходит на соседнюю запись списка (по кругу).</summary>
    public void Move(int delta)
    {
        if (Files.Count == 0)
        {
            return;
        }

        int index = _selectedEntry is null ? -1 : Files.IndexOf(_selectedEntry);
        int target = index < 0 ? (delta > 0 ? 0 : Files.Count - 1) : (((index + delta) % Files.Count) + Files.Count) % Files.Count;
        SelectedEntry = Files[target];
    }

    /// <summary>Переходит к следующей записи, которую текущий разметчик ещё не разметил верно.</summary>
    public void NextUnannotated()
    {
        if (Files.Count == 0)
        {
            return;
        }

        int start = _selectedEntry is null ? -1 : Files.IndexOf(_selectedEntry);
        for (int step = 1; step <= Files.Count; step++)
        {
            CorpusEntry candidate = Files[(start + step) % Files.Count];
            AnnotatorState? mine = candidate.AnnotatorStates.FirstOrDefault(a => string.Equals(a.Annotator, _annotator, StringComparison.Ordinal));
            if (candidate.ReadError is null && (mine is null || mine.State == AnnotationState.Draft))
            {
                SelectedEntry = candidate;
                return;
            }
        }

        Status = "Все записи размечены этим разметчиком.";
    }

    private static string FlagTitle(string flag) => flag switch
    {
        QualityFlags.WrongContent => "написано не то",
        QualityFlags.Corrected => "исправления поверх",
        QualityFlags.Hesitation => "колебание, остановка",
        QualityFlags.OutOfGuides => "далеко за линейками",
        QualityFlags.DeviceGlitch => "сбой устройства",
        _ => "другое (см. заметку)",
    };

    private static string? VariantOf(PenLog log)
    {
        if (Recording.RecordingHeaders.TryParseTaskId(log.Header.Task?.TaskId, out _, out _, out string? parsed) && parsed is not null)
        {
            return parsed;
        }

        InkEvent? prompt = log.Events.FirstOrDefault(e => e.Data is not null && e.Data.ContainsKey("variant"));
        return prompt?.Data?["variant"];
    }

    /// <summary>Начальная разметка: текст из цели записи, годна, без стыков.</summary>
    private AnnotationFile NewAnnotation(PenLog log)
    {
        string? target = log.Header.Task?.Target;
        string text = string.Empty;
        if (target is not null)
        {
            int colon = target.IndexOf(':', StringComparison.Ordinal);
            if (colon >= 0 && colon < target.Length - 1)
            {
                text = target[(colon + 1)..];
            }
        }

        return new AnnotationFile(
            AnnotationFormat.Schema, string.Empty, _sha, AnnotationFormat.Indexing, target, _variant, _annotator, null, _toolName, text, QualityMark.Good, [], [], []);
    }

    /// <summary>Разметка текущего разметчика: его файл, а при его отсутствии — файл прежнего вида, если автор в нём тот же.</summary>
    private (AnnotationFile? File, string Note) ReadOwnAnnotation(string penLogPath)
    {
        string note = string.Empty;
        string own = CorpusStore.AnnotationPathFor(penLogPath, _annotator);
        string legacy = CorpusStore.LegacyAnnotationPathFor(penLogPath);
        foreach (string candidate in new[] { own, legacy })
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                AnnotationFile file = AnnotationJson.Load(candidate);
                if (candidate == legacy && !string.Equals(file.Annotator, _annotator, StringComparison.Ordinal))
                {
                    continue; // файл прежнего вида чужого автора — только для чтения в списке «Другие разметчики»
                }

                return (file, candidate == legacy ? " (прочитан файл прежнего вида .ann.json; при сохранении будет записан новый)" : string.Empty);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AnnotationFormatException)
            {
                note = " Файл разметки не читается (" + ex.Message + "): начата новая.";
            }
        }

        return (null, note);
    }

    private void RefreshOthers(string penLogPath, PenLog log)
    {
        OtherAnnotations.Clear();
        string own = CorpusStore.AnnotationPathFor(penLogPath, _annotator);
        foreach (AnnotatorState state in CorpusIndex.AnnotationStates(penLogPath, log))
        {
            if (!string.Equals(state.Path, own, StringComparison.OrdinalIgnoreCase) && !string.Equals(state.Annotator, _annotator, StringComparison.Ordinal))
            {
                OtherAnnotations.Add(state);
            }
        }
    }

    private void ApplyAnnotation(AnnotationFile annotation)
    {
        _text = annotation.Text;
        _usable = annotation.Quality.Usable;
        _note = annotation.Quality.Note ?? string.Empty;
        foreach (FlagItem flag in Flags)
        {
            flag.IsChecked = annotation.Quality.Flags.Contains(flag.Name);
        }

        foreach (Junction j in annotation.Junctions)
        {
            _junctionCache[j.After] = (j.End.Point, j.Start.Point, j.Confidence);
        }

        RebuildJunctionSlots();

        Deferred.Clear();
        foreach (DeferredMark d in annotation.Deferred.OrderBy(d => d.Stroke))
        {
            Deferred.Add(new DeferredRow(d.Stroke, d.Letter, d.Kind, d.Timing, d.TimingOpinion));
        }

        RecomputeTimings();
        _selectedLetter = 0;
        JunctionRow? first = Junctions.FirstOrDefault(j => !j.IsPlaced) ?? Junctions.FirstOrDefault();
        SelectJunction(first?.After ?? -1);
        Raise(nameof(Text));
        Raise(nameof(IsUsable));
        Raise(nameof(Note));
        Raise(nameof(Annotator));
        Raise(nameof(SelectedLetter));
    }

    /// <summary>Строит стыки по тексту; поставленные границы берутся из кэша, поэтому правка текста («at» → «a» → «at») их не теряет.</summary>
    private void RebuildJunctionSlots()
    {
        int selected = Junctions.FirstOrDefault(j => j.IsSelected)?.After ?? -1;
        Junctions.Clear();
        for (int i = 0; i < _text.Length - 1; i++)
        {
            var row = new JunctionRow(i, SceneBuilder.Label(_text, i));
            if (_junctionCache.TryGetValue(i, out (StrokePoint? End, StrokePoint? Start, Confidence Confidence) cached))
            {
                row.End = cached.End;
                row.Start = cached.Start;
                row.Confidence = cached.Confidence;
            }

            row.IsSelected = i == selected;
            row.Phase = row.End is not null && row.Start is null ? PlacePhase.Start : PlacePhase.End;
            Junctions.Add(row);
        }

        if (!Junctions.Any(j => j.IsSelected) && Junctions.Count > 0)
        {
            Junctions[0].IsSelected = true;
        }
    }

    /// <summary>Допустимые точки для ставящейся точки стыка: в порядке вдоль следа между соседними стыками.</summary>
    private Func<StrokePoint, bool> AllowedPoints(JunctionRow row, PlacePhase phase)
    {
        StrokePoint? lower = row.After > 0 && Junctions.FirstOrDefault(j => j.After == row.After - 1)?.Start is { } previousStart ? previousStart : null;
        StrokePoint? upper = Junctions.FirstOrDefault(j => j.After == row.After + 1)?.End;
        StrokePoint? end = row.End;
        return point =>
        {
            if (phase == PlacePhase.End)
            {
                return (lower is null || point > lower.Value) && (upper is null || point < upper.Value);
            }

            return (end is null || point >= end.Value) && (upper is null || point < upper.Value);
        };
    }

    private void SetRow(JunctionRow row, StrokePoint? end, StrokePoint? start)
    {
        row.End = end;
        row.Start = start;
        CacheRow(row);
    }

    private void Complete(JunctionRow row)
    {
        _active = (row, PlacePhase.Start);
        Changed();
        JunctionRow? next = Junctions.FirstOrDefault(j => !j.IsPlaced && j.After != row.After);
        SelectJunction(next?.After ?? row.After);
        _active = (row, PlacePhase.Start);
    }

    private void CacheRow(JunctionRow row) => _junctionCache[row.After] = (row.End, row.Start, row.Confidence);

    /// <summary>Точка на <paramref name="delta"/> точек вдоль основного следа (переходит через границы штрихов, пропускает отложенные).</summary>
    private StrokePoint Step(StrokePoint point, int delta, HashSet<int> deferred)
    {
        StrokePoint current = point;
        int direction = Math.Sign(delta);
        for (int i = 0; i < Math.Abs(delta); i++)
        {
            StrokePoint next;
            if (direction > 0)
            {
                if (current.Sample + 1 < _counts[current.Stroke])
                {
                    next = new StrokePoint(current.Stroke, current.Sample + 1);
                }
                else
                {
                    int stroke = AnnotationValidator.NextMainStroke(current.Stroke, _counts, deferred);
                    if (stroke < 0)
                    {
                        return current;
                    }

                    next = new StrokePoint(stroke, 0);
                }
            }
            else if (current.Sample > 0)
            {
                next = new StrokePoint(current.Stroke, current.Sample - 1);
            }
            else
            {
                int stroke = current.Stroke - 1;
                while (stroke >= 0 && (deferred.Contains(stroke) || _counts[stroke] == 0))
                {
                    stroke--;
                }

                if (stroke < 0)
                {
                    return current;
                }

                next = new StrokePoint(stroke, _counts[stroke] - 1);
            }

            current = next;
        }

        return current;
    }

    private void OnFlagChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FlagItem.IsChecked))
        {
            Changed();
        }
    }

    /// <summary>
    /// Изменился набор отложенных штрихов: основной след другой, поэтому стыки, чьи точки оказались на отложенном штрихе, сбрасываются
    /// (иначе граница осталась бы на точке, которой нет в основном следе), а выведенное время пересчитывается.
    /// </summary>
    private void OnDeferredChanged()
    {
        HashSet<int> deferred = [.. Deferred.Select(d => d.Stroke)];
        var cleared = new List<string>();
        foreach (JunctionRow row in Junctions)
        {
            bool onDeferred = (row.End is { } e && deferred.Contains(e.Stroke)) || (row.Start is { } s && deferred.Contains(s.Stroke));
            if (onDeferred)
            {
                row.End = null;
                row.Start = null;
                CacheRow(row);
                row.Phase = PlacePhase.End;
                cleared.Add(row.Label);
            }
        }

        RecomputeTimings();
        if (cleared.Count > 0)
        {
            Status = "Стыки сброшены, потому что их точки оказались на отложенном штрихе: " + string.Join(", ", cleared) + ". Поставьте их заново.";
        }
    }

    private void RecomputeTimings()
    {
        HashSet<int> deferred = [.. Deferred.Select(d => d.Stroke)];
        foreach (DeferredRow row in Deferred)
        {
            row.Timing = AnnotationFile.IsImmediate(row.Stroke, _counts, deferred) ? DeferredTiming.Immediate : DeferredTiming.AfterWord;
        }
    }

    /// <summary>Что-то изменилось в разметке: проверка, сцена, пометка «не сохранено».</summary>
    private void Changed()
    {
        if (_isLoading)
        {
            return;
        }

        RecomputeTimings();
        IsDirty = true;
        Revalidate();
        RebuildScene();
    }

    private void Revalidate()
    {
        if (_path is null)
        {
            return;
        }

        Issues.Clear();
        IReadOnlyList<AnnotationIssue> issues = AnnotationValidator.Validate(BuildAnnotation(), _strokes, _sha);
        foreach (AnnotationIssue issue in _staleIssues.Concat(issues.Where(i => !_staleIssues.Any(s => s.Code == i.Code))))
        {
            Issues.Add(issue);
        }

        IsValid = Issues.All(i => i.Severity != IssueSeverity.Error);
    }

    private void RebuildScene()
    {
        if (_attempt is null)
        {
            Scene = InkScene.Empty with { Guides = _guides };
            return;
        }

        AnnotationFile annotation = BuildAnnotation();
        InkScene scene = SceneBuilder.FromAnnotation(_strokes, _attempt.Guides, _guides, _text, annotation.Junctions, annotation.Deferred);

        // Выход без входа (первый клик уже был) и активная точка для стрелок.
        var extra = new List<SceneMarker>();
        foreach (JunctionRow row in Junctions.Where(j => j.End is not null && j.Start is null))
        {
            StrokePoint end = row.End!.Value;
            if (PointRef.Of(_strokes, end) is not null)
            {
                ScenePoint p = SceneBuilder.ToScene(_attempt.Guides, _strokes[end.Stroke].Samples[end.Sample]);
                extra.Add(new SceneMarker(p.U, p.V, SceneBuilder.EndLabel(_text, row.After), MarkerKind.Junction));
            }
        }

        if (_active is { } active && (active.Phase == PlacePhase.End ? active.Row.End : active.Row.Start) is { } point && PointRef.Of(_strokes, point) is not null)
        {
            ScenePoint p = SceneBuilder.ToScene(_attempt.Guides, _strokes[point.Stroke].Samples[point.Sample]);
            extra.Add(new SceneMarker(p.U, p.V, string.Empty, MarkerKind.Info));
        }

        Scene = extra.Count == 0 ? scene : scene with { Markers = scene.Markers.AddRange(extra) };
    }

    private void ReloadFiles()
    {
        Files.Clear();
        _selectedEntry = null;
        Raise(nameof(SelectedEntry));
        if (_folder.Length == 0)
        {
            return;
        }

        foreach (CorpusEntry entry in CorpusIndex.Scan(_folder))
        {
            Files.Add(entry);
        }

        _nextCommand.RaiseCanExecuteChanged();
        _previousCommand.RaiseCanExecuteChanged();
        _nextUnannotatedCommand.RaiseCanExecuteChanged();
    }

    private void RefreshSelectedEntry()
    {
        if (_selectedEntry is null || _path is null || _log is null)
        {
            return;
        }

        int index = Files.IndexOf(_selectedEntry);
        if (index < 0)
        {
            return;
        }

        CorpusEntry updated = CorpusIndex.Describe(_path);
        _selectedEntry = updated;
        Files[index] = updated;
        Raise(nameof(SelectedEntry));
        RefreshOthers(_path, _log);
    }
}
