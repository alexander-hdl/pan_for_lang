using System.Globalization;
using System.Text;
using PenForLang.Ink;
using PenForLang.Platform.Windows;
using PenForLang.Wpf.Controls;

namespace PenForLang.App.Wpf;

/// <summary>Результат попадания в одну из 9 мишеней точности (последнее касание у этой мишени).</summary>
/// <param name="Number">Номер мишени 1..9 (слева направо, сверху вниз).</param>
/// <param name="DxPx">Смещение касания от центра мишени по X, физические px.</param>
/// <param name="DyPx">Смещение по Y, физические px.</param>
/// <param name="DxMm">То же по X, мм (по физическому размеру монитора, 0 — размер неизвестен).</param>
/// <param name="DyMm">То же по Y, мм.</param>
/// <param name="Count">Сколько касаний у этой мишени было за сеанс.</param>
public sealed record TargetResult(int Number, double DxPx, double DyPx, double DxMm, double DyMm, int Count);

/// <summary>Снимок по клавише S: ключевые счётчики на момент нажатия.</summary>
/// <param name="Number">Номер снимка с 1.</param>
/// <param name="Time">Момент снимка.</param>
/// <param name="Stats">Статистика потока точек.</param>
/// <param name="PointerStats">Счётчики WM_POINTER (<c>null</c> в режиме мыши).</param>
/// <param name="ButtonClicks">Нажатий тестовой кнопки.</param>
/// <param name="WpfMouseDowns">Мышиных нажатий, дошедших до WPF.</param>
public sealed record ReportSnapshot(int Number, DateTimeOffset Time, PenStats Stats, PointerDiagnostics? PointerStats, long ButtonClicks, long WpfMouseDowns);

/// <summary>Всё, что попадает в отчёт сеанса.</summary>
public sealed record ReportInput
{
    /// <summary>Причина записи: <c>exit</c>, <c>snapshot</c>, <c>crash</c>.</summary>
    public required string Reason { get; init; }

    /// <summary>Момент записи.</summary>
    public required DateTimeOffset Now { get; init; }

    /// <summary>Момент старта приложения.</summary>
    public required DateTimeOffset Started { get; init; }

    /// <summary>Аргументы командной строки.</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>Описание ОС (<c>RuntimeInformation.OSDescription</c>).</summary>
    public required string OsDescription { get; init; }

    /// <summary>Версия ОС (major.minor.build).</summary>
    public required string OsVersion { get; init; }

    /// <summary>ОС не ниже минимально поддерживаемой (Windows 10 22H2).</summary>
    public required bool OsSupported { get; init; }

    /// <summary>Описание среды .NET.</summary>
    public required string FrameworkDescription { get; init; }

    /// <summary>Разрядность процесса.</summary>
    public required string Architecture { get; init; }

    /// <summary>Версия приложения.</summary>
    public required string AppVersion { get; init; }

    /// <summary>Переключатель <c>DisableStylusAndTouchSupport</c>.</summary>
    public required bool StylusDisabledSwitch { get; init; }

    /// <summary>Переключатель <c>EnablePointerSupport</c>.</summary>
    public required bool PointerSupportSwitch { get; init; }

    /// <summary>Режим источника: <c>Win32Pointer</c>, <c>Mouse</c>.</summary>
    public required string SourceKind { get; init; }

    /// <summary>Название устройства.</summary>
    public string? DeviceProduct { get; init; }

    /// <summary>Устройство сообщило наклон.</summary>
    public bool TiltReported { get; init; }

    /// <summary>Все мониторы (порядок — номера для <c>--monitor N</c>).</summary>
    public required IReadOnlyList<MonitorDescription> Monitors { get; init; }

    /// <summary>Выбор монитора пера.</summary>
    public required PenMonitorChoice Choice { get; init; }

    /// <summary>Устройства-указатели (<c>GetPointerDevices</c>).</summary>
    public required IReadOnlyList<PointerDeviceDescription> Devices { get; init; }

    /// <summary>Масштаб DPI окна.</summary>
    public double DpiScale { get; init; } = 1.0;

    /// <summary>Прямоугольник холста.</summary>
    public PenSurface? Surface { get; init; }

    /// <summary>Окно совпадает с целевым прямоугольником.</summary>
    public bool? WindowFitsTarget { get; init; }

    /// <summary>Итог подавления помех Windows Ink.</summary>
    public InkSuppressionResult? Suppression { get; init; }

    /// <summary>Счётчики WM_POINTER (<c>null</c> в режиме мыши).</summary>
    public PointerDiagnostics? PointerStats { get; init; }

    /// <summary>Мышиных движений, дошедших до WPF.</summary>
    public long WpfMouseMoves { get; init; }

    /// <summary>Мышиных нажатий, дошедших до WPF.</summary>
    public long WpfMouseDowns { get; init; }

    /// <summary>Движений стилуса WPF.</summary>
    public long WpfStylusMoves { get; init; }

    /// <summary>Касаний стилуса WPF.</summary>
    public long WpfStylusDowns { get; init; }

    /// <summary>Нажатий тестовой кнопки в полосе вне холста.</summary>
    public long ButtonClicks { get; init; }

    /// <summary>Статистика потока точек.</summary>
    public required PenStats Stats { get; init; }

    /// <summary>Результаты мишеней.</summary>
    public required IReadOnlyList<TargetResult> Targets { get; init; }

    /// <summary>Снимки по клавише S.</summary>
    public required IReadOnlyList<ReportSnapshot> Snapshots { get; init; }

    /// <summary>Ошибки и предупреждения за сеанс.</summary>
    public required IReadOnlyList<string> Errors { get; init; }

    /// <summary>Путь файла отчёта.</summary>
    public string? ReportPath { get; init; }

    /// <summary>Внутренняя задержка «метка точки → Rendering» (V0.6); пусто, если не измерялась.</summary>
    public RenderLatency Latency { get; init; }

    /// <summary>Штрихов в живых чернилах на момент записи (V0.6).</summary>
    public int LiveInkStrokes { get; init; }

    /// <summary>Отрезков в живых чернилах на момент записи.</summary>
    public int LiveInkSegments { get; init; }

    /// <summary>Сколько раз за сеанс включался режим GC <c>SustainedLowLatency</c>.</summary>
    public int GcLowLatencyActivations { get; init; }

    /// <summary>Число сборок GC поколений 0, 1, 2 за сеанс.</summary>
    public (int Gen0, int Gen1, int Gen2) GcCollections { get; init; }

    /// <summary>Суммарное время пауз GC.</summary>
    public TimeSpan GcPause { get; init; }

    /// <summary>Режим разработчика (запись penlog и оверлей).</summary>
    public bool DevMode { get; init; }

    /// <summary>Папка penlog (<c>null</c> — запись выключена).</summary>
    public string? PenLogDirectory { get; init; }

    /// <summary>Записанные за сеанс попытки.</summary>
    public IReadOnlyList<RecordedAttempt> RecordedAttempts { get; init; } = [];

    /// <summary>Ошибка записи penlog.</summary>
    public string? PenLogError { get; init; }
}

/// <summary>
/// Итоговый отчёт сеанса проверки пера (markdown): всё, что протокол <c>docs/spikes/V0-hardware.md</c> просил переписывать
/// руками. Чистая функция от <see cref="ReportInput"/> — тестируется без окна и железа.
/// </summary>
public static class SessionReport
{
    /// <summary>Строит текст отчёта.</summary>
    public static string Build(ReportInput r)
    {
        ArgumentNullException.ThrowIfNull(r);
        CultureInfo inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();

        sb.AppendLine("# PenForLang V0.6 - отчёт проверки пера");
        sb.AppendLine();
        sb.AppendLine(inv, $"- Запись: **{r.Reason}**, {r.Now:yyyy-MM-dd HH:mm:ss zzz}; старт {r.Started:HH:mm:ss}, длительность {(r.Now - r.Started).TotalSeconds:0} с");
        sb.AppendLine(inv, $"- Аргументы: `{(r.Arguments.Count == 0 ? "(нет)" : string.Join(' ', r.Arguments))}`");
        sb.AppendLine(inv, $"- Файл отчёта: `{r.ReportPath ?? "-"}`");
        sb.AppendLine();

        AppendVerdict(sb, r, inv);
        AppendEnvironment(sb, r, inv);
        AppendMonitors(sb, r, inv);
        AppendPointer(sb, r, inv);
        AppendRate(sb, r, inv);
        AppendLiveInk(sb, r, inv);
        AppendPressure(sb, r, inv);
        AppendButtons(sb, r, inv);
        AppendTargets(sb, r, inv);
        AppendSnapshots(sb, r, inv);

        sb.AppendLine("## Ошибки и предупреждения");
        sb.AppendLine();
        if (r.Errors.Count == 0 && string.IsNullOrEmpty(ErrorLine(r)))
        {
            sb.AppendLine("Нет.");
        }
        else
        {
            foreach (string e in r.Errors)
            {
                sb.AppendLine(inv, $"- {e}");
            }

            if (!string.IsNullOrEmpty(ErrorLine(r)))
            {
                sb.AppendLine(inv, $"- {ErrorLine(r)}");
            }
        }

        return sb.ToString();
    }

    private static string? ErrorLine(ReportInput r) =>
        r.PointerStats is PointerDiagnostics p && p.LastHistoryError != 0
            ? $"Последний сбой GetPointerPenInfoHistory: код Win32 {p.LastHistoryError} (сбоев {p.HistoryFailures}, спасено запасным GetPointerPenInfo {p.HistoryFallbacks})"
            : null;

    private static void AppendVerdict(StringBuilder sb, ReportInput r, CultureInfo inv)
    {
        sb.AppendLine("## Краткие выводы (автоматические подсказки, не решение)");
        sb.AppendLine();
        PenStats s = r.Stats;
        if (r.PointerStats is PointerDiagnostics p)
        {
            bool arrive = p.PenMessages > 0;
            sb.AppendLine(inv, $"- WM_POINTER от пера доходят до окна: **{(arrive ? "ДА" : "НЕТ")}** (pen {p.PenMessages}, принято холстом {p.HandledMessages}, не-перо {p.NonPenMessages})");
            if (!arrive && (r.WpfMouseDowns > 0 || r.WpfStylusDowns > 0))
            {
                sb.AppendLine(inv, $"- Перо пришло в окно НЕ через WM_POINTER: WPF увидел мышь (нажатий {r.WpfMouseDowns}) / стилус (касаний {r.WpfStylusDowns}) - см. вариант Б в ADR-003");
            }

            sb.AppendLine(inv, $"- Метки времени ОС (PerformanceCount): **{(p.Samples > 0 ? (p.TimeFallbackSamples == 0 ? "есть" : $"нет у {p.TimeFallbackSamples} из {p.Samples} точек") : "нет данных")}**");
        }
        else
        {
            sb.AppendLine("- Режим мыши: проверка WM_POINTER не выполнялась.");
        }

        sb.AppendLine(inv, $"- Частота в контакте: медиана **{s.OverallMedianHz:0.0} Гц**, p5 {s.OverallP5Hz:0.0} Гц, интервалов > 12 мс: {s.GapsOverThreshold}, доля coalesced {Share(s.CoalescedSamples, s.TotalSamples):0.0}%");
        if (!r.Latency.IsEmpty)
        {
            sb.AppendLine(inv, $"- Внутренняя задержка «метка точки → Rendering»: среднее **{r.Latency.MeanPointToRenderMs:0.0} мс**, максимум {r.Latency.MaxPointToRenderMs:0.0} мс ({r.Latency.Frames} кадров)");
        }

        sb.AppendLine(inv, $"- Давление в контакте: {s.MinPressure:0.000} .. {s.MaxPressure:0.000}, различных уровней {s.PressureLevels}; наклон {(r.TiltReported ? "сообщается" : "НЕ сообщается")}");
        sb.AppendLine(inv, $"- Кнопки пера замечены: barrel {(s.BarrelSamples > 0 ? "да" : "нет")}, ластик {(s.EraserSamples > 0 ? "да" : "нет")}");
        sb.AppendLine(inv, $"- Тестовая кнопка вне холста нажата пером/мышью: **{(r.ButtonClicks > 0 ? $"да ({r.ButtonClicks})" : "нет")}**");
        sb.AppendLine(inv, $"- Монитор пера: **{r.Choice.Monitor?.DeviceName ?? "не найден"}**, способ выбора {r.Choice.Source}, кандидатов {r.Choice.CandidateCount}");
        sb.AppendLine();
    }

    private static void AppendEnvironment(StringBuilder sb, ReportInput r, CultureInfo inv)
    {
        sb.AppendLine("## Среда");
        sb.AppendLine();
        sb.AppendLine(inv, $"- ОС: {r.OsDescription} (версия {r.OsVersion}); не ниже Windows 10 22H2: **{(r.OsSupported ? "да" : "НЕТ")}**");
        sb.AppendLine(inv, $"- .NET: {r.FrameworkDescription}, процесс {r.Architecture}; приложение {r.AppVersion}");
        sb.AppendLine(inv, $"- Источник: **{r.SourceKind}**, устройство: {r.DeviceProduct ?? "-"}");
        sb.AppendLine(inv, $"- Переключатели WPF: `DisableStylusAndTouchSupport` = {r.StylusDisabledSwitch}, `EnablePointerSupport` = {r.PointerSupportSwitch}");
        if (r.Suppression is InkSuppressionResult sup)
        {
            sb.AppendLine(inv, $"- Подавление помех Windows Ink: свойство окна {(sup.TabletPropertySet ? "выставлено" : "НЕ выставлено")}, обратная связь отключена {sup.FeedbackSettingsApplied} из {WindowInkSuppressor.PenFeedbackTypes.Count}");
        }

        sb.AppendLine();
    }

    private static void AppendMonitors(StringBuilder sb, ReportInput r, CultureInfo inv)
    {
        sb.AppendLine("## Мониторы и устройства");
        sb.AppendLine();
        sb.AppendLine("| N | Имя | Размер px | Положение | мм | DPI | Основной | Выбран |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        for (int i = 0; i < r.Monitors.Count; i++)
        {
            MonitorDescription m = r.Monitors[i];
            string mm = m.PhysicalWidthMm is double w && m.PhysicalHeightMm is double h ? $"{w:0}x{h:0}" : "?";
            bool chosen = r.Choice.Monitor?.Handle == m.Handle;
            sb.AppendLine(inv, $"| {i + 1} | {m.DeviceName} | {m.BoundsPx.Width}x{m.BoundsPx.Height} | ({m.BoundsPx.Left},{m.BoundsPx.Top}) | {mm} | {m.DpiScale * 100:0}% | {(m.IsPrimary ? "да" : "")} | {(chosen ? "**да**" : "")} |");
        }

        sb.AppendLine();
        sb.AppendLine(inv, $"Выбор монитора пера: **{r.Choice.Source}**; кандидатов {r.Choice.CandidateCount}; нужно подтверждение родителя: {(r.Choice.NeedsParentConfirmation ? "да" : "нет")}.");
        sb.AppendLine(inv, $"Масштаб окна: {r.DpiScale * 100:0}%; холст: {(r.Surface is PenSurface sf ? $"{sf.WidthPx:0}x{sf.HeightPx:0} px в ({sf.OriginXPx:0},{sf.OriginYPx:0})" : "-")}; окно на целевом месте: {(r.WindowFitsTarget is bool fit ? (fit ? "да" : "НЕТ") : "?")}");
        sb.AppendLine();
        sb.AppendLine("Устройства-указатели (GetPointerDevices):");
        sb.AppendLine();
        if (r.Devices.Count == 0)
        {
            sb.AppendLine("(список пуст или API недоступен)");
        }
        else
        {
            sb.AppendLine("| Вид | Название | Монитор |");
            sb.AppendLine("|---|---|---|");
            foreach (PointerDeviceDescription d in r.Devices)
            {
                MonitorDescription? monitor = r.Monitors.FirstOrDefault(m => m.Handle == d.MonitorHandle);
                int number = monitor is null ? 0 : r.Monitors.ToList().IndexOf(monitor) + 1;
                sb.AppendLine(inv, $"| {d.Kind} | {d.Product} | {(monitor is null ? "не сопоставлен" : $"{number} ({monitor.DeviceName})")} |");
            }
        }

        sb.AppendLine();
    }

    private static void AppendPointer(StringBuilder sb, ReportInput r, CultureInfo inv)
    {
        sb.AppendLine("## Сообщения и события");
        sb.AppendLine();
        if (r.PointerStats is PointerDiagnostics p)
        {
            sb.AppendLine("| Счётчик WM_POINTER | Значение |");
            sb.AppendLine("|---|---|");
            sb.AppendLine(inv, $"| всего сообщений | {p.PointerMessages} |");
            sb.AppendLine(inv, $"| от пера | {p.PenMessages} |");
            sb.AppendLine(inv, $"| не от пера (мышь/касание) | {p.NonPenMessages} |");
            sb.AppendLine(inv, $"| принято холстом | {p.HandledMessages} |");
            sb.AppendLine(inv, $"| ENTER/LEAVE (только счёт) | {p.EnterLeaveMessages} |");
            sb.AppendLine(inv, $"| отброшено повторов кадра | {p.DuplicateFrames} |");
            sb.AppendLine(inv, $"| сбоев истории | {p.HistoryFailures} (последний код Win32: {p.LastHistoryError}) |");
            sb.AppendLine(inv, $"| спасено запасным GetPointerPenInfo | {p.HistoryFallbacks} |");
            sb.AppendLine(inv, $"| расширений буфера истории | {p.HistoryBufferGrowths} |");
            sb.AppendLine(inv, $"| точек выдано | {p.Samples} |");
            sb.AppendLine(inv, $"| из них coalesced | {p.CoalescedSamples} |");
            sb.AppendLine(inv, $"| за краем холста | {p.ClippedSamples} |");
            sb.AppendLine(inv, $"| без PerformanceCount | {p.TimeFallbackSamples} |");
        }
        else
        {
            sb.AppendLine("Режим мыши: счётчиков WM_POINTER нет.");
        }

        sb.AppendLine();
        sb.AppendLine("| Что увидел WPF | Значение |");
        sb.AppendLine("|---|---|");
        sb.AppendLine(inv, $"| мышь: движений / нажатий | {r.WpfMouseMoves} / {r.WpfMouseDowns} |");
        sb.AppendLine(inv, $"| стилус: движений / касаний | {r.WpfStylusMoves} / {r.WpfStylusDowns} |");
        sb.AppendLine();
    }

    private static void AppendRate(StringBuilder sb, ReportInput r, CultureInfo inv)
    {
        PenStats s = r.Stats;
        sb.AppendLine("## Частота в контакте");
        sb.AppendLine();
        sb.AppendLine(inv, $"Точек: {s.TotalSamples} (контакт {s.ContactSamples}, наведение {s.HoverSamples}), штрихов {s.Strokes}, coalesced {s.CoalescedSamples} ({Share(s.CoalescedSamples, s.TotalSamples):0.0}%), синтетическое время {s.SyntheticTimeSamples} (в частоту не входят).");
        sb.AppendLine();
        sb.AppendLine(inv, $"Общая: медиана интервала {s.MedianDtMs:0.00} мс, максимум {s.MaxDtMs:0.00} мс, медианная частота {s.OverallMedianHz:0.0} Гц, p5 {s.OverallP5Hz:0.0} Гц, интервалов > 12 мс: {s.GapsOverThreshold}.");
        sb.AppendLine();
        if (s.StrokeRates.Count == 0)
        {
            sb.AppendLine("Штрихов с достаточным числом точек не было.");
        }
        else
        {
            sb.AppendLine("| Штрих | Точек | Длительность, мс | Медиана, Гц | p5, Гц | coalesced, % |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (StrokeRate st in s.StrokeRates)
            {
                sb.AppendLine(inv, $"| {st.Index} | {st.Samples} | {st.DurationMs:0} | {st.MedianHz:0.0} | {st.P5Hz:0.0} | {st.CoalescedShare * 100:0} |");
            }
        }

        sb.AppendLine();
    }

    private static void AppendLiveInk(StringBuilder sb, ReportInput r, CultureInfo inv)
    {
        sb.AppendLine("## Живые чернила и задержка (V0.6)");
        sb.AppendLine();
        if (r.Latency.IsEmpty)
        {
            sb.AppendLine("- Внутренняя задержка: нет данных (в этом сеансе точки не рисовались или источник не даёт абсолютных меток времени).");
        }
        else
        {
            RenderLatency l = r.Latency;
            sb.AppendLine(inv, $"- Внутренняя задержка «метка точки → `CompositionTarget.Rendering`»: среднее **{l.MeanPointToRenderMs:0.0} мс**, максимум {l.MaxPointToRenderMs:0.0} мс, последний кадр {l.LastPointToRenderMs:0.0} мс; кадров с новыми точками {l.Frames}");
            sb.AppendLine(inv, $"- Из неё на программу («пачка получена → Rendering»): среднее {l.MeanArrivalToRenderMs:0.0} мс, максимум {l.MaxArrivalToRenderMs:0.0} мс. Остальное — часть ОС и драйвера до хука и ожидание кадра");
            sb.AppendLine("- Это только внутренняя часть «перо → пиксель». Композитор, DWM и панель измеряются камерой 240 fps (клавиша L, раздел «Замер задержки» в `docs/spikes/V0-hardware.md`).");
        }

        sb.AppendLine(inv, $"- Чернила: штрихов {r.LiveInkStrokes}, отрезков {r.LiveInkSegments}");
        sb.AppendLine(inv, $"- Режим GC `SustainedLowLatency` на время попытки включался {r.GcLowLatencyActivations} раз; сборок GC (0/1/2): {r.GcCollections.Gen0}/{r.GcCollections.Gen1}/{r.GcCollections.Gen2}, суммарная пауза {r.GcPause.TotalMilliseconds:0.0} мс");
        sb.AppendLine();
        sb.AppendLine("### Penlog попыток (dev-режим)");
        sb.AppendLine();
        if (!r.DevMode || r.PenLogDirectory is null)
        {
            sb.AppendLine("Запись penlog выключена (режим разработчика `--dev` не включён).");
        }
        else
        {
            sb.AppendLine(inv, $"Папка: `{r.PenLogDirectory}`; записано попыток: {r.RecordedAttempts.Count}.");
            if (r.RecordedAttempts.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("| Цель | Точек | «Заново» | Завершение | Файл |");
                sb.AppendLine("|---|---|---|---|---|");
                foreach (RecordedAttempt a in r.RecordedAttempts)
                {
                    sb.AppendLine(inv, $"| {a.Target} | {a.Samples} | {a.Restarts} | {a.CompletedBy} | `{a.Path}` |");
                }
            }
        }

        if (r.PenLogError is not null)
        {
            sb.AppendLine();
            sb.AppendLine(inv, $"Ошибка записи penlog: {r.PenLogError}");
        }

        sb.AppendLine();
    }

    private static void AppendPressure(StringBuilder sb, ReportInput r, CultureInfo inv)
    {
        PenStats s = r.Stats;
        sb.AppendLine("## Давление и наклон");
        sb.AppendLine();
        sb.AppendLine(inv, $"- Давление в контакте: минимум {s.MinPressure:0.000}, максимум {s.MaxPressure:0.000} (шкала Windows 0..1024), различных значений {s.PressureLevels}");
        sb.AppendLine(inv, $"- Наклон: {(r.TiltReported ? "сообщается" : "не сообщается")}; X {s.TiltXMin:0}..{s.TiltXMax:0} град, Y {s.TiltYMin:0}..{s.TiltYMax:0} град");
        sb.AppendLine();
    }

    private static void AppendButtons(StringBuilder sb, ReportInput r, CultureInfo inv)
    {
        PenStats s = r.Stats;
        sb.AppendLine("## Кнопки пера и тестовая кнопка");
        sb.AppendLine();
        sb.AppendLine(inv, $"- Точек с нажатой barrel-кнопкой: {s.BarrelSamples}; с ластиком/перевёрнутым пером: {s.EraserSamples}");
        sb.AppendLine(inv, $"- Нажатий тестовой кнопки в полосе вне холста: **{r.ButtonClicks}** (если перо работает «как мышь» на кнопках интерфейса, число растёт)");
        sb.AppendLine();
    }

    private static void AppendTargets(StringBuilder sb, ReportInput r, CultureInfo inv)
    {
        sb.AppendLine("## Мишени точности (последнее касание у каждой мишени)");
        sb.AppendLine();
        if (r.Targets.Count == 0)
        {
            sb.AppendLine("Касаний у мишеней не было (мишени показывает клавиша T).");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| Мишень | dx, px | dy, px | dx, мм | dy, мм | касаний |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (TargetResult t in r.Targets.OrderBy(t => t.Number))
        {
            sb.AppendLine(inv, $"| {t.Number} | {t.DxPx:0.0} | {t.DyPx:0.0} | {t.DxMm:0.00} | {t.DyMm:0.00} | {t.Count} |");
        }

        sb.AppendLine();
    }

    private static void AppendSnapshots(StringBuilder sb, ReportInput r, CultureInfo inv)
    {
        sb.AppendLine("## Снимки (клавиша S)");
        sb.AppendLine();
        if (r.Snapshots.Count == 0)
        {
            sb.AppendLine("Снимков не делали.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| # | Время | Точек | Штрихов | Частота, Гц (медиана / p5) | Давление max | WM_POINTER pen / принято | Кнопка | Мышь WPF (нажатий) |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (ReportSnapshot sn in r.Snapshots)
        {
            string pointer = sn.PointerStats is PointerDiagnostics p ? $"{p.PenMessages} / {p.HandledMessages}" : "-";
            sb.AppendLine(inv, $"| {sn.Number} | {sn.Time:HH:mm:ss} | {sn.Stats.TotalSamples} | {sn.Stats.Strokes} | {sn.Stats.OverallMedianHz:0.0} / {sn.Stats.OverallP5Hz:0.0} | {sn.Stats.MaxPressure:0.000} | {pointer} | {sn.ButtonClicks} | {sn.WpfMouseDowns} |");
        }

        sb.AppendLine();
    }

    private static double Share(long part, long total) => total > 0 ? part * 100.0 / total : 0;
}
