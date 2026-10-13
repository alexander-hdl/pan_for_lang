using PenForLang.Core.Geometry;
using PenForLang.Glyphs;

namespace PenForLang.App.Wpf.Lesson;

/// <summary>
/// Мягкий сигнал неверного старта по ходу письма (METHODOLOGY.md §7.1): первая точка первого штриха далеко от допустимых мест старта
/// эталона. Намеренно осторожнее оценщика (порог в x-height берётся из конфигурации, с запасом): ложный сигнал вреднее пропущенного, а
/// решает после попытки оценщик. Работает только когда эталон стоит под пером, то есть положение штриха имеет смысл (обводка).
/// </summary>
public static class LiveStartCheck
{
    /// <summary>Расстояние от точки до ближайшего допустимого места старта эталона, x-height.</summary>
    /// <param name="glyph">Эталон.</param>
    /// <param name="point">Первая точка штриха в линейке (u, v).</param>
    public static double Clearance(Glyph glyph, Vec2 point)
    {
        ArgumentNullException.ThrowIfNull(glyph);
        double best = double.PositiveInfinity;
        foreach (Vec2[] path in TemplateGeometry.AllowedStartPaths(glyph))
        {
            best = path.Length == 1
                ? Math.Min(best, Vec2.Distance(path[0], point))
                : Math.Min(best, PolylineMath.DistanceToPolyline(point, path));
        }

        return best;
    }

    /// <summary>Подавать ли сигнал: расстояние до допустимых мест старта больше порога.</summary>
    /// <param name="glyph">Эталон.</param>
    /// <param name="point">Первая точка штриха в линейке.</param>
    /// <param name="startClearance">Порог, x-height.</param>
    public static bool IsWrongStart(Glyph glyph, Vec2 point, double startClearance) => Clearance(glyph, point) > startClearance;
}
