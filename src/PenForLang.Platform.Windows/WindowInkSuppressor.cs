using System.Runtime.InteropServices;
using System.Windows.Interop;
using PenForLang.Platform.Windows.Native;

namespace PenForLang.Platform.Windows;

/// <summary>Итог <see cref="WindowInkSuppressor.Apply"/>.</summary>
/// <param name="TabletPropertySet">Свойство <c>MicrosoftTabletPenServiceProperty</c> выставлено (press-and-hold, flicks, круги).</param>
/// <param name="FeedbackSettingsApplied">Сколько видов обратной связи Windows Ink отключено через <c>SetWindowFeedbackSetting</c>.</param>
/// <param name="Errors">Описания неудачных шагов (код Win32); пусто при полном успехе.</param>
public sealed record InkSuppressionResult(bool TabletPropertySet, int FeedbackSettingsApplied, IReadOnlyList<string> Errors)
{
    /// <summary>Все шаги выполнены.</summary>
    public bool Succeeded => TabletPropertySet && FeedbackSettingsApplied == WindowInkSuppressor.PenFeedbackTypes.Count;
}

/// <summary>
/// Подавление помех Windows Ink ТОЛЬКО для своего окна (ARCHITECTURE.md §7.2); системные настройки Windows не меняются.
/// <list type="bullet">
/// <item>Свойство окна <c>MicrosoftTabletPenServiceProperty</c> (<c>SetProp</c>, значение — маска <c>TABLET_DISABLE_*</c>):
/// press-and-hold → правый клик, круги и рябь при касании и нажатии кнопки, flicks.
/// <see href="https://learn.microsoft.com/en-us/windows/win32/tablet/wm-tablet-querysystemgesturestatus-message"/>.</item>
/// <item>Те же флаги возвращаются в ответ на <c>WM_TABLET_QUERYSYSTEMGESTURESTATUS</c> (при <see cref="Attach"/>).</item>
/// <item><c>SetWindowFeedbackSetting(hwnd, FEEDBACK_PEN_*, 0, sizeof(BOOL), &amp;FALSE)</c> для пяти видов пера.
/// <see href="https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowfeedbacksetting"/>.</item>
/// </list>
/// WPF-свойства <c>Stylus.IsPressAndHoldEnabled</c> и др. задаются в XAML окна. Свойство окна снимается при <c>WM_NCDESTROY</c>
/// (требование документации <c>SetProp</c>). Эффект на XPPen — [не проверено, V0.5/S1].
/// </summary>
public static class WindowInkSuppressor
{
    /// <summary>Имя глобального атома/свойства окна, которое читает служба пера.</summary>
    public const string PenServicePropertyName = "MicrosoftTabletPenServiceProperty";

    /// <summary><c>TABLET_DISABLE_PRESSANDHOLD</c> — press-and-hold (правый клик).</summary>
    public const uint DisablePressAndHold = 0x00000001;

    /// <summary><c>TABLET_DISABLE_PENTAPFEEDBACK</c> — рябь при касании пера.</summary>
    public const uint DisablePenTapFeedback = 0x00000008;

    /// <summary><c>TABLET_DISABLE_PENBARRELFEEDBACK</c> — круг при нажатии кнопки пера.</summary>
    public const uint DisablePenBarrelFeedback = 0x00000010;

    /// <summary><c>TABLET_DISABLE_FLICKS</c> — flicks (назад, вперёд, вверх, вниз).</summary>
    public const uint DisableFlicks = 0x00010000;

    /// <summary>Итоговая маска отключаемого для окна.</summary>
    public const uint DisableMask = DisablePressAndHold | DisablePenTapFeedback | DisablePenBarrelFeedback | DisableFlicks;

    /// <summary>Виды обратной связи пера, отключаемые <c>SetWindowFeedbackSetting</c> (значения <c>FEEDBACK_TYPE</c>).</summary>
    public static IReadOnlyList<(string Name, uint Value)> PenFeedbackTypes { get; } =
    [
        ("FEEDBACK_PEN_BARRELVISUALIZATION", 2),
        ("FEEDBACK_PEN_TAP", 3),
        ("FEEDBACK_PEN_DOUBLETAP", 4),
        ("FEEDBACK_PEN_PRESSANDHOLD", 5),
        ("FEEDBACK_PEN_RIGHTTAP", 6),
    ];

    /// <summary>
    /// Чистое правило ответа на системные сообщения: для <c>WM_TABLET_QUERYSYSTEMGESTURESTATUS</c> возвращает маску
    /// <see cref="DisableMask"/>.
    /// </summary>
    /// <returns><c>true</c>, если сообщение обработано и результат в <paramref name="result"/>.</returns>
    public static bool TryGetMessageResult(uint message, out nint result)
    {
        if (message == NativeConstants.WmTabletQuerySystemGestureStatus)
        {
            result = (nint)DisableMask;
            return true;
        }

        result = 0;
        return false;
    }

    /// <summary>Подключает ответ на <c>WM_TABLET_QUERYSYSTEMGESTURESTATUS</c> и снятие свойства при уничтожении окна.</summary>
    public static void Attach(HwndSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.AddHook(Hook);
    }

    /// <summary>Применяет подавление к окну <paramref name="hwnd"/>.</summary>
    public static InkSuppressionResult Apply(nint hwnd)
    {
        var errors = new List<string>();

        // Документированный порядок: GlobalAddAtom → SetProp по имени → (атом можно удалить).
        ushort atom = NativeMethods.GlobalAddAtom(PenServicePropertyName);
        if (atom == 0)
        {
            errors.Add($"GlobalAddAtom failed, Win32 error {Marshal.GetLastPInvokeError()}");
        }

        bool propertySet = NativeMethods.SetProp(hwnd, PenServicePropertyName, (nint)DisableMask);
        if (!propertySet)
        {
            errors.Add($"SetProp({PenServicePropertyName}) failed, Win32 error {Marshal.GetLastPInvokeError()}");
        }

        if (atom != 0)
        {
            NativeMethods.GlobalDeleteAtom(atom);
        }

        int applied = 0;
        int disabled = 0; // BOOL FALSE
        foreach ((string name, uint value) in PenFeedbackTypes)
        {
            if (NativeMethods.SetWindowFeedbackSetting(hwnd, value, 0, sizeof(int), in disabled))
            {
                applied++;
            }
            else
            {
                errors.Add($"SetWindowFeedbackSetting({name}) failed, Win32 error {Marshal.GetLastPInvokeError()}");
            }
        }

        return new InkSuppressionResult(propertySet, applied, errors);
    }

    /// <summary>Снимает свойство окна (возвращает прежнее поведение службы пера для окна).</summary>
    public static void Remove(nint hwnd) => NativeMethods.RemoveProp(hwnd, PenServicePropertyName);

    private static nint Hook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (TryGetMessageResult((uint)msg, out nint result))
        {
            handled = true;
            return result;
        }

        if ((uint)msg == NativeConstants.WmNcDestroy)
        {
            Remove(hwnd);
        }

        return 0;
    }
}
