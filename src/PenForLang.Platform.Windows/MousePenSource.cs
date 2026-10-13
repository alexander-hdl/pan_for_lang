using PenForLang.Core;
using PenForLang.Ink;
using PenForLang.Platform.Windows.Native;

namespace PenForLang.Platform.Windows;

/// <summary>
/// Мышь как перо — режим разработчика без планшета (ARCHITECTURE.md §7.1). Левая кнопка — контакт (давление 0,5), движение
/// без кнопки — наведение. Время точек — QPC в мкс (<see cref="PenTimeBase.MonotonicAbsolute"/>), как у
/// <see cref="Win32PointerPenSource"/>. Логика — в <see cref="MouseMessageProcessor"/>.
/// </summary>
public sealed class MousePenSource : HwndPenSourceBase
{
    private static readonly PenDeviceInfo MouseDevice = new(
        Product: "Mouse (development mode)",
        Source: PenSourceKind.Mouse,
        RateHzNominal: null,
        RateHzMeasured: null,
        PressureLevels: 1,
        Tilt: false,
        Timestamps: PenTimestampKind.Qpc);

    private readonly MouseMessageProcessor _processor = new();
    private readonly PenSample[] _one = new PenSample[1];

    /// <summary>Создаёт источник.</summary>
    public MousePenSource(IMonotonicClock clock)
        : base(clock)
    {
    }

    /// <inheritdoc />
    public override PenSourceKind Kind => PenSourceKind.Mouse;

    /// <inheritdoc />
    public override PenDeviceInfo? Device => MouseDevice;

    /// <inheritdoc />
    protected override void OnSurfaceChanged(PenSurface surface) => _processor.SetSurface(surface);

    /// <inheritdoc />
    protected override void OnStopped() => _processor.Reset();

    /// <inheritdoc />
    protected override bool OnMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (!MouseMessageProcessor.IsMouseMessage(message))
        {
            return false;
        }

        // lParam — клиентские координаты (знаковые слова); для PerMonitorV2 это физические px окна.
        var point = new NativePoint { X = (short)(lParam & 0xFFFF), Y = (short)((lParam >> 16) & 0xFFFF) };
        if (message != NativeConstants.WmCaptureChanged)
        {
            NativeMethods.ClientToScreen(hwnd, ref point);
        }

        long nowUs = MonotonicTime.ToMicroseconds(Clock.Ticks, Clock.Frequency);
        bool handled = _processor.TryProcess(message, wParam, point.X, point.Y, nowUs, out PenSample sample, out bool hasSample);

        if (hasSample)
        {
            _one[0] = sample;
            Emit(_one);
        }

        // Захват мыши, чтобы отрыв вне окна не потерялся.
        if (handled && sample.Phase == PenPhase.Down)
        {
            NativeMethods.SetCapture(hwnd);
        }
        else if (handled && sample.Phase == PenPhase.Up)
        {
            NativeMethods.ReleaseCapture();
        }

        return handled;
    }
}
