using System.Globalization;

namespace PenForLang.Tools.InkLab;

/// <summary>
/// Параметры командной строки InkLab.
/// <list type="bullet">
/// <item><c>--mouse</c> — мышь как перо (<c>MousePenSource</c>) вместо <c>Win32PointerPenSource</c> (разработка без планшета).</item>
/// <item><c>--corpus-dir &lt;папка&gt;</c> — корень корпуса записей (по умолчанию <c>%LOCALAPPDATA%\PenForLang\corpus</c>).</item>
/// <item><c>--content-dir &lt;папка&gt;</c> — корень контента (в нём <c>styles/zaner-bloser</c>) вместо найденного рядом с программой.</item>
/// <item><c>--monitor &lt;N&gt;</c> — открыть окно развёрнутым на мониторе номер N (с 1); иначе — на мониторе пера, если он найден.</item>
/// <item><c>--xheight-mm &lt;мм&gt;</c> — x-height линейки письма в миллиметрах (по умолчанию 15).</item>
/// <item><c>--subject &lt;id&gt;</c> — подставить идентификатор субъекта на вкладке «Запись».</item>
/// <item><c>--annotator &lt;id&gt;</c> — идентификатор разметчика (по умолчанию <c>A</c>).</item>
/// <item><c>--exit-after &lt;секунды&gt;</c> — закрыть окно само (для проверок запуска).</item>
/// </list>
/// </summary>
/// <param name="UseMouse">Мышь как перо.</param>
/// <param name="CorpusDirectory">Корень корпуса или <c>null</c> — по умолчанию.</param>
/// <param name="ContentDirectory">Корень контента или <c>null</c> — найти рядом с программой.</param>
/// <param name="MonitorIndex">Номер монитора (с 1) или <c>null</c>.</param>
/// <param name="XHeightMm">x-height линейки письма, мм.</param>
/// <param name="Subject">Предзаполненный идентификатор субъекта.</param>
/// <param name="Annotator">Идентификатор разметчика.</param>
/// <param name="ExitAfter">Автозакрытие.</param>
/// <param name="UnknownArguments">Нераспознанные аргументы (показываются в строке состояния).</param>
public sealed record LabOptions(
    bool UseMouse,
    string? CorpusDirectory,
    string? ContentDirectory,
    int? MonitorIndex,
    double XHeightMm,
    string? Subject,
    string Annotator,
    TimeSpan? ExitAfter,
    IReadOnlyList<string> UnknownArguments)
{
    /// <summary>x-height линейки по умолчанию, мм.</summary>
    public const double DefaultXHeightMm = 15.0;

    /// <summary>Разбирает аргументы; неизвестные собираются в <see cref="UnknownArguments"/>, исключений нет.</summary>
    public static LabOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        bool mouse = false;
        string? corpus = null;
        string? content = null;
        int? monitor = null;
        double xHeight = DefaultXHeightMm;
        string? subject = null;
        string annotator = "A";
        TimeSpan? exitAfter = null;
        var unknown = new List<string>();

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            bool hasValue = i + 1 < args.Count;
            switch (arg.ToLowerInvariant())
            {
                case "--mouse":
                    mouse = true;
                    break;
                case "--corpus-dir" when hasValue && !string.IsNullOrWhiteSpace(args[i + 1]):
                    corpus = args[++i];
                    break;
                case "--content-dir" when hasValue && !string.IsNullOrWhiteSpace(args[i + 1]):
                    content = args[++i];
                    break;
                case "--subject" when hasValue && !string.IsNullOrWhiteSpace(args[i + 1]):
                    subject = args[++i];
                    break;
                case "--annotator" when hasValue && !string.IsNullOrWhiteSpace(args[i + 1]):
                    annotator = args[++i];
                    break;
                case "--monitor" when hasValue
                    && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) && index >= 1:
                    monitor = index;
                    i++;
                    break;
                case "--xheight-mm" when hasValue
                    && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double xh)
                    && double.IsFinite(xh) && xh is >= 3 and <= 40:
                    xHeight = xh;
                    i++;
                    break;
                case "--exit-after" when hasValue
                    && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                    && double.IsFinite(seconds) && seconds > 0:
                    exitAfter = TimeSpan.FromSeconds(seconds);
                    i++;
                    break;
                default:
                    unknown.Add(arg);
                    break;
            }
        }

        return new LabOptions(mouse, corpus, content, monitor, xHeight, subject, annotator, exitAfter, unknown);
    }
}
