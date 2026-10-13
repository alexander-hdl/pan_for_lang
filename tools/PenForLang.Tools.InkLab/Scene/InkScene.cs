using System.Collections.Immutable;

namespace PenForLang.Tools.InkLab.Scene;

/// <summary>Точка сцены в координатах линейки: <c>u</c> вправо, <c>v</c> вверх, единица — x-height.</summary>
public readonly record struct ScenePoint(double U, double V);

/// <summary>Вид линии на сцене.</summary>
public enum SceneStyle
{
    /// <summary>Обычный след.</summary>
    Normal,

    /// <summary>Отложенный штрих (точка, перекладина).</summary>
    Deferred,

    /// <summary>Бледная «тень» ещё не проигранной части.</summary>
    Ghost,
}

/// <summary>Ломаная на сцене: часть штриха одной буквы.</summary>
/// <param name="Points">Точки в координатах линейки.</param>
/// <param name="Group">Номер буквы (цвет); <see cref="SceneBuilder.NeutralGroup"/> — без буквы; <see cref="SceneBuilder.JoinGroup"/> — соединительный участок.</param>
/// <param name="Style">Вид линии.</param>
/// <param name="StrokeIndex">Номер штриха записи.</param>
public sealed record ScenePolyline(ImmutableArray<ScenePoint> Points, int Group, SceneStyle Style, int StrokeIndex);

/// <summary>Вид метки на сцене.</summary>
public enum MarkerKind
{
    /// <summary>Граница буквы (конец левой буквы или начало правой).</summary>
    Junction,

    /// <summary>Подсказка (например, текущая точка).</summary>
    Info,
}

/// <summary>Метка на сцене.</summary>
/// <param name="U">Положение по <c>u</c>.</param>
/// <param name="V">Положение по <c>v</c>.</param>
/// <param name="Label">Подпись.</param>
/// <param name="Kind">Вид.</param>
public sealed record SceneMarker(double U, double V, string Label, MarkerKind Kind);

/// <summary>Линии линейки в x-height (из стиля): асцендер, x-height, базовая линия, десцендер.</summary>
public sealed record SceneGuides(double Ascender, double Cap, double XHeight, double Baseline, double Descender)
{
    /// <summary>Линии Zaner-Bloser из <c>style.json</c> v0 (на случай, когда стиль не загрузился).</summary>
    public static SceneGuides Default { get; } = new(2.0, 2.0, 1.0, 0.0, -1.0);
}

/// <summary>Всё, что рисует <c>StrokeView</c>: линейки, ломаные и метки. Чистые данные, без WPF.</summary>
/// <param name="Guides">Линии линейки.</param>
/// <param name="Polylines">Ломаные.</param>
/// <param name="Markers">Метки.</param>
public sealed record InkScene(SceneGuides Guides, ImmutableArray<ScenePolyline> Polylines, ImmutableArray<SceneMarker> Markers)
{
    /// <summary>Пустая сцена с линиями по умолчанию.</summary>
    public static InkScene Empty { get; } = new(SceneGuides.Default, [], []);

    /// <summary>Границы содержимого в координатах линейки (по ломаным и меткам) или <c>null</c>, если рисовать нечего.</summary>
    public (double UMin, double UMax, double VMin, double VMax)? ContentBounds()
    {
        double uMin = double.PositiveInfinity;
        double uMax = double.NegativeInfinity;
        double vMin = double.PositiveInfinity;
        double vMax = double.NegativeInfinity;
        foreach (ScenePolyline line in Polylines)
        {
            foreach (ScenePoint p in line.Points)
            {
                uMin = Math.Min(uMin, p.U);
                uMax = Math.Max(uMax, p.U);
                vMin = Math.Min(vMin, p.V);
                vMax = Math.Max(vMax, p.V);
            }
        }

        foreach (SceneMarker m in Markers)
        {
            uMin = Math.Min(uMin, m.U);
            uMax = Math.Max(uMax, m.U);
            vMin = Math.Min(vMin, m.V);
            vMax = Math.Max(vMax, m.V);
        }

        return double.IsFinite(uMin) ? (uMin, uMax, vMin, vMax) : null;
    }
}
