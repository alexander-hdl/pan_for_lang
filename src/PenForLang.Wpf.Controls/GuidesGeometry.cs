using PenForLang.Glyphs;
using PenForLang.Ink;

namespace PenForLang.Wpf.Controls;

/// <summary>Вид линии прописи.</summary>
public enum GuideKind
{
    /// <summary>Линия асцендеров (верх высоких букв).</summary>
    Ascender,

    /// <summary>Линия x-height (середина строки, «midline»).</summary>
    XHeight,

    /// <summary>Базовая линия.</summary>
    Baseline,

    /// <summary>Линия десцендеров (низ хвостов).</summary>
    Descender,

    /// <summary>Наклонная линия.</summary>
    Slant,
}

/// <summary>Отрезок линейки в DIP относительно левого верхнего угла слоя.</summary>
/// <param name="Kind">Вид линии.</param>
/// <param name="X1">Начало, X.</param>
/// <param name="Y1">Начало, Y (ось вниз).</param>
/// <param name="X2">Конец, X.</param>
/// <param name="Y2">Конец, Y.</param>
public readonly record struct GuideSegment(GuideKind Kind, double X1, double Y1, double X2, double Y2);

/// <summary>
/// Чистая геометрия линеек прописи: из линейки попытки (<see cref="GuideFrame"/>, физические px поверхности, ARCHITECTURE.md §3.1)
/// и стиля (<see cref="StyleManifest.Lines"/>, <see cref="StyleManifest.SlantDeg"/>) получает отрезки в DIP.
/// </summary>
/// <remarks>
/// Размеры строки заданы в px поверхности, а <c>GuideFrame</c> строится через <c>GuideFrame.FromMm</c> (мм × <c>PxPerMm</c>) или
/// <c>FromDip</c>, поэтому DPI-масштаб входит ровно один раз — здесь, при переводе px → DIP (<c>px / dpiScale</c>, A12). Физический
/// размер строки на экране от масштаба Windows не зависит.
/// </remarks>
public static class GuidesGeometry
{
    /// <summary>Шаг наклонных линий по умолчанию, в x-height.</summary>
    public const double DefaultSlantSpacingXHeights = 1.0;

    /// <summary>Строит отрезки линейки.</summary>
    /// <param name="frame">Линейка попытки.</param>
    /// <param name="style">Стиль (линии и наклон).</param>
    /// <param name="dpiScale">DPI-масштаб монитора окна (1.0 = 96 dpi); &gt; 0.</param>
    /// <param name="widthDip">Ширина слоя, DIP.</param>
    /// <param name="heightDip">Высота слоя, DIP.</param>
    /// <param name="repeatRows">Повторять строки через <c>LineSpacing</c> x-height вверх и вниз, пока они попадают в слой.</param>
    /// <param name="slantSpacingXHeights">Горизонтальный шаг наклонных линий в x-height; &gt; 0.</param>
    public static IReadOnlyList<GuideSegment> Compute(
        GuideFrame frame,
        StyleManifest style,
        double dpiScale,
        double widthDip,
        double heightDip,
        bool repeatRows = false,
        double slantSpacingXHeights = DefaultSlantSpacingXHeights)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(style);
        if (!double.IsFinite(dpiScale) || dpiScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpiScale), dpiScale, "DPI scale must be finite and positive.");
        }

        if (!(slantSpacingXHeights > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(slantSpacingXHeights), slantSpacingXHeights, "Spacing must be positive.");
        }

        var result = new List<GuideSegment>();
        if (!(widthDip > 0) || !(heightDip > 0))
        {
            return result;
        }

        double xh = frame.XHeightPx;
        double rowStep = Math.Max(frame.LineSpacing, 0) * xh;
        double slantDeg = frame.SlantDeg ?? style.SlantDeg ?? 0;
        double slantK = Math.Tan(slantDeg * Math.PI / 180.0);

        // Базовые линии строк в px поверхности: главная и (при repeatRows) её повторения, видимые в слое.
        var baselines = new List<double> { frame.BaselineYPx };
        if (repeatRows && rowStep > 0)
        {
            double layerHeightPx = heightDip * dpiScale;
            for (double b = frame.BaselineYPx - rowStep; b + (-style.Lines.Descender * xh) >= 0; b -= rowStep)
            {
                baselines.Add(b);
            }

            for (double b = frame.BaselineYPx + rowStep; b - (style.Lines.Ascender * xh) <= layerHeightPx; b += rowStep)
            {
                baselines.Add(b);
            }
        }

        foreach (double baselinePx in baselines)
        {
            double Y(double v) => (baselinePx - (v * xh)) / dpiScale;

            AddHorizontal(result, GuideKind.Ascender, Y(style.Lines.Ascender), widthDip);
            AddHorizontal(result, GuideKind.XHeight, Y(style.Lines.XHeight), widthDip);
            AddHorizontal(result, GuideKind.Baseline, Y(style.Lines.Baseline), widthDip);
            AddHorizontal(result, GuideKind.Descender, Y(style.Lines.Descender), widthDip);

            if (slantDeg != 0)
            {
                AddSlants(result, frame, style, dpiScale, widthDip, baselinePx, slantK, slantSpacingXHeights);
            }
        }

        return result;
    }

    private static void AddHorizontal(List<GuideSegment> result, GuideKind kind, double y, double widthDip) =>
        result.Add(new GuideSegment(kind, 0, y, widthDip, y));

    private static void AddSlants(
        List<GuideSegment> result,
        GuideFrame frame,
        StyleManifest style,
        double dpiScale,
        double widthDip,
        double baselinePx,
        double slantK,
        double spacingXHeights)
    {
        double xh = frame.XHeightPx;
        double vTop = style.Lines.Ascender;
        double vBottom = style.Lines.Descender;

        // Линия i пересекает baseline в x = x0 + i · spacing; верх и низ смещены на slantK · v · xh (наклон вправо вверх).
        double step = spacingXHeights * xh;
        double xTopOffset = slantK * vTop * xh;
        double xBottomOffset = slantK * vBottom * xh;
        double widthPx = widthDip * dpiScale;
        double leftmost = Math.Min(xTopOffset, xBottomOffset);
        double rightmost = Math.Max(xTopOffset, xBottomOffset);

        // Номера линий, у которых отрезок пересекает [0, widthPx].
        int first = (int)Math.Floor((0 - rightmost - frame.OriginXPx) / step);
        int last = (int)Math.Ceiling((widthPx - leftmost - frame.OriginXPx) / step);
        for (int i = first; i <= last; i++)
        {
            double xBase = frame.OriginXPx + (i * step);
            double x1 = (xBase + xBottomOffset) / dpiScale;
            double y1 = (baselinePx - (vBottom * xh)) / dpiScale;
            double x2 = (xBase + xTopOffset) / dpiScale;
            double y2 = (baselinePx - (vTop * xh)) / dpiScale;
            result.Add(new GuideSegment(GuideKind.Slant, x1, y1, x2, y2));
        }
    }
}
