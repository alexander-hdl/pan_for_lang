using PenForLang.Tools.InkLab.Scene;

namespace PenForLang.Tools.InkLab.Views;

/// <summary>
/// Преобразование координат линейки <c>(u, v)</c> в точки вида и обратно: сцена вписывается в область с полями, масштаб одинаков
/// по обеим осям (форма следа не искажается), <c>v</c> растёт вверх. Чистая функция, без WPF.
/// </summary>
/// <param name="Scale">Пикселей вида на одну x-height.</param>
/// <param name="OffsetX">Положение <c>u = 0</c> по X вида.</param>
/// <param name="OffsetY">Положение <c>v = 0</c> (базовой линии) по Y вида.</param>
public readonly record struct ViewTransform(double Scale, double OffsetX, double OffsetY)
{
    private const double MinUSpan = 4.0;
    private const double MarginXHeights = 0.4;

    /// <summary>Вид X для <paramref name="u"/>.</summary>
    public double X(double u) => OffsetX + (u * Scale);

    /// <summary>Вид Y для <paramref name="v"/>.</summary>
    public double Y(double v) => OffsetY - (v * Scale);

    /// <summary>Координата <c>u</c> для X вида.</summary>
    public double U(double x) => (x - OffsetX) / Scale;

    /// <summary>Координата <c>v</c> для Y вида.</summary>
    public double V(double y) => (OffsetY - y) / Scale;

    /// <summary>Вписывает сцену (линии линейки и содержимое) в область <paramref name="width"/> × <paramref name="height"/>.</summary>
    public static ViewTransform Fit(InkScene scene, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(scene);
        SceneGuides g = scene.Guides;
        double vMin = Math.Min(g.Descender, g.Baseline) - MarginXHeights;
        double vMax = Math.Max(g.Ascender, g.Cap) + MarginXHeights;
        double uMin = -MarginXHeights;
        double uMax = MinUSpan;
        if (scene.ContentBounds() is { } b)
        {
            uMin = Math.Min(uMin, b.UMin - MarginXHeights);
            uMax = Math.Max(uMax, b.UMax + MarginXHeights);
            vMin = Math.Min(vMin, b.VMin - MarginXHeights);
            vMax = Math.Max(vMax, b.VMax + MarginXHeights);
        }

        double w = Math.Max(width, 1);
        double h = Math.Max(height, 1);
        double scale = Math.Min(w / (uMax - uMin), h / (vMax - vMin));
        double offsetX = ((w - ((uMax - uMin) * scale)) / 2) - (uMin * scale);
        double offsetY = ((h - ((vMax - vMin) * scale)) / 2) + (vMax * scale);
        return new ViewTransform(scale, offsetX, offsetY);
    }
}
