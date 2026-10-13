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
/// <item><c>--dev</c> / <c>--no-dev</c> (V0.6) — режим разработчика: penlog каждой попытки пишется в dev-папку (только взрослый, ребёнку в этом
/// режиме не писать) и показывается dev-оверлей. По умолчанию ВЫКЛЮЧЕН; запускатели протокола железа указывают <c>--dev</c> явно.</item>
/// <item><c>--pressure-width</c> (V0.6) — только для разработчика: ширина линии зависит от давления (по умолчанию выключено: методика не даёт
/// видимой обратной связи по давлению; клавиша P переключает на лету).</item>
/// <item><c>--penlog-dir &lt;папка&gt;</c> (V0.6) — писать penlog сюда вместо <c>%LOCALAPPDATA%\PenForLang\dev\penlogs</c>.</item>
/// <item><c>--content-dir &lt;папка&gt;</c> (V0.6) — корень контента (в нём <c>styles/zaner-bloser</c>) вместо найденного рядом с программой.</item>
/// <item><c>--xheight-mm &lt;мм&gt;</c> (V0.6) — x-height линейки прописи в миллиметрах (по умолчанию 15).</item>
/// <item><c>--latency</c> (V0.6) — сразу открыть режим замера задержки «перо → пиксель» (клавиша L переключает его).</item>
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
/// <param name="Dev">Режим разработчика: запись penlog и dev-оверлей (по умолчанию выключен).</param>
/// <param name="PenLogDirectory">Папка penlog; <c>null</c> — dev-папка по умолчанию.</param>
/// <param name="ContentDirectory">Корень контента; <c>null</c> — найти рядом с программой.</param>
/// <param name="StartInLatencyMode">Открыть режим замера задержки.</param>
/// <param name="XHeightMm">x-height линейки, мм.</param>
/// <param name="PressureWidth">Ширина линии от давления (только разработчик).</param>
public sealed record AppOptions(
    bool UseMouse,
    bool KeepWpfStylus,
    bool WpfPointer,
    bool Windowed,
    TimeSpan? ExitAfter,
    int? MonitorIndex,
    string? ReportDirectory,
    IReadOnlyList<string> UnknownArguments,
    bool Dev = false,
    string? PenLogDirectory = null,
    string? ContentDirectory = null,
    bool StartInLatencyMode = false,
    double XHeightMm = SandboxLayout.DefaultXHeightMm,
    bool PressureWidth = false)
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
        bool dev = false;
        bool pressureWidth = false;
        string? penLogDir = null;
        string? contentDir = null;
        bool latency = false;
        double xHeightMm = SandboxLayout.DefaultXHeightMm;
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
                case "--dev":
                    dev = true;
                    break;
                case "--no-dev":
                    dev = false;
                    break;
                case "--pressure-width":
                    pressureWidth = true;
                    break;
                case "--latency":
                    latency = true;
                    break;
                case "--xheight-mm":
                    if (i + 1 < args.Count
                        && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double xh)
                        && double.IsFinite(xh)
                        && xh is >= 3 and <= 40)
                    {
                        xHeightMm = xh;
                        i++;
                    }
                    else
                    {
                        unknown.Add(arg);
                    }

                    break;
                case "--penlog-dir":
                    if (i + 1 < args.Count && !string.IsNullOrWhiteSpace(args[i + 1]))
                    {
                        penLogDir = args[i + 1];
                        i++;
                    }
                    else
                    {
                        unknown.Add(arg);
                    }

                    break;
                case "--content-dir":
                    if (i + 1 < args.Count && !string.IsNullOrWhiteSpace(args[i + 1]))
                    {
                        contentDir = args[i + 1];
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

        return new AppOptions(mouse, wpfStylus || wpfPointer, wpfPointer, windowed, exitAfter, monitor, reportDir, unknown, dev, penLogDir, contentDir, latency, xHeightMm, pressureWidth);
    }
}
