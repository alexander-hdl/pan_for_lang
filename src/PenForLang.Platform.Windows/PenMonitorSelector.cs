namespace PenForLang.Platform.Windows;

/// <summary>Прямоугольник монитора в физических пикселях виртуального экрана.</summary>
/// <param name="Left">Левый край.</param>
/// <param name="Top">Верхний край.</param>
/// <param name="Right">Правый край (не включается).</param>
/// <param name="Bottom">Нижний край (не включается).</param>
public readonly record struct MonitorRect(int Left, int Top, int Right, int Bottom)
{
    /// <summary>Ширина, px.</summary>
    public int Width => Right - Left;

    /// <summary>Высота, px.</summary>
    public int Height => Bottom - Top;
}

/// <summary>Описание монитора для выбора монитора пера.</summary>
/// <param name="Handle">HMONITOR.</param>
/// <param name="DeviceName">Имя устройства (<c>\\.\DISPLAY1</c>).</param>
/// <param name="BoundsPx">Границы в физических px виртуального экрана.</param>
/// <param name="IsPrimary">Основной монитор.</param>
/// <param name="PhysicalWidthMm">Ширина изображения, мм (<c>GetDeviceCaps(HORZSIZE)</c>), если известна.</param>
/// <param name="PhysicalHeightMm">Высота изображения, мм (<c>VERTSIZE</c>), если известна.</param>
/// <param name="DpiScale">DPI-масштаб монитора (1,0 = 96 dpi, 1,5 = 144 dpi).</param>
public sealed record MonitorDescription(
    nint Handle,
    string DeviceName,
    MonitorRect BoundsPx,
    bool IsPrimary,
    double? PhysicalWidthMm,
    double? PhysicalHeightMm,
    double DpiScale = 1.0);

/// <summary>Вид устройства-указателя (<c>POINTER_DEVICE_TYPE</c>).</summary>
public enum PointerDeviceKind
{
    /// <summary>Не определён или неизвестный вид.</summary>
    Other = 0,

    /// <summary>Перо, встроенное в дисплей (<c>POINTER_DEVICE_TYPE_INTEGRATED_PEN</c>).</summary>
    IntegratedPen = 1,

    /// <summary>Внешнее перо (<c>POINTER_DEVICE_TYPE_EXTERNAL_PEN</c>).</summary>
    ExternalPen = 2,

    /// <summary>Сенсорный экран.</summary>
    Touch = 3,

    /// <summary>Тачпад.</summary>
    TouchPad = 4,
}

/// <summary>Описание устройства-указателя из <c>GetPointerDevices</c>.</summary>
/// <param name="Handle">Дескриптор устройства.</param>
/// <param name="Kind">Вид устройства.</param>
/// <param name="MonitorHandle">HMONITOR, на который отображено устройство (не обязательно физически подключённый).</param>
/// <param name="Product">Название продукта.</param>
public sealed record PointerDeviceDescription(nint Handle, PointerDeviceKind Kind, nint MonitorHandle, string Product)
{
    /// <summary>Устройство — перо (встроенное или внешнее).</summary>
    public bool IsPen => Kind is PointerDeviceKind.IntegratedPen or PointerDeviceKind.ExternalPen;
}

/// <summary>Чем определён монитор пера.</summary>
public enum PenMonitorChoiceSource
{
    /// <summary>Мониторов нет.</summary>
    None,

    /// <summary>Монитор задан параметром командной строки <c>--monitor N</c>.</summary>
    CommandLine,

    /// <summary>Устройство пера, отображённое на монитор (<c>GetPointerDevices</c>) — основной путь.</summary>
    PointerDevice,

    /// <summary>Запасной путь: монитор 1920×1080 размером ≈ 256×144 мм.</summary>
    PhysicalSizeHeuristic,

    /// <summary>Основной монитор; выбор родителем — позже (§7.4).</summary>
    PrimaryFallback,
}

/// <summary>Результат выбора монитора пера.</summary>
/// <param name="Monitor">Выбранный монитор; <c>null</c>, если мониторов нет.</param>
/// <param name="Source">Способ выбора.</param>
/// <param name="Device">Устройство пера, если выбор по нему.</param>
/// <param name="CandidateCount">Сколько кандидатов подошло по критерию (больше 1 — выбор неоднозначен, родителю стоит подтвердить).</param>
public sealed record PenMonitorChoice(
    MonitorDescription? Monitor,
    PenMonitorChoiceSource Source,
    PointerDeviceDescription? Device,
    int CandidateCount)
{
    /// <summary>Выбор нужно подтвердить родителю: монитор выбран по умолчанию или кандидатов несколько.</summary>
    public bool NeedsParentConfirmation => Source == PenMonitorChoiceSource.PrimaryFallback || CandidateCount > 1;
}

/// <summary>
/// Чистая функция выбора монитора пера (ARCHITECTURE.md §7.4 п.1):
/// <list type="number">
/// <item>устройство-перо из <c>GetPointerDevices</c> → монитор, на который оно отображено;</item>
/// <item>иначе монитор 1920×1080 размером ≈ 256×144 мм (XPPen Artist 12 Pro; допуск <see cref="SizeTolerance"/>);</item>
/// <item>иначе основной монитор (выбор родителя — позже).</item>
/// </list>
/// </summary>
public static class PenMonitorSelector
{
    /// <summary>Ожидаемая ширина экрана пера, мм.</summary>
    public const double ExpectedWidthMm = 256.0;

    /// <summary>Ожидаемая высота экрана пера, мм.</summary>
    public const double ExpectedHeightMm = 144.0;

    /// <summary>Ожидаемая ширина экрана пера, px.</summary>
    public const int ExpectedWidthPx = 1920;

    /// <summary>Ожидаемая высота экрана пера, px.</summary>
    public const int ExpectedHeightPx = 1080;

    /// <summary>Допустимое относительное отклонение физического размера (4%): панель 11,6" на деле 256,8×144,5 мм.</summary>
    public const double SizeTolerance = 0.04;

    /// <summary>Выбирает монитор пера.</summary>
    /// <param name="devices">Устройства-указатели.</param>
    /// <param name="monitors">Мониторы.</param>
    public static PenMonitorChoice Select(
        IReadOnlyList<PointerDeviceDescription> devices,
        IReadOnlyList<MonitorDescription> monitors)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(monitors);

        if (monitors.Count == 0)
        {
            return new PenMonitorChoice(null, PenMonitorChoiceSource.None, null, 0);
        }

        // 1. Устройство пера → его монитор. Перебор в порядке списка; учитываются только мониторы из списка.
        var byDevice = new List<(PointerDeviceDescription Device, MonitorDescription Monitor)>();
        foreach (PointerDeviceDescription device in devices.Where(d => d.IsPen))
        {
            MonitorDescription? monitor = monitors.FirstOrDefault(m => m.Handle == device.MonitorHandle);
            if (monitor is not null)
            {
                byDevice.Add((device, monitor));
            }
        }

        if (byDevice.Count > 0)
        {
            // Несколько устройств на одном мониторе — один кандидат; на разных — неоднозначность.
            int distinctMonitors = byDevice.Select(p => p.Monitor.Handle).Distinct().Count();
            return new PenMonitorChoice(byDevice[0].Monitor, PenMonitorChoiceSource.PointerDevice, byDevice[0].Device, distinctMonitors);
        }

        // 2. Запасной путь: 1920×1080 и ≈256×144 мм. Среди совпавших предпочитается неосновной (перо — дополнительный экран).
        List<MonitorDescription> bySize = monitors.Where(MatchesPenPanelSize).ToList();
        if (bySize.Count > 0)
        {
            MonitorDescription pick = bySize.FirstOrDefault(m => !m.IsPrimary) ?? bySize[0];
            return new PenMonitorChoice(pick, PenMonitorChoiceSource.PhysicalSizeHeuristic, null, bySize.Count);
        }

        // 3. Основной монитор (при его отсутствии в списке — первый).
        MonitorDescription primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
        return new PenMonitorChoice(primary, PenMonitorChoiceSource.PrimaryFallback, null, monitors.Count);
    }

    /// <summary>
    /// Принудительный выбор (<c>--monitor N</c>): монитор с номером <paramref name="oneBasedIndex"/> в списке. Если номер вне списка —
    /// обычный выбор (<see cref="Select"/>).
    /// </summary>
    public static PenMonitorChoice SelectForced(
        IReadOnlyList<MonitorDescription> monitors,
        int oneBasedIndex,
        IReadOnlyList<PointerDeviceDescription> devices)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        ArgumentNullException.ThrowIfNull(devices);
        if (oneBasedIndex < 1 || oneBasedIndex > monitors.Count)
        {
            return Select(devices, monitors);
        }

        return new PenMonitorChoice(monitors[oneBasedIndex - 1], PenMonitorChoiceSource.CommandLine, null, 1);
    }

    /// <summary>Монитор похож на экран пера: 1920×1080 px и ≈256×144 мм.</summary>
    public static bool MatchesPenPanelSize(MonitorDescription monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        if (monitor.BoundsPx.Width != ExpectedWidthPx || monitor.BoundsPx.Height != ExpectedHeightPx)
        {
            return false;
        }

        return monitor.PhysicalWidthMm is double w
            && monitor.PhysicalHeightMm is double h
            && Math.Abs(w - ExpectedWidthMm) <= ExpectedWidthMm * SizeTolerance
            && Math.Abs(h - ExpectedHeightMm) <= ExpectedHeightMm * SizeTolerance;
    }
}
