namespace PenForLang.Core.Geometry;

/// <summary>
/// Положение точки на ломаной: сегмент <see cref="Segment"/> (от вершины <c>Segment</c> к <c>Segment + 1</c>)
/// и доля пути <see cref="T"/> ∈ [0, 1] вдоль него.
/// </summary>
/// <param name="Segment">Индекс начальной вершины сегмента.</param>
/// <param name="T">Доля пути вдоль сегмента.</param>
public readonly record struct SamplePosition(int Segment, double T);

/// <summary>
/// Ресэмплинг ломаной по длине дуги с равным шагом. Сама ломаная не меняется; дополнительные каналы
/// (время, давление) переносятся линейной интерполяцией по <see cref="SamplePosition"/>.
/// </summary>
public static class Resampler
{
    private const int StackLimit = 256;

    /// <summary>
    /// Число точек для шага, ближайшего к <paramref name="step"/>: <c>max(2, round(L/step) + 1)</c>. Фактический шаг
    /// <c>L/(count−1)</c> отличается от заданного не более чем на <c>step/(2L)</c> относительно шага, то есть меньше 1% при
    /// <c>L ≥ 50·step</c>. Для нулевой длины возвращает 1.
    /// </summary>
    /// <param name="length">Длина ломаной.</param>
    /// <param name="step">Желаемый шаг, &gt; 0.</param>
    public static int CountForStep(double length, double step)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(step);
        if (!(length > 0.0))
        {
            return 1;
        }

        double intervals = Math.Round(length / step, MidpointRounding.AwayFromZero);
        if (intervals > int.MaxValue - 2)
        {
            throw new ArgumentOutOfRangeException(nameof(step), "Step is too small for this length.");
        }

        return Math.Max(2, (int)intervals + 1);
    }

    /// <summary>
    /// Вычисляет положения <c>destination.Length</c> точек, равноотстоящих по длине дуги (первая — начало, последняя — конец ломаной).
    /// Для нулевой длины все положения указывают на начало.
    /// </summary>
    /// <param name="source">Вершины исходной ломаной (не менее одной).</param>
    /// <param name="destination">Буфер положений; его длина — число выходных точек.</param>
    public static void ResamplePositions(ReadOnlySpan<Vec2> source, Span<SamplePosition> destination)
    {
        if (source.Length == 0)
        {
            throw new ArgumentException("Polyline is empty.", nameof(source));
        }

        int count = destination.Length;
        if (count == 0)
        {
            return;
        }

        double length = PolylineMath.Length(source);
        if (source.Length == 1 || !(length > 0.0) || count == 1)
        {
            for (int k = 0; k < count; k++)
            {
                destination[k] = new SamplePosition(0, 0.0);
            }

            return;
        }

        int lastSegment = source.Length - 2;
        double step = length / (count - 1);
        int seg = 0;
        double segStart = 0.0;
        double segLen = Vec2.Distance(source[0], source[1]);
        for (int k = 0; k < count; k++)
        {
            double s = k == count - 1 ? length : k * step;
            while (seg < lastSegment && segStart + segLen < s)
            {
                segStart += segLen;
                seg++;
                segLen = Vec2.Distance(source[seg], source[seg + 1]);
            }

            double t = segLen > 0.0 ? Math.Clamp((s - segStart) / segLen, 0.0, 1.0) : 0.0;
            destination[k] = new SamplePosition(seg, t);
        }
    }

    /// <summary>Точка ломаной в заданном положении.</summary>
    /// <param name="source">Вершины ломаной.</param>
    /// <param name="position">Положение, полученное из <see cref="ResamplePositions"/>.</param>
    public static Vec2 Interpolate(ReadOnlySpan<Vec2> source, SamplePosition position)
    {
        if (source.Length == 1)
        {
            return source[0];
        }

        return Vec2.Lerp(source[position.Segment], source[position.Segment + 1], position.T);
    }

    /// <summary>Значение дополнительного канала (время, давление) в заданном положении; канал параллелен вершинам ломаной.</summary>
    /// <param name="channel">Значения в вершинах ломаной.</param>
    /// <param name="position">Положение, полученное из <see cref="ResamplePositions"/>.</param>
    public static double Interpolate(ReadOnlySpan<double> channel, SamplePosition position)
    {
        if (channel.Length == 1)
        {
            return channel[0];
        }

        double a = channel[position.Segment];
        double b = channel[position.Segment + 1];
        return a + ((b - a) * position.T);
    }

    /// <summary>
    /// Равномерный по длине дуги ресэмплинг в <c>destination.Length</c> точек (шаг <c>L/(count−1)</c>); первая и последняя
    /// точки совпадают с концами ломаной. Не выделяет память при <c>destination.Length ≤ 256</c>.
    /// </summary>
    /// <param name="source">Вершины исходной ломаной.</param>
    /// <param name="destination">Буфер выходных точек.</param>
    public static void Resample(ReadOnlySpan<Vec2> source, Span<Vec2> destination)
    {
        int count = destination.Length;
        Span<SamplePosition> positions = count <= StackLimit
            ? stackalloc SamplePosition[count]
            : new SamplePosition[count];
        ResamplePositions(source, positions);
        for (int k = 0; k < count; k++)
        {
            destination[k] = Interpolate(source, positions[k]);
        }
    }

    /// <summary>
    /// Ресэмплинг с шагом, ближайшим к <paramref name="step"/> (см. <see cref="CountForStep"/>). Возвращает новый массив.
    /// </summary>
    /// <param name="source">Вершины исходной ломаной.</param>
    /// <param name="step">Желаемый шаг по длине дуги, &gt; 0.</param>
    public static Vec2[] ResampleByStep(ReadOnlySpan<Vec2> source, double step)
    {
        if (source.Length == 0)
        {
            throw new ArgumentException("Polyline is empty.", nameof(source));
        }

        var result = new Vec2[CountForStep(PolylineMath.Length(source), step)];
        Resample(source, result);
        return result;
    }
}
