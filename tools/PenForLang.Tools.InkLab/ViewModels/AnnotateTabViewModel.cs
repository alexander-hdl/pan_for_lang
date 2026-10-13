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
    /// <summary>Клик ставит границу выбранного стыка между буквами.</summary>
    Boundary,

    /// <summary>Клик по штриху отмечает (или снимает) его как отложенный штрих выбранной буквы.</summary>
    Deferred,
}

/// <summary>Строка стыка между двумя буквами в редакторе.</summary>
public sealed class JunctionRow : ViewModelBase
{
    private JunctionKind _kind;
    private StrokePoint? _end;
    private StrokePoint? _start;
    private bool _isSelected;

    /// <summary>Создаёт строку.</summary>
    public JunctionRow(int after, string label, JunctionKind kind)
    {
        After = after;
        Label = label;
        _kind = kind;
    }

    /// <summary>Стык между буквами <c>After</c> и <c>After + 1</c>.</summary>
    public int After { get; }

    /// <summary>Подпись: «a|t».</summary>
    public string Label { get; }

    /// <summary>Соединены или отрыв.</summary>
    public JunctionKind Kind
    {
        get => _kind;
        internal set
        {
            if (Set(ref _kind, value))
            {
                Raise(nameof(KindText));
            }
        }
    }

    /// <summary>Конец левой буквы или <c>null</c>, если стык не поставлен.</summary>
    public StrokePoint? End
    {
        get => _end;
        internal set
        {
            if (Set(ref _end, value))
            {
                Raise(nameof(IsPlaced));
                Raise(nameof(PointText));
            }
        }
    }

    /// <summary>Начало правой буквы.</summary>
    public StrokePoint? Start
    {
        get => _start;
        internal set
        {
            if (Set(ref _start, value))
            {
                Raise(nameof(PointText));
            }
        }
    }

    /// <summary>Стык выбран для установки клика.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        internal set => Set(ref _isSelected, value);
    }

    /// <summary>Стык поставлен.</summary>
    public bool IsPlaced => _end is not null && _start is not null;

    /// <summary>«соединены» или «отрыв пера».</summary>
    public string KindText => _kind == JunctionKind.Connected ? "соединены" : "отрыв пера";

    /// <summary>Где стоит: «штрих 0, точка 41» или «не поставлен».</summary>
    public string PointText => _end is { } e
        ? (_start is { } s && s != e
            ? string.Create(CultureInfo.InvariantCulture, $"конец {e.Stroke}:{e.Sample}, начало {s.Stroke}:{s.Sample}")
            : string.Create(CultureInfo.InvariantCulture, $"{e.Stroke}:{e.Sample}"))
        : "не поставлен";
}

/// <summary>Строка отложенного штриха в редакторе.</summary>
public sealed class DeferredRow : ViewModelBase
{
    private DeferredTiming _timing;

    /// <summary>Создаёт строку.</summary>
    public DeferredRow(int stroke, int letter, DeferredPartKind kind, DeferredTiming timing, bool timingOverridden)
    {
        Stroke = stroke;
        Letter = letter;
        Kind = kind;
        _timing = timing;
        TimingOverridden = timingOverridden;
    }

    /// <summary>Номер штриха.</summary>
    public int Stroke { get; }

    /// <summary>Буква, которой принадлежит штрих.</summary>
    public int Letter { get; }

    /// <summary>Точка или перекладина.</summary>
    public DeferredPartKind Kind { get; }

    /// <summary>Разметчик задал время вручную (иначе оно выводится из порядка штрихов).</summary>
    public bool TimingOverridden { get; internal set; }

    /// <summary>Сразу или после слова.</summary>
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

    /// <summary>Строка для списка.</summary>
    public string Text =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"штрих {Stroke} · буква {Letter + 1} · {(Kind == DeferredPartKind.Dot ? "точка" : Kind == DeferredPartKind.Crossbar ? "перекладина" : "другое")} · {(_timing == DeferredTiming.Immediate ? "сразу" : "после слова")}");
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
/// Вкладка «Разметка» без окна. Разметчик открывает запись и задаёт: что фактически написано, границы букв (клик по следу),
/// соединён ли каждый стык или перо оторвано, какие штрихи отложенные (точка, перекладина) и какой букве принадлежат, годится
/// запись или брак. Результат — файл <c>&lt;запись&gt;.pfl.jsonl.gz.ann.json</c> (<see cref="AnnotationFile"/>); проверка идёт на
/// каждое изменение (<see cref="Issues"/>).
/// </summary>
public sealed class AnnotateTabViewModel : ViewModelBase
{
    /// <summary>Дальше этого расстояния от следа (x-height) клик не засчитывается.</summary>
    public const double MaxHitDistance = 1.0;

    private readonly SceneGuides _guides;
    private readonly Func<DateTimeOffset> _now;
    private readonly RelayCommand _saveCommand;
    private readonly RelayCommand _nextCommand;
    private readonly RelayCommand _previousCommand;
    private readonly RelayCommand _nextUnannotatedCommand;
    private readonly RelayCommand<JunctionRow> _selectJunctionCommand;
    private readonly RelayCommand<JunctionRow> _toggleKindCommand;
    private readonly RelayCommand<JunctionRow> _clearJunctionCommand;
    private readonly RelayCommand<DeferredRow> _removeDeferredCommand;
    private readonly RelayCommand<DeferredRow> _toggleTimingCommand;

    private string? _path;
    private PenLog? _log;
    private InkAttempt? _attempt;
    private ImmutableArray<InkStroke> _strokes = [];
    private ImmutableArray<int> _counts = [];
    private ImmutableArray<int>? _staleCounts;
    private string? _target;
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
    private JunctionKind _defaultKind = JunctionKind.Connected;
    private InkScene _scene;

    /// <summary>Создаёт вкладку.</summary>
    /// <param name="guides">Линии линейки для рисования; <c>null</c> — по умолчанию.</param>
    /// <param name="now">Календарное время (UTC) для поля <c>annotatedAt</c>.</param>
    /// <param name="annotator">Идентификатор разметчика (без имени) по умолчанию.</param>
    public AnnotateTabViewModel(SceneGuides? guides, Func<DateTimeOffset> now, string annotator = "A")
    {
        ArgumentNullException.ThrowIfNull(now);
        ArgumentException.ThrowIfNullOrWhiteSpace(annotator);
        _guides = guides ?? SceneGuides.Default;
        _now = now;
        _annotator = annotator;
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
        _selectJunctionCommand = new RelayCommand<JunctionRow>(row => SelectJunction(row?.After ?? -1));
        _toggleKindCommand = new RelayCommand<JunctionRow>(row =>
        {
            if (row is not null)
            {
                SetKind(row.After, row.Kind == JunctionKind.Connected ? JunctionKind.Lifted : JunctionKind.Connected);
            }
        });
        _clearJunctionCommand = new RelayCommand<JunctionRow>(row =>
        {
            if (row is not null)
            {
                ClearJunction(row.After);
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
                ToggleTiming(row.Stroke);
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

    /// <summary>Команда «Сохранить».</summary>
    public RelayCommand SaveCommand => _saveCommand;

    /// <summary>Команда «Следующая запись».</summary>
    public RelayCommand NextCommand => _nextCommand;

    /// <summary>Команда «Предыдущая запись».</summary>
    public RelayCommand PreviousCommand => _previousCommand;

    /// <summary>Команда «К следующей неразмеченной».</summary>
    public RelayCommand NextUnannotatedCommand => _nextUnannotatedCommand;

    /// <summary>Команда выбора стыка.</summary>
    public RelayCommand<JunctionRow> SelectJunctionCommand => _selectJunctionCommand;

    /// <summary>Команда «соединены / отрыв» для стыка.</summary>
    public RelayCommand<JunctionRow> ToggleKindCommand => _toggleKindCommand;

    /// <summary>Команда «сбросить стык».</summary>
    public RelayCommand<JunctionRow> ClearJunctionCommand => _clearJunctionCommand;

    /// <summary>Команда «снять отметку отложенного штриха».</summary>
    public RelayCommand<DeferredRow> RemoveDeferredCommand => _removeDeferredCommand;

    /// <summary>Команда «сразу / после слова».</summary>
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

    /// <summary>Штрихи открытой записи (после последнего «Заново»).</summary>
    public ImmutableArray<InkStroke> Strokes => _strokes;

    /// <summary>Линейка открытой записи или <c>null</c>.</summary>
    public GuideFrame? Frame => _attempt?.Guides;

    /// <summary>Выбранная в списке запись; смена открывает её (несохранённая разметка сохраняется как черновик).</summary>
    public CorpusEntry? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedEntry))
            {
                return;
            }

            if (_isDirty && _path is not null)
            {
                Save();
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
                Changed();
            }
        }
    }

    /// <summary>Идентификатор разметчика.</summary>
    public string Annotator
    {
        get => _annotator;
        set
        {
            if (Set(ref _annotator, value ?? string.Empty))
            {
                Changed();
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

    /// <summary>Открывает папку и показывает её записи с состоянием разметки.</summary>
    public void OpenFolder(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        Folder = folder;
        ReloadFiles();
        Status = Files.Count == 0 ? "В папке нет записей (*.pfl.jsonl.gz)." : $"Записей: {Files.Count}. Выберите запись.";
    }

    /// <summary>Загружает запись и её разметку (если файл разметки есть) либо готовит чистую разметку.</summary>
    /// <returns><c>false</c>, если запись не читается.</returns>
    public bool Load(string penLogPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(penLogPath);
        PenLog log;
        try
        {
            log = CorpusStore.ReadPenLog(penLogPath);
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
            _log = log;
            _attempt = log.ToInkAttempt();
            _strokes = _attempt.Strokes;
            _counts = SceneBuilder.StrokeSampleCounts(_strokes);
            _target = log.Header.Task?.Target;

            string? variant = null;
            if (Recording.RecordingHeaders.TryParseTaskId(log.Header.Task?.TaskId, out _, out _, out string? parsed))
            {
                variant = parsed;
            }

            _defaultKind = variant == Protocol.PromptVariants.Lift ? JunctionKind.Lifted : JunctionKind.Connected;

            string annotationPath = CorpusStore.AnnotationPathFor(penLogPath);
            AnnotationFile? existing = null;
            string loadNote = string.Empty;
            if (File.Exists(annotationPath))
            {
                try
                {
                    existing = AnnotationJson.Load(annotationPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AnnotationFormatException)
                {
                    loadNote = " Файл разметки не читается (" + ex.Message + "): начата новая.";
                }
            }

            // Разметка сделана для другой раскладки штрихов: границы могут не соответствовать записи, пока разметчик не проверит и не сохранит.
            _staleCounts = existing is { StrokeSamples.IsEmpty: false } stale && !stale.StrokeSamples.SequenceEqual(_counts) ? stale.StrokeSamples : null;
            if (_staleCounts is not null)
            {
                loadNote += " Запись изменилась после разметки: проверьте границы и сохраните заново.";
            }

            ApplyAnnotation(existing ?? NewAnnotation(log));
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
        Raise(nameof(Target));
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
            .. Junctions.Where(j => j.IsPlaced)
                .Select(j => new Junction(j.After, j.Kind, j.End!.Value, j.Start!.Value)),
        ];
        ImmutableArray<DeferredMark> deferred =
        [
            .. Deferred.OrderBy(d => d.Stroke).Select(d => new DeferredMark(d.Stroke, d.Letter, d.Kind, d.Timing)),
        ];
        ImmutableArray<string> flags = [.. Flags.Where(f => f.IsChecked).Select(f => f.Name)];
        return new AnnotationFile(
            AnnotationFormat.Schema,
            penlogName,
            _target,
            _annotator.Trim(),
            _now().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            _text,
            new QualityMark(_usable, flags, string.IsNullOrWhiteSpace(_note) ? null : _note.Trim()),
            _counts,
            junctions,
            deferred);
    }

    /// <summary>Записывает файл разметки рядом с записью. Черновик с ошибками тоже сохраняется (помечается в списке как «начата»).</summary>
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
            AnnotationJson.Save(CorpusStore.AnnotationPathFor(_path), BuildAnnotation());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "Не удалось сохранить разметку: " + ex.Message;
            return false;
        }

        _staleCounts = null;
        IsDirty = false;
        RefreshSelectedEntry();
        Status = IsValid ? "Разметка сохранена." : "Разметка сохранена как черновик: есть ошибки (см. список).";
        return true;
    }

    /// <summary>Клик по следу в координатах линейки (<c>u</c> вправо, <c>v</c> вверх, в x-height).</summary>
    public void HandleClick(double u, double v)
    {
        if (_attempt is null)
        {
            return;
        }

        HashSet<int> deferred = [.. Deferred.Select(d => d.Stroke)];
        (StrokePoint Point, double Distance)? hit = SceneBuilder.Nearest(
            _strokes,
            _attempt.Guides,
            u,
            v,
            _tool == AnnotateTool.Boundary ? s => !deferred.Contains(s) : null);
        if (hit is null || hit.Value.Distance > MaxHitDistance)
        {
            Status = "Клик мимо следа.";
            return;
        }

        if (_tool == AnnotateTool.Deferred)
        {
            ToggleDeferred(hit.Value.Point.Stroke);
        }
        else
        {
            PlaceSelectedJunction(hit.Value.Point);
        }
    }

    /// <summary>Выбирает стык для установки границы (-1 — снять выбор).</summary>
    public void SelectJunction(int after)
    {
        foreach (JunctionRow row in Junctions)
        {
            row.IsSelected = row.After == after;
        }

        Tool = AnnotateTool.Boundary;
    }

    /// <summary>Ставит выбранный стык в точку <paramref name="point"/> (для «отрыва» — привязывает к концу штриха).</summary>
    public void PlaceSelectedJunction(StrokePoint point)
    {
        JunctionRow? row = Junctions.FirstOrDefault(j => j.IsSelected);
        if (row is null)
        {
            Status = _text.Length < 2 ? "Сначала введите, что написано (минимум две буквы для стыков)." : "Выберите стык в списке слева.";
            return;
        }

        if (!TryResolve(row.Kind, point, out StrokePoint end, out StrokePoint start, out string? problem))
        {
            Status = problem!;
            return;
        }

        row.End = end;
        row.Start = start;
        Changed();

        JunctionRow? next = Junctions.FirstOrDefault(j => !j.IsPlaced && j.After != row.After);
        SelectJunction(next?.After ?? row.After);
    }

    /// <summary>Меняет вид стыка и перепривязывает поставленную границу.</summary>
    public void SetKind(int after, JunctionKind kind)
    {
        JunctionRow? row = Junctions.FirstOrDefault(j => j.After == after);
        if (row is null || row.Kind == kind)
        {
            return;
        }

        row.Kind = kind;
        if (row.End is { } end)
        {
            if (TryResolve(kind, end, out StrokePoint newEnd, out StrokePoint newStart, out string? problem))
            {
                row.End = newEnd;
                row.Start = newStart;
            }
            else
            {
                // Поставить в новом виде нельзя: граница сбрасывается, чтобы не остаться неверной.
                row.End = null;
                row.Start = null;
                Status = problem!;
            }
        }

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
            Deferred.Add(new DeferredRow(stroke, letter, KindOf(letter), DeferredTiming.AfterWord, timingOverridden: false));
        }

        Changed();
    }

    /// <summary>Снимает отметку отложенного штриха.</summary>
    public void RemoveDeferred(int stroke)
    {
        DeferredRow? existing = Deferred.FirstOrDefault(d => d.Stroke == stroke);
        if (existing is not null)
        {
            Deferred.Remove(existing);
            Changed();
        }
    }

    /// <summary>Переключает «сразу / после слова» у отложенного штриха (ручное значение больше не пересчитывается).</summary>
    public void ToggleTiming(int stroke)
    {
        DeferredRow? row = Deferred.FirstOrDefault(d => d.Stroke == stroke);
        if (row is null)
        {
            return;
        }

        row.Timing = row.Timing == DeferredTiming.Immediate ? DeferredTiming.AfterWord : DeferredTiming.Immediate;
        row.TimingOverridden = true;
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
        int target = index < 0 ? (delta > 0 ? 0 : Files.Count - 1) : ((index + delta) % Files.Count + Files.Count) % Files.Count;
        SelectedEntry = Files[target];
    }

    /// <summary>Переходит к следующей записи без полной разметки.</summary>
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
            if (candidate.ReadError is null && candidate.Annotation is AnnotationState.None or AnnotationState.Draft)
            {
                SelectedEntry = candidate;
                return;
            }
        }

        Status = "Все записи размечены.";
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

    private static DeferredPartKind KindOf(char letter) => char.ToLowerInvariant(letter) switch
    {
        'i' or 'j' => DeferredPartKind.Dot,
        't' or 'f' => DeferredPartKind.Crossbar,
        _ => DeferredPartKind.Other,
    };

    private DeferredPartKind KindOf(int letter) => letter >= 0 && letter < _text.Length ? KindOf(_text[letter]) : DeferredPartKind.Other;

    /// <summary>Начальная разметка: текст из цели записи, годна, без стыков.</summary>
    private static AnnotationFile NewAnnotation(PenLog log)
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

        return new AnnotationFile(AnnotationFormat.Schema, string.Empty, target, string.Empty, null, text, QualityMark.Good, [], [], []);
    }

    private void ApplyAnnotation(AnnotationFile annotation)
    {
        _text = annotation.Text;
        _usable = annotation.Quality.Usable;
        _note = annotation.Quality.Note ?? string.Empty;
        if (annotation.Annotator.Length > 0)
        {
            _annotator = annotation.Annotator;
        }

        foreach (FlagItem flag in Flags)
        {
            flag.IsChecked = annotation.Quality.Flags.Contains(flag.Name);
        }

        RebuildJunctionSlots();
        foreach (Junction j in annotation.Junctions)
        {
            JunctionRow? row = Junctions.FirstOrDefault(r => r.After == j.After);
            if (row is not null)
            {
                row.Kind = j.Kind;
                row.End = j.End;
                row.Start = j.Start;
            }
        }

        Deferred.Clear();
        foreach (DeferredMark d in annotation.Deferred.OrderBy(d => d.Stroke))
        {
            Deferred.Add(new DeferredRow(d.Stroke, d.Letter, d.Kind, d.Timing, timingOverridden: true));
        }

        _selectedLetter = 0;
        JunctionRow? first = Junctions.FirstOrDefault(j => !j.IsPlaced) ?? Junctions.FirstOrDefault();
        SelectJunction(first?.After ?? -1);
        Raise(nameof(Text));
        Raise(nameof(IsUsable));
        Raise(nameof(Note));
        Raise(nameof(Annotator));
        Raise(nameof(SelectedLetter));
    }

    private void RebuildJunctionSlots()
    {
        var old = Junctions.ToDictionary(j => j.After);
        Junctions.Clear();
        for (int i = 0; i < _text.Length - 1; i++)
        {
            JunctionKind kind = old.TryGetValue(i, out JunctionRow? previous) ? previous.Kind : _defaultKind;
            var row = new JunctionRow(i, SceneBuilder.Label(_text, i), kind);
            if (previous is not null)
            {
                row.End = previous.End;
                row.Start = previous.Start;
                row.IsSelected = previous.IsSelected;
            }

            Junctions.Add(row);
        }

        if (!Junctions.Any(j => j.IsSelected) && Junctions.Count > 0)
        {
            Junctions.First().IsSelected = true;
        }
    }

    private bool TryResolve(JunctionKind kind, StrokePoint point, out StrokePoint end, out StrokePoint start, out string? problem)
    {
        end = point;
        start = point;
        problem = null;
        if (kind == JunctionKind.Connected)
        {
            return true;
        }

        // Отрыв: левая буква кончается в последней точке штриха, правая начинается с первой точки следующего основного штриха.
        HashSet<int> deferred = [.. Deferred.Select(d => d.Stroke)];
        int next = AnnotationValidator.NextMainStroke(point.Stroke, _counts, deferred);
        if (next < 0)
        {
            problem = "После этого штриха нет основного штриха: отрыв поставить некуда.";
            return false;
        }

        end = new StrokePoint(point.Stroke, _counts[point.Stroke] - 1);
        start = new StrokePoint(next, 0);
        return true;
    }

    private void OnFlagChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FlagItem.IsChecked))
        {
            Changed();
        }
    }

    /// <summary>Что-то изменилось в разметке: пересчёт времени отложенных, проверка, сцена, пометка «не сохранено».</summary>
    private void Changed()
    {
        if (_isLoading)
        {
            return;
        }

        RecomputeAutomaticTimings();
        IsDirty = true;
        Revalidate();
        RebuildScene();
    }

    private void RecomputeAutomaticTimings()
    {
        // Сразу, если после этого штриха есть основной (не отложенный) штрих; иначе — после слова.
        HashSet<int> deferred = [.. Deferred.Select(d => d.Stroke)];
        foreach (DeferredRow row in Deferred.Where(d => !d.TimingOverridden))
        {
            row.Timing = AnnotationValidator.NextMainStroke(row.Stroke, _counts, deferred) >= 0 ? DeferredTiming.Immediate : DeferredTiming.AfterWord;
        }
    }

    private void Revalidate()
    {
        if (_path is null)
        {
            return;
        }

        Issues.Clear();
        AnnotationFile current = BuildAnnotation();
        if (_staleCounts is { } stale)
        {
            current = current with { StrokeSamples = stale };
        }

        IReadOnlyList<AnnotationIssue> issues = AnnotationValidator.Validate(current, _counts);
        foreach (AnnotationIssue issue in issues)
        {
            Issues.Add(issue);
        }

        IsValid = AnnotationValidator.IsValid(issues);
    }

    private void RebuildScene()
    {
        if (_attempt is null)
        {
            Scene = InkScene.Empty with { Guides = _guides };
            return;
        }

        AnnotationFile annotation = BuildAnnotation();
        Scene = SceneBuilder.FromAnnotation(_strokes, _attempt.Guides, _guides, _text, annotation.Junctions, annotation.Deferred);
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

        (AnnotationState state, int errors) = CorpusIndex.AnnotationStateOf(_path, _log);
        CorpusEntry updated = _selectedEntry with { Annotation = state, ErrorCount = errors };
        _selectedEntry = updated;
        Files[index] = updated;
        Raise(nameof(SelectedEntry));
    }
}
