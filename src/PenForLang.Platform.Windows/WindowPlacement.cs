using PenForLang.Platform.Windows.Native;

namespace PenForLang.Platform.Windows;

/// <summary>
/// Размещение окна в ФИЗИЧЕСКИХ пикселях виртуального экрана (ARCHITECTURE.md §7.4 п.2: <c>SetWindowPos</c> после
/// <c>SourceInitialized</c>). WPF-свойства <c>Left/Top/Width/Height</c> — DIP и на мониторах с разным DPI дают сдвиги,
/// поэтому окно ребёнка ставится напрямую Win32.
/// </summary>
public static class WindowPlacement
{
    /// <summary>Ставит окно в прямоугольник <paramref name="bounds"/> (физические px).</summary>
    /// <param name="hwnd">Дескриптор окна.</param>
    /// <param name="bounds">Прямоугольник на виртуальном экране.</param>
    /// <param name="topmost">Поверх остальных окон (<c>HWND_TOPMOST</c>); иначе <c>HWND_NOTOPMOST</c>.</param>
    /// <returns><c>true</c>, если вызов удался.</returns>
    public static bool Place(nint hwnd, MonitorRect bounds, bool topmost)
    {
        if (hwnd == 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return false;
        }

        return NativeMethods.SetWindowPos(
            hwnd,
            topmost ? NativeConstants.HwndTopmost : NativeConstants.HwndNoTopmost,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            NativeConstants.SwpNoActivate | NativeConstants.SwpFrameChanged | NativeConstants.SwpShowWindow);
    }
}
