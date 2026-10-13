namespace PenForLang.App.Wpf;

/// <summary>
/// Параметры командной строки (V0.5, режим разработки и проверки на железе).
/// <list type="bullet">
/// <item><c>--mouse</c> — мышь как перо (<c>MousePenSource</c>) вместо <c>Win32PointerPenSource</c>.</item>
/// <item><c>--wpf-stylus</c> — оставить стилус WPF (WISP) включённым: не задавать
/// <c>Switch.System.Windows.Input.Stylus.DisableStylusAndTouchSupport</c> (по умолчанию — задан в runtimeconfig, ADR-003).</item>
/// <item><c>--windowed</c> — не разворачивать окно на весь монитор пера и не ставить поверх остальных (удобно при отладке).</item>
/// <item><c>--exit-after &lt;секунды&gt;</c> — закрыть окно само через указанное время (для проверок и защиты от «застрявшего» киоска).</item>
/// </list>
/// </summary>
/// <param name="UseMouse">Мышь как перо.</param>
/// <param name="KeepWpfStylus">Не отключать стилус WPF.</param>
/// <param name="Windowed">Оконный режим вместо полноэкранного.</param>
/// <param name="ExitAfter">Автозакрытие через интервал.</param>
/// <param name="UnknownArguments">Нераспознанные аргументы (показываются в панели).</param>
public sealed record AppOptions(
    bool UseMouse,
    bool KeepWpfStylus,
    bool Windowed,
    TimeSpan? ExitAfter,
    IReadOnlyList<string> UnknownArguments)
{
    /// <summary>Имя переключателя WPF, отключающего WISP/стилус (ARCHITECTURE.md §7.1).</summary>
    public const string DisableStylusSwitch = "Switch.System.Windows.Input.Stylus.DisableStylusAndTouchSupport";

    /// <summary>Разбирает аргументы; неизвестные собираются в <see cref="UnknownArguments"/>, исключений нет.</summary>
    public static AppOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        bool mouse = false;
        bool wpfStylus = false;
        bool windowed = false;
        TimeSpan? exitAfter = null;
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
                case "--windowed":
                    windowed = true;
                    break;
                case "--exit-after":
                    if (i + 1 < args.Count
                        && double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double seconds)
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
                default:
                    unknown.Add(arg);
                    break;
            }
        }

        return new AppOptions(mouse, wpfStylus, windowed, exitAfter, unknown);
    }
}
