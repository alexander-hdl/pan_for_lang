namespace PenForLang.Ink;

/// <summary>
/// Преобразования между экранными физическими пикселями, координатами поверхности холста (физические px от левого
/// верхнего угла), миллиметрами и DIP визуального слоя WPF (ARCHITECTURE.md §3.1, §7.3, A12).
/// </summary>
/// <remarks>
/// Главный инвариант A12: <c>PointToScreen</c> в WPF уже возвращает физические пиксели, поэтому переход
/// «экран → поверхность» — только вычитание начала холста, <b>без</b> участия DPI. DPI-масштаб монитора окна
/// нужен лишь для перехода в DIP и обратно (<see cref="SurfaceToDip"/>, <see cref="DipToSurface"/>).
/// Экземпляр неизменяем; при смене монитора, DPI или положения окна создаётся новый (все три параметра задаются вместе,
/// чтобы нельзя было получить прямоугольник нового монитора со старым DPI).
/// </remarks>
public sealed class SurfaceMapper
{
    /// <summary>Значение <c>PxPerMm</c> по умолчанию (ARCHITECTURE.md §7.3, 03 §1.1), если нет калибровки и EDID.</summary>
    public const double DefaultPxPerMm = 7.5;

    /// <summary>Создаёт преобразователь.</summary>
    /// <param name="surface">Экранный прямоугольник холста в физических px.</param>
    /// <param name="pxPerMm">Физических пикселей в миллиметре; &gt; 0.</param>
    /// <param name="dpiScale">DPI-масштаб монитора окна (1.0 = 96 dpi, 1.5 = 144 dpi); &gt; 0.</param>
    /// <exception cref="ArgumentException">Числа не конечны или не положительны.</exception>
    public SurfaceMapper(PenSurface surface, double pxPerMm, double dpiScale)
    {
        if (!double.IsFinite(surface.OriginXPx) || !double.IsFinite(surface.OriginYPx)
            || !double.IsFinite(surface.WidthPx) || !double.IsFinite(surface.HeightPx))
        {
            throw new ArgumentException("Surface must be finite.", nameof(surface));
        }

        if (!double.IsFinite(pxPerMm) || pxPerMm <= 0)
        {
            throw new ArgumentException("PxPerMm must be finite and positive.", nameof(pxPerMm));
        }

        if (!double.IsFinite(dpiScale) || dpiScale <= 0)
        {
            throw new ArgumentException("DpiScale must be finite and positive.", nameof(dpiScale));
        }

        Surface = surface;
        PxPerMm = pxPerMm;
        DpiScale = dpiScale;
    }

    /// <summary>Экранный прямоугольник холста в физических px.</summary>
    public PenSurface Surface { get; }

    /// <summary>Физических пикселей в миллиметре.</summary>
    public double PxPerMm { get; }

    /// <summary>DPI-масштаб монитора окна.</summary>
    public double DpiScale { get; }

    /// <summary>Экранные физические px → поверхность (физические px от угла холста). DPI не участвует (A12).</summary>
    public (double X, double Y) ScreenToSurface(double screenXPx, double screenYPx) =>
        (screenXPx - Surface.OriginXPx, screenYPx - Surface.OriginYPx);

    /// <summary>Поверхность → экранные физические px.</summary>
    public (double X, double Y) SurfaceToScreen(double surfaceXPx, double surfaceYPx) =>
        (surfaceXPx + Surface.OriginXPx, surfaceYPx + Surface.OriginYPx);

    /// <summary>Поверхность (px) → миллиметры от угла холста.</summary>
    public (double X, double Y) SurfaceToMm(double surfaceXPx, double surfaceYPx) =>
        (surfaceXPx / PxPerMm, surfaceYPx / PxPerMm);

    /// <summary>Миллиметры от угла холста → поверхность (px).</summary>
    public (double X, double Y) MmToSurface(double xMm, double yMm) => (xMm * PxPerMm, yMm * PxPerMm);

    /// <summary>Экранные физические px → миллиметры от угла холста.</summary>
    public (double X, double Y) ScreenToMm(double screenXPx, double screenYPx)
    {
        (double x, double y) = ScreenToSurface(screenXPx, screenYPx);
        return SurfaceToMm(x, y);
    }

    /// <summary>Миллиметры от угла холста → экранные физические px.</summary>
    public (double X, double Y) MmToScreen(double xMm, double yMm)
    {
        (double x, double y) = MmToSurface(xMm, yMm);
        return SurfaceToScreen(x, y);
    }

    /// <summary>Поверхность (физические px) → DIP визуального слоя относительно угла холста: <c>px / dpiScale</c>.</summary>
    public (double X, double Y) SurfaceToDip(double surfaceXPx, double surfaceYPx) =>
        (surfaceXPx / DpiScale, surfaceYPx / DpiScale);

    /// <summary>DIP относительно угла холста → поверхность (физические px): <c>dip * dpiScale</c>.</summary>
    public (double X, double Y) DipToSurface(double xDip, double yDip) => (xDip * DpiScale, yDip * DpiScale);

    /// <summary>Лежит ли экранная точка внутри холста; границы включены.</summary>
    public bool ContainsScreenPoint(double screenXPx, double screenYPx)
    {
        (double x, double y) = ScreenToSurface(screenXPx, screenYPx);
        return Surface.ContainsSurfacePoint(x, y);
    }
}
