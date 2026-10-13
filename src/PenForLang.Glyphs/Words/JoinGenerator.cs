using PenForLang.Core.Geometry;

namespace PenForLang.Glyphs;

/// <summary>
/// Генератор геометрии соединения между буквами слова (ARCHITECTURE.md §3.2.2, «Формы соединения»). Соединение строится
/// между точкой и направлением ухода левой буквы и точкой и направлением прихода правой (G1-непрерывность на обоих концах).
/// Чистая детерминированная функция.
/// </summary>
public static class JoinGenerator
{
    /// <summary>Длина касательной Эрмита в долях хорды (кубика Безье: контрольные точки на 0,4 хорды от концов).</summary>
    public const double TangentShare = 0.4;

    /// <summary>Параметр правила для формы <see cref="JoinShape.HermiteOver"/>: на сколько x-height «перекат» вытягивает подход (по умолчанию 0,12).</summary>
    public const string OvershootParam = "overshootU";

    private const double DefaultOvershoot = 0.12;
    private const double MinChord = 1e-6;

    /// <summary>
    /// Строит путь соединения. Для <see cref="JoinShape.Break"/> возвращает <c>null</c>: ожидаемый отрыв, геометрии нет.
    /// </summary>
    /// <param name="rule">Правило соединения.</param>
    /// <param name="exitPoint">Точка выхода левой буквы (конец тела).</param>
    /// <param name="exitTangent">Единичное направление ухода из левой буквы.</param>
    /// <param name="entryPoint">Точка входа правой буквы (начало тела).</param>
    /// <param name="entryTangent">Единичное направление прихода в правую букву.</param>
    public static GlyphPath? Generate(JoinRule rule, Vec2 exitPoint, Vec2 exitTangent, Vec2 entryPoint, Vec2 entryTangent)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule.Shape == JoinShape.Break)
        {
            return null;
        }

        double chord = Vec2.Distance(exitPoint, entryPoint);
        if (chord < MinChord)
        {
            return null;
        }

        Vec2 te = exitTangent.Normalized();
        Vec2 tn = entryTangent.Normalized();
        double reach = chord * TangentShare;
        double approach = reach;
        string label = rule.Family is null ? "join" : "join." + rule.Family;
        if (rule.Shape == JoinShape.HermiteOver)
        {
            // «Перекат» к старту овала: подход длиннее, кривая подходит к входу по касательной уже раскатанной.
            double overshoot = rule.Params.TryGetValue(OvershootParam, out double value) ? value : DefaultOvershoot;
            approach += overshoot;
        }

        Vec2 p1 = exitPoint + (te * reach);
        Vec2 p2 = entryPoint - (tn * approach);
        return new GlyphPath([GlyphSegment.Cubic(exitPoint, p1, p2, entryPoint, retrace: false, label: label)]);
    }
}
