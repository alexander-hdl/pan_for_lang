using System.Globalization;
using PenForLang.Glyphs;
using PenForLang.Ink;
using PenForLang.Platform.Windows;

namespace PenForLang.App.Wpf.Lesson;

/// <summary>
/// Раскладка урока V0.8 и заголовок penlog его попыток. Две строки одного размера: эталон над строкой (линейка эталона, METHODOLOGY.md §4.1:
/// «не под пером и не там, где его закроет рука») и строка письма под ним; обе на одной горизонтальной позиции.
/// </summary>
public static class LessonLayout
{
    /// <summary>Высота полосы сообщений в мм: инструкция и подсказка в одну строку каждая; высота постоянна, чтобы раскладка не прыгала.</summary>
    public const double BannerHeightMm = 16.0;

    /// <summary>Отступ линеек от краёв холста, мм (как в <see cref="SandboxLayout.Frames"/>).</summary>
    public const double MarginMm = 6.0;

    /// <summary>Наименьший x-height, который ещё читается, мм.</summary>
    public const double MinXHeightMm = 6.0;

    /// <summary>
    /// x-height, при котором строка со всеми выносными элементами помещается на холсте: не больше запрошенного и не меньше
    /// <see cref="MinXHeightMm"/>. На экране пера 1080p запрошенные 15 мм помещаются; в маленьком окне буква уменьшается, а не обрезается.
    /// </summary>
    /// <param name="requestedMm">Желаемый x-height, мм.</param>
    /// <param name="canvasHeightMm">Высота холста, мм.</param>
    /// <param name="style">Стиль письма (линии выносных элементов).</param>
    public static double FitXHeightMm(double requestedMm, double canvasHeightMm, StyleManifest style)
    {
        ArgumentNullException.ThrowIfNull(style);
        double units = Math.Max(style.Lines.Ascender, style.Lines.Cap) - style.Lines.Descender + SandboxLayout.LineSpacing;
        double fit = (canvasHeightMm - (2 * MarginMm)) / units;
        return double.IsFinite(fit) ? Math.Max(MinXHeightMm, Math.Min(requestedMm, fit)) : requestedMm;
    }

    /// <summary>
    /// Линейка эталона (над строкой) и линейка письма (под ней): строка письма прижата к низу холста, базовая линия эталона выше на
    /// <see cref="SandboxLayout.LineSpacing"/> x-height; по горизонтали обе начинаются на трёх x-height от левого края.
    /// </summary>
    /// <param name="mapper">Преобразователь текущего холста.</param>
    /// <param name="style">Стиль письма.</param>
    /// <param name="xHeightMm">x-height, мм; &gt; 0.</param>
    public static (GuideFrame Model, GuideFrame Writing) Frames(SurfaceMapper mapper, StyleManifest style, double xHeightMm)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        ArgumentNullException.ThrowIfNull(style);
        if (!double.IsFinite(xHeightMm) || xHeightMm <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(xHeightMm), xHeightMm, "x-height must be positive.");
        }

        double heightMm = mapper.Surface.HeightPx / mapper.PxPerMm;
        double descender = -style.Lines.Descender * xHeightMm;
        double writingBaseline = heightMm - descender - MarginMm;
        double modelBaseline = writingBaseline - (SandboxLayout.LineSpacing * xHeightMm);
        double origin = (3 * xHeightMm) + MarginMm;
        return (
            GuideFrame.FromMm(mapper, style.Id, origin, modelBaseline, xHeightMm, SandboxLayout.LineSpacing),
            GuideFrame.FromMm(mapper, style.Id, origin, writingBaseline, xHeightMm, SandboxLayout.LineSpacing));
    }

    /// <summary>Заголовок penlog попытки урока: разработчик (взрослый), без согласия и без имени (запись ребёнка без согласия запрещена, §3.4).</summary>
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
        LessonStep step,
        long qpcFrequency)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(guides);
        ArgumentNullException.ThrowIfNull(step);
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
            "lesson-" + attemptNumber.ToString("000", CultureInfo.InvariantCulture),
            step.Code,
            "glyph:" + letter,
            step.Level.ToString(),
            step.Kind switch
            {
                StepKind.Demo => "demo",
                StepKind.Trace => "trace",
                StepKind.Copy => "copy",
                _ => "memory",
            },
            "right");
        return new PenLogHeader(appVersion, os, device, screen, guides, task, new PenLogSubject("dev", null, PenSubjectKind.Adult), null, qpcFrequency);
    }
}
