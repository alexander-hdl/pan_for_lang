namespace PenForLang.Platform.Windows.Native;

/// <summary>
/// Константы Win32. Значения сверены с WinUser.h / tpcshrd.h (Windows SDK 10.0.26100) и страницами learn.microsoft.com
/// (ссылки — рядом с группами).
/// </summary>
internal static class NativeConstants
{
    // --- Сообщения WM_POINTER (WinUser.h) ---
    public const uint WmPointerDown = 0x0246;
    public const uint WmPointerUpdate = 0x0245;
    public const uint WmPointerUp = 0x0247;
    public const uint WmPointerEnter = 0x0249;
    public const uint WmPointerLeave = 0x024A;
    public const uint WmPointerCaptureChanged = 0x024C;

    // --- Мышь ---
    public const uint WmMouseMove = 0x0200;
    public const uint WmLButtonDown = 0x0201;
    public const uint WmLButtonUp = 0x0202;
    public const uint WmCaptureChanged = 0x0215;
    public const uint MkLButton = 0x0001;

    // --- Окно ---
    public const uint WmNcDestroy = 0x0082;

    /// <summary>WM_TABLET_DEFBASE (0x02C0) + 12 — tpcshrd.h.</summary>
    public const uint WmTabletQuerySystemGestureStatus = 0x02C0 + 12;

    // --- POINTER_INPUT_TYPE: https://learn.microsoft.com/en-us/windows/win32/api/winuser/ne-winuser-tagpointer_input_type ---
    public const uint PtPen = 3;

    // --- POINTER_FLAGS: https://learn.microsoft.com/en-us/windows/win32/inputmsg/pointer-flags-contants ---
    public const uint PointerFlagNew = 0x00000001;
    public const uint PointerFlagInRange = 0x00000002;
    public const uint PointerFlagInContact = 0x00000004;
    public const uint PointerFlagCanceled = 0x00008000;
    public const uint PointerFlagDown = 0x00010000;
    public const uint PointerFlagUpdate = 0x00020000;
    public const uint PointerFlagUp = 0x00040000;

    // --- PEN_FLAGS: https://learn.microsoft.com/en-us/windows/win32/inputmsg/pen-flags-constants ---
    public const uint PenFlagBarrel = 0x00000001;
    public const uint PenFlagInverted = 0x00000002;
    public const uint PenFlagEraser = 0x00000004;

    // --- PEN_MASK (WinUser.h) ---
    public const uint PenMaskPressure = 0x00000001;
    public const uint PenMaskRotation = 0x00000002;
    public const uint PenMaskTiltX = 0x00000004;
    public const uint PenMaskTiltY = 0x00000008;

    /// <summary>Нормированное давление POINTER_PEN_INFO.pressure: 0..1024.</summary>
    public const float PenPressureMax = 1024f;

    // --- POINTER_DEVICE_TYPE: https://learn.microsoft.com/en-us/windows/win32/api/winuser/ne-winuser-pointer_device_type ---
    public const uint PointerDeviceTypeIntegratedPen = 1;
    public const uint PointerDeviceTypeExternalPen = 2;
    public const uint PointerDeviceTypeTouch = 3;
    public const uint PointerDeviceTypeTouchPad = 4;

    // --- POINTER_FEEDBACK_MODE (только для инжектора): https://learn.microsoft.com/en-us/windows/win32/api/winuser/ne-winuser-pointer_feedback_mode ---
    public const uint PointerFeedbackNone = 3;

    // --- Монитор / GDI ---
    public const uint MonitorInfoFlagPrimary = 0x00000001;
    public const int MdtEffectiveDpi = 0;
    public const int HorzSize = 4;
    public const int VertSize = 6;

    // --- SetWindowPos ---
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpFrameChanged = 0x0020;
    public const uint SwpShowWindow = 0x0040;

    /// <summary>HWND_TOPMOST = (HWND)-1.</summary>
    public static readonly nint HwndTopmost = -1;

    /// <summary>HWND_NOTOPMOST = (HWND)-2.</summary>
    public static readonly nint HwndNoTopmost = -2;
}
