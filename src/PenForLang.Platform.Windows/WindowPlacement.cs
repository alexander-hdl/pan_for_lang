using PenForLang.Platform.Windows.Native;

namespace PenForLang.Platform.Windows;

/// <summary>
/// Размещение окна в ФИЗИЧЕСКИХ пикселях виртуального экрана (ARCHITECTURE.md §7.4 п.2: <c>SetWindowPos</c> после
/// <c>SourceInitialized</c>). WPF-свойства <c>Left/Top/Width/Height</c> — DIP и на мониторах с разным DPI дают сдвиги,
/// поэтому окно ребёнка ставится напрямую Win32.
/// </summary>
/// <remarks>
/// WPF при смене DPI монитора сам вызывает <c>SetWindowPos</c> с предложенным системой прямоугольником и перетирает размер.
/// Поэтому окно сначала переносится на нужный монитор без изменения размера (<see cref="MoveTo"/>), а точная постановка
/// (<see cref="Place"/>) повторяется отложенно, после обработки WPF, пока <see cref="GetBounds"/> не совпадёт с целью.
/// Окно здесь не показывается (<c>SWP_SHOWWINDOW</c> не используется): показ — дело <c>Window.Show</c>.
/// </remarks>
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
            NativeConstants.SwpNoActivate | NativeConstants.SwpFrameChanged);
    }

    /// <summary>Переносит окно левым верхним углом в точку виртуального экрана, не меняя размер (<c>SWP_NOSIZE</c>).</summary>
    public static bool MoveTo(nint hwnd, int leftPx, int topPx)
    {
        if (hwnd == 0)
        {
            return false;
        }

        return NativeMethods.SetWindowPos(
            hwnd,
            0,
            leftPx,
            topPx,
            0,
            0,
            NativeConstants.SwpNoSize | NativeConstants.SwpNoZOrder | NativeConstants.SwpNoActivate);
    }

    /// <summary>Текущий прямоугольник окна в физических px (<c>GetWindowRect</c>); <c>null</c>, если окно недоступно.</summary>
    public static MonitorRect? GetBounds(nint hwnd) =>
        hwnd != 0 && NativeMethods.GetWindowRect(hwnd, out NativeRect r)
            ? new MonitorRect(r.Left, r.Top, r.Right, r.Bottom)
            : null;
}
