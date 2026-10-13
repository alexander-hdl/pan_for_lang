using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using PenForLang.Core;
using PenForLang.Core.Geometry;
using PenForLang.Evaluation;
using PenForLang.Glyphs;
using PenForLang.Ink;

namespace PenForLang.App.Wpf.Lesson;

/// <summary>Фаза урока.</summary>
public enum LessonPhase
{
    /// <summary>Показ (E01): анимация, письма нет.</summary>
    Demo,

    /// <summary>Письмо по памяти (E04): эталон виден (вспышка), перо не принимается.</summary>
    Look,

    /// <summary>Письмо по памяти: эталон скрыт, пауза, видны только линейки, перо не принимается.</summary>
    Delay,

    /// <summary>Ребёнок пишет попытку.</summary>
    Writing,

    /// <summary>Попытка оценивается в фоне.</summary>
    Evaluating,

    /// <summary>Показана одна подсказка с наложением; ждём «Дальше» (касание пером строки или пауза у молчаливой попытки тоже ведут дальше).</summary>
    Feedback,

    /// <summary>Урок закончен.</summary>
    Finished,
}

/// <summary>Почему анимация показывается снова.</summary>
public enum ReplayReason
{
    /// <summary>Автоповтор показа (E01 проигрывается сам несколько раз).</summary>
    Auto,

    /// <summary>Кнопка «Покажи ещё раз».</summary>
    Button,

    /// <summary>Действие подсказки (процессная ошибка).</summary>
    Hint,

    /// <summary>Смена способа после повторов одной подсказки.</summary>
    Overload,

    /// <summary>Два «Заново» подряд в одной попытке (METHODOLOGY.md §7.6, ARCHITECTURE.md §7.6).</summary>
    Restarts,
}

/// <summary>Итог одной оценённой попытки (для журнала, dev-оверлея и тестов).</summary>
/// <param name="StepCode">Код упражнения шага.</param>
/// <param name="AttemptNo">Номер попытки в шаге.</param>
/// <param name="Summary">Результат оценки или <c>null</c>, если оценщик не отработал.</param>
/// <param name="Plan">План обратной связи.</param>
public sealed record AttemptOutcome(string StepCode, int AttemptNo, EvaluationSummary? Summary, FeedbackPlan Plan);

/// <summary>Среда выполнения урока: где считать оценку, часы и время источника.</summary>
/// <param name="RunBackground">Выполняет оценку вне потока UI; результат возвращается в контекст вызова (UI-поток в приложении).</param>
/// <param name="Clock">Монотонные часы: время фаз (вспышка, пауза, автопереход) и пересчёт времени живого источника; <c>null</c> — системные.</param>
/// <param name="TimeBase">Основа времени точек источника.</param>
/// <param name="Device">Описание устройства для записи попытки.</param>
/// <param name="EvaluationTimeout">Сколько ждать оценку; <c>null</c> — по конфигурации.</param>
public sealed record LessonRuntime(
    Func<Func<EvaluationResult>, Task<EvaluationResult>> RunBackground,
    IMonotonicClock? Clock,
    PenTimeBase TimeBase,
    PenDeviceInfo Device,
    TimeSpan? EvaluationTimeout = null)
{
    private static readonly PenDeviceInfo UnknownDevice = new("unknown", PenSourceKind.Replay, null, null, null, false, PenTimestampKind.Qpc);

    /// <summary>Живой источник: оценка в пуле потоков, время точек — абсолютные тики часов.</summary>
    public static LessonRuntime Live(IMonotonicClock clock, PenDeviceInfo? device) =>
        new(work => Task.Run(work), clock ?? throw new ArgumentNullException(nameof(clock)), PenTimeBase.MonotonicAbsolute, device ?? UnknownDevice);

    /// <summary>Воспроизведение или тест: оценка синхронно (≤ 5 мс по заданию V0.7), время точек уже от начала задания.</summary>
    public static LessonRuntime Inline(PenDeviceInfo? device = null, IMonotonicClock? clock = null) =>
        new(work => Task.FromResult(work()), clock, PenTimeBase.TaskRelative, device ?? UnknownDevice);
}

/// <summary>
/// Состояние и логика фиксированного урока V0.8 для одной буквы (a, i или t) по методике §4.1, §4.2, §6.2: E01 показ (L4) →
/// E02 обводка (L4, 1–2 попытки) → E03 копия ×3 (L3, эталон над строкой) → E04 по памяти ×2 (L2, затем L1: вспышка эталона, скрытие,
/// пауза, письмо). Без окна: ввод приходит через <see cref="OnSamples"/> (живой источник или <see cref="ReplayPenSource"/>), время фаз —
/// через <see cref="Tick"/>, оценка идёт в фоне (<see cref="LessonRuntime.RunBackground"/>), после каждой попытки показывается одна
/// подсказка (<see cref="MiniFeedback"/>). Все тексты берутся из каталога выбранного языка по ключам.
/// </summary>
public sealed class LessonViewModel : INotifyPropertyChanged
{
    private readonly LessonContent _content;
    private readonly LessonRuntime _runtime;
    private readonly List<AttemptOutcome> _outcomes = [];
    private readonly int[] _stepDone;

    private string _language;
    private string _letter;
    private LessonPhase _phase;
    private int _stepIndex;
    private int _attemptInStep;
    private HintHistory _history = HintHistory.Empty;
    private StrokeAssembler _assembler = new();
    private AttemptTimeRebaser? _rebaser;
    private ImmutableArray<InkEvent>.Builder _events = ImmutableArray.CreateBuilder<InkEvent>();
    private long _lastSampleUs;
    private int _restarts;
    private int _cues;
    private int _raisedAidsLeft;
    private bool _aidActive;
    private bool _attemptAided;
    private bool _repeatPending;
    private readonly int[] _memoryRepeats;
    private bool _demoRequested;
    private int _demoFinished;
    private int _autoRepeatsLeft;
    private int _correctInStep;
    private bool _lastHadProcessError;
    private bool _autoAdvance;
    private double _phaseStartMs;
    private int _generation;
    private GuideFrame? _writingFrame;
    private GuideFrame? _demoFrame;
    private FeedbackPlan? _plan;
    private ResolvedHighlight? _highlight;
    private string? _noticeKey;

    /// <summary>Создаёт урок.</summary>
    /// <param name="content">Контент урока.</param>
    /// <param name="runtime">Среда выполнения.</param>
    /// <param name="language">Язык интерфейса (<c>ru</c>, <c>en</c>).</param>
    /// <param name="letter">Буква: a, i или t.</param>
    public LessonViewModel(LessonContent content, LessonRuntime runtime, string language = "ru", string letter = "a")
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(runtime);
        _content = content;
        _runtime = runtime;
        _language = Normalize(language);
        _letter = letter;
        _content.GetGlyph(letter); // нет такой буквы — сразу ошибка
        _stepDone = new int[content.Config.Steps.Length];
        _memoryRepeats = new int[content.Config.Steps.Length];
        EnterStep(0);
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Анимацию буквы нужно показать (снова).</summary>
    public event Action<ReplayReason>? ReplayRequested;

    /// <summary>Чернила на экране нужно стереть.</summary>
    public event Action? InkClearRequested;

    /// <summary>Мягкий сигнал неверного старта: точка старта (линейка) мигает.</summary>
    public event Action<Vec2>? LiveCueRaised;

    /// <summary>Началась попытка (шаг, номер попытки в шаге; повторная попытка после U0 — тот же номер).</summary>
    public event Action<LessonStep, int>? AttemptStarted;

    /// <summary>В попытке нажато «Заново» (холст очищен, попытка продолжается).</summary>
    public event Action? AttemptRestarted;

    /// <summary>Попытка принята кнопкой «Готово» (до оценки): запись пера закрывается.</summary>
    public event Action? AttemptFinished;

    /// <summary>
    /// В письме по памяти нажато «Покажи ещё раз»: попытка помечена «с опорой» (в penlog — событие <c>aid-shown</c>, причина — <c>button</c>,
    /// режим <c>copy-aided</c>). Она не засчитывается как попытка по памяти.
    /// </summary>
    public event Action<string>? AidShown;

    /// <summary>Эталон по памяти показан (<c>true</c>) или скрыт (<c>false</c>): события <c>stimulus-start</c> и <c>stimulus-end</c> в penlog.</summary>
    public event Action<bool>? StimulusChanged;

    /// <summary>Буквы урока.</summary>
    public static IReadOnlyList<string> Letters => SandboxContent.Letters;

    /// <summary>Выбранная буква.</summary>
    public string Letter => _letter;

    /// <summary>Язык интерфейса.</summary>
    public string Language => _language;

    /// <summary>Фаза.</summary>
    public LessonPhase Phase => _phase;

    /// <summary>Шаги сценария.</summary>
    public IReadOnlyList<LessonStep> Steps => _content.Config.Steps;

    /// <summary>Индекс текущего шага.</summary>
    public int StepIndex => _stepIndex;

    /// <summary>Текущий шаг; после конца урока — последний.</summary>
    public LessonStep Step => _content.Config.Steps[Math.Min(_stepIndex, _content.Config.Steps.Length - 1)];

    /// <summary>Номер попытки в шаге, с 1; у показа — 0.</summary>
    public int AttemptInStep => _attemptInStep;

    /// <summary>
    /// Сколько попыток письма в сценарии с учётом пропусков: у законченных шагов — сколько было, у текущего — с учётом второй попытки обводки
    /// (она нужна, только если в первой была процессная ошибка), у будущих — наименьшее число.
    /// </summary>
    public int TotalAttempts
    {
        get
        {
            int total = 0;
            ImmutableArray<LessonStep> steps = _content.Config.Steps;
            for (int i = 0; i < steps.Length; i++)
            {
                LessonStep step = steps[i];
                if (step.Attempts == 0)
                {
                    continue;
                }

                if (_phase == LessonPhase.Finished || i < _stepIndex)
                {
                    total += _stepDone[i];
                }
                else if (i == _stepIndex)
                {
                    bool inProgress = _phase is LessonPhase.Look or LessonPhase.Delay or LessonPhase.Writing or LessonPhase.Evaluating
                        || _plan is { CountsAsAttempt: false }
                        || _repeatPending;
                    int need = Math.Max(step.MinAttempts, _stepDone[i] + (inProgress ? 1 : 0));
                    if (!inProgress && step.Kind == StepKind.Trace && _lastHadProcessError && _stepDone[i] < step.Attempts)
                    {
                        need = Math.Max(need, _stepDone[i] + 1);
                    }

                    total += Math.Min(need, step.Attempts);
                }
                else
                {
                    total += step.MinAttempts;
                }
            }

            return total;
        }
    }

    /// <summary>Сколько попыток засчитано.</summary>
    public int CompletedAttempts => _stepDone.Sum();

    /// <summary>Оценённые попытки по порядку (включая повторные после U0).</summary>
    public IReadOnlyList<AttemptOutcome> Outcomes => _outcomes;

    /// <summary>История подсказок.</summary>
    public HintHistory History => _history;

    /// <summary>План обратной связи последней попытки в фазе <see cref="LessonPhase.Feedback"/>.</summary>
    public FeedbackPlan? Plan => _plan;

    /// <summary>Подсветка на эталоне для текущей обратной связи.</summary>
    public ResolvedHighlight? Highlight => _highlight;

    /// <summary>Подсветка рисуется на эталоне под пером (линейка письма), иначе — на эталоне над строкой (линейка эталона).</summary>
    public bool HighlightOnWritingFrame => ModelUnderPen;

    /// <summary>Сколько раз подан мягкий сигнал за всё время урока.</summary>
    public int LiveCueSerial { get; private set; }

    /// <summary>Точка старта эталона (линейка), на которую указывал последний сигнал.</summary>
    public Vec2? LiveCuePoint { get; private set; }

    /// <summary>Сколько раз нажато «Заново» в текущей попытке.</summary>
    public int Restarts => _restarts;

    /// <summary>Чернила сейчас принимаются (идёт попытка; вспышка эталона и пауза — нет).</summary>
    public bool AcceptsInk => _phase == LessonPhase.Writing;

    /// <summary>Эталон бледно под пером на линейке письма: обводка или добавленная опора копии.</summary>
    public bool ModelUnderPen =>
        InAttemptView && (Step.Kind == StepKind.Trace || (Step.Kind == StepKind.Copy && _aidActive));

    /// <summary>
    /// Эталон над строкой (линейка эталона): копия; вспышка в письме по памяти и сравнение после попытки; письмо по памяти с добавленной
    /// опорой (следующая попытка как копия).
    /// </summary>
    public bool ModelAbove =>
        _phase == LessonPhase.Look
        || (InAttemptView && (Step.Kind == StepKind.Copy || (Step.Kind == StepKind.Memory && (MemoryAided || _phase != LessonPhase.Writing))));

    /// <summary>Попытка по памяти идёт с опорой: добавлена при перегрузе или нажата «Покажи ещё раз».</summary>
    public bool MemoryAided => Step.Kind == StepKind.Memory && (_aidActive || _attemptAided);

    /// <summary>Точка старта на строке письма (письмо по памяти на L2: на строке видна только она).</summary>
    public bool StartDotOnWritingRow => _phase == LessonPhase.Writing && Step.Kind == StepKind.Memory && Step.StartDot && !MemoryAided;

    /// <summary>Стрелки на эталоне видны (в копии они скрываются после <see cref="LessonStep.HideArrowsAfterCorrect"/> верных попыток).</summary>
    public bool ArrowsVisible =>
        !(Step.Kind == StepKind.Copy && Step.HideArrowsAfterCorrect > 0 && _correctInStep >= Step.HideArrowsAfterCorrect);

    /// <summary>Слой анимации виден: показ, эталон над строкой либо по просьбе (кнопка, подсказка).</summary>
    public bool DemoVisible => _phase == LessonPhase.Demo || ModelAbove || _demoRequested;

    /// <summary>Можно ли показать анимацию ещё раз.</summary>
    public bool CanShowAgain =>
        _phase is LessonPhase.Demo or LessonPhase.Writing or LessonPhase.Feedback
        || (Step.Kind == StepKind.Memory && _phase is LessonPhase.Look or LessonPhase.Delay);

    /// <summary>«Заново» доступно, пока идёт попытка (перо опущено — решает окно).</summary>
    public bool CanRestart => _phase == LessonPhase.Writing;

    /// <summary>«Готово» доступно, пока идёт попытка.</summary>
    public bool CanDone => _phase == LessonPhase.Writing;

    /// <summary>«Дальше» доступно после первого полного показа и после обратной связи.</summary>
    public bool CanNext => (_phase == LessonPhase.Demo && _demoFinished >= 1) || _phase == LessonPhase.Feedback;

    /// <summary>Букву можно сменить: в начале урока и после его конца.</summary>
    public bool CanSelectLetter => (_phase == LessonPhase.Demo && _stepIndex == 0) || _phase == LessonPhase.Finished;

    /// <summary>Последняя ошибка оценщика (для журнала), если была.</summary>
    public string? LastError { get; private set; }

    /// <summary>Линейка эталона (над строкой).</summary>
    public GuideFrame? DemoFrame => _demoFrame;

    /// <summary>Линейка письма.</summary>
    public GuideFrame? WritingFrame => _writingFrame;

    /// <summary>Заголовок урока.</summary>
    public string Title => Text("ui.letter", ("letter", _letter));

    /// <summary>Что делать на этом шаге.</summary>
    public string InstructionText
    {
        get
        {
            if (_phase == LessonPhase.Finished)
            {
                return Text("ui.finished");
            }

            switch (Step.Kind)
            {
                case StepKind.Demo:
                    return Text("ui.step.demo");
                case StepKind.Trace:
                    return Text("ui.step.trace");
                case StepKind.Copy:
                    return Text("ui.step.copy", ("n", Number(_attemptInStep)), ("total", Number(Step.Attempts)));
                default:
                    if (_phase is LessonPhase.Look or LessonPhase.Delay)
                    {
                        return Text("ui.step.memoryLook");
                    }

                    if (_phase is LessonPhase.Evaluating or LessonPhase.Feedback)
                    {
                        return Text("ui.step.memoryCompare");
                    }

                    return Text(MemoryAided ? "ui.step.memoryAided" : (Step.StartDot ? "ui.step.memoryWriteDot" : "ui.step.memoryWrite"));
            }
        }
    }

    /// <summary>
    /// Подсказка (одна) или нейтральное сообщение; на показе — словесная метка буквы из меток сегментов глифа (<c>a.rollUp</c>,
    /// <c>a.backAround</c> …).
    /// </summary>
    public string HintText
    {
        get
        {
            if (_phase == LessonPhase.Demo)
            {
                return _content.Phrase(_letter, _language);
            }

            if (_phase == LessonPhase.Evaluating)
            {
                return Text("ui.evaluating");
            }

            if (_noticeKey is not null)
            {
                return Text(_noticeKey);
            }

            if (_phase != LessonPhase.Feedback || _plan is null)
            {
                return string.Empty;
            }

            if (_plan.HintKey is { } key)
            {
                return Text(key, _plan.Parameters.Select(p => (p.Key, Text("part." + p.Value))).ToArray());
            }

            return _plan.PraiseKey is null ? Text("ui.accepted") : string.Empty;
        }
    }

    /// <summary>Похвала процесса (одна) или пусто.</summary>
    public string PraiseText => _phase == LessonPhase.Feedback && _plan?.PraiseKey is { } key ? Text(key) : string.Empty;

    private bool InAttemptView => _phase is LessonPhase.Writing or LessonPhase.Evaluating or LessonPhase.Feedback;

    /// <summary>Строка из каталога выбранного языка.</summary>
    public string T(string key) => Text(key);

    /// <summary>Переключает язык RU ↔ EN.</summary>
    public void ToggleLanguage()
    {
        _language = string.Equals(_language, "ru", StringComparison.Ordinal) ? "en" : "ru";
        Changed();
    }

    /// <summary>Задаёт полотно: линейка эталона (над строкой) и письма. Смена линейки при начатой попытке отменяет её.</summary>
    public void SetFrames(GuideFrame demo, GuideFrame writing)
    {
        ArgumentNullException.ThrowIfNull(demo);
        ArgumentNullException.ThrowIfNull(writing);
        bool moved = _writingFrame is not null && !Equals(_writingFrame, writing);
        _demoFrame = demo;
        _writingFrame = writing;
        if (moved && _phase == LessonPhase.Writing && _assembler.StrokeCount > 0)
        {
            ResetInk();
            InkClearRequested?.Invoke();
        }

        Changed();
    }

    /// <summary>Выбирает букву и начинает урок сначала.</summary>
    public void SelectLetter(string letter)
    {
        ArgumentException.ThrowIfNullOrEmpty(letter);
        if (!CanSelectLetter || string.Equals(letter, _letter, StringComparison.Ordinal))
        {
            return;
        }

        _content.GetGlyph(letter);
        _letter = letter;
        RestartLesson();
    }

    /// <summary>Начинает урок сначала (история подсказок и журнал очищаются; идущая оценка отбрасывается).</summary>
    public void RestartLesson()
    {
        _generation++;
        _history = HintHistory.Empty;
        _outcomes.Clear();
        Array.Clear(_stepDone);
        Array.Clear(_memoryRepeats);
        _repeatPending = false;
        _raisedAidsLeft = 0;
        _aidActive = false;
        _demoRequested = false;
        _lastHadProcessError = false;
        LiveCueSerial = 0;
        LiveCuePoint = null;
        EnterStep(0);
        InkClearRequested?.Invoke();
        Changed();
    }

    /// <summary>Кнопка «Покажи ещё раз».</summary>
    public void ShowAgain()
    {
        if (!CanShowAgain)
        {
            return;
        }

        RequestReplay(ReplayReason.Button);
        if (Step.Kind == StepKind.Memory && !MemoryAided && _phase is LessonPhase.Look or LessonPhase.Delay or LessonPhase.Writing)
        {
            // Методика §6.2: кнопка доступна в любом задании, но память этой попытки не проверена: попытка «с опорой».
            _attemptAided = true;
            AidShown?.Invoke("button");
        }

        Changed();
    }

    /// <summary>
    /// Анимация показа доиграла до конца (вызывает окно). Показ проигрывается сам несколько раз (<see cref="LessonStep.AutoPlays"/>);
    /// «Дальше» открывается после первого полного показа.
    /// </summary>
    public void DemoPlaybackFinished()
    {
        if (_phase != LessonPhase.Demo)
        {
            return;
        }

        _demoFinished++;
        if (_autoRepeatsLeft > 0)
        {
            _autoRepeatsLeft--;
            RequestReplay(ReplayReason.Auto);
        }

        Changed();
    }

    /// <summary>
    /// Ход времени (окно вызывает по таймеру): вспышка эталона → скрытие → пауза → письмо; молчаливая попытка переходит дальше сама.
    /// </summary>
    public void Tick()
    {
        bool changed = false;
        while (true)
        {
            double elapsed = NowMs() - _phaseStartMs;
            if (_phase == LessonPhase.Look && elapsed >= Step.FlashMs)
            {
                _phase = LessonPhase.Delay;
                _phaseStartMs = NowMs();
                StimulusChanged?.Invoke(false);
                changed = true;
            }
            else if (_phase == LessonPhase.Delay && elapsed >= Step.DelayMs)
            {
                _phase = LessonPhase.Writing;
                changed = true;
            }
            else if (_phase == LessonPhase.Feedback && _autoAdvance && elapsed >= _content.Config.Feedback.AutoAdvanceMs)
            {
                Next();
                return;
            }
            else
            {
                break;
            }
        }

        if (changed)
        {
            Changed();
        }
    }

    /// <summary>
    /// Касание пером строки в фазе обратной связи ведёт дальше: первый штрих сразу попадает в новую попытку. Окно вызывает до отрисовки
    /// пачки; <see cref="OnSamples"/> вызывает то же сам.
    /// </summary>
    public void BeginTouch(ReadOnlySpan<PenSample> samples)
    {
        if (_phase != LessonPhase.Feedback || !CanNext)
        {
            return;
        }

        foreach (PenSample sample in samples)
        {
            if (sample.Phase == PenPhase.Down && !sample.IsAuxiliary)
            {
                Next();
                return;
            }
        }
    }

    /// <summary>Точки пера (поверхность, физические px). Принимаются только во время попытки.</summary>
    public void OnSamples(ReadOnlySpan<PenSample> samples)
    {
        BeginTouch(samples);
        if (_phase != LessonPhase.Writing)
        {
            return;
        }

        foreach (PenSample original in samples)
        {
            PenSample sample = original;
            if (_rebaser is not null && !_rebaser.TryRebase(original, out sample))
            {
                continue;
            }

            bool firstDown = sample.Phase == PenPhase.Down && _assembler.StrokeCount == 0 && !sample.IsAuxiliary;
            _assembler.Add(sample);
            _lastSampleUs = Math.Max(_lastSampleUs, sample.TimeUs);
            if (firstDown)
            {
                CheckLiveStart(sample);
            }
        }
    }

    /// <summary>
    /// «Заново» (A10, §7.6): холст очищен, попытка продолжается, штрафа нет. Второе нажатие подряд повторяет анимацию с мягким
    /// «давай посмотрим ещё раз». Событие несёт время последней точки попытки.
    /// </summary>
    public void Restart()
    {
        if (!CanRestart)
        {
            return;
        }

        _restarts++;
        _events.Add(new InkEvent(_lastSampleUs, InkEvent.Names.RestartButton));
        _assembler = new StrokeAssembler();
        _noticeKey = null;
        if (_restarts >= _content.Config.Feedback.RestartsBeforeReplay)
        {
            _noticeKey = FeedbackKeys.LookAgain;
            RequestReplay(ReplayReason.Restarts);
        }

        InkClearRequested?.Invoke();
        AttemptRestarted?.Invoke();
        Changed();
    }

    /// <summary>
    /// «Готово»: оценка попытки в фоне (не дольше таймаута), затем одна подсказка. Пустую попытку не оценивает (подсказка «сначала напиши»).
    /// </summary>
    public async Task DoneAsync()
    {
        if (_phase != LessonPhase.Writing)
        {
            return;
        }

        if (_assembler.StrokeCount == 0)
        {
            _noticeKey = "ui.writeFirst";
            Changed();
            return;
        }

        GuideFrame frame = _writingFrame ?? throw new InvalidOperationException("The writing frame is not set.");
        AttemptFinished?.Invoke();
        InkAttempt attempt = new(
            _assembler.ToStrokes(),
            frame,
            _runtime.Device,
            null,
            InkCompletion.DoneButton,
            _restarts,
            _events.ToImmutable());
        RulerInk ink = RulerProjection.Project(attempt);
        Glyph glyph = _content.GetGlyph(_letter);
        int generation = ++_generation;
        _phase = LessonPhase.Evaluating;
        _noticeKey = null;
        Changed();

        EvaluationSummary? summary = null;
        try
        {
            ILetterEvaluator evaluator = _content.Evaluator;
            TimeSpan timeout = _runtime.EvaluationTimeout ?? TimeSpan.FromMilliseconds(_content.Config.Feedback.EvaluationTimeoutMs);
            EvaluationResult result = await _runtime
                .RunBackground(() => evaluator.Evaluate(new LetterEvaluationRequest(ink, glyph, _content.Style, ToleranceProfile.Learning)))
                .WaitAsync(timeout)
                .ConfigureAwait(true);
            summary = result.Summary;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // §4.1: исключение или слишком долгая оценка — попытка без оценки, ребёнок видит «спасибо, дальше».
            LastError = ex.GetType().Name + ": " + ex.Message;
        }

        if (generation != _generation)
        {
            return; // урок перезапущен, пока шла оценка: устаревший результат отброшен
        }

        var context = new FeedbackContext(_letter, Step.Code, Step.Level, ElementStatus.Learning, _attemptInStep, _history, _content.Config.Feedback);
        FeedbackPlan plan = summary is null
            ? new FeedbackPlan(
                FeedbackKeys.Next, null, null, null, FeedbackAction.None, FeedbackTone.Neutral,
                CountsAsAttempt: true, FeedbackReason.EvaluationFailed, ImmutableDictionary<string, string>.Empty)
            : MiniFeedback.Plan(summary, context);
        _history = _history.Add(MiniFeedback.Record(plan, context));
        _outcomes.Add(new AttemptOutcome(Step.Code, _attemptInStep, summary, plan));
        _plan = plan;
        _highlight = plan.Highlight is { } spec ? TemplateGeometry.Resolve(glyph, spec, plan.Tone) : null;
        _lastHadProcessError = summary is not null && summary.Findings.Any(f =>
            f.Kind == FindingKind.Error && _content.Config.Feedback.ProcessCodes.Contains(f.Code));
        bool aidedMemory = Step.Kind == StepKind.Memory && _attemptAided && !_aidActive;
        _repeatPending = false;
        if (aidedMemory && plan.CountsAsAttempt)
        {
            // Не засчитывается как попытка по памяти: после обратной связи тот же уровень повторяется один раз, затем идём дальше.
            _repeatPending = _memoryRepeats[_stepIndex] < 1;
        }
        else if (plan.CountsAsAttempt)
        {
            _stepDone[_stepIndex]++;
            if (summary is not null && plan.HintKey is null && plan.Reason is FeedbackReason.Praise or FeedbackReason.Clean)
            {
                _correctInStep++;
            }
        }

        _autoAdvance = !plan.HasMessage && plan.CountsAsAttempt;
        _phaseStartMs = NowMs();
        _phase = LessonPhase.Feedback;
        if (plan.Action.HasFlag(FeedbackAction.AddAid) && Step.Kind is StepKind.Copy or StepKind.Memory)
        {
            _raisedAidsLeft = _content.Config.Feedback.RaisedAidsAttempts;
        }

        if (plan.Action.HasFlag(FeedbackAction.ReplayAnimation))
        {
            RequestReplay(plan.Reason == FeedbackReason.Overload ? ReplayReason.Overload : ReplayReason.Hint);
        }

        Changed();
    }

    /// <summary>«Дальше»: следующая попытка, следующий шаг или конец урока.</summary>
    public void Next()
    {
        if (!CanNext)
        {
            return;
        }

        if (_phase == LessonPhase.Demo)
        {
            EnterStep(_stepIndex + 1);
        }
        else if (_plan is { CountsAsAttempt: false })
        {
            BeginAttempt(isRetry: true); // U0: одна повторная попытка в том же номере
        }
        else if (_repeatPending)
        {
            _repeatPending = false;
            _memoryRepeats[_stepIndex]++;
            BeginAttempt(isRetry: false); // тот же уровень ещё раз
        }
        else if (_attemptInStep >= Step.Attempts
            || (Step.Kind == StepKind.Trace && _attemptInStep >= Step.MinAttempts && !_lastHadProcessError))
        {
            EnterStep(_stepIndex + 1); // обводка: вторая попытка — только после процессной ошибки в первой
        }
        else
        {
            _attemptInStep++;
            BeginAttempt(isRetry: false);
        }

        InkClearRequested?.Invoke();
        Changed();
    }

    private void EnterStep(int index)
    {
        _stepIndex = index;
        _demoRequested = false;
        _correctInStep = 0;
        _lastHadProcessError = false;
        if (index >= _content.Config.Steps.Length)
        {
            _phase = LessonPhase.Finished;
            _plan = null;
            _highlight = null;
            _noticeKey = null;
            return;
        }

        LessonStep step = _content.Config.Steps[index];
        if (step.Kind == StepKind.Demo)
        {
            _phase = LessonPhase.Demo;
            _attemptInStep = 0;
            _demoFinished = 0;
            _autoRepeatsLeft = Math.Max(0, step.AutoPlays - 1);
            _plan = null;
            _highlight = null;
            _noticeKey = null;
            return;
        }

        _attemptInStep = 1;
        BeginAttempt(isRetry: false);
    }

    private void BeginAttempt(bool isRetry)
    {
        if (!isRetry)
        {
            _aidActive = Step.Kind is StepKind.Copy or StepKind.Memory && _raisedAidsLeft > 0;
            if (_aidActive)
            {
                _raisedAidsLeft--;
            }
        }

        ResetInk();
        _attemptAided = false;
        _repeatPending = false;
        _cues = 0;
        _plan = null;
        _highlight = null;
        _noticeKey = null;
        _demoRequested = false;
        _autoAdvance = false;
        AttemptStarted?.Invoke(Step, _attemptInStep);
        if (Step.Kind == StepKind.Memory && !_aidActive)
        {
            _phase = LessonPhase.Look; // эталон виден FlashMs, затем скрыт и пауза DelayMs; перо не принимается
            _phaseStartMs = NowMs();
            StimulusChanged?.Invoke(true);
        }
        else
        {
            _phase = LessonPhase.Writing;
        }
    }

    private void ResetInk()
    {
        _assembler = new StrokeAssembler();
        _events = ImmutableArray.CreateBuilder<InkEvent>();
        _restarts = 0;
        _lastSampleUs = 0;
        _rebaser = _runtime.TimeBase == PenTimeBase.MonotonicAbsolute && _runtime.Clock is { } clock ? AttemptTimeRebaser.Start(clock) : null;
    }

    private void RequestReplay(ReplayReason reason)
    {
        _demoRequested = true;
        ReplayRequested?.Invoke(reason);
    }

    private void CheckLiveStart(PenSample sample)
    {
        LiveCueSettings cue = _content.Config.LiveCue;
        if (!cue.Enabled || !Step.LiveCue || MemoryAided || _aidActive || _cues >= cue.MaxPerAttempt || _writingFrame is not { } frame)
        {
            return;
        }

        (double u, double v) = RulerProjection.ToRuler(frame, sample.X, sample.Y);
        Glyph glyph = _content.GetGlyph(_letter);
        if (!LiveStartCheck.IsWrongStart(glyph, new Vec2(u, v), cue.StartClearance))
        {
            return;
        }

        _cues++;
        LiveCueSerial++;
        Vec2 start = TemplateGeometry.StartPoint(glyph);
        LiveCuePoint = start;
        LiveCueRaised?.Invoke(start);
    }

    private double NowMs()
    {
        if (_runtime.Clock is { } clock)
        {
            return MonotonicTime.ToMicroseconds(clock.Ticks, clock.Frequency) / 1000.0;
        }

        return Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
    }

    private string Text(string key, params (string Name, string Value)[] parameters)
    {
        LocaleCatalog catalog = _content.Locale(_language);
        return parameters.Length == 0
            ? catalog.Get(key)
            : catalog.Get(key, parameters.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal));
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Normalize(string language) =>
        LessonContent.Languages.Contains(language, StringComparer.Ordinal) ? language : LessonContent.Languages[0];

    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
