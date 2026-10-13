namespace PenForLang.Core.Geometry;

/// <summary>
/// Кубическая кривая Безье с контрольными точками <see cref="P0"/>…<see cref="P3"/>, параметр <c>t ∈ [0, 1]</c>.
/// Даёт точку, производные, кривизну, длину дуги, обратную задачу «параметр по длине дуги», разбиение и
/// разбиение на полилинию с гарантированной точностью.
/// </summary>
/// <param name="P0">Начальная точка.</param>
/// <param name="P1">Первая контрольная точка.</param>
/// <param name="P2">Вторая контрольная точка.</param>
/// <param name="P3">Конечная точка.</param>
public readonly record struct CubicBezier(Vec2 P0, Vec2 P1, Vec2 P2, Vec2 P3)
{
    private const int MaxLengthDepth = 14;
    private const int MaxFlattenSegments = 1 << 20;

    // Узлы и веса квадратуры Гаусса–Лежандра на [-1, 1], 5 точек (точна для многочленов степени ≤ 9).
    private const double GlNode1 = 0.5384693101056831;
    private const double GlNode2 = 0.9061798459386640;
    private const double GlWeight0 = 0.5688888888888889;
    private const double GlWeight1 = 0.4786286704993665;
    private const double GlWeight2 = 0.2369268850561891;

    /// <summary>Точка кривой при параметре <paramref name="t"/>.</summary>
    public Vec2 PointAt(double t)
    {
        double s = 1.0 - t;
        double b0 = s * s * s;
        double b1 = 3.0 * s * s * t;
        double b2 = 3.0 * s * t * t;
        double b3 = t * t * t;
        return new Vec2(
            (b0 * P0.X) + (b1 * P1.X) + (b2 * P2.X) + (b3 * P3.X),
            (b0 * P0.Y) + (b1 * P1.Y) + (b2 * P2.Y) + (b3 * P3.Y));
    }

    /// <summary>Первая производная <c>B'(t)</c> (вектор скорости по параметру).</summary>
    public Vec2 DerivativeAt(double t)
    {
        double s = 1.0 - t;
        Vec2 d0 = P1 - P0;
        Vec2 d1 = P2 - P1;
        Vec2 d2 = P3 - P2;
        return (3.0 * s * s * d0) + (6.0 * s * t * d1) + (3.0 * t * t * d2);
    }

    /// <summary>Вторая производная <c>B''(t)</c>.</summary>
    public Vec2 SecondDerivativeAt(double t)
    {
        Vec2 a = P2 - (2.0 * P1) + P0;
        Vec2 b = P3 - (2.0 * P2) + P1;
        return 6.0 * (((1.0 - t) * a) + (t * b));
    }

    /// <summary>Скорость по параметру <c>|B'(t)|</c> (подынтегральная функция длины дуги).</summary>
    public double SpeedAt(double t) => DerivativeAt(t).Length;

    /// <summary>
    /// Знаковая кривизна <c>(x'y'' − y'x'') / |B'|³</c> (положительна при повороте против часовой стрелки при оси y вверх).
    /// В точках, где скорость равна нулю, возвращает 0.
    /// </summary>
    public double CurvatureAt(double t)
    {
        Vec2 d1 = DerivativeAt(t);
        Vec2 d2 = SecondDerivativeAt(t);
        double speed2 = d1.LengthSquared;
        if (speed2 <= 0.0)
        {
            return 0.0;
        }

        return Vec2.Cross(d1, d2) / (speed2 * Math.Sqrt(speed2));
    }

    /// <summary>Делит кривую в точке <paramref name="t"/> (алгоритм де Кастельжо) на две кривые Безье.</summary>
    /// <param name="t">Параметр деления.</param>
    /// <param name="left">Часть кривой на <c>[0, t]</c>.</param>
    /// <param name="right">Часть кривой на <c>[t, 1]</c>.</param>
    public void Split(double t, out CubicBezier left, out CubicBezier right)
    {
        Vec2 p01 = Vec2.Lerp(P0, P1, t);
        Vec2 p12 = Vec2.Lerp(P1, P2, t);
        Vec2 p23 = Vec2.Lerp(P2, P3, t);
        Vec2 p012 = Vec2.Lerp(p01, p12, t);
        Vec2 p123 = Vec2.Lerp(p12, p23, t);
        Vec2 mid = Vec2.Lerp(p012, p123, t);
        left = new CubicBezier(P0, p01, p012, mid);
        right = new CubicBezier(mid, p123, p23, P3);
    }

    /// <summary>Длина контрольной ломаной: верхняя оценка длины дуги.</summary>
    public double ControlPolygonLength() => Vec2.Distance(P0, P1) + Vec2.Distance(P1, P2) + Vec2.Distance(P2, P3);

    /// <summary>Длина всей дуги <c>[0, 1]</c>.</summary>
    /// <param name="relativeTolerance">Допустимая погрешность относительно длины контрольной ломаной.</param>
    public double Length(double relativeTolerance = 1e-10) => LengthBetween(0.0, 1.0, relativeTolerance);

    /// <summary>Длина дуги от параметра 0 до <paramref name="t"/>.</summary>
    /// <param name="t">Параметр, значения вне [0, 1] обрезаются.</param>
    /// <param name="relativeTolerance">Допустимая погрешность относительно длины контрольной ломаной.</param>
    public double LengthTo(double t, double relativeTolerance = 1e-10) => LengthBetween(0.0, t, relativeTolerance);

    /// <summary>Длина дуги между параметрами <paramref name="t0"/> и <paramref name="t1"/> (адаптивная квадратура Гаусса–Лежандра).</summary>
    /// <param name="t0">Начало отрезка параметра.</param>
    /// <param name="t1">Конец отрезка параметра.</param>
    /// <param name="relativeTolerance">Допустимая погрешность относительно длины контрольной ломаной.</param>
    public double LengthBetween(double t0, double t1, double relativeTolerance = 1e-10)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(relativeTolerance);
        t0 = Math.Clamp(t0, 0.0, 1.0);
        t1 = Math.Clamp(t1, 0.0, 1.0);
        if (t1 <= t0)
        {
            return 0.0;
        }

        double scale = ControlPolygonLength();
        if (scale <= 0.0)
        {
            return 0.0;
        }

        return AdaptiveLength(t0, t1, relativeTolerance * scale, 0);
    }

    /// <summary>
    /// Параметр <c>t</c>, при котором длина дуги от начала равна <paramref name="arcLength"/>
    /// (метод Ньютона с защитой бисекцией). Значения вне [0, длина] обрезаются до 0 и 1.
    /// </summary>
    /// <param name="arcLength">Длина дуги от начала кривой.</param>
    /// <param name="relativeTolerance">Допустимая погрешность относительно длины контрольной ломаной.</param>
    public double ParameterAtLength(double arcLength, double relativeTolerance = 1e-10)
    {
        double total = Length(relativeTolerance);
        if (arcLength <= 0.0 || total <= 0.0)
        {
            return 0.0;
        }

        if (arcLength >= total)
        {
            return 1.0;
        }

        double tol = relativeTolerance * ControlPolygonLength();
        double lo = 0.0;
        double hi = 1.0;
        double t = arcLength / total;
        for (int iter = 0; iter < 64; iter++)
        {
            double f = LengthBetween(0.0, t, relativeTolerance) - arcLength;
            if (Math.Abs(f) <= tol)
            {
                return t;
            }

            if (f > 0.0)
            {
                hi = t;
            }
            else
            {
                lo = t;
            }

            double speed = SpeedAt(t);
            double next = speed > 1e-12 ? t - (f / speed) : double.NaN;
            t = next > lo && next < hi ? next : (lo + hi) * 0.5;
        }

        return t;
    }

    /// <summary>
    /// Число равных по параметру отрезков, достаточное, чтобы полилиния отклонялась от кривой не более чем на
    /// <paramref name="tolerance"/>. Оценка: <c>err ≤ max|B''| / (8 n²)</c>, <c>max|B''| ≤ 6·max(|P0−2P1+P2|, |P1−2P2+P3|)</c>.
    /// Результат ограничен сверху значением 2²⁰.
    /// </summary>
    /// <param name="tolerance">Допустимое максимальное отклонение полилинии от кривой, &gt; 0.</param>
    public int FlattenSegmentCount(double tolerance)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tolerance);
        double m = Math.Max((P0 - (2.0 * P1) + P2).Length, (P1 - (2.0 * P2) + P3).Length);
        double n = Math.Ceiling(Math.Sqrt(0.75 * m / tolerance));
        if (!(n >= 1.0))
        {
            return 1;
        }

        return n >= MaxFlattenSegments ? MaxFlattenSegments : (int)n;
    }

    /// <summary>
    /// Дописывает в <paramref name="destination"/> вершины полилинии, отклоняющейся от кривой не более чем на
    /// <paramref name="tolerance"/>; первая вершина — <see cref="P0"/>, последняя — <see cref="P3"/> (точно).
    /// </summary>
    /// <param name="tolerance">Допустимое максимальное отклонение, &gt; 0.</param>
    /// <param name="destination">Список, в конец которого добавляются вершины.</param>
    /// <param name="includeStart">Если <c>false</c>, начальная точка не добавляется (удобно при сшивании кривых пути).</param>
    public void Flatten(double tolerance, List<Vec2> destination, bool includeStart = true)
    {
        ArgumentNullException.ThrowIfNull(destination);
        int n = FlattenSegmentCount(tolerance);
        if (includeStart)
        {
            destination.Add(P0);
        }

        for (int k = 1; k < n; k++)
        {
            destination.Add(PointAt((double)k / n));
        }

        destination.Add(P3);
    }

    private double GaussLegendre(double a, double b)
    {
        double half = (b - a) * 0.5;
        double mid = (a + b) * 0.5;
        double sum = GlWeight0 * SpeedAt(mid)
            + (GlWeight1 * (SpeedAt(mid - (half * GlNode1)) + SpeedAt(mid + (half * GlNode1))))
            + (GlWeight2 * (SpeedAt(mid - (half * GlNode2)) + SpeedAt(mid + (half * GlNode2))));
        return sum * half;
    }

    private double AdaptiveLength(double a, double b, double tol, int depth)
    {
        double mid = (a + b) * 0.5;
        double whole = GaussLegendre(a, b);
        double refined = GaussLegendre(a, mid) + GaussLegendre(mid, b);
        if (depth >= MaxLengthDepth || Math.Abs(refined - whole) <= tol)
        {
            return refined;
        }

        return AdaptiveLength(a, mid, tol * 0.5, depth + 1) + AdaptiveLength(mid, b, tol * 0.5, depth + 1);
    }
}
