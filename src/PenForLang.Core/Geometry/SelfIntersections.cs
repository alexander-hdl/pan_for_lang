namespace PenForLang.Core.Geometry;

/// <summary>Пересечение двух несмежных сегментов ломаной.</summary>
/// <param name="SegmentA">Индекс первого сегмента (сегмент <c>k</c> идёт от вершины <c>k</c> к <c>k + 1</c>); <c>SegmentA &lt; SegmentB</c>.</param>
/// <param name="SegmentB">Индекс второго сегмента.</param>
/// <param name="TA">Доля пути вдоль первого сегмента в точке пересечения, [0, 1).</param>
/// <param name="TB">Доля пути вдоль второго сегмента, [0, 1).</param>
/// <param name="Point">Точка пересечения.</param>
public readonly record struct SegmentIntersection(int SegmentA, int SegmentB, double TA, double TB, Vec2 Point);

/// <summary>Петля: часть ломаной между двумя прохождениями через одну точку самопересечения.</summary>
/// <param name="Intersection">Самопересечение, замыкающее петлю.</param>
/// <param name="Length">Длина петли по дуге (от точки пересечения до неё же).</param>
/// <param name="SignedArea">Площадь петли со знаком (формула шнуровки): положительна при обходе против часовой стрелки при оси y вверх.</param>
public readonly record struct PolylineLoop(SegmentIntersection Intersection, double Length, double SignedArea)
{
    /// <summary>Модуль площади петли.</summary>
    public double Area => Math.Abs(SignedArea);
}

/// <summary>
/// Самопересечения и петли ломаной (ARCHITECTURE.md §6.4.9: F3 <c>loopOk</c> — петля на участке <c>checks.loops</c> присутствует
/// и достаточна; различение петли и ретрейса P6; малые петли в соединении J4). Сложность O(n²) по числу сегментов.
/// </summary>
public static class SelfIntersections
{
    /// <summary>
    /// Пересечение двух отрезков в виде долей пути. Возвращает <c>false</c> для параллельных (в том числе коллинеарных) отрезков.
    /// Доли полуоткрытые: <c>ta, tb ∈ [0, 1)</c> — пересечение ровно в вершине засчитывается одному из двух сегментов.
    /// </summary>
    /// <param name="a0">Начало первого отрезка.</param>
    /// <param name="a1">Конец первого отрезка.</param>
    /// <param name="b0">Начало второго отрезка.</param>
    /// <param name="b1">Конец второго отрезка.</param>
    /// <param name="ta">Доля пути на первом отрезке.</param>
    /// <param name="tb">Доля пути на втором отрезке.</param>
    public static bool TryIntersectSegments(Vec2 a0, Vec2 a1, Vec2 b0, Vec2 b1, out double ta, out double tb)
    {
        Vec2 r = a1 - a0;
        Vec2 s = b1 - b0;
        double denominator = Vec2.Cross(r, s);
        ta = 0.0;
        tb = 0.0;
        if (denominator == 0.0)
        {
            return false;
        }

        Vec2 diff = b0 - a0;
        double t = Vec2.Cross(diff, s) / denominator;
        double u = Vec2.Cross(diff, r) / denominator;
        if (t < 0.0 || t >= 1.0 || u < 0.0 || u >= 1.0)
        {
            return false;
        }

        ta = t;
        tb = u;
        return true;
    }

    /// <summary>
    /// Все пересечения несмежных сегментов ломаной в порядке (<c>SegmentA</c>, <c>SegmentB</c>). Смежные сегменты (общая
    /// вершина) не рассматриваются, поэтому касание соседей не считается самопересечением.
    /// </summary>
    /// <param name="points">Вершины ломаной.</param>
    /// <param name="destination">Список, в конец которого добавляются пересечения.</param>
    public static void Find(ReadOnlySpan<Vec2> points, List<SegmentIntersection> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        int segments = points.Length - 1;
        for (int i = 0; i < segments; i++)
        {
            for (int j = i + 2; j < segments; j++)
            {
                if (TryIntersectSegments(points[i], points[i + 1], points[j], points[j + 1], out double ta, out double tb))
                {
                    destination.Add(new SegmentIntersection(i, j, ta, tb, Vec2.Lerp(points[i], points[i + 1], ta)));
                }
            }
        }
    }

    /// <summary>
    /// Петли: для каждого самопересечения — длина и площадь замкнутой части ломаной между его двумя прохождениями.
    /// Петли могут вкладываться и перекрываться; отбор по размеру (малые петли шума, F3 <c>minLoopArea</c>) — за вызывающим кодом.
    /// </summary>
    /// <param name="points">Вершины ломаной.</param>
    /// <param name="destination">Список, в конец которого добавляются петли.</param>
    public static void FindLoops(ReadOnlySpan<Vec2> points, List<PolylineLoop> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var intersections = new List<SegmentIntersection>();
        Find(points, intersections);
        foreach (SegmentIntersection x in intersections)
        {
            // Многоугольник: точка пересечения, вершины x.SegmentA + 1 … x.SegmentB, обратно к точке пересечения.
            double length = Vec2.Distance(x.Point, points[x.SegmentA + 1]);
            double twiceArea = Vec2.Cross(x.Point, points[x.SegmentA + 1]);
            for (int k = x.SegmentA + 1; k < x.SegmentB; k++)
            {
                length += Vec2.Distance(points[k], points[k + 1]);
                twiceArea += Vec2.Cross(points[k], points[k + 1]);
            }

            length += Vec2.Distance(points[x.SegmentB], x.Point);
            twiceArea += Vec2.Cross(points[x.SegmentB], x.Point);
            destination.Add(new PolylineLoop(x, length, twiceArea * 0.5));
        }
    }
}
