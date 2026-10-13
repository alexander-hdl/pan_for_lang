using System.Windows.Interop;
using PenForLang.Core;
using PenForLang.Ink;

namespace PenForLang.Platform.Windows;

/// <summary>
/// Общая часть источников пера, которые получают сообщения окна через <see cref="HwndSource.AddHook"/>
/// (<see cref="Win32PointerPenSource"/>, <see cref="MousePenSource"/>). Хук работает в потоке окна (UI); события
/// источника поднимаются синхронно в том же потоке (ARCHITECTURE.md §2.5).
/// </summary>
public abstract class HwndPenSourceBase : IPenInputSource
{
    private HwndSource? _source;
    private bool _running;

    /// <summary>Создаёт источник.</summary>
    /// <param name="clock">Монотонные часы (QPC) — для перевода времени и для точек без метки времени.</param>
    protected HwndPenSourceBase(IMonotonicClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        Clock = clock;
    }

    /// <inheritdoc />
    public event Action<ReadOnlyMemory<PenSample>>? SamplesReceived;

    /// <inheritdoc />
    public event Action<PenDeviceEvent>? DeviceChanged;

    /// <inheritdoc />
    public abstract PenSourceKind Kind { get; }

    /// <inheritdoc />
    public PenTimeBase TimeBase => PenTimeBase.MonotonicAbsolute;

    /// <inheritdoc />
    public abstract PenDeviceInfo? Device { get; }

    /// <summary>Источник запущен (<see cref="StartAsync"/> вызван, <see cref="StopAsync"/> — нет).</summary>
    public bool IsRunning => _running;

    /// <summary>Монотонные часы.</summary>
    protected IMonotonicClock Clock { get; }

    /// <summary>Окно, к которому подключён хук; <c>null</c>, если не подключён.</summary>
    protected nint Hwnd => _source?.Handle ?? 0;

    /// <summary>Подключает хук к окну. Один источник — одно окно.</summary>
    /// <exception cref="InvalidOperationException">Источник уже подключён.</exception>
    public void Attach(HwndSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_source is not null)
        {
            throw new InvalidOperationException("The source is already attached to a window.");
        }

        _source = source;
        source.AddHook(Hook);
    }

    /// <inheritdoc />
    public void SetSurface(PenSurface surface) => OnSurfaceChanged(surface);

    /// <inheritdoc />
    public ValueTask StartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_running)
        {
            return ValueTask.CompletedTask;
        }

        _running = true;
        OnStarted();
        DeviceChanged?.Invoke(new PenDeviceEvent(PenDeviceEventKind.Connected, Device));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask StopAsync()
    {
        _running = false;
        OnStopped();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (_source is not null)
        {
            _source.RemoveHook(Hook);
            _source = null;
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Сообщает подписчикам о событии устройства (смена монитора и т. п.).</summary>
    protected void RaiseDeviceChanged(PenDeviceEvent deviceEvent) => DeviceChanged?.Invoke(deviceEvent);

    /// <summary>Выдаёт пачку точек подписчикам.</summary>
    protected void Emit(ReadOnlyMemory<PenSample> samples)
    {
        if (!samples.IsEmpty)
        {
            SamplesReceived?.Invoke(samples);
        }
    }

    /// <summary>Холст получил новый экранный прямоугольник.</summary>
    protected abstract void OnSurfaceChanged(PenSurface surface);

    /// <summary>Источник запущен.</summary>
    protected virtual void OnStarted()
    {
    }

    /// <summary>Источник остановлен: сбросить владение указателями.</summary>
    protected abstract void OnStopped();

    /// <summary>
    /// Обрабатывает сообщение окна (источник запущен). Возвращает <c>true</c>, если сообщение принадлежит холсту и его нужно
    /// пометить обработанным; точки, если они есть, выдаются через <see cref="Emit"/>.
    /// </summary>
    protected abstract bool OnMessage(nint hwnd, uint message, nint wParam, nint lParam);

    private nint Hook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (!_running)
        {
            return 0;
        }

        if (OnMessage(hwnd, (uint)msg, wParam, lParam))
        {
            handled = true;
        }

        return 0;
    }
}
