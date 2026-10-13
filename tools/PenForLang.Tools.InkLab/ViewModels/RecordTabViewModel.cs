using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using PenForLang.Core;
using PenForLang.Ink;
using PenForLang.Tools.InkLab.Corpus;
using PenForLang.Tools.InkLab.Mvvm;
using PenForLang.Tools.InkLab.Protocol;
using PenForLang.Tools.InkLab.Recording;

namespace PenForLang.Tools.InkLab.ViewModels;

/// <summary>Строка списка подсказок протокола.</summary>
public sealed class PromptItem : ViewModelBase
{
    private PromptStatus _status;
    private bool _isCurrent;

    /// <summary>Создаёт строку.</summary>
    public PromptItem(int index, RecordingPrompt prompt, PromptStatus status)
    {
        Index = index;
        Prompt = prompt;
        _status = status;
    }

    /// <summary>Номер в протоколе, с 0.</summary>
    public int Index { get; }

    /// <summary>Подсказка.</summary>
    public RecordingPrompt Prompt { get; }

    /// <summary>Заголовок строки: «at · с отрывом · 3/10».</summary>
    public string Title =>
        string.Create(CultureInfo.InvariantCulture, $"{Prompt.Text}{VariantText(Prompt.Variant)}  {Prompt.Number}/{Prompt.Total}");

    /// <summary>Состояние.</summary>
    public PromptStatus Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value))
            {
                Raise(nameof(Mark));
            }
        }
    }

    /// <summary>Это текущая подсказка.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set => Set(ref _isCurrent, value);
    }

    /// <summary>Значок состояния: ✓ записано, – пропущено, пусто — ждёт.</summary>
    public string Mark => _status switch
    {
        PromptStatus.Done => "✓",
        PromptStatus.Skipped => "–",
        _ => " ",
    };

    private static string VariantText(string? variant) => variant switch
    {
        PromptVariants.Lift => " · с отрывом",
        PromptVariants.Crossbar => " · перекладина сразу",
        _ => string.Empty,
    };
}

/// <summary>
/// Вкладка «Запись» без окна: протокол записи, метаданные субъекта, кнопки «Готово / Заново / Пропустить / Назад», запись
/// penlog каждой попытки в папку корпуса и сохранение прогресса. Перо подключается через <see cref="Attach"/> (в тестах —
/// <c>FakePenInputSource</c>); холст, эталон и линейки — забота представления (события <see cref="ClearInkRequested"/> и
/// <see cref="ReferenceRequested"/>, свойство <see cref="Context"/>).
/// </summary>
public sealed class RecordTabViewModel : ViewModelBase
{
    private readonly Func<DateTimeOffset> _now;
    private readonly AttemptCapture _capture;
    private readonly RelayCommand _startCommand;
    private readonly RelayCommand _doneCommand;
    private readonly RelayCommand _redoCommand;
    private readonly RelayCommand _skipCommand;
    private readonly RelayCommand _backCommand;
    private readonly RelayCommand _referenceCommand;
    private readonly RelayCommand _endSessionCommand;

    private IPenInputSource? _source;
    private CorpusStore? _store;
    private ProtocolSession? _session;
    private SubjectInfo? _subject;
    private PenLog? _unsaved;
    private bool _stimulusOpen;
    private bool _isInputEnabled;

    private string _corpusRoot;
    private string _subjectId = string.Empty;
    private bool _leftHanded;
    private string _ageText = string.Empty;
    private string _consentText = string.Empty;
    private string _message = "Введите идентификатор субъекта и начните запись.";
    private RecordingContext? _context;

    /// <summary>Создаёт вкладку.</summary>
    /// <param name="corpusRoot">Корень корпуса (папка записи; создаётся при первой записи).</param>
    /// <param name="clock">Монотонные часы источника пера.</param>
    /// <param name="now">Календарное время для имён файлов.</param>
    /// <param name="protocol">Протокол; <c>null</c> — протокол V0.9 по умолчанию.</param>
    public RecordTabViewModel(string corpusRoot, IMonotonicClock clock, Func<DateTimeOffset> now, RecordingProtocol? protocol = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusRoot);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(now);
        _corpusRoot = corpusRoot;
        _now = now;
        Protocol = protocol ?? RecordingProtocol.CreateDefault();
        _capture = new AttemptCapture(clock);

        _startCommand = new RelayCommand(StartSession, () => !IsSessionActive);
        _doneCommand = new RelayCommand(Done, () => IsSessionActive && !IsComplete);
        _redoCommand = new RelayCommand(Redo, () => IsSessionActive && !IsComplete);
        _skipCommand = new RelayCommand(Skip, () => IsSessionActive && !IsComplete);
        _backCommand = new RelayCommand(Back, () => IsSessionActive && _session is { CurrentIndex: not 0 });
        _referenceCommand = new RelayCommand(RequestReference, () => IsSessionActive && !IsComplete);
        _endSessionCommand = new RelayCommand(EndSession, () => IsSessionActive);
    }

    /// <summary>Нужно очистить холст (новая подсказка или «Заново»).</summary>
    public event Action? ClearInkRequested;

    /// <summary>Нужно показать экранный эталон подсказки.</summary>
    public event Action<RecordingPrompt>? ReferenceRequested;

    /// <summary>Протокол записи.</summary>
    public RecordingProtocol Protocol { get; }

    /// <summary>Строки списка подсказок.</summary>
    public ObservableCollection<PromptItem> Prompts { get; } = [];

    /// <summary>Идентификаторы субъектов, уже имеющих папку с прогрессом в корне корпуса.</summary>
    public ObservableCollection<string> KnownSubjects { get; } = [];

    /// <summary>Команда «Начать / продолжить запись».</summary>
    public RelayCommand StartCommand => _startCommand;

    /// <summary>Команда «Готово».</summary>
    public RelayCommand DoneCommand => _doneCommand;

    /// <summary>Команда «Заново».</summary>
    public RelayCommand RedoCommand => _redoCommand;

    /// <summary>Команда «Пропустить».</summary>
    public RelayCommand SkipCommand => _skipCommand;

    /// <summary>Команда «Назад».</summary>
    public RelayCommand BackCommand => _backCommand;

    /// <summary>Команда «Показать эталон».</summary>
    public RelayCommand ReferenceCommand => _referenceCommand;

    /// <summary>Команда «Сменить субъекта».</summary>
    public RelayCommand EndSessionCommand => _endSessionCommand;

    /// <summary>Корень корпуса (меняется до начала записи).</summary>
    public string CorpusRoot
    {
        get => _corpusRoot;
        set
        {
            if (!IsSessionActive && !string.IsNullOrWhiteSpace(value))
            {
                Set(ref _corpusRoot, value);
                RefreshKnownSubjects();
            }
        }
    }

    /// <summary>Идентификатор субъекта-взрослого (без имени).</summary>
    public string SubjectId
    {
        get => _subjectId;
        set => Set(ref _subjectId, value ?? string.Empty);
    }

    /// <summary>Ведущая рука — левая.</summary>
    public bool IsLeftHanded
    {
        get => _leftHanded;
        set => Set(ref _leftHanded, value);
    }

    /// <summary>Возраст, лет (необязательно, текстом).</summary>
    public string AgeText
    {
        get => _ageText;
        set => Set(ref _ageText, value ?? string.Empty);
    }

    /// <summary>Номер согласия (необязательно: для взрослого согласие не требуется, поле — для учёта).</summary>
    public string ConsentText
    {
        get => _consentText;
        set => Set(ref _consentText, value ?? string.Empty);
    }

    /// <summary>Сообщение для записывающего.</summary>
    public string Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    /// <summary>Окружение записи (устройство, экран, линейка); задаёт представление, когда линейка построена.</summary>
    public RecordingContext? Context
    {
        get => _context;
        set => Set(ref _context, value);
    }

    /// <summary>Вкладка видима и принимает перо.</summary>
    public bool IsInputEnabled
    {
        get => _isInputEnabled;
        set => Set(ref _isInputEnabled, value);
    }

    /// <summary>Запись идёт (субъект выбран, протокол загружен).</summary>
    public bool IsSessionActive => _session is not null;

    /// <summary>Поля субъекта можно менять (запись ещё не начата).</summary>
    public bool CanEditSubject => _session is null;

    /// <summary>Протокол пройден: незаписанных и непропущенных подсказок нет.</summary>
    public bool IsComplete => _session is { IsComplete: true };

    /// <summary>Текущая подсказка.</summary>
    public RecordingPrompt? CurrentPrompt => _session?.Current;

    /// <summary>Текущая подсказка текстом для крупного показа.</summary>
    public string PromptText => CurrentPrompt is { } p ? p.Text : (IsComplete ? "Готово" : string.Empty);

    /// <summary>Указание записывающему для текущей подсказки.</summary>
    public string InstructionText => CurrentPrompt?.Instruction ?? string.Empty;

    /// <summary>Прогресс: «Записано 37 из 110 · пропущено 2».</summary>
    public string ProgressText => _session is null
        ? string.Empty
        : string.Create(
            CultureInfo.InvariantCulture,
            $"Записано {_session.DoneCount} из {Protocol.Prompts.Length} · пропущено {_session.SkippedCount}");

    /// <summary>Хотя бы одна точка контакта в текущей попытке.</summary>
    public bool HasInk => _capture.ContactSamples > 0;

    /// <summary>Перо касается холста.</summary>
    public bool IsPenDown => _capture.IsPenDown;

    /// <summary>Точек контакта в текущей попытке.</summary>
    public int ContactSamples => _capture.ContactSamples;

    /// <summary>Сколько раз нажато «Заново» в текущей попытке.</summary>
    public int Restarts => _capture.Restarts;

    /// <summary>Путь последней записанной попытки (для сообщений и тестов).</summary>
    public string? LastWrittenPath { get; private set; }

    /// <summary>Подписывается на источник пера (живые чернила подписываются раньше — в представлении).</summary>
    public void Attach(IPenInputSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Detach();
        _source = source;
        source.SamplesReceived += OnSamplesReceived;
    }

    /// <summary>Отписывается от источника пера.</summary>
    public void Detach()
    {
        if (_source is not null)
        {
            _source.SamplesReceived -= OnSamplesReceived;
            _source = null;
        }
    }

    /// <summary>Принимает пачку точек в текущую попытку (если вкладка активна и запись идёт).</summary>
    public void OnSamples(ReadOnlySpan<PenSample> samples)
    {
        if (!_isInputEnabled || _session is null || !_capture.IsActive || samples.IsEmpty)
        {
            return;
        }

        bool hadInk = HasInk;
        bool wasDown = IsPenDown;

        // Первое касание во время показа: показ считается законченным (событие stimulus-end).
        if (_stimulusOpen && ContainsDown(samples))
        {
            EndStimulus();
        }

        _capture.AddSamples(samples);
        if (hadInk != HasInk)
        {
            Raise(nameof(HasInk));
        }

        if (wasDown != IsPenDown)
        {
            Raise(nameof(IsPenDown));
        }

        Raise(nameof(ContactSamples));
    }

    /// <summary>Представление начало показ эталона: событие <c>stimulus-start</c> (предыдущий показ закрывается).</summary>
    public void ReferenceStarted()
    {
        if (_session?.Current is not { } prompt || !_capture.IsActive)
        {
            return;
        }

        EndStimulus();
        _stimulusOpen = true;
        var data = new Dictionary<string, string> { ["key"] = prompt.StimulusKey };
        if (prompt.Variant is not null)
        {
            data["variant"] = prompt.Variant;
        }

        _capture.RecordEvent(InkEvent.Names.StimulusStart, data);
    }

    /// <summary>Показ эталона закончился (доиграл или прерван): событие <c>stimulus-end</c>, если показ был открыт.</summary>
    public void ReferenceEnded() => EndStimulus();

    /// <summary>Обновляет список субъектов с прогрессом в корне корпуса.</summary>
    public void RefreshKnownSubjects()
    {
        KnownSubjects.Clear();
        try
        {
            if (!Directory.Exists(_corpusRoot))
            {
                return;
            }

            foreach (string directory in Directory.EnumerateDirectories(_corpusRoot).Order(StringComparer.OrdinalIgnoreCase))
            {
                string progress = Path.Combine(directory, CorpusStore.ProgressFileName);
                if (File.Exists(progress))
                {
                    KnownSubjects.Add(Path.GetFileName(directory));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = "Не удалось прочитать папку корпуса: " + ex.Message;
        }
    }

    /// <summary>Начинает запись или продолжает прошлую: читает прогресс субъекта и переходит к первой незаписанной подсказке.</summary>
    public void StartSession()
    {
        if (IsSessionActive)
        {
            return;
        }

        if (!SubjectInfo.IsValidId(_subjectId))
        {
            Message = "Укажите идентификатор субъекта (до 40 символов, без имени).";
            return;
        }

        int? age = null;
        if (_ageText.Trim().Length > 0)
        {
            if (!int.TryParse(_ageText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int parsedAge) || parsedAge is < 1 or > 120)
            {
                Message = "Возраст — целое число лет от 1 до 120 (или оставьте пустым).";
                return;
            }

            age = parsedAge;
        }

        int? consent = null;
        if (_consentText.Trim().Length > 0)
        {
            if (!int.TryParse(_consentText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int parsedConsent))
            {
                Message = "Номер согласия — целое число (или оставьте пустым: взрослому согласие не нужно).";
                return;
            }

            consent = parsedConsent;
        }

        var subject = new SubjectInfo(_subjectId.Trim(), _leftHanded ? SubjectInfo.Left : SubjectInfo.Right, age, consent);
        try
        {
            var store = new CorpusStore(_corpusRoot);
            ProtocolProgress progress = store.LoadProgress(subject, Protocol.Id);
            _store = store;
            _subject = subject;
            _session = new ProtocolSession(Protocol, progress);
            store.SaveProgress(progress);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            _store = null;
            _subject = null;
            _session = null;
            Message = "Не удалось начать запись: " + ex.Message;
            return;
        }

        RebuildPromptItems();
        string resumed = _session.DoneCount > 0 || _session.SkippedCount > 0 ? " Продолжаем с места остановки." : string.Empty;
        BeginPrompt($"Запись субъекта «{subject.Id}».{resumed}");
        RaiseSessionChanged();
    }

    /// <summary>Заканчивает запись этого субъекта (прогресс сохранён) — можно выбрать другого.</summary>
    public void EndSession()
    {
        if (!IsSessionActive)
        {
            return;
        }

        EndStimulus();
        _capture.Discard();
        _unsaved = null;
        _session = null;
        _store = null;
        _subject = null;
        Prompts.Clear();
        ClearInkRequested?.Invoke();
        Message = "Запись остановлена. Прогресс сохранён: можно продолжить позже.";
        RefreshKnownSubjects();
        RaiseSessionChanged();
    }

    /// <summary>«Готово»: попытка записывается отдельным penlog, прогресс сохраняется, показывается следующая подсказка.</summary>
    public void Done()
    {
        if (_session is not { Current: { } prompt } session || _store is null || _subject is null)
        {
            return;
        }

        if (_unsaved is null)
        {
            if (_capture.IsPenDown)
            {
                Message = "Сначала поднимите перо.";
                return;
            }

            if (_capture.ContactSamples == 0)
            {
                Message = "На холсте нет штрихов: напишите, затем нажмите «Готово» (или «Пропустить»).";
                return;
            }

            if (_context is null)
            {
                Message = "Линейка холста ещё не построена: подождите секунду и повторите «Готово».";
                return;
            }

            EndStimulus();
            PenLogHeader header = RecordingHeaders.Build(_context, _subject, Protocol.Id, prompt);
            _unsaved = _capture.Complete(header);
        }

        if (_unsaved is null)
        {
            Message = "На холсте нет штрихов.";
            return;
        }

        PromptProgress entry = session.Progress.Entry(prompt.Id);
        string? previous = entry.Status == PromptStatus.Done && entry.File is not null
            ? Path.Combine(_store.SubjectDirectory(_subject), entry.File)
            : null;
        int restarts = _unsaved.Entries.OfType<PenLogEventEntry>().Count(e => e.Event.Name == InkEvent.Names.RestartButton);
        try
        {
            string stem = prompt.Id + "-" + _now().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string path = _store.WritePenLog(_subject, stem, _unsaved);
            LastWrittenPath = path;
            string relative = Path.GetRelativePath(_store.SubjectDirectory(_subject), path).Replace('\\', '/');
            _unsaved = null;
            session.MarkDone(relative, restarts);
            _store.SaveProgress(session.Progress);
            if (previous is not null && File.Exists(previous))
            {
                CorpusStore.Supersede(previous);
            }

            RefreshItems();
            BeginPrompt("Сохранено: " + Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Данные не потеряны: попытка остаётся в памяти, повторное «Готово» запишет её снова.
            Message = "Не удалось сохранить запись: " + ex.Message + " Исправьте причину и снова нажмите «Готово».";
        }
    }

    /// <summary>«Заново»: холст очищается, попытка продолжается (в penlog — событие <c>restart-button</c>). Неактивна, пока перо опущено.</summary>
    public void Redo()
    {
        if (_session is not { Current: not null } || !_capture.IsActive)
        {
            return;
        }

        if (_capture.IsPenDown)
        {
            Message = "Поднимите перо, затем «Заново».";
            return;
        }

        _capture.Restart();
        ClearInkRequested?.Invoke();
        Message = "Холст очищен. Пишите ещё раз.";
        Raise(nameof(HasInk));
        Raise(nameof(Restarts));
    }

    /// <summary>«Пропустить»: подсказка помечается пропущенной, показывается следующая.</summary>
    public void Skip()
    {
        if (_session is null || _session.Current is null)
        {
            return;
        }

        _session.Skip();
        SaveProgressSafely();
        RefreshItems();
        BeginPrompt("Подсказка пропущена.");
    }

    /// <summary>«Назад»: возврат к предыдущей подсказке (её можно записать заново — прежняя запись уйдёт в <c>superseded</c>).</summary>
    public void Back()
    {
        if (_session is null || !_session.Back())
        {
            return;
        }

        SaveProgressSafely();
        RefreshItems();
        BeginPrompt("Предыдущая подсказка.");
    }

    /// <summary>Переход к подсказке из списка.</summary>
    public void GoTo(int index)
    {
        if (_session is null || !_session.GoTo(index))
        {
            return;
        }

        SaveProgressSafely();
        RefreshItems();
        BeginPrompt("Подсказка выбрана из списка.");
    }

    /// <summary>Просит показать экранный эталон текущей подсказки.</summary>
    public void RequestReference()
    {
        if (_session?.Current is { } prompt)
        {
            ReferenceRequested?.Invoke(prompt);
        }
    }

    private static bool ContainsDown(ReadOnlySpan<PenSample> samples)
    {
        foreach (PenSample sample in samples)
        {
            if (sample.Phase == PenPhase.Down)
            {
                return true;
            }
        }

        return false;
    }

    private void OnSamplesReceived(ReadOnlyMemory<PenSample> samples) => OnSamples(samples.Span);

    private void EndStimulus()
    {
        if (!_stimulusOpen)
        {
            return;
        }

        _stimulusOpen = false;
        _capture.RecordEvent(InkEvent.Names.StimulusEnd);
    }

    private void BeginPrompt(string message)
    {
        _unsaved = null;
        _stimulusOpen = false;
        _capture.Discard();
        if (_session?.Current is { } prompt)
        {
            _capture.Begin();
            Message = message;
            ClearInkRequested?.Invoke();
            ReferenceRequested?.Invoke(prompt);
        }
        else
        {
            ClearInkRequested?.Invoke();
            Message = "Протокол пройден. Спасибо! Пропущенные подсказки можно записать, выбрав их в списке.";
        }

        RaisePromptState();
    }

    private void SaveProgressSafely()
    {
        if (_store is null || _session is null)
        {
            return;
        }

        try
        {
            _store.SaveProgress(_session.Progress);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = "Не удалось сохранить прогресс: " + ex.Message;
        }
    }

    private void RebuildPromptItems()
    {
        Prompts.Clear();
        if (_session is null)
        {
            return;
        }

        for (int i = 0; i < Protocol.Prompts.Length; i++)
        {
            Prompts.Add(new PromptItem(i, Protocol.Prompts[i], _session.StatusAt(i)));
        }

        RefreshItems();
    }

    private void RefreshItems()
    {
        if (_session is null)
        {
            return;
        }

        foreach (PromptItem item in Prompts)
        {
            item.Status = _session.StatusAt(item.Index);
            item.IsCurrent = item.Index == _session.CurrentIndex;
        }
    }

    private void RaisePromptState()
    {
        RefreshItems();
        Raise(nameof(CurrentPrompt));
        Raise(nameof(PromptText));
        Raise(nameof(InstructionText));
        Raise(nameof(ProgressText));
        Raise(nameof(IsComplete));
        Raise(nameof(HasInk));
        Raise(nameof(IsPenDown));
        Raise(nameof(ContactSamples));
        Raise(nameof(Restarts));
        RaiseCommands();
    }

    private void RaiseSessionChanged()
    {
        Raise(nameof(IsSessionActive));
        Raise(nameof(CanEditSubject));
        RaisePromptState();
    }

    private void RaiseCommands()
    {
        _startCommand.RaiseCanExecuteChanged();
        _doneCommand.RaiseCanExecuteChanged();
        _redoCommand.RaiseCanExecuteChanged();
        _skipCommand.RaiseCanExecuteChanged();
        _backCommand.RaiseCanExecuteChanged();
        _referenceCommand.RaiseCanExecuteChanged();
        _endSessionCommand.RaiseCanExecuteChanged();
    }
}
