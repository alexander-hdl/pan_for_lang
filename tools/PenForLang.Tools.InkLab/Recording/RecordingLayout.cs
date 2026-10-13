using PenForLang.Glyphs;
using PenForLang.Ink;
using PenForLang.Platform.Windows;

namespace PenForLang.Tools.InkLab.Recording;

/// <summary>
/// Раскладка вкладки «Запись»: слева — экранный эталон, правее на той же базовой линии — линейка письма. Размеры задаются в мм
/// (ARCHITECTURE.md §7.3) и переводятся в px через <see cref="GuideFrame.FromMm"/> без DPI-масштаба Windows. Чистые функции.
/// </summary>
public static class RecordingLayout
{
    /// <summary>x-height по умолчанию, мм (параметр <c>--xheight-mm</c>).</summary>
    public const double DefaultXHeightMm = 15.0;

    /// <summary>Межстрочный интервал в x-height.</summary>
    public const double LineSpacing = 3.0;

    private const double BaselineShare = 0.6;
    private const double MarginMm = 6.0;
    private const double MinGapXHeights = 5.0;
    private const double ReferenceMarginXHeights = 2.5;

    /// <summary>
    /// Физических пикселей в миллиметре: размер монитора из EDID, иначе 7,5 по умолчанию.
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

    /// <summary>Описание экрана для заголовка penlog.</summary>
    public static PenScreenInfo Screen(MonitorDescription? monitor, double dpiScale, double pxPerMm, PxPerMmSource source)
    {
        int widthPx = monitor?.BoundsPx.Width ?? 0;
        int heightPx = monitor?.BoundsPx.Height ?? 0;
        return new PenScreenInfo(
            widthPx,
            heightPx,
            monitor?.PhysicalWidthMm ?? (widthPx / pxPerMm),
            monitor?.PhysicalHeightMm ?? (heightPx / pxPerMm),
            dpiScale,
            pxPerMm,
            source);
    }

    /// <summary>Линейки эталона (слева) и письма (правее) на одной базовой линии.</summary>
    /// <param name="mapper">Преобразователь текущего холста.</param>
    /// <param name="style">Стиль письма.</param>
    /// <param name="xHeightMm">x-height, мм; &gt; 0.</param>
    /// <param name="referenceWidthXHeights">Ширина эталона в x-height (слово шире буквы).</param>
    public static (GuideFrame Demo, GuideFrame Writing) Frames(SurfaceMapper mapper, StyleManifest style, double xHeightMm, double referenceWidthXHeights)
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
        double gap = Math.Max(MinGapXHeights, Math.Max(0, referenceWidthXHeights) + ReferenceMarginXHeights) * xHeightMm;
        double writingOrigin = demoOrigin + gap;
        writingOrigin = Math.Min(writingOrigin, Math.Max(widthMm - (3 * xHeightMm), demoOrigin + xHeightMm));

        return (
            GuideFrame.FromMm(mapper, style.Id, demoOrigin, baseline, xHeightMm, LineSpacing),
            GuideFrame.FromMm(mapper, style.Id, writingOrigin, baseline, xHeightMm, LineSpacing));
    }
}
