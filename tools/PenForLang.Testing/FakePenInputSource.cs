using PenForLang.Ink;

namespace PenForLang.Testing;

/// <summary>
/// Ручной источник пера для тестов: пачки точек и события устройства подаются вызовами <see cref="Push(PenSample[])"/>
/// и <see cref="RaiseDeviceEvent"/>. Не зависит от платформы и времени.
/// </summary>
public sealed class FakePenInputSource : IPenInputSource
{
    /// <summary>Создаёт источник заданного вида (по умолчанию <see cref="PenSourceKind.Mouse"/>).</summary>
    public FakePenInputSource(
        PenSourceKind kind = PenSourceKind.Mouse,
        PenDeviceInfo? device = null,
        PenTimeBase timeBase = PenTimeBase.MonotonicAbsolute)
    {
        Kind = kind;
        Device = device;
        TimeBase = timeBase;
    }

    /// <inheritdoc />
    public event Action<ReadOnlyMemory<PenSample>>? SamplesReceived;

    /// <inheritdoc />
    public event Action<PenDeviceEvent>? DeviceChanged;

    /// <inheritdoc />
    public PenSourceKind Kind { get; }

    /// <inheritdoc />
    public PenTimeBase TimeBase { get; }

    /// <inheritdoc />
    public PenDeviceInfo? Device { get; }

    /// <summary>Последний заданный прямоугольник холста.</summary>
    public PenSurface? Surface { get; private set; }

    /// <summary>Источник запущен.</summary>
    public bool IsStarted { get; private set; }

    /// <summary>Сколько раз вызывали <see cref="StartAsync"/>.</summary>
    public int StartCount { get; private set; }

    /// <summary>Сколько раз вызывали <see cref="StopAsync"/>.</summary>
    public int StopCount { get; private set; }

    /// <summary>Выдаёт пачку точек подписчикам, если источник запущен; иначе пачка теряется. Возвращает, была ли она выдана.</summary>
    public bool Push(params PenSample[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (!IsStarted)
        {
            return false;
        }

        SamplesReceived?.Invoke(samples);
        return true;
    }

    /// <summary>Выдаёт событие устройства подписчикам.</summary>
    public void RaiseDeviceEvent(PenDeviceEvent deviceEvent) => DeviceChanged?.Invoke(deviceEvent);

    /// <inheritdoc />
    public void SetSurface(PenSurface surface) => Surface = surface;

    /// <inheritdoc />
    public ValueTask StartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IsStarted = true;
        StartCount++;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask StopAsync()
    {
        IsStarted = false;
        StopCount++;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => StopAsync();
}
