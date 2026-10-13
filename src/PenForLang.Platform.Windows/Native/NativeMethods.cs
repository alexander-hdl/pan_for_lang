using System.Runtime.InteropServices;

namespace PenForLang.Platform.Windows.Native;

/// <summary>
/// Все P/Invoke проекта (R4: P/Invoke только в <c>Platform.Windows</c>). Сигнатуры сверены с документацией Microsoft;
/// ссылка на страницу — над каждым методом.
/// </summary>
internal static unsafe partial class NativeMethods
{
    private const string User32 = "user32.dll";

    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getpointertype
    [LibraryImport(User32, EntryPoint = "GetPointerType")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetPointerType(uint pointerId, out uint pointerType);

    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getpointerpeninfohistory
    // История приходит в обратном хронологическом порядке (новейшая — первая); entriesCount — in/out.
    [LibraryImport(User32, EntryPoint = "GetPointerPenInfoHistory", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetPointerPenInfoHistory(uint pointerId, ref uint entriesCount, ref NativePointerPenInfo penInfo);

    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getpointerpeninfohistory (форма с NULL-буфером)
    [LibraryImport(User32, EntryPoint = "GetPointerPenInfoHistory", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetPointerPenInfoHistoryCount(uint pointerId, ref uint entriesCount, nint penInfo);

    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getpointerdevices
    [LibraryImport(User32, EntryPoint = "GetPointerDevices", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetPointerDevices(ref uint deviceCount, ref NativePointerDeviceInfo pointerDevices);

    [LibraryImport(User32, EntryPoint = "GetPointerDevices", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetPointerDevicesCount(ref uint deviceCount, nint pointerDevices);

    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowfeedbacksetting
    // dwFlags — зарезервировано (0); size — sizeof(BOOL); configuration — указатель на BOOL.
    [LibraryImport(User32, EntryPoint = "SetWindowFeedbackSetting", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowFeedbackSetting(nint hwnd, uint feedback, uint flags, uint size, in int configuration);

    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setpropw
    [LibraryImport(User32, EntryPoint = "SetPropW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProp(nint hwnd, string name, nint data);

    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-removepropw
    [LibraryImport(User32, EntryPoint = "RemovePropW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint RemoveProp(nint hwnd, string name);

    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setcapture
    [LibraryImport(User32, EntryPoint = "SetCapture")]
    internal static partial nint SetCapture(nint hwnd);

    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-releasecapture
    [LibraryImport(User32, EntryPoint = "ReleaseCapture")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ReleaseCapture();

    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-clienttoscreen
    [LibraryImport(User32, EntryPoint = "ClientToScreen")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ClientToScreen(nint hwnd, ref NativePoint point);

    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumdisplaymonitors
    internal static bool EnumDisplayMonitors(delegate* unmanaged<nint, nint, NativeRect*, nint, int> callback, nint data) =>
        EnumDisplayMonitorsImpl(0, 0, callback, data) != 0;

    [LibraryImport(User32, EntryPoint = "EnumDisplayMonitors")]
    private static partial int EnumDisplayMonitorsImpl(
        nint hdc,
        nint clip,
        delegate* unmanaged<nint, nint, NativeRect*, nint, int> callback,
        nint data);

    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getmonitorinfow
    [LibraryImport(User32, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetMonitorInfo(nint monitor, ref NativeMonitorInfoEx info);

    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos
    [LibraryImport(User32, EntryPoint = "SetWindowPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-createsyntheticpointerdevice (Windows 10 1809+)
    [LibraryImport(User32, EntryPoint = "CreateSyntheticPointerDevice", SetLastError = true)]
    internal static partial nint CreateSyntheticPointerDevice(uint pointerType, uint maxCount, uint mode);

    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-injectsyntheticpointerinput (Windows 10 1809+)
    [LibraryImport(User32, EntryPoint = "InjectSyntheticPointerInput", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool InjectSyntheticPointerInput(nint device, in NativePointerTypeInfoPen pointerInfo, uint count);

    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-destroysyntheticpointerdevice
    [LibraryImport(User32, EntryPoint = "DestroySyntheticPointerDevice")]
    internal static partial void DestroySyntheticPointerDevice(nint device);

    // https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-globaladdatomw
    [LibraryImport("kernel32.dll", EntryPoint = "GlobalAddAtomW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial ushort GlobalAddAtom(string name);

    // https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-globaldeleteatom
    [LibraryImport("kernel32.dll", EntryPoint = "GlobalDeleteAtom")]
    internal static partial ushort GlobalDeleteAtom(ushort atom);

    // https://learn.microsoft.com/en-us/windows/win32/api/shellscalingapi/nf-shellscalingapi-getdpiformonitor (возвращает HRESULT)
    [LibraryImport("shcore.dll", EntryPoint = "GetDpiForMonitor")]
    internal static partial int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    // https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-createdcw
    [LibraryImport("gdi32.dll", EntryPoint = "CreateDCW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateDC(string driver, string device, string? output, nint initData);

    // https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-getdevicecaps
    [LibraryImport("gdi32.dll", EntryPoint = "GetDeviceCaps")]
    internal static partial int GetDeviceCaps(nint hdc, int index);

    // https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-deletedc
    [LibraryImport("gdi32.dll", EntryPoint = "DeleteDC")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteDC(nint hdc);
}
