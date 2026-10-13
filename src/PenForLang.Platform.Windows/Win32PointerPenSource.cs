using PenForLang.Core;
using PenForLang.Ink;

namespace PenForLang.Platform.Windows;

/// <summary>
/// Основной источник пера: сообщения <c>WM_POINTERDOWN/UPDATE/UP/ENTER/LEAVE/CAPTURECHANGED</c> из хука окна
/// (ARCHITECTURE.md §7.1, ADR-003). Для пера читает все coalesced-точки через <c>GetPointerPenInfoHistory</c>
/// прямо в хуке, разворачивает их в хронологический порядок, переводит метку QPC в монотонные мкс и применяет
/// владение по <c>pointerId</c> (A11). Всё содержательное — в чистом <see cref="PenPointerMessageProcessor"/>.
/// </summary>
/// <remarks>
/// <para>Использование: создать, <see cref="HwndPenSourceBase.Attach"/> к <c>HwndSource</c> окна, задать
/// <see cref="HwndPenSourceBase.SetSurface"/> (физические px из <c>PointToScreen</c>, A12), <c>StartAsync</c>.</para>
/// <para>Приходят ли <c>WM_POINTER</c> в хук при <c>DisableStylusAndTouchSupport</c> — [не проверено, V0.5/S1]: счётчики
/// <see cref="Diagnostics"/> показывают, доходят ли сообщения и какого они типа.</para>
/// </remarks>
public sealed class Win32PointerPenSource : HwndPenSourceBase
{
    private readonly PenPointerMessageProcessor _processor;
    private PenDeviceInfo? _device;

    /// <summary>Создаёт источник на реальном API Windows.</summary>
    public Win32PointerPenSource(IMonotonicClock clock)
        : this(new Win32PointerApi(), clock)
    {
    }

    internal Win32PointerPenSource(IPointerApi api, IMonotonicClock clock)
        : base(clock)
    {
        _processor = new PenPointerMessageProcessor(api, clock);
    }

    /// <inheritdoc />
    public override PenSourceKind Kind => PenSourceKind.Win32Pointer;

    /// <inheritdoc />
    public override PenDeviceInfo? Device =>
        _device is null ? null : _device with { Tilt = _processor.TiltReported };

    /// <summary>Счётчики обработчика сообщений (для панели диагностики).</summary>
    public PointerDiagnostics Diagnostics => _processor.Diagnostics;

    /// <summary>Сколько указателей сейчас принадлежит холсту.</summary>
    public int OwnedPointerCount => _processor.OwnedCount;

    /// <inheritdoc />
    protected override void OnSurfaceChanged(PenSurface surface) => _processor.SetSurface(surface);

    /// <inheritdoc />
    protected override void OnStarted()
    {
        PointerDeviceDescription? pen = PenMonitorLocator.EnumeratePointerDevices().FirstOrDefault(d => d.IsPen);
        _device = new PenDeviceInfo(
            Product: pen?.Product is { Length: > 0 } product ? product : "Windows pointer (pen)",
            Source: PenSourceKind.Win32Pointer,
            RateHzNominal: null,
            RateHzMeasured: null,
            PressureLevels: null,
            Tilt: false,
            Timestamps: PenTimestampKind.Qpc);
    }

    /// <inheritdoc />
    protected override void OnStopped() => _processor.Reset();

    /// <inheritdoc />
    protected override bool OnMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (!PenPointerMessageProcessor.IsPointerMessage(message))
        {
            return false;
        }

        bool handled = _processor.TryProcess(message, wParam, out ReadOnlyMemory<PenSample> samples);
        Emit(samples);
        return handled;
    }
}
