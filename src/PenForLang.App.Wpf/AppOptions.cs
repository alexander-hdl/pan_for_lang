using System.Globalization;

namespace PenForLang.App.Wpf;

/// <summary>
/// Параметры командной строки (V0.5, режим разработки и проверки на железе).
/// <list type="bullet">
/// <item><c>--mouse</c> — мышь как перо (<c>MousePenSource</c>) вместо <c>Win32PointerPenSource</c>.</item>
/// <item><c>--wpf-stylus</c> — оставить стилус WPF (WISP) включённым: не задавать
/// <c>Switch.System.Windows.Input.Stylus.DisableStylusAndTouchSupport</c> (по умолчанию — задан в runtimeconfig, ADR-003).</item>
/// <item><c>--wpf-pointer</c> — стилус WPF включён и использует WM_POINTER
/// (<c>Switch.System.Windows.Input.Stylus.EnablePointerSupport=true</c>); необязательный прогон протокола.</item>
/// <item><c>--windowed</c> — не разворачивать окно на весь монитор пера и не ставить поверх остальных (удобно при отладке).</item>
/// <item><c>--monitor &lt;N&gt;</c> — принудительно открыть окно на мониторе номер N (нумерация и имена — в панели и отчёте).</item>
/// <item><c>--exit-after &lt;секунды&gt;</c> — закрыть окно само через указанное время (для проверок и защиты от «застрявшего» киоска).</item>
/// <item><c>--report-dir &lt;папка&gt;</c> — писать отчёт сюда вместо рабочего стола.</item>
/// </list>
/// </summary>
/// <param name="UseMouse">Мышь как перо.</param>
/// <param name="KeepWpfStylus">Не отключать стилус WPF.</param>
/// <param name="WpfPointer">Стилус WPF на WM_POINTER (<c>EnablePointerSupport</c>).</param>
/// <param name="Windowed">Оконный режим вместо полноэкранного.</param>
/// <param name="ExitAfter">Автозакрытие через интервал.</param>
/// <param name="MonitorIndex">Номер монитора (с 1), на котором открыть окно.</param>
/// <param name="ReportDirectory">Папка отчёта; <c>null</c> — рабочий стол.</param>
/// <param name="UnknownArguments">Нераспознанные аргументы (показываются в панели).</param>
public sealed record AppOptions(
    bool UseMouse,
    bool KeepWpfStylus,
    bool WpfPointer,
    bool Windowed,
    TimeSpan? ExitAfter,
    int? MonitorIndex,
    string? ReportDirectory,
    IReadOnlyList<string> UnknownArguments)
{
    /// <summary>Имя переключателя WPF, отключающего WISP/стилус (ARCHITECTURE.md §7.1).</summary>
    public const string DisableStylusSwitch = "Switch.System.Windows.Input.Stylus.DisableStylusAndTouchSupport";

    /// <summary>Имя переключателя WPF, включающего стилус на WM_POINTER.</summary>
    public const string EnablePointerSupportSwitch = "Switch.System.Windows.Input.Stylus.EnablePointerSupport";

    /// <summary>Разбирает аргументы; неизвестные собираются в <see cref="UnknownArguments"/>, исключений нет.</summary>
    public static AppOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        bool mouse = false;
        bool wpfStylus = false;
        bool wpfPointer = false;
        bool windowed = false;
        TimeSpan? exitAfter = null;
        int? monitor = null;
        string? reportDir = null;
        var unknown = new List<string>();

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--mouse":
                    mouse = true;
                    break;
                case "--wpf-stylus":
                    wpfStylus = true;
                    break;
                case "--wpf-pointer":
                    wpfPointer = true;
                    break;
                case "--windowed":
                    windowed = true;
                    break;
                case "--exit-after":
                    if (i + 1 < args.Count
                        && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                        && seconds > 0
                        && double.IsFinite(seconds))
                    {
                        exitAfter = TimeSpan.FromSeconds(seconds);
                        i++;
                    }
                    else
                    {
                        unknown.Add(arg);
                    }

                    break;
                case "--monitor":
                    if (i + 1 < args.Count
                        && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
                        && index >= 1)
                    {
                        monitor = index;
                        i++;
                    }
                    else
                    {
                        unknown.Add(arg);
                    }

                    break;
                case "--report-dir":
                    if (i + 1 < args.Count && !string.IsNullOrWhiteSpace(args[i + 1]))
                    {
                        reportDir = args[i + 1];
                        i++;
                    }
                    else
                    {
                        unknown.Add(arg);
                    }

                    break;
                default:
                    unknown.Add(arg);
                    break;
            }
        }

        return new AppOptions(mouse, wpfStylus || wpfPointer, wpfPointer, windowed, exitAfter, monitor, reportDir, unknown);
    }
}
