using System.Buffers;
using System.Collections.Concurrent;
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

/// <summary>Приёмник записи одной попытки: файловый ввод-вывод. Вызывается только из фонового потока <see cref="AttemptRecorder"/>.</summary>
public interface IAttemptLogSink
{
    /// <summary>Записывает точку.</summary>
    void WriteSample(in PenSample sample);

    /// <summary>Записывает событие.</summary>
    void WriteEvent(InkEvent ev);

    /// <summary>Закрывает запись; при <paramref name="discard"/> файл удаляется (попытка без касаний).</summary>
    void Close(bool discard);
}

/// <summary>Создаёт приёмник записи: открывает файл и пишет заголовок. Вызывается из фонового потока.</summary>
/// <param name="path">Путь файла.</param>
/// <param name="header">Заголовок penlog.</param>
public delegate IAttemptLogSink AttemptLogSinkFactory(string path, PenLogHeader header);

/// <summary>Приёмник в файл <c>*.pfl.jsonl.gz</c> (gzip, JSON Lines) на <see cref="PenLogWriter"/>.</summary>
public sealed class FileAttemptLogSink : IAttemptLogSink, IDisposable
{
    private readonly string _path;
    private readonly PenLogWriter _writer;

    /// <summary>Создаёт каталог и файл (<see cref="FileMode.CreateNew"/>) и пишет заголовок.</summary>
    public FileAttemptLogSink(string path, PenLogHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        _writer = new PenLogWriter(new GZipStream(file, CompressionLevel.Fastest));
        try
        {
            _writer.WriteHeader(header);
        }
        catch
        {
            // Заголовок не прошёл проверку формата: не оставляем ни открытого потока, ни пустого файла.
            _writer.Dispose();
            File.Delete(path);
            throw;
        }
    }

    /// <inheritdoc />
    public void WriteSample(in PenSample sample) => _writer.WriteSample(sample);

    /// <inheritdoc />
    public void WriteEvent(InkEvent ev) => _writer.WriteEvent(ev);

    /// <inheritdoc />
    public void Close(bool discard)
    {
        Dispose();
        if (discard)
        {
            File.Delete(_path);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _writer.Dispose();
}

/// <summary>
/// Пишет одну попытку в penlog <c>pfl.penlog/1</c> (ARCHITECTURE.md §3.4). Время точек пересчитывается в «мкс от начала задания»
/// (<see cref="AttemptTimeRebaser"/>, начало — <see cref="Begin"/>).
/// </summary>
/// <remarks>
/// <b>Поток UI не делает ввода-вывода</b> (ревью V0.6, F2): точки копируются в буфер из пула и ставятся в очередь, а создание каталога,
/// открытие файла, gzip и запись выполняет один фоновый поток (<see cref="IAttemptLogSink"/>). Файл открывается при начале попытки
/// (<see cref="Begin"/>/<see cref="Arm"/>, как только известен заголовок), сброса на диск на каждом отрыве пера нет — запись
/// закрывается по завершении попытки и при выходе (<see cref="Dispose"/>). Попытка без касаний файла не оставляет (он удаляется).
/// Точки наведения не пишутся. Только режим разработчика; запись ребёнка без согласия формат запрещает (§3.4, §11.2).
/// </remarks>
public sealed class AttemptRecorder : IDisposable
{
    private const int WorkerJoinMilliseconds = 10_000;

    private readonly string _directory;
    private readonly IMonotonicClock _clock;
    private readonly AttemptLogSinkFactory _sinkFactory;
    private readonly List<InkEvent> _pendingEvents = [];
    private readonly HashSet<string> _usedStems = new(StringComparer.OrdinalIgnoreCase);
    private readonly BlockingCollection<Command> _queue = new();
    private readonly object _workerLock = new();

    private Thread? _worker;
    private AttemptTimeRebaser? _rebaser;
    private Func<PenLogHeader?>? _headerProvider;
    private string _fileStem = string.Empty;
    private string _target = string.Empty;
    private string? _path;
    private bool _armed;
    private bool _openFailed;
    private long _lastTimeUs;
    private int _samples;
    private int _restarts;
    private volatile string? _lastError;
    private volatile bool _workerFailed;

    /// <summary>Создаёт писатель.</summary>
    /// <param name="directory">Папка penlog (создаётся фоновым потоком при необходимости).</param>
    /// <param name="clock">Монотонные часы (QPC) для начала задания и времени событий.</param>
    /// <param name="sinkFactory">Фабрика приёмника записи; <c>null</c> — файл <see cref="FileAttemptLogSink"/>.</param>
    public AttemptRecorder(string directory, IMonotonicClock clock, AttemptLogSinkFactory? sinkFactory = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(clock);
        _directory = directory;
        _clock = clock;
        _sinkFactory = sinkFactory ?? ((path, header) => new FileAttemptLogSink(path, header));
    }

    private enum CommandKind
    {
        Open,
        Samples,
        Event,
        Close,
        Sync,
    }

    /// <summary>Попытка начата и не закончена.</summary>
    public bool IsActive => _rebaser is not null;

    /// <summary>Файл текущей попытки, когда в неё уже записаны точки контакта, иначе <c>null</c>.</summary>
    public string? CurrentPath => _samples > 0 ? _path : null;

    /// <summary>Сколько точек контакта принято в текущую попытку (поставлено в очередь записи).</summary>
    public int SamplesWritten => _samples;

    /// <summary>Последняя ошибка записи (запись не должна ронять приложение), иначе <c>null</c>.</summary>
    public string? LastError => _lastError;

    /// <summary>Начинает попытку: фиксирует начало задания по часам и, если заголовок уже известен, ставит открытие файла в очередь.</summary>
    /// <param name="headerProvider">Заголовок (без ввода-вывода); <c>null</c> — линейка ещё не известна, попытка повторит запрос позже (<see cref="Arm"/>).</param>
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
        _armed = false;
        _openFailed = false;
        _path = null;
        _pendingEvents.Clear();
        Arm();
    }

    /// <summary>
    /// Запрашивает заголовок и ставит открытие файла в очередь, если это ещё не сделано. Вызывать, когда линейка стала известной.
    /// Ввода-вывода в вызывающем потоке нет.
    /// </summary>
    /// <returns><c>true</c>, если запись готова.</returns>
    public bool Arm()
    {
        if (_armed)
        {
            return true;
        }

        if (_rebaser is null || _openFailed)
        {
            return false;
        }

        PenLogHeader? header = _headerProvider?.Invoke();
        if (header is null)
        {
            return false;
        }

        if (header.Subject is { Kind: PenSubjectKind.Child } && header.Consent is null)
        {
            _lastError = "penlog: a recording of a child (subject.kind = child) requires consent (ARCHITECTURE.md section 3.4).";
            _openFailed = true;
            return false;
        }

        _path = Path.Combine(_directory, UniqueStem() + ".pfl.jsonl.gz");
        Enqueue(new Command(CommandKind.Open, Path: _path, Header: header));
        _armed = true;
        foreach (InkEvent ev in _pendingEvents)
        {
            Enqueue(new Command(CommandKind.Event, Event: ev));
        }

        _pendingEvents.Clear();
        return true;
    }

    /// <summary>Принимает пачку точек (абсолютное время источника пересчитывается). Hover пропускается. Не блокирует.</summary>
    public void AddSamples(ReadOnlySpan<PenSample> samples)
    {
        if (_rebaser is null || samples.IsEmpty)
        {
            return;
        }

        if (!_armed && !Arm())
        {
            return;
        }

        PenSample[]? buffer = null;
        int count = 0;
        foreach (PenSample sample in samples)
        {
            if (!sample.IsContact || !_rebaser.TryRebase(in sample, out PenSample rebased))
            {
                continue;
            }

            // Метки устройства могут отставать от «сейчас» события на миллисекунды: писатель требует неубывающего времени.
            PenSample toWrite = rebased.TimeUs < _lastTimeUs ? rebased with { TimeUs = _lastTimeUs } : rebased;
            _lastTimeUs = toWrite.TimeUs;
            buffer ??= ArrayPool<PenSample>.Shared.Rent(samples.Length);
            buffer[count++] = toWrite;
        }

        if (buffer is null)
        {
            return;
        }

        _samples += count;
        Enqueue(new Command(CommandKind.Samples, Samples: buffer, Count: count));
    }

    /// <summary>Записывает событие попытки в текущий момент (до готовности заголовка хранится в памяти).</summary>
    public void RecordEvent(string name, IReadOnlyDictionary<string, string>? data = null)
    {
        if (_rebaser is null)
        {
            return;
        }

        long time = Math.Max(_rebaser.ToTaskTime(MonotonicTime.ToMicroseconds(_clock.Ticks, _clock.Frequency)), _lastTimeUs);
        var ev = new InkEvent(time, name, data);
        if (!_armed && !Arm())
        {
            _pendingEvents.Add(ev);
            return;
        }

        _lastTimeUs = time;
        Enqueue(new Command(CommandKind.Event, Event: ev));
    }

    /// <summary>«Заново»: событие <c>restart-button</c>, счётчик попыток внутри записи растёт.</summary>
    public void Restart()
    {
        _restarts++;
        RecordEvent(InkEvent.Names.RestartButton);
    }

    /// <summary>Завершает попытку: событие <c>completed</c> с причиной и постановка закрытия файла в очередь (не блокирует).</summary>
    /// <param name="by">Причина (<c>done-button</c>, <c>cancelled</c>).</param>
    /// <returns>Итог или <c>null</c>, если файла не будет (касаний не было, запись недоступна).</returns>
    public RecordedAttempt? Complete(string by)
    {
        if (_rebaser is null)
        {
            return null;
        }

        RecordEvent(InkEvent.Names.Completed, new Dictionary<string, string> { ["by"] = by });
        RecordedAttempt? result = null;
        if (_armed && _path is not null)
        {
            bool hadContact = _samples > 0;
            Enqueue(new Command(CommandKind.Close, Discard: !hadContact));
            if (hadContact && !_workerFailed)
            {
                result = new RecordedAttempt(_path, _samples, _restarts, by, _target);
            }
        }

        _rebaser = null;
        _armed = false;
        _path = null;
        _samples = 0;
        _pendingEvents.Clear();
        return result;
    }

    /// <summary>Ждёт, пока очередь записи будет обработана (при выходе и в тестах); в потоке UI во время работы не вызывается.</summary>
    /// <returns><c>false</c>, если за <paramref name="timeout"/> не успели.</returns>
    public bool Drain(TimeSpan timeout)
    {
        lock (_workerLock)
        {
            if (_worker is null)
            {
                return true;
            }
        }

        using var signal = new ManualResetEventSlim(false);
        _queue.Add(new Command(CommandKind.Sync, Signal: signal));
        return signal.Wait(timeout);
    }

    /// <summary>Закрывает незавершённую попытку, дожидается записи и останавливает фоновый поток.</summary>
    public void Dispose()
    {
        Abandon();
        Thread? worker;
        lock (_workerLock)
        {
            worker = _worker;
            _worker = null;
        }

        _queue.CompleteAdding();
        worker?.Join(WorkerJoinMilliseconds);
        _queue.Dispose();
    }

    private void Abandon()
    {
        if (_rebaser is not null)
        {
            Complete("cancelled");
        }
    }

    private string UniqueStem()
    {
        string stem = _fileStem;
        for (int i = 2; !_usedStems.Add(stem); i++)
        {
            stem = _fileStem + "-" + i.ToString(CultureInfo.InvariantCulture);
        }

        return stem;
    }

    private void Enqueue(Command command)
    {
        lock (_workerLock)
        {
            if (_worker is null)
            {
                _worker = new Thread(Run) { IsBackground = true, Name = "PenLogWriter" };
                _worker.Start();
            }
        }

        if (!_queue.IsAddingCompleted)
        {
            _queue.Add(command);
        }
    }

    private void Run()
    {
        IAttemptLogSink? sink = null;
        foreach (Command command in _queue.GetConsumingEnumerable())
        {
            try
            {
                switch (command.Kind)
                {
                    case CommandKind.Open:
                        sink?.Close(false);
                        sink = null;
                        _workerFailed = false;
                        sink = _sinkFactory(command.Path!, command.Header!);
                        break;
                    case CommandKind.Samples:
                        if (sink is not null && !_workerFailed)
                        {
                            for (int i = 0; i < command.Count; i++)
                            {
                                sink.WriteSample(in command.Samples![i]);
                            }
                        }

                        break;
                    case CommandKind.Event:
                        if (sink is not null && !_workerFailed)
                        {
                            sink.WriteEvent(command.Event!);
                        }

                        break;
                    case CommandKind.Close:
                        sink?.Close(command.Discard);
                        sink = null;
                        _workerFailed = false;
                        break;
                    case CommandKind.Sync:
                        command.Signal!.Set();
                        break;
                    default:
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or ObjectDisposedException or NotSupportedException)
            {
                _lastError = "penlog: " + ex.Message;
                _workerFailed = true;
            }
            finally
            {
                if (command.Samples is not null)
                {
                    ArrayPool<PenSample>.Shared.Return(command.Samples);
                }
            }
        }

        try
        {
            sink?.Close(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            _lastError = "penlog: " + ex.Message;
        }
    }

    private readonly record struct Command(
        CommandKind Kind,
        string? Path = null,
        PenLogHeader? Header = null,
        PenSample[]? Samples = null,
        int Count = 0,
        InkEvent? Event = null,
        bool Discard = false,
        ManualResetEventSlim? Signal = null);
}
