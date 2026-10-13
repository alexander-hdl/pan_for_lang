namespace PenForLang.Core.Geometry;

/// <summary>
/// Деслантинг: сдвиг по x, пропорциональный высоте (shear), убирающий наклон письма.
/// Система координат линейки: y вверх (ARCHITECTURE.md §3.1).
/// </summary>
public static class Deslant
{
    /// <summary>
    /// Применяет сдвиг <c>x' = x − (y − pivotY)·tan(slantRadians)</c>, <c>y' = y</c>. Угол отсчитывается от вертикали,
    /// положительный — наклон вправо (верх письма правее низа): преобразование сдвигает верх влево и выпрямляет письмо.
    /// Точки на линии <c>y = pivotY</c> остаются на месте. Допускается один и тот же буфер в
    /// <paramref name="source"/> и <paramref name="destination"/>.
    /// </summary>
    /// <param name="source">Исходные точки.</param>
    /// <param name="destination">Результат, длиной не меньше исходных точек.</param>
    /// <param name="slantRadians">Угол наклона от вертикали в радианах, |угол| &lt; π/2.</param>
    /// <param name="pivotY">Ордината, остающаяся неподвижной (обычно baseline).</param>
    public static void Apply(ReadOnlySpan<Vec2> source, Span<Vec2> destination, double slantRadians, double pivotY = 0.0)
    {
        if (destination.Length < source.Length)
        {
            throw new ArgumentException("Destination is shorter than the source.", nameof(destination));
        }

        if (!(Math.Abs(slantRadians) < Math.PI / 2.0))
        {
            throw new ArgumentOutOfRangeException(nameof(slantRadians), "Slant must be within (-π/2, π/2).");
        }

        double k = Math.Tan(slantRadians);
        for (int i = 0; i < source.Length; i++)
        {
            Vec2 p = source[i];
            destination[i] = new Vec2(p.X - ((p.Y - pivotY) * k), p.Y);
        }
    }

    /// <summary>То же, что <see cref="Apply"/>, но угол задан в градусах.</summary>
    /// <param name="source">Исходные точки.</param>
    /// <param name="destination">Результат.</param>
    /// <param name="slantDegrees">Угол наклона от вертикали в градусах.</param>
    /// <param name="pivotY">Ордината, остающаяся неподвижной.</param>
    public static void ApplyDegrees(ReadOnlySpan<Vec2> source, Span<Vec2> destination, double slantDegrees, double pivotY = 0.0) =>
        Apply(source, destination, slantDegrees * (Math.PI / 180.0), pivotY);
}
