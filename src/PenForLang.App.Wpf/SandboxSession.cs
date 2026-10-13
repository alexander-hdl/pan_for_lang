using System.Globalization;
using PenForLang.Core;
using PenForLang.Ink;

namespace PenForLang.App.Wpf;

/// <summary>
/// Состояние «урока-песочницы» V0.6 без окна: выбранная буква, номер попытки, счётчик «Заново» и запись penlog каждой попытки
/// (ARCHITECTURE.md §7.6). Попытка заканчивается кнопкой «Готово» или сменой буквы; «Заново» остаётся внутри той же попытки
/// (событие <c>restart-button</c>, <c>Restarts + 1</c>, штрафа нет). Запись ведётся только в режиме разработчика и только для
/// взрослого/разработчика (<c>subject.kind = adult</c>, <c>anon = dev</c>): детские данные без согласия не пишутся (§3.4, §11.2).
/// </summary>
public sealed class SandboxSession : IDisposable
{
    private readonly AttemptRecorder? _recorder;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<string, int, PenLogHeader?> _headerProvider;
    private readonly List<RecordedAttempt> _recorded = [];
    private string _letter;

    /// <summary>Создаёт сеанс.</summary>
    /// <param name="clock">Монотонные часы (QPC).</param>
    /// <param name="penLogDirectory">Папка penlog; <c>null</c> — не записывать (режим не dev).</param>
    /// <param name="now">Календарное время для имён файлов.</param>
    /// <param name="headerProvider">Заголовок penlog для (буква, номер попытки); <c>null</c>, пока линейка не известна.</param>
    /// <param name="initialLetter">Буква, с которой начинается сеанс.</param>
    public SandboxSession(
        IMonotonicClock clock,
        string? penLogDirectory,
        Func<DateTimeOffset> now,
        Func<string, int, PenLogHeader?> headerProvider,
        string initialLetter)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(now);
        ArgumentNullException.ThrowIfNull(headerProvider);
        ArgumentException.ThrowIfNullOrEmpty(initialLetter);
        _recorder = penLogDirectory is null ? null : new AttemptRecorder(penLogDirectory, clock);
        _now = now;
        _headerProvider = headerProvider;
        _letter = initialLetter;
        AttemptNumber = 1;
        BeginAttempt();
    }

    /// <summary>Выбранная буква.</summary>
    public string Letter => _letter;

    /// <summary>Номер текущей попытки с 1 (растёт после «Готово»).</summary>
    public int AttemptNumber { get; private set; }

    /// <summary>Сколько раз нажато «Заново» в текущей попытке.</summary>
    public int Restarts { get; private set; }

    /// <summary>Файл текущей попытки, если уже есть касания.</summary>
    public string? CurrentPath => _recorder?.CurrentPath;

    /// <summary>Записывается ли penlog.</summary>
    public bool IsRecording => _recorder is not null;

    /// <summary>Сколько точек контакта записано в текущую попытку.</summary>
    public int SamplesInAttempt => _recorder?.SamplesWritten ?? 0;

    /// <summary>Завершённые записанные попытки сеанса.</summary>
    public IReadOnlyList<RecordedAttempt> Recorded => _recorded;

    /// <summary>Последняя ошибка записи.</summary>
    public string? LastError => _recorder?.LastError;

    /// <summary>Цель текущей попытки (<c>glyph:a</c>).</summary>
    public string Target => "glyph:" + _letter;

    /// <summary>Точки пера (абсолютное время источника) — в запись.</summary>
    public void OnSamples(ReadOnlySpan<PenSample> samples) => _recorder?.AddSamples(samples);

    /// <summary>Меняет букву: текущая попытка завершается как отменённая, начинается новая.</summary>
    public void SelectLetter(string letter)
    {
        ArgumentException.ThrowIfNullOrEmpty(letter);
        if (string.Equals(letter, _letter, StringComparison.Ordinal))
        {
            return;
        }

        EndAttempt("cancelled");
        _letter = letter;
        BeginAttempt();
    }

    /// <summary>Эталон показан («Показать»): событие начала стимула.</summary>
    public void StimulusStarted() =>
        _recorder?.RecordEvent(InkEvent.Names.StimulusStart, new Dictionary<string, string> { ["key"] = "glyph/" + _letter });

    /// <summary>Показ эталона закончен.</summary>
    public void StimulusEnded() => _recorder?.RecordEvent(InkEvent.Names.StimulusEnd);

    /// <summary>«Заново»: холст очищен, попытка продолжается (A10).</summary>
    public void Restart()
    {
        Restarts++;
        _recorder?.Restart();
    }

    /// <summary>«Готово»: попытка завершена кнопкой; начинается следующая.</summary>
    /// <returns>Записанная попытка или <c>null</c>, если записи нет (не dev, касаний не было).</returns>
    public RecordedAttempt? Done()
    {
        RecordedAttempt? result = EndAttempt("done-button");
        AttemptNumber++;
        BeginAttempt();
        return result;
    }

    /// <summary>Закрывает сеанс: незавершённая попытка с касаниями записывается как отменённая.</summary>
    public void Close() => EndAttempt("cancelled");

    /// <inheritdoc />
    public void Dispose()
    {
        Close();
        _recorder?.Dispose();
    }

    private RecordedAttempt? EndAttempt(string by)
    {
        RecordedAttempt? result = _recorder?.Complete(by);
        if (result is not null)
        {
            _recorded.Add(result);
        }

        return result;
    }

    private void BeginAttempt()
    {
        Restarts = 0;
        if (_recorder is null)
        {
            return;
        }

        string letter = _letter;
        int attempt = AttemptNumber;
        string stem = _now().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
            + "-" + letter + "-" + attempt.ToString("000", CultureInfo.InvariantCulture);
        _recorder.Begin(() => _headerProvider(letter, attempt), stem, "glyph:" + letter);
    }
}
