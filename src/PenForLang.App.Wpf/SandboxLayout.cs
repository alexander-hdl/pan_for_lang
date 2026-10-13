using PenForLang.Glyphs;
using PenForLang.Ink;
using PenForLang.Platform.Windows;

namespace PenForLang.App.Wpf;

/// <summary>
/// Раскладка «урока-песочницы» на холсте (V0.6): где стоят линейки показа и письма и сколько пикселей в миллиметре.
/// Чистые функции — проверяются без окна. Размеры задаются в мм (ARCHITECTURE.md §7.3) и переводятся в px через
/// <see cref="GuideFrame.FromMm"/> без DPI-масштаба Windows.
/// </summary>
public static class SandboxLayout
{
    /// <summary>x-height по умолчанию, мм (параметр <c>--xheight-mm</c>).</summary>
    public const double DefaultXHeightMm = 15.0;

    /// <summary>Межстрочный интервал в x-height (§3.4, пример заголовка).</summary>
    public const double LineSpacing = 3.0;

    /// <summary>Наименьшая сторона цели для пера, мм (ARCHITECTURE.md §7.8, A34: ≥ 12 мм).</summary>
    public const double TargetMinMm = 12.0;

    /// <summary>Наименьший отступ между целями, мм (A34: ≥ 4 мм).</summary>
    public const double TargetGapMm = 4.0;

    private const double BaselineShare = 0.6;
    private const double MarginMm = 6.0;
    private const double WritingGapXHeights = 5.0;

    /// <summary>
    /// Физических пикселей в миллиметре (ARCHITECTURE.md §7.3): EDID-размер монитора, иначе 7,5 по умолчанию. Калибровки родителем в v0 нет.
    /// </summary>
    public static (double PxPerMm, PxPerMmSource Source) ResolvePxPerMm(MonitorDescription? monitor)
    {
        if (monitor is { PhysicalWidthMm: double widthMm, BoundsPx.Width: > 0 } && widthMm > 50)
        {
            double value = monitor.BoundsPx.Width / widthMm;
            if (double.IsFinite(value) && value is > 1.0 and < 40.0)
            {
                return (value, PxPerMmSource.Edid);
            }
        }

        return (SurfaceMapper.DefaultPxPerMm, PxPerMmSource.Default);
    }

    /// <summary>Миллиметры в DIP: <c>mm · pxPerMm / dpiScale</c> (DPI входит один раз).</summary>
    public static double MmToDip(double millimeters, double pxPerMm, double dpiScale) =>
        millimeters * pxPerMm / (dpiScale > 0 ? dpiScale : 1.0);

    /// <summary>Линейки показа (слева) и письма (правее) на одной базовой линии.</summary>
    /// <param name="mapper">Преобразователь текущего холста.</param>
    /// <param name="style">Стиль письма.</param>
    /// <param name="xHeightMm">x-height, мм; &gt; 0.</param>
    public static (GuideFrame Demo, GuideFrame Writing) Frames(SurfaceMapper mapper, StyleManifest style, double xHeightMm)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        ArgumentNullException.ThrowIfNull(style);
        if (!double.IsFinite(xHeightMm) || xHeightMm <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(xHeightMm), xHeightMm, "x-height must be positive.");
        }

        double widthMm = mapper.Surface.WidthPx / mapper.PxPerMm;
        double heightMm = mapper.Surface.HeightPx / mapper.PxPerMm;

        double ascender = Math.Max(style.Lines.Ascender, style.Lines.Cap) * xHeightMm;
        double descender = -style.Lines.Descender * xHeightMm;
        double minBaseline = ascender + MarginMm;
        double maxBaseline = heightMm - descender - MarginMm;
        double baseline = minBaseline <= maxBaseline
            ? Math.Clamp(heightMm * BaselineShare, minBaseline, maxBaseline)
            : (minBaseline + maxBaseline) / 2;

        // Входящий штрих букв уходит влево от u = 0 примерно на 0,7 x-height.
        double demoOrigin = (1.3 * xHeightMm) + MarginMm;
        double writingOrigin = demoOrigin + (WritingGapXHeights * xHeightMm);
        writingOrigin = Math.Min(writingOrigin, Math.Max(widthMm - (3 * xHeightMm), demoOrigin + xHeightMm));

        return (
            GuideFrame.FromMm(mapper, style.Id, demoOrigin, baseline, xHeightMm, LineSpacing),
            GuideFrame.FromMm(mapper, style.Id, writingOrigin, baseline, xHeightMm, LineSpacing));
    }

    /// <summary>Заголовок penlog для попытки песочницы: разработчик (взрослый), без согласия и без имени.</summary>
    public static PenLogHeader Header(
        string appVersion,
        string os,
        PenDeviceInfo device,
        MonitorDescription? monitor,
        double dpiScale,
        double pxPerMm,
        PxPerMmSource pxPerMmSource,
        GuideFrame guides,
        string letter,
        int attemptNumber,
        long qpcFrequency)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(guides);
        int widthPx = monitor?.BoundsPx.Width ?? 0;
        int heightPx = monitor?.BoundsPx.Height ?? 0;
        var screen = new PenScreenInfo(
            widthPx,
            heightPx,
            monitor?.PhysicalWidthMm ?? (widthPx / pxPerMm),
            monitor?.PhysicalHeightMm ?? (heightPx / pxPerMm),
            dpiScale,
            pxPerMm,
            pxPerMmSource);
        var task = new PenLogTask(
            "sandbox-" + attemptNumber.ToString("000", System.Globalization.CultureInfo.InvariantCulture),
            "SANDBOX",
            "glyph:" + letter,
            "L0",
            "sandbox",
            "right");
        return new PenLogHeader(appVersion, os, device, screen, guides, task, new PenLogSubject("dev", null, PenSubjectKind.Adult), null, qpcFrequency);
    }
}
