using PenForLang.Core;

namespace PenForLang.Ink;

/// <summary>
/// Линейка попытки в координатах поверхности письма (ARCHITECTURE.md §3.1): фиксирует, где baseline, какова x-height
/// и сколько пикселей в миллиметре. Экземпляр неизменяем и проверен при создании.
/// </summary>
public sealed record GuideFrame
{
    /// <summary>Создаёт линейку.</summary>
    /// <param name="originXPx">Левая граница строки (x0), физические px поверхности.</param>
    /// <param name="baselineYPx">Y базовой линии (baseline), физические px поверхности; ось вниз.</param>
    /// <param name="xHeightPx">Высота строчной буквы без выносных элементов (x-height) в px; &gt; 0.</param>
    /// <param name="pxPerMm">Физических пикселей в миллиметре; &gt; 0.</param>
    /// <param name="lineSpacing">Межстрочный интервал в x-height.</param>
    /// <param name="slantDeg">Наклон линейки в градусах; <c>null</c> — не задан (в v0 в проекцию не входит).</param>
    /// <param name="style">Идентификатор стиля письма (например <c>zaner-bloser</c>); не <c>default</c>.</param>
    /// <exception cref="ArgumentException">Числа не конечны, либо <paramref name="xHeightPx"/>/<paramref name="pxPerMm"/> не положительны.</exception>
    public GuideFrame(
        double originXPx,
        double baselineYPx,
        double xHeightPx,
        double pxPerMm,
        double lineSpacing,
        double? slantDeg,
        StyleId style)
    {
        if (style.IsDefault)
        {
            throw new ArgumentException("Style must be set.", nameof(style));
        }

        RequireFinite(originXPx, nameof(originXPx));
        RequireFinite(baselineYPx, nameof(baselineYPx));
        RequireFinite(lineSpacing, nameof(lineSpacing));
        if (slantDeg is { } slant)
        {
            RequireFinite(slant, nameof(slantDeg));
        }

        RequirePositive(xHeightPx, nameof(xHeightPx));
        RequirePositive(pxPerMm, nameof(pxPerMm));

        OriginXPx = originXPx;
        BaselineYPx = baselineYPx;
        XHeightPx = xHeightPx;
        PxPerMm = pxPerMm;
        LineSpacing = lineSpacing;
        SlantDeg = slantDeg;
        Style = style;
    }

    /// <summary>Левая граница строки (x0), физические px поверхности.</summary>
    public double OriginXPx { get; }

    /// <summary>Y базовой линии, физические px поверхности; ось вниз.</summary>
    public double BaselineYPx { get; }

    /// <summary>x-height в px.</summary>
    public double XHeightPx { get; }

    /// <summary>Физических пикселей в миллиметре.</summary>
    public double PxPerMm { get; }

    /// <summary>Межстрочный интервал в x-height.</summary>
    public double LineSpacing { get; }

    /// <summary>Наклон линейки в градусах или <c>null</c>.</summary>
    public double? SlantDeg { get; }

    /// <summary>Идентификатор стиля письма.</summary>
    public StyleId Style { get; }

    /// <summary>x-height в миллиметрах (<c>XHeightPx / PxPerMm</c>).</summary>
    public double XHeightMm => XHeightPx / PxPerMm;

    /// <summary>
    /// Строит линейку из размеров в миллиметрах, отсчитанных от левого верхнего угла холста: пиксели получаются через
    /// <see cref="SurfaceMapper.MmToSurface"/> (то есть умножением на <c>PxPerMm</c>, без DPI-масштаба Windows).
    /// </summary>
    /// <param name="mapper">Преобразователь текущего монитора и холста.</param>
    /// <param name="style">Стиль письма.</param>
    /// <param name="originXMm">Левая граница строки от угла холста, мм.</param>
    /// <param name="baselineYMm">Базовая линия от верхнего края холста, мм.</param>
    /// <param name="xHeightMm">x-height, мм; &gt; 0.</param>
    /// <param name="lineSpacing">Межстрочный интервал в x-height.</param>
    /// <param name="slantDeg">Наклон или <c>null</c>.</param>
    public static GuideFrame FromMm(
        SurfaceMapper mapper,
        StyleId style,
        double originXMm,
        double baselineYMm,
        double xHeightMm,
        double lineSpacing,
        double? slantDeg = null)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        RequirePositive(xHeightMm, nameof(xHeightMm));
        (double x0, double baseline) = mapper.MmToSurface(originXMm, baselineYMm);
        return new GuideFrame(x0, baseline, xHeightMm * mapper.PxPerMm, mapper.PxPerMm, lineSpacing, slantDeg, style);
    }

    /// <summary>
    /// Строит линейку из размеров в DIP относительно угла холста (так задаётся разметка WPF): пиксели получаются через
    /// <see cref="SurfaceMapper.DipToSurface"/> (умножением на DPI-масштаб монитора окна).
    /// </summary>
    /// <param name="mapper">Преобразователь текущего монитора и холста.</param>
    /// <param name="style">Стиль письма.</param>
    /// <param name="originXDip">Левая граница строки, DIP.</param>
    /// <param name="baselineYDip">Базовая линия, DIP.</param>
    /// <param name="xHeightDip">x-height, DIP; &gt; 0.</param>
    /// <param name="lineSpacing">Межстрочный интервал в x-height.</param>
    /// <param name="slantDeg">Наклон или <c>null</c>.</param>
    public static GuideFrame FromDip(
        SurfaceMapper mapper,
        StyleId style,
        double originXDip,
        double baselineYDip,
        double xHeightDip,
        double lineSpacing,
        double? slantDeg = null)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        RequirePositive(xHeightDip, nameof(xHeightDip));
        (double x0, double baseline) = mapper.DipToSurface(originXDip, baselineYDip);
        return new GuideFrame(x0, baseline, xHeightDip * mapper.DpiScale, mapper.PxPerMm, lineSpacing, slantDeg, style);
    }

    private static void RequireFinite(double value, string name)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentException("Value must be finite.", name);
        }
    }

    private static void RequirePositive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentException("Value must be finite and positive.", name);
        }
    }
}
