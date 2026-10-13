using System.Globalization;
using System.IO;
using System.IO.Compression;
using PenForLang.Core;
using PenForLang.Ink;

namespace PenForLang.App.Wpf;

/// <summary>Итог записанной попытки.</summary>
/// <param name="Path">Файл penlog (<c>*.pfl.jsonl.gz</c>).</param>
/// <param name="Samples">Записано точек контакта.</param>
/// <param name="Restarts">Сколько раз нажато «Заново».</param>
/// <param name="CompletedBy">Причина завершения (<c>done-button</c>, <c>cancelled</c>).</param>
/// <param name="Target">Цель попытки, например <c>glyph:a</c>.</param>
public sealed record RecordedAttempt(string Path, int Samples, int Restarts, string CompletedBy, string Target);

/// <summary>
/// Пишет одну попытку в penlog <c>pfl.penlog/1</c> (ARCHITECTURE.md §3.4) в dev-папку: gzip, строки JSON. Время точек пересчитывается
/// в «мкс от начала задания» через <see cref="AttemptTimeRebaser"/> (начало — <see cref="Begin"/>). Файл создаётся лениво — при
/// первой точке контакта; попытка без касаний файла не оставляет. Точки наведения не пишутся (в оценку не идут, объём лога иначе
/// растёт на порядок). Только для режима разработчика; запись детей без согласия формат запрещает сам (<c>PenLogWriter</c>).
/// </summary>
public sealed class AttemptRecorder : IDisposable
{
    private readonly string _directory;
    private readonly IMonotonicClock _clock;
    private readonly List<InkEvent> _pendingEvents = [];

    private AttemptTimeRebaser? _rebaser;
    private Func<PenLogHeader?>? _headerProvider;
    private string _fileStem = string.Empty;
    private string _target = string.Empty;
    private PenLogWriter? _writer;
    private string? _path;
    private long _lastTimeUs;
    private int _samples;
    private int _restarts;
    private bool _openFailed;

    /// <summary>Создаёт писатель.</summary>
    /// <param name="directory">Папка penlog (создаётся при необходимости).</param>
    /// <param name="clock">Монотонные часы (QPC) для начала задания и времени событий.</param>
    public AttemptRecorder(string directory, IMonotonicClock clock)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(clock);
        _directory = directory;
        _clock = clock;
    }

    /// <summary>Попытка начата и не закончена.</summary>
    public bool IsActive => _rebaser is not null;

    /// <summary>Файл текущей попытки (после первой точки контакта), иначе <c>null</c>.</summary>
    public string? CurrentPath => _path;

    /// <summary>Сколько точек контакта записано в текущую попытку.</summary>
    public int SamplesWritten => _samples;

    /// <summary>Последняя ошибка записи (запись не должна ронять приложение), иначе <c>null</c>.</summary>
    public string? LastError { get; private set; }

    /// <summary>Начинает попытку: фиксирует начало задания по часам.</summary>
    /// <param name="headerProvider">Заголовок; вызывается при первой точке контакта (к тому времени линейка и монитор известны); <c>null</c> — не писать.</param>
    /// <param name="fileStem">Начало имени файла (без расширения).</param>
    /// <param name="target">Цель для событий и итога, например <c>glyph:a</c>.</param>
    public void Begin(Func<PenLogHeader?> headerProvider, string fileStem, string target)
    {
        ArgumentNullException.ThrowIfNull(headerProvider);
        ArgumentException.ThrowIfNullOrEmpty(fileStem);
        Abandon();
        _headerProvider = headerProvider;
        _fileStem = fileStem;
        _target = target;
        _rebaser = AttemptTimeRebaser.Start(_clock);
        _lastTimeUs = 0;
        _samples = 0;
        _restarts = 0;
        _openFailed = false;
        _pendingEvents.Clear();
    }

    /// <summary>Записывает пачку точек (абсолютное время источника пересчитывается). Hover пропускается.</summary>
    public void AddSamples(ReadOnlySpan<PenSample> samples)
    {
        if (_rebaser is null)
        {
            return;
        }

        foreach (PenSample sample in samples)
        {
            if (!sample.IsContact || !_rebaser.TryRebase(in sample, out PenSample rebased))
            {
                continue;
            }

            if (_writer is null && (_openFailed || !TryOpen()))
            {
                return;
            }

            // Метки устройства могут отставать от «сейчас» события на миллисекунды: писатель требует неубывающего времени.
            PenSample toWrite = rebased.TimeUs < _lastTimeUs ? rebased with { TimeUs = _lastTimeUs } : rebased;
            try
            {
                _writer!.WriteSample(toWrite);
                if (sample.Phase == PenPhase.Up)
                {
                    _writer.Flush();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or ObjectDisposedException)
            {
                LastError = "penlog: " + ex.Message;
            }

            _lastTimeUs = toWrite.TimeUs;
            _samples++;
        }
    }

    /// <summary>Записывает событие попытки в текущий момент (до первой точки хранится в памяти).</summary>
    public void RecordEvent(string name, IReadOnlyDictionary<string, string>? data = null)
    {
        if (_rebaser is null)
        {
            return;
        }

        long time = Math.Max(_rebaser.ToTaskTime(MonotonicTime.ToMicroseconds(_clock.Ticks, _clock.Frequency)), _lastTimeUs);
        var ev = new InkEvent(time, name, data);
        if (_writer is null)
        {
            _pendingEvents.Add(ev);
        }
        else
        {
            Guarded(() => _writer.WriteEvent(ev));
            _lastTimeUs = time;
        }
    }

    /// <summary>«Заново»: событие <c>restart-button</c>, счётчик попыток внутри записи растёт.</summary>
    public void Restart()
    {
        _restarts++;
        RecordEvent(InkEvent.Names.RestartButton);
    }

    /// <summary>
    /// Завершает попытку: событие <c>completed</c> с причиной и закрытие файла.
    /// </summary>
    /// <param name="by">Причина (<c>done-button</c>, <c>cancelled</c>).</param>
    /// <returns>Итог или <c>null</c>, если файла не было (касаний не было или запись недоступна).</returns>
    public RecordedAttempt? Complete(string by)
    {
        if (_rebaser is null)
        {
            return null;
        }

        RecordEvent(InkEvent.Names.Completed, new Dictionary<string, string> { ["by"] = by });
        RecordedAttempt? result = null;
        if (_writer is not null && _path is not null)
        {
            Guarded(() => _writer.Dispose());
            result = new RecordedAttempt(_path, _samples, _restarts, by, _target);
        }

        _writer = null;
        _path = null;
        _rebaser = null;
        _pendingEvents.Clear();
        return result;
    }

    /// <inheritdoc />
    public void Dispose() => Abandon();

    private void Abandon()
    {
        if (_rebaser is not null)
        {
            Complete("cancelled");
        }
    }

    private bool TryOpen()
    {
        try
        {
            PenLogHeader? header = _headerProvider?.Invoke();
            if (header is null)
            {
                return false;
            }

            Directory.CreateDirectory(_directory);
            string path = UniquePath();
            var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            var gzip = new GZipStream(file, CompressionLevel.Fastest);
            var writer = new PenLogWriter(gzip);
            try
            {
                writer.WriteHeader(header);
                foreach (InkEvent ev in _pendingEvents)
                {
                    writer.WriteEvent(ev);
                    _lastTimeUs = Math.Max(_lastTimeUs, ev.TimeUs);
                }
            }
            catch
            {
                // Заголовок не прошёл проверку формата (например, ребёнок без согласия): не оставляем ни открытого потока, ни пустого файла.
                writer.Dispose();
                File.Delete(path);
                throw;
            }

            _pendingEvents.Clear();
            _writer = writer;
            _path = path;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            LastError = "penlog: " + ex.Message;
            _openFailed = true;
            return false;
        }
    }

    private string UniquePath()
    {
        string stem = Path.Combine(_directory, _fileStem);
        string path = stem + ".pfl.jsonl.gz";
        for (int i = 2; File.Exists(path); i++)
        {
            path = stem + "-" + i.ToString(CultureInfo.InvariantCulture) + ".pfl.jsonl.gz";
        }

        return path;
    }

    private void Guarded(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or ObjectDisposedException)
        {
            LastError = "penlog: " + ex.Message;
        }
    }
}
