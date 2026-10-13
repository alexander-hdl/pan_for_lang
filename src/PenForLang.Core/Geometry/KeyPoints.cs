namespace PenForLang.Core.Geometry;

/// <summary>Вид ключевой точки полилинии.</summary>
public enum KeyPointKind : byte
{
    /// <summary>Локальный максимум y (верх горба, вершина ascender); ось y вверх.</summary>
    LocalMaxY = 0,

    /// <summary>Локальный минимум y (низ овала, baseline, descender).</summary>
    LocalMinY = 1,

    /// <summary>Излом: резкий поворот, меньший порога разворота.</summary>
    Corner = 2,

    /// <summary>Точка возврата: поворот не менее порога разворота (ретрейс, острая вершина).</summary>
    Reversal = 3,
}

/// <summary>Ключевая точка полилинии.</summary>
/// <param name="Index">Индекс вершины.</param>
/// <param name="Position">Координаты вершины.</param>
/// <param name="Kind">Вид точки.</param>
/// <param name="Strength">Для экстремумов — размах (проминенс) относительно предыдущего экстремума, в единицах y (0 для начальной вершины); для изломов и возвратов — угол поворота в радианах, (0, π].</param>
public readonly record struct KeyPoint(int Index, Vec2 Position, KeyPointKind Kind, double Strength);

/// <summary>
/// Поиск ключевых точек: экстремумы по y с гистерезисом (горбы, овалы, baseline; ARCHITECTURE.md §6.4.9: F4, F5 и число горбов)
/// и изломы/точки возврата по углу поворота (якоря выравнивания §6.4.4, ретрейс P6, углы соединений J4).
/// Работает на ломаной после сглаживания и равномерного ресэмплинга; результаты упорядочены по индексу.
/// </summary>
public static class KeyPoints
{
    /// <summary>
    /// Локальные экстремумы y с гистерезисом: экстремум принимается, только если после него y отходит от него не менее чем на
    /// <paramref name="minProminence"/> (мелкий шум не даёт ложных экстремумов). Экстремумы чередуются: максимум, минимум…
    /// Начальная и конечная вершины — экстремумы лишь при <paramref name="includeEndpoints"/>.
    /// </summary>
    /// <param name="points">Вершины ломаной.</param>
    /// <param name="minProminence">Минимальный размах между соседними экстремумами, &gt; 0.</param>
    /// <param name="destination">Список, в конец которого добавляются точки.</param>
    /// <param name="includeEndpoints">Считать ли экстремумом начало и конец, если они — наибольшее или наименьшее значение своего участка.</param>
    public static void FindYExtrema(
        ReadOnlySpan<Vec2> points,
        double minProminence,
        List<KeyPoint> destination,
        bool includeEndpoints = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minProminence);
        ArgumentNullException.ThrowIfNull(destination);
        int n = points.Length;
        if (n < 2)
        {
            return;
        }

        int direction = 0; // +1 растёт к максимуму, -1 падает к минимуму
        int extreme = 0;
        double previousPivotY = points[0].Y;
        for (int i = 1; i < n; i++)
        {
            double y = points[i].Y;
            if (direction == 0)
            {
                if (y - points[0].Y >= minProminence)
                {
                    direction = 1;
                    extreme = i;
                    if (includeEndpoints)
                    {
                        destination.Add(new KeyPoint(0, points[0], KeyPointKind.LocalMinY, 0.0));
                    }
                }
                else if (points[0].Y - y >= minProminence)
                {
                    direction = -1;
                    extreme = i;
                    if (includeEndpoints)
                    {
                        destination.Add(new KeyPoint(0, points[0], KeyPointKind.LocalMaxY, 0.0));
                    }
                }

                continue;
            }

            if (direction > 0)
            {
                if (y > points[extreme].Y)
                {
                    extreme = i;
                }
                else if (points[extreme].Y - y >= minProminence)
                {
                    Emit(points, destination, previousPivotY, extreme, KeyPointKind.LocalMaxY, includeEndpoints);
                    previousPivotY = points[extreme].Y;
                    direction = -1;
                    extreme = i;
                }
            }
            else
            {
                if (y < points[extreme].Y)
                {
                    extreme = i;
                }
                else if (y - points[extreme].Y >= minProminence)
                {
                    Emit(points, destination, previousPivotY, extreme, KeyPointKind.LocalMinY, includeEndpoints);
                    previousPivotY = points[extreme].Y;
                    direction = 1;
                    extreme = i;
                }
            }
        }

        if (direction != 0)
        {
            Emit(
                points,
                destination,
                previousPivotY,
                extreme,
                direction > 0 ? KeyPointKind.LocalMaxY : KeyPointKind.LocalMinY,
                includeEndpoints);
        }
    }

    private static void Emit(
        ReadOnlySpan<Vec2> points,
        List<KeyPoint> destination,
        double previousPivotY,
        int index,
        KeyPointKind kind,
        bool includeEndpoints)
    {
        bool endpoint = index == 0 || index == points.Length - 1;
        if (endpoint && !includeEndpoints)
        {
            return;
        }

        destination.Add(new KeyPoint(index, points[index], kind, Math.Abs(points[index].Y - previousPivotY)));
    }

    /// <summary>
    /// Изломы и точки возврата. Для вершины <c>i</c> берутся точки на расстоянии <paramref name="armLength"/> по дуге
    /// до и после неё; угол поворота — между направлениями «из предыдущей точки в вершину» и «из вершины в следующую».
    /// Вершины, у которых угол не менее <paramref name="minTurnRadians"/>, подавляются по немаксимумам в окне
    /// ±<paramref name="armLength"/>; точки с углом не менее <paramref name="reversalRadians"/> получают вид
    /// <see cref="KeyPointKind.Reversal"/>, остальные — <see cref="KeyPointKind.Corner"/>. Вершины ближе
    /// <paramref name="armLength"/> к концам ломаной не рассматриваются (поворот там не определён).
    /// </summary>
    /// <param name="points">Вершины ломаной.</param>
    /// <param name="armLength">Плечо измерения угла по длине дуги, &gt; 0 (порядка нескольких шагов ресэмплинга).</param>
    /// <param name="minTurnRadians">Минимальный угол поворота излома, (0, π].</param>
    /// <param name="reversalRadians">Угол, начиная с которого излом считается разворотом, ≥ <paramref name="minTurnRadians"/>.</param>
    /// <param name="destination">Список, в конец которого добавляются точки.</param>
    public static void FindCorners(
        ReadOnlySpan<Vec2> points,
        double armLength,
        double minTurnRadians,
        double reversalRadians,
        List<KeyPoint> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(armLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minTurnRadians);
        ArgumentOutOfRangeException.ThrowIfLessThan(reversalRadians, minTurnRadians);
        ArgumentNullException.ThrowIfNull(destination);
        int n = points.Length;
        if (n < 3)
        {
            return;
        }

        var cumulative = new double[n];
        PolylineMath.CumulativeLengths(points, cumulative);
        var turn = new double[n];
        for (int i = 0; i < n; i++)
        {
            double s = cumulative[i];
            if (s - armLength < 0.0 || s + armLength > cumulative[n - 1])
            {
                continue;
            }

            Vec2 before = PolylineMath.PointAtLength(points, cumulative, s - armLength);
            Vec2 after = PolylineMath.PointAtLength(points, cumulative, s + armLength);
            Vec2 d1 = points[i] - before;
            Vec2 d2 = after - points[i];
            if (d1.LengthSquared <= 0.0 || d2.LengthSquared <= 0.0)
            {
                continue;
            }

            turn[i] = Math.Abs(Math.Atan2(Vec2.Cross(d1, d2), Vec2.Dot(d1, d2)));
        }

        for (int i = 0; i < n; i++)
        {
            if (turn[i] < minTurnRadians)
            {
                continue;
            }

            // Локальный максимум в окне ±arm по длине дуги; при равенстве выигрывает меньший индекс.
            bool isPeak = true;
            for (int j = i - 1; j >= 0 && cumulative[i] - cumulative[j] <= armLength; j--)
            {
                if (turn[j] >= turn[i])
                {
                    isPeak = false;
                    break;
                }
            }

            for (int j = i + 1; isPeak && j < n && cumulative[j] - cumulative[i] <= armLength; j++)
            {
                if (turn[j] > turn[i])
                {
                    isPeak = false;
                }
            }

            if (isPeak)
            {
                KeyPointKind kind = turn[i] >= reversalRadians ? KeyPointKind.Reversal : KeyPointKind.Corner;
                destination.Add(new KeyPoint(i, points[i], kind, turn[i]));
            }
        }
    }
}
