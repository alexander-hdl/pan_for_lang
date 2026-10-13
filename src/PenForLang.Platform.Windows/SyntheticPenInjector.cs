using System.Runtime.InteropServices;
using PenForLang.Platform.Windows.Native;

namespace PenForLang.Platform.Windows;

/// <summary>
/// Синтетический указатель-перо для интерактивных тестов (ARCHITECTURE.md §10.1): <c>CreateSyntheticPointerDevice(PT_PEN)</c> и
/// <c>InjectSyntheticPointerInput</c> (Windows 10 1809+). Не используется продуктовым кодом; internal, доступен тестам через
/// <c>InternalsVisibleTo</c>. Координаты — физические px виртуального экрана.
/// <see href="https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-injectsyntheticpointerinput"/>.
/// </summary>
internal sealed class SyntheticPenInjector : IDisposable
{
    private const uint PtPen = NativeConstants.PtPen;
    private nint _device;
    private bool _arrived;

    private SyntheticPenInjector(nint device) => _device = device;

    /// <summary>Создаёт устройство; <c>null</c> и код ошибки Win32, если ОС или права не позволяют.</summary>
    public static SyntheticPenInjector? TryCreate(out int win32Error)
    {
        win32Error = 0;
        try
        {
            nint device = NativeMethods.CreateSyntheticPointerDevice(PtPen, 1, NativeConstants.PointerFeedbackNone);
            if (device == 0)
            {
                win32Error = Marshal.GetLastPInvokeError();
                return null;
            }

            return new SyntheticPenInjector(device);
        }
        catch (EntryPointNotFoundException)
        {
            win32Error = -1; // API отсутствует (Windows старше 1809)
            return null;
        }
    }

    /// <summary>Наведение пера в точке (без контакта).</summary>
    public bool Hover(int xPx, int yPx)
    {
        uint flags = NativeConstants.PointerFlagInRange | NativeConstants.PointerFlagUpdate;
        if (!_arrived)
        {
            // Первое сообщение нового указателя несёт POINTER_FLAG_NEW (появление в зоне).
            flags |= NativeConstants.PointerFlagNew;
            _arrived = true;
        }

        return Inject(xPx, yPx, pressure: 0, flags);
    }

    /// <summary>Касание.</summary>
    public bool Down(int xPx, int yPx, uint pressure) =>
        Inject(xPx, yPx, pressure, ContactFlags | NativeConstants.PointerFlagDown);

    /// <summary>Движение в контакте.</summary>
    public bool Move(int xPx, int yPx, uint pressure) =>
        Inject(xPx, yPx, pressure, ContactFlags | NativeConstants.PointerFlagUpdate);

    /// <summary>Отрыв.</summary>
    public bool Up(int xPx, int yPx) =>
        Inject(xPx, yPx, pressure: 0, NativeConstants.PointerFlagInRange | NativeConstants.PointerFlagUp);

    /// <summary>Последняя ошибка Win32 неудачной инъекции.</summary>
    public int LastError { get; private set; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_device != 0)
        {
            NativeMethods.DestroySyntheticPointerDevice(_device);
            _device = 0;
        }
    }

    // POINTER_FLAG_INRANGE | INCONTACT | FIRSTBUTTON (0x10): перо касается без кнопок.
    private static uint ContactFlags =>
        NativeConstants.PointerFlagInRange | NativeConstants.PointerFlagInContact | 0x00000010;

    private bool Inject(int xPx, int yPx, uint pressure, uint flags)
    {
        var info = new NativePointerTypeInfoPen { Type = PtPen };
        info.PenInfo.PointerInfo.PointerType = PtPen;
        info.PenInfo.PointerInfo.PointerId = 0;
        info.PenInfo.PointerInfo.PointerFlags = flags;
        info.PenInfo.PointerInfo.PtPixelLocation = new NativePoint { X = xPx, Y = yPx };
        info.PenInfo.PenMask = NativeConstants.PenMaskPressure | NativeConstants.PenMaskTiltX | NativeConstants.PenMaskTiltY;
        info.PenInfo.Pressure = pressure;

        if (NativeMethods.InjectSyntheticPointerInput(_device, in info, 1))
        {
            return true;
        }

        LastError = Marshal.GetLastPInvokeError();
        return false;
    }
}
