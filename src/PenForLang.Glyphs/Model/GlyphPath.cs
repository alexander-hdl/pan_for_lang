using System.Collections.Immutable;
using PenForLang.Core.Geometry;

namespace PenForLang.Glyphs;

/// <summary>
/// Составная траектория из сегментов (<see cref="GlyphSegment"/>) с накопленными длинами дуги.
/// Параметр положения — «доля длины дуги» <c>f ∈ [0, 1]</c> всего пути либо расстояние <c>d ∈ [0, Length]</c>
/// в единицах линейки. Экземпляр неизменяем; все методы детерминированы.
/// </summary>
public sealed class GlyphPath
{
    private const double LengthTolerance = 1e-9;
    private const double MinTangentLength = 1e-12;

    private readonly double[] _lengths;
    private readonly double[] _starts;

    /// <summary>Создаёт путь. Непрерывность сегментов не проверяется (это делает валидатор стиль-пакета).</summary>
    /// <param name="segments">Сегменты в порядке написания, не пусто.</param>
    /// <exception cref="ArgumentException">Список сегментов пуст.</exception>
    public GlyphPath(IEnumerable<GlyphSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        Segments = [.. segments];
        if (Segments.Length == 0)
        {
            throw new ArgumentException("A path needs at least one segment.", nameof(segments));
        }

        _lengths = new double[Segments.Length];
        _starts = new double[Segments.Length];
        double total = 0.0;
        for (int i = 0; i < Segments.Length; i++)
        {
            _starts[i] = total;
            _lengths[i] = Segments[i].Curve.Length(LengthTolerance);
            total += _lengths[i];
        }

        Length = total;
    }

    /// <summary>Сегменты в порядке написания.</summary>
    public ImmutableArray<GlyphSegment> Segments { get; }

    /// <summary>Полная длина дуги, x-height.</summary>
    public double Length { get; }

    /// <summary>Начальная точка пути.</summary>
    public Vec2 Start => Segments[0].P0;

    /// <summary>Конечная точка пути.</summary>
    public Vec2 End => Segments[^1].P3;

    /// <summary>Единичная касательная в начале пути.</summary>
    public Vec2 StartTangent => TangentAtDistance(0.0);

    /// <summary>Единичная касательная в конце пути (направление движения при выходе).</summary>
    public Vec2 EndTangent => TangentAtSegment(Segments.Length - 1, 1.0);

    /// <summary>Длина дуги сегмента <paramref name="index"/>.</summary>
    /// <param name="index">Индекс сегмента.</param>
    public double SegmentLength(int index) => _lengths[index];

    /// <summary>Расстояние от начала пути до начала сегмента <paramref name="index"/>.</summary>
    /// <param name="index">Индекс сегмента.</param>
    public double SegmentStart(int index) => _starts[index];

    /// <summary>Точка пути на расстоянии <paramref name="distance"/> от начала (значения вне [0, Length] обрезаются).</summary>
    /// <param name="distance">Расстояние по дуге.</param>
    public Vec2 PointAtDistance(double distance)
    {
        Locate(distance, out int index, out double t);
        return Segments[index].Curve.PointAt(t);
    }

    /// <summary>Единичная касательная на расстоянии <paramref name="distance"/>; в стыке сегментов — касательная следующего.</summary>
    /// <param name="distance">Расстояние по дуге.</param>
    public Vec2 TangentAtDistance(double distance)
    {
        Locate(distance, out int index, out double t);
        return TangentAtSegment(index, t);
    }

    /// <summary>Точка пути в доле длины <paramref name="fraction"/> ∈ [0, 1].</summary>
    /// <param name="fraction">Доля длины дуги.</param>
    public Vec2 PointAtFraction(double fraction) => PointAtDistance(Math.Clamp(fraction, 0.0, 1.0) * Length);

    /// <summary>Единичная касательная в доле длины <paramref name="fraction"/> ∈ [0, 1].</summary>
    /// <param name="fraction">Доля длины дуги.</param>
    public Vec2 TangentAtFraction(double fraction) => TangentAtDistance(Math.Clamp(fraction, 0.0, 1.0) * Length);

    /// <summary>Доля длины дуги, на которой лежит конец сегмента <paramref name="index"/> (стык с <c>index + 1</c>).</summary>
    /// <param name="index">Индекс сегмента.</param>
    public double FractionAtSegmentEnd(int index) => Length > 0.0 ? (_starts[index] + _lengths[index]) / Length : 0.0;

    /// <summary>Полилиния, отклоняющаяся от пути не более чем на <paramref name="tolerance"/> (x-height).</summary>
    /// <param name="tolerance">Допуск, &gt; 0.</param>
    public Vec2[] Flatten(double tolerance)
    {
        var points = new List<Vec2>();
        for (int i = 0; i < Segments.Length; i++)
        {
            Segments[i].Curve.Flatten(tolerance, points, includeStart: i == 0);
        }

        return [.. points];
    }

    /// <summary>Границы пути (по полилинии с допуском 0,002 x-height).</summary>
    public (Vec2 Min, Vec2 Max) GetBounds()
    {
        Vec2[] points = Flatten(0.002);
        double minX = double.MaxValue;
        double minY = double.MaxValue;
        double maxX = double.MinValue;
        double maxY = double.MinValue;
        foreach (Vec2 p in points)
        {
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }

        return (new Vec2(minX, minY), new Vec2(maxX, maxY));
    }

    /// <summary>Находит сегмент и локальный параметр кривой для расстояния <paramref name="distance"/> (с обрезкой).</summary>
    /// <param name="distance">Расстояние по дуге от начала пути.</param>
    /// <param name="segmentIndex">Индекс сегмента.</param>
    /// <param name="t">Параметр кривой Безье сегмента, [0, 1].</param>
    public void Locate(double distance, out int segmentIndex, out double t)
    {
        if (!(distance > 0.0))
        {
            segmentIndex = 0;
            t = 0.0;
            return;
        }

        if (distance >= Length)
        {
            segmentIndex = Segments.Length - 1;
            t = 1.0;
            return;
        }

        int index = 0;
        for (int i = Segments.Length - 1; i >= 0; i--)
        {
            if (_starts[i] <= distance && _lengths[i] > 0.0)
            {
                index = i;
                break;
            }
        }

        segmentIndex = index;
        t = Segments[index].Curve.ParameterAtLength(distance - _starts[index], LengthTolerance);
    }

    private Vec2 TangentAtSegment(int index, double t)
    {
        CubicBezier curve = Segments[index].Curve;
        Vec2 d = curve.DerivativeAt(t);
        if (d.Length < MinTangentLength)
        {
            // Совпадающие контрольные точки на конце: берём ближайшее ненулевое направление.
            d = t < 0.5 ? curve.P2 - curve.P0 : curve.P3 - curve.P1;
            if (d.Length < MinTangentLength)
            {
                d = curve.P3 - curve.P0;
            }
        }

        return d.Normalized();
    }
}
