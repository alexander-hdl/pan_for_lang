using PenForLang.Core;

namespace PenForLang.Ink;

/// <summary>
/// Воспроизводит penlog через <see cref="IPenInputSource"/> по часам <see cref="IClock"/> (ARCHITECTURE.md §7.1, §10):
/// запись с меткой <c>t</c> выдаётся, когда с момента <see cref="StartAsync"/> прошло не меньше <c>t</c> микросекунд.
/// Источник пассивен и детерминирован: новые записи выдаёт только <see cref="Poll"/> (или <see cref="RunToEndAsync"/>),
/// поэтому при <c>FakeClock</c> воспроизведение точно и не зависит от планировщика потоков.
/// </summary>
/// <remarks>
/// Точность: запись выдаётся при первом <see cref="Poll"/> после наступления её времени. Если вызывать <see cref="Poll"/>
/// не реже чем раз в миллисекунду часов (или ровно в <see cref="TimeUntilNext"/>), опоздание не превышает 1 мс.
/// Точки лога уже в координатах поверхности, поэтому <see cref="SetSurface"/> только запоминает прямоугольник
/// и точки не преобразует. События лога (<c>stimulus-start</c>, <c>completed</c> и т. д.) приходят через <see cref="LogEventRaised"/>
/// в том же порядке, что и точки. Не потокобезопасен.
/// </remarks>
public sealed class ReplayPenSource : IPenInputSource
{
    private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;

    private readonly IClock _clock;
    private readonly PenLogEntry[] _entries;
    private readonly PenSample[] _batch;
    private int _position;
    private DateTimeOffset _startedAt;
    private bool _running;

    /// <summary>Создаёт источник по логу и часам.</summary>
    public ReplayPenSource(PenLog log, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
        Device = log.Header.Device;

        // Стабильная сортировка: записи с равным временем сохраняют порядок лога.
        _entries = [.. log.Entries.OrderBy(e => e.TimeUs)];
        _batch = new PenSample[_entries.Count(e => e is PenLogSampleEntry)];
    }

    /// <inheritdoc />
    public event Action<ReadOnlyMemory<PenSample>>? SamplesReceived;

    /// <inheritdoc />
    public event Action<PenDeviceEvent>? DeviceChanged;

    /// <summary>Событие лога (<c>ev</c>), выдаётся в хронологическом порядке между пачками точек.</summary>
    public event Action<InkEvent>? LogEventRaised;

    /// <inheritdoc />
    public PenSourceKind Kind => PenSourceKind.Replay;

    /// <summary>Устройство, на котором записан лог.</summary>
    public PenDeviceInfo? Device { get; }

    /// <summary>Последний заданный через <see cref="SetSurface"/> прямоугольник (на воспроизведение не влияет).</summary>
    public PenSurface? Surface { get; private set; }

    /// <summary>Источник запущен и не остановлен.</summary>
    public bool IsRunning => _running;

    /// <summary>Все записи лога выданы.</summary>
    public bool IsCompleted => _running && _position >= _entries.Length;

    /// <summary>Сколько записей уже выдано.</summary>
    public int EmittedCount => _position;

    /// <summary>
    /// Сколько осталось ждать до следующей записи по часам (не меньше нуля) или <c>null</c>, если источник не запущен
    /// либо записей больше нет.
    /// </summary>
    public TimeSpan? TimeUntilNext
    {
        get
        {
            if (!_running || _position >= _entries.Length)
            {
                return null;
            }

            long dueTicks = _entries[_position].TimeUs * TicksPerMicrosecond;
            long remaining = dueTicks - (_clock.UtcNow - _startedAt).Ticks;
            return TimeSpan.FromTicks(Math.Max(0, remaining));
        }
    }

    /// <inheritdoc />
    public void SetSurface(PenSurface surface) => Surface = surface;

    /// <summary>Запускает воспроизведение с начала лога: момент старта — текущее время часов.</summary>
    public ValueTask StartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _startedAt = _clock.UtcNow;
        _position = 0;
        _running = true;
        DeviceChanged?.Invoke(new PenDeviceEvent(PenDeviceEventKind.Connected, Device));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask StopAsync()
    {
        _running = false;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => StopAsync();

    /// <summary>
    /// Выдаёт все записи, время которых уже наступило. Подряд идущие точки собираются в одну пачку
    /// (<see cref="SamplesReceived"/>); событие лога разрывает пачку, чтобы порядок сохранился.
    /// </summary>
    /// <returns>Число выданных записей.</returns>
    public int Poll()
    {
        if (!_running)
        {
            return 0;
        }

        long elapsedUs = Math.Max(0, (_clock.UtcNow - _startedAt).Ticks / TicksPerMicrosecond);
        int emitted = 0;
        int batchCount = 0;

        while (_position < _entries.Length && _entries[_position].TimeUs <= elapsedUs)
        {
            PenLogEntry entry = _entries[_position++];
            emitted++;
            if (entry is PenLogSampleEntry sample)
            {
                _batch[batchCount++] = sample.Sample;
            }
            else if (entry is PenLogEventEntry ev)
            {
                FlushBatch(ref batchCount);
                LogEventRaised?.Invoke(ev.Event);
            }
        }

        FlushBatch(ref batchCount);
        return emitted;
    }

    /// <summary>
    /// Воспроизводит лог до конца: опрашивает <see cref="Poll"/> и ждёт ровно до следующей записи через
    /// <paramref name="delay"/>. Реальный запуск передаёт <c>Task.Delay</c>; в тестах делегат сдвигает <c>FakeClock</c>.
    /// Делегат обязан продвигать часы, иначе метод не завершится.
    /// </summary>
    /// <param name="delay">Ожидание заданной длительности по тем же часам, что и <see cref="IClock"/>.</param>
    /// <param name="ct">Отмена.</param>
    public async ValueTask RunToEndAsync(Func<TimeSpan, CancellationToken, ValueTask> delay, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(delay);
        while (_running)
        {
            ct.ThrowIfCancellationRequested();
            Poll();
            if (TimeUntilNext is not { } wait)
            {
                return;
            }

            await delay(wait, ct).ConfigureAwait(false);
        }
    }

    private void FlushBatch(ref int batchCount)
    {
        if (batchCount == 0)
        {
            return;
        }

        SamplesReceived?.Invoke(new ReadOnlyMemory<PenSample>(_batch, 0, batchCount));
        batchCount = 0;
    }
}
