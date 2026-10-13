using System.Collections.Immutable;
using System.ComponentModel;
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

    /// <summary>Ребёнок пишет попытку.</summary>
    Writing,

    /// <summary>Попытка оценивается в фоне.</summary>
    Evaluating,

    /// <summary>Показана одна подсказка с наложением; ждём «Дальше».</summary>
    Feedback,

    /// <summary>Урок закончен.</summary>
    Finished,
}

/// <summary>Почему анимация показывается снова.</summary>
public enum ReplayReason
{
    /// <summary>Кнопка «Показать ещё».</summary>
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
/// <param name="Clock">Монотонные часы для пересчёта времени живого источника; <c>null</c> для воспроизведения.</param>
/// <param name="TimeBase">Основа времени точек источника.</param>
/// <param name="Device">Описание устройства для записи попытки.</param>
public sealed record LessonRuntime(
    Func<Func<EvaluationResult>, Task<EvaluationResult>> RunBackground,
    IMonotonicClock? Clock,
    PenTimeBase TimeBase,
    PenDeviceInfo Device)
{
    private static readonly PenDeviceInfo UnknownDevice = new("unknown", PenSourceKind.Replay, null, null, null, false, PenTimestampKind.Qpc);

    /// <summary>Живой источник: оценка в пуле потоков, время точек — абсолютные тики часов.</summary>
    public static LessonRuntime Live(IMonotonicClock clock, PenDeviceInfo? device) =>
        new(work => Task.Run(work), clock ?? throw new ArgumentNullException(nameof(clock)), PenTimeBase.MonotonicAbsolute, device ?? UnknownDevice);

    /// <summary>Воспроизведение или тест: оценка синхронно (≤ 5 мс по заданию V0.7), время точек уже от начала задания.</summary>
    public static LessonRuntime Inline(PenDeviceInfo? device = null) =>
        new(work => Task.FromResult(work()), null, PenTimeBase.TaskRelative, device ?? UnknownDevice);
}

/// <summary>
/// Состояние и логика фиксированного урока V0.8 для одной буквы (a, i или t): E01 (показ) → E03 ×3 (обводка) → E04 ×2 (копия).
/// Без окна: ввод приходит через <see cref="OnSamples"/> (живой источник или <see cref="ReplayPenSource"/>), оценка идёт в фоне
/// (<see cref="LessonRuntime.RunBackground"/>), после каждой попытки показывается одна подсказка (<see cref="MiniFeedback"/>) и ждётся «Дальше».
/// Все тексты берутся из каталога выбранного языка по ключам.
/// </summary>
public sealed class LessonViewModel : INotifyPropertyChanged
{
    private readonly LessonContent _content;
    private readonly LessonRuntime _runtime;
    private readonly List<AttemptOutcome> _outcomes = [];

    private string _language;
    private string _letter;
    private LessonPhase _phase;
    private int _stepIndex;
    private int _attemptInStep;
    private HintHistory _history = HintHistory.Empty;
    private StrokeAssembler _assembler = new();
    private AttemptTimeRebaser? _rebaser;
    private ImmutableArray<InkEvent>.Builder _events = ImmutableArray.CreateBuilder<InkEvent>();
    private int _restarts;
    private int _cues;
    private int _raisedAidsLeft;
    private bool _aidActive;
    private bool _demoRequested;
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

    /// <summary>Началась попытка письма (шаг, номер попытки в шаге; повторная попытка после U0 — тот же номер).</summary>
    public event Action<LessonStep, int>? AttemptStarted;

    /// <summary>В попытке нажато «Заново» (холст очищен, попытка продолжается).</summary>
    public event Action? AttemptRestarted;

    /// <summary>Попытка принята кнопкой «Готово» (до оценки): запись пера закрывается.</summary>
    public event Action? AttemptFinished;

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

    /// <summary>Сколько попыток письма всего в сценарии.</summary>
    public int TotalAttempts => _content.Config.Steps.Sum(s => s.Attempts);

    /// <summary>Сколько попыток засчитано.</summary>
    public int CompletedAttempts => _outcomes.Count(o => o.Plan.CountsAsAttempt);

    /// <summary>Оценённые попытки по порядку (включая повторные после U0).</summary>
    public IReadOnlyList<AttemptOutcome> Outcomes => _outcomes;

    /// <summary>История подсказок.</summary>
    public HintHistory History => _history;

    /// <summary>План обратной связи последней попытки в фазе <see cref="LessonPhase.Feedback"/>.</summary>
    public FeedbackPlan? Plan => _plan;

    /// <summary>Подсветка на эталоне для текущей обратной связи.</summary>
    public ResolvedHighlight? Highlight => _highlight;

    /// <summary>Подсветка рисуется на эталоне под пером (линейка письма), иначе — на эталоне рядом (линейка показа).</summary>
    public bool HighlightOnWritingFrame => ModelUnderPen;

    /// <summary>Сколько раз подан мягкий сигнал за всё время урока.</summary>
    public int LiveCueSerial { get; private set; }

    /// <summary>Точка старта эталона (линейка), на которую указывал последний сигнал.</summary>
    public Vec2? LiveCuePoint { get; private set; }

    /// <summary>Сколько раз нажато «Заново» в текущей попытке.</summary>
    public int Restarts => _restarts;

    /// <summary>Чернила сейчас принимаются (идёт попытка).</summary>
    public bool AcceptsInk => _phase == LessonPhase.Writing;

    /// <summary>Эталон бледно под пером на линейке письма: обводка или добавленная опора.</summary>
    public bool ModelUnderPen =>
        _phase is LessonPhase.Writing or LessonPhase.Evaluating or LessonPhase.Feedback
        && (Step.Kind == StepKind.Trace || (Step.Kind == StepKind.Copy && _aidActive));

    /// <summary>Эталон рядом (на линейке показа): копия.</summary>
    public bool ModelBeside => (_phase is LessonPhase.Writing or LessonPhase.Evaluating or LessonPhase.Feedback) && Step.Kind == StepKind.Copy;

    /// <summary>Слой анимации виден: показ, копия, либо по просьбе (кнопка, подсказка).</summary>
    public bool DemoVisible => _phase == LessonPhase.Demo || ModelBeside || _demoRequested;

    /// <summary>Можно ли показать анимацию ещё раз.</summary>
    public bool CanShowAgain => _phase is LessonPhase.Demo or LessonPhase.Writing or LessonPhase.Feedback;

    /// <summary>«Заново» доступно, пока идёт попытка (перо опущено — решает окно).</summary>
    public bool CanRestart => _phase == LessonPhase.Writing;

    /// <summary>«Готово» доступно, пока идёт попытка.</summary>
    public bool CanDone => _phase == LessonPhase.Writing;

    /// <summary>«Дальше» доступно после показа и после обратной связи.</summary>
    public bool CanNext => _phase is LessonPhase.Demo or LessonPhase.Feedback;

    /// <summary>Букву можно сменить: в начале урока и после его конца.</summary>
    public bool CanSelectLetter => (_phase == LessonPhase.Demo && _stepIndex == 0) || _phase == LessonPhase.Finished;

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

            return Step.Kind switch
            {
                StepKind.Demo => Text("ui.step.demo"),
                StepKind.Trace => Text("ui.step.trace", ("n", Number(_attemptInStep)), ("total", Number(Step.Attempts))),
                _ => Text(_aidActive ? "ui.step.copyAided" : "ui.step.copy", ("n", Number(_attemptInStep)), ("total", Number(Step.Attempts))),
            };
        }
    }

    /// <summary>Подсказка (одна) или нейтральное сообщение.</summary>
    public string HintText
    {
        get
        {
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

    /// <summary>Строка из каталога выбранного языка.</summary>
    public string T(string key) => Text(key);

    /// <summary>Переключает язык RU ↔ EN.</summary>
    public void ToggleLanguage()
    {
        _language = string.Equals(_language, "ru", StringComparison.Ordinal) ? "en" : "ru";
        Changed();
    }

    /// <summary>Задаёт полотно: линейка показа (слева) и письма (справа). Смена линейки при начатой попытке отменяет её.</summary>
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

    /// <summary>Линейка показа.</summary>
    public GuideFrame? DemoFrame => _demoFrame;

    /// <summary>Линейка письма.</summary>
    public GuideFrame? WritingFrame => _writingFrame;

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

    /// <summary>Начинает урок сначала (история подсказок и журнал очищаются).</summary>
    public void RestartLesson()
    {
        _generation++;
        _history = HintHistory.Empty;
        _outcomes.Clear();
        _raisedAidsLeft = 0;
        _aidActive = false;
        _demoRequested = false;
        LiveCueSerial = 0;
        LiveCuePoint = null;
        EnterStep(0);
        InkClearRequested?.Invoke();
        Changed();
    }

    /// <summary>Кнопка «Показать ещё» / «Покажи ещё раз».</summary>
    public void ShowAgain()
    {
        if (!CanShowAgain)
        {
            return;
        }

        RequestReplay(ReplayReason.Button);
        Changed();
    }

    /// <summary>Точки пера (поверхность, физические px). Принимаются только во время попытки.</summary>
    public void OnSamples(ReadOnlySpan<PenSample> samples)
    {
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
            if (firstDown)
            {
                CheckLiveStart(sample);
            }
        }
    }

    /// <summary>
    /// «Заново» (A10, §7.6): холст очищен, попытка продолжается, штрафа нет. Второе нажатие подряд повторяет анимацию с мягким
    /// «давай посмотрим ещё раз».
    /// </summary>
    public void Restart()
    {
        if (!CanRestart)
        {
            return;
        }

        _restarts++;
        long now = _events.Count > 0 ? _events[^1].TimeUs : 0;
        _events.Add(new InkEvent(now, InkEvent.Names.RestartButton));
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
    /// «Готово»: оценка попытки в фоне, затем одна подсказка. Пустую попытку не оценивает (подсказка «сначала напиши»).
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
            EvaluationResult result = await _runtime.RunBackground(() =>
                evaluator.Evaluate(new LetterEvaluationRequest(ink, glyph, _content.Style, ToleranceProfile.Learning))).ConfigureAwait(true);
            summary = result.Summary;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // §4.1: исключение в оценщике — попытка без оценки, ребёнок видит «спасибо, дальше».
            LastError = ex.GetType().Name + ": " + ex.Message;
        }

        if (generation != _generation)
        {
            return; // урок перезапущен, пока шла оценка
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
        _phase = LessonPhase.Feedback;
        if (plan.Action.HasFlag(FeedbackAction.AddAid) && Step.Kind == StepKind.Copy)
        {
            _raisedAidsLeft = _content.Config.Feedback.RaisedAidsAttempts;
        }

        if (plan.Action.HasFlag(FeedbackAction.ReplayAnimation))
        {
            RequestReplay(plan.Reason == FeedbackReason.Overload ? ReplayReason.Overload : ReplayReason.Hint);
        }

        Changed();
    }

    /// <summary>Последняя ошибка оценщика (для журнала), если была.</summary>
    public string? LastError { get; private set; }

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
        else if (_attemptInStep >= Step.Attempts)
        {
            EnterStep(_stepIndex + 1);
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
            _aidActive = Step.Kind == StepKind.Copy && _raisedAidsLeft > 0;
            if (_aidActive)
            {
                _raisedAidsLeft--;
            }
        }

        ResetInk();
        _cues = 0;
        _plan = null;
        _highlight = null;
        _noticeKey = null;
        _demoRequested = false;
        _phase = LessonPhase.Writing;
        AttemptStarted?.Invoke(Step, _attemptInStep);
    }

    private void ResetInk()
    {
        _assembler = new StrokeAssembler();
        _events = ImmutableArray.CreateBuilder<InkEvent>();
        _restarts = 0;
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
        if (!cue.Enabled || !Step.LiveCue || _cues >= cue.MaxPerAttempt || _writingFrame is not { } frame)
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
