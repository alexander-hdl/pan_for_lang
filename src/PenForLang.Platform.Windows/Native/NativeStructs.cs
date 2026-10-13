using System.Runtime.InteropServices;

namespace PenForLang.Platform.Windows.Native;

/// <summary>POINT (windef.h).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    public int X;
    public int Y;
}

/// <summary>RECT (windef.h).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

/// <summary>
/// POINTER_INFO (winuser.h) —
/// https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-pointer_info.
/// Порядок и типы полей совпадают с заголовком; размер на x64 — 96 байт (проверяется тестом).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativePointerInfo
{
    public uint PointerType;
    public uint PointerId;
    public uint FrameId;
    public uint PointerFlags;
    public nint SourceDevice;
    public nint HwndTarget;
    public NativePoint PtPixelLocation;
    public NativePoint PtHimetricLocation;
    public NativePoint PtPixelLocationRaw;
    public NativePoint PtHimetricLocationRaw;
    public uint DwTime;
    public uint HistoryCount;
    public int InputData;
    public uint DwKeyStates;
    public ulong PerformanceCount;
    public int ButtonChangeType;
}

/// <summary>
/// POINTER_PEN_INFO (winuser.h) —
/// https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-pointer_pen_info.
/// Размер на x64 — 120 байт (проверяется тестом).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativePointerPenInfo
{
    public NativePointerInfo PointerInfo;
    public uint PenFlags;
    public uint PenMask;
    public uint Pressure;
    public uint Rotation;
    public int TiltX;
    public int TiltY;
}

/// <summary>
/// POINTER_TYPE_INFO для пера (winuser.h): тип + объединение. Объединение выровнено по 8 и занимает размер наибольшего члена
/// (POINTER_TOUCH_INFO, 144 байта), поэтому размер структуры фиксирован — 152 байта; иначе InjectSyntheticPointerInput
/// прочитал бы за концом массива.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 152)]
internal struct NativePointerTypeInfoPen
{
    public uint Type;
    public NativePointerPenInfo PenInfo;
}

/// <summary>
/// POINTER_DEVICE_INFO (winuser.h), <c>productString[POINTER_DEVICE_PRODUCT_STRING_MAX = 520]</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePointerDeviceInfo
{
    public const int ProductStringMax = 520;

    public uint DisplayOrientation;
    public nint Device;
    public uint PointerDeviceType;
    public nint Monitor;
    public uint StartingCursorId;
    public ushort MaxActiveContacts;
    public fixed char ProductString[ProductStringMax];

    /// <summary>Название продукта (до нулевого символа).</summary>
    public readonly string GetProductString()
    {
        fixed (char* p = ProductString)
        {
            int length = 0;
            while (length < ProductStringMax && p[length] != '\0')
            {
                length++;
            }

            return new string(p, 0, length);
        }
    }
}

/// <summary>MONITORINFOEXW (winuser.h): <c>szDevice[CCHDEVICENAME = 32]</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeMonitorInfoEx
{
    public const int DeviceNameMax = 32;

    public uint Size;
    public NativeRect Monitor;
    public NativeRect Work;
    public uint Flags;
    public fixed char Device[DeviceNameMax];

    /// <summary>Имя устройства монитора (например, <c>\\.\DISPLAY1</c>).</summary>
    public readonly string GetDeviceName()
    {
        fixed (char* p = Device)
        {
            int length = 0;
            while (length < DeviceNameMax && p[length] != '\0')
            {
                length++;
            }

            return new string(p, 0, length);
        }
    }
}
