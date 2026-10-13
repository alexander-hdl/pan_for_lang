using PenForLang.Core.Geometry;

namespace PenForLang.Glyphs;

/// <summary>Вид сегмента траектории глифа.</summary>
public enum SegmentKind
{
    /// <summary>Отрезок (<c>{"l": [p0, p1]}</c> в JSON).</summary>
    Line,

    /// <summary>Кубическая кривая Безье (<c>{"c": [p0, p1, p2, p3]}</c> в JSON).</summary>
    Cubic,
}

/// <summary>
/// Сегмент траектории глифа в координатах линейки (baseline = 0, x-height = 1, ось v вверх; ARCHITECTURE.md §3.1).
/// Отрезок хранится как вырожденная кубическая кривая с контрольными точками в третях: тогда параметр кривой
/// пропорционален длине дуги, а вся геометрия работает с одним типом (<see cref="CubicBezier"/>).
/// </summary>
/// <param name="Kind">Вид сегмента (из JSON).</param>
/// <param name="P0">Начальная точка.</param>
/// <param name="P1">Первая контрольная точка (для отрезка — на трети).</param>
/// <param name="P2">Вторая контрольная точка (для отрезка — на двух третях).</param>
/// <param name="P3">Конечная точка.</param>
/// <param name="Retrace">Обратный ход по уже написанному (ретрейс).</param>
/// <param name="Label">Ключ словесной метки (<c>labels/*.json</c>) или <c>null</c>.</param>
/// <param name="Tempo">Множитель скорости анимации на сегменте, &gt; 0 (1 — обычная).</param>
/// <param name="PauseBeforeMs">Пауза анимации перед сегментом, мс (0 — нет).</param>
public sealed record GlyphSegment(
    SegmentKind Kind,
    Vec2 P0,
    Vec2 P1,
    Vec2 P2,
    Vec2 P3,
    bool Retrace = false,
    string? Label = null,
    double Tempo = 1.0,
    int PauseBeforeMs = 0)
{
    /// <summary>Создаёт отрезок.</summary>
    /// <param name="from">Начало.</param>
    /// <param name="to">Конец.</param>
    /// <param name="retrace">Ретрейс.</param>
    /// <param name="label">Ключ метки.</param>
    /// <param name="tempo">Множитель скорости анимации.</param>
    /// <param name="pauseBeforeMs">Пауза перед сегментом, мс.</param>
    public static GlyphSegment Line(Vec2 from, Vec2 to, bool retrace = false, string? label = null, double tempo = 1.0, int pauseBeforeMs = 0) =>
        new(
            SegmentKind.Line,
            from,
            Vec2.Lerp(from, to, 1.0 / 3.0),
            Vec2.Lerp(from, to, 2.0 / 3.0),
            to,
            retrace,
            label,
            tempo,
            pauseBeforeMs);

    /// <summary>Создаёт кубическую кривую Безье.</summary>
    /// <param name="p0">Начальная точка.</param>
    /// <param name="p1">Первая контрольная точка.</param>
    /// <param name="p2">Вторая контрольная точка.</param>
    /// <param name="p3">Конечная точка.</param>
    /// <param name="retrace">Ретрейс.</param>
    /// <param name="label">Ключ метки.</param>
    /// <param name="tempo">Множитель скорости анимации.</param>
    /// <param name="pauseBeforeMs">Пауза перед сегментом, мс.</param>
    public static GlyphSegment Cubic(Vec2 p0, Vec2 p1, Vec2 p2, Vec2 p3, bool retrace = false, string? label = null, double tempo = 1.0, int pauseBeforeMs = 0) =>
        new(SegmentKind.Cubic, p0, p1, p2, p3, retrace, label, tempo, pauseBeforeMs);

    /// <summary>Кривая Безье сегмента (для отрезка — вырожденная).</summary>
    public CubicBezier Curve => new(P0, P1, P2, P3);
}
