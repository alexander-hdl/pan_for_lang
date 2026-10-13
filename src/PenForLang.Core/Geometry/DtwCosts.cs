namespace PenForLang.Core.Geometry;

/// <summary>Квадрат евклидова расстояния между точками двух ломаных: <c>|a[i] − b[j]|²</c>.</summary>
public readonly ref struct SquaredEuclideanCost : IDtwCost
{
    private readonly ReadOnlySpan<Vec2> _a;
    private readonly ReadOnlySpan<Vec2> _b;

    /// <summary>Создаёт стоимость над двумя последовательностями точек.</summary>
    /// <param name="a">Первая последовательность.</param>
    /// <param name="b">Вторая последовательность.</param>
    public SquaredEuclideanCost(ReadOnlySpan<Vec2> a, ReadOnlySpan<Vec2> b)
    {
        _a = a;
        _b = b;
    }

    /// <inheritdoc />
    public double Cost(int i, int j) => Vec2.DistanceSquared(_a[i], _b[j]);
}

/// <summary>Квадрат разности скаляров: <c>(a[i] − b[j])²</c>.</summary>
public readonly ref struct SquaredScalarCost : IDtwCost
{
    private readonly ReadOnlySpan<double> _a;
    private readonly ReadOnlySpan<double> _b;

    /// <summary>Создаёт стоимость над двумя скалярными рядами.</summary>
    /// <param name="a">Первый ряд.</param>
    /// <param name="b">Второй ряд.</param>
    public SquaredScalarCost(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        _a = a;
        _b = b;
    }

    /// <inheritdoc />
    public double Cost(int i, int j)
    {
        double d = _a[i] - _b[j];
        return d * d;
    }
}

/// <summary>Способ сравнения одного признака в <see cref="WeightedFeatureCost"/>.</summary>
public enum FeatureKind : byte
{
    /// <summary>Квадрат разности: <c>d²</c>.</summary>
    Squared = 0,

    /// <summary>Модуль разности: <c>|d|</c>.</summary>
    Absolute = 1,

    /// <summary>
    /// Угловой признак в радианах: <c>1 − cos d</c> (не зависит от обёртки угла на 2π). Вызывает <see cref="Math.Cos(double)"/>
    /// в каждой клетке — удобно для разовых задач; в горячем пути используйте <see cref="DirectionCost"/> или
    /// <see cref="PositionDirectionCost"/> с заранее посчитанными (cosθ, sinθ) (<see cref="DirectionFeatures"/>).
    /// </summary>
    Angular = 2,
}

/// <summary>
/// Многопризнаковая стоимость: <c>Σₖ wₖ · fₖ(a[i][k] − b[j][k])</c>, где вид <c>fₖ</c> задаёт <see cref="FeatureKind"/>.
/// Признаки хранятся построчно (<c>row-major</c>): элемент <c>i</c> занимает <c>a[i·dimension … i·dimension + dimension − 1]</c>.
/// Типичный набор оценщика: позиция (x, y) как <see cref="FeatureKind.Squared"/> и направление θ как <see cref="FeatureKind.Angular"/>.
/// </summary>
public readonly ref struct WeightedFeatureCost : IDtwCost
{
    private readonly ReadOnlySpan<double> _a;
    private readonly ReadOnlySpan<double> _b;
    private readonly ReadOnlySpan<double> _weights;
    private readonly ReadOnlySpan<FeatureKind> _kinds;
    private readonly int _dimension;

    /// <summary>Создаёт стоимость.</summary>
    /// <param name="a">Признаки первой последовательности, <c>n · dimension</c> чисел.</param>
    /// <param name="b">Признаки второй последовательности, <c>m · dimension</c> чисел.</param>
    /// <param name="dimension">Число признаков на элемент, ≥ 1.</param>
    /// <param name="weights">Веса признаков, <c>dimension</c> неотрицательных чисел.</param>
    /// <param name="kinds">Вид сравнения каждого признака; пустой — все <see cref="FeatureKind.Squared"/>.</param>
    public WeightedFeatureCost(
        ReadOnlySpan<double> a,
        ReadOnlySpan<double> b,
        int dimension,
        ReadOnlySpan<double> weights,
        ReadOnlySpan<FeatureKind> kinds = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dimension, 1);
        if (weights.Length != dimension)
        {
            throw new ArgumentException("Weights length must equal dimension.", nameof(weights));
        }

        if (!kinds.IsEmpty && kinds.Length != dimension)
        {
            throw new ArgumentException("Kinds length must equal dimension (or be empty).", nameof(kinds));
        }

        if ((a.Length % dimension) != 0 || (b.Length % dimension) != 0)
        {
            throw new ArgumentException("Feature arrays must be a multiple of dimension.");
        }

        _a = a;
        _b = b;
        _dimension = dimension;
        _weights = weights;
        _kinds = kinds;
    }

    /// <inheritdoc />
    public double Cost(int i, int j)
    {
        int ia = i * _dimension;
        int ib = j * _dimension;
        double sum = 0.0;
        for (int k = 0; k < _dimension; k++)
        {
            double d = _a[ia + k] - _b[ib + k];
            FeatureKind kind = _kinds.IsEmpty ? FeatureKind.Squared : _kinds[k];
            double f = kind switch
            {
                FeatureKind.Absolute => Math.Abs(d),
                FeatureKind.Angular => 1.0 - Math.Cos(d),
                _ => d * d,
            };
            sum += _weights[k] * f;
        }

        return sum;
    }
}

/// <summary>Стоимость, заданная делегатом; удобна для разовых проверок и стоимостей с замыканием (меньше всего оптимизируется).</summary>
public readonly struct DelegateCost : IDtwCost
{
    private readonly Func<int, int, double> _cost;

    /// <summary>Создаёт стоимость из делегата.</summary>
    /// <param name="cost">Функция <c>(i, j) → стоимость</c>.</param>
    public DelegateCost(Func<int, int, double> cost)
    {
        ArgumentNullException.ThrowIfNull(cost);
        _cost = cost;
    }

    /// <inheritdoc />
    public double Cost(int i, int j) => _cost(i, j);
}

/// <summary>Обёртка, обращающая вторую последовательность: <c>Cost(i, j) = inner.Cost(i, m − 1 − j)</c>.</summary>
/// <typeparam name="TCost">Тип исходной стоимости.</typeparam>
public readonly ref struct ReversedReferenceCost<TCost> : IDtwCost
    where TCost : IDtwCost, allows ref struct
{
    private readonly TCost _inner;
    private readonly int _last;

    /// <summary>Создаёт обёртку.</summary>
    /// <param name="inner">Исходная стоимость.</param>
    /// <param name="referenceLength">Длина второй последовательности <c>m</c>.</param>
    public ReversedReferenceCost(in TCost inner, int referenceLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(referenceLength, 1);
        _inner = inner;
        _last = referenceLength - 1;
    }

    /// <inheritdoc />
    public double Cost(int i, int j) => _inner.Cost(i, _last - j);
}

/// <summary>
/// Ограничение полосой по x: пары с <c>|xa[i] − xb[j]| &gt; band</c> запрещены (стоимость +∞) либо, если задан конечный штраф,
/// к их стоимости прибавляется <c>outOfBandPenalty</c> («мягкая» полоса).
/// </summary>
/// <typeparam name="TCost">Тип исходной стоимости.</typeparam>
public readonly ref struct XBandedCost<TCost> : IDtwCost
    where TCost : IDtwCost, allows ref struct
{
    private readonly TCost _inner;
    private readonly ReadOnlySpan<double> _xa;
    private readonly ReadOnlySpan<double> _xb;
    private readonly double _band;
    private readonly double _penalty;

    /// <summary>Создаёт обёртку.</summary>
    /// <param name="inner">Исходная стоимость.</param>
    /// <param name="xa">Координаты x первой последовательности.</param>
    /// <param name="xb">Координаты x второй последовательности.</param>
    /// <param name="band">Полуширина полосы, ≥ 0.</param>
    /// <param name="outOfBandPenalty">Штраф вне полосы, ≥ 0; +∞ (по умолчанию) — пары вне полосы запрещены.</param>
    public XBandedCost(
        in TCost inner,
        ReadOnlySpan<double> xa,
        ReadOnlySpan<double> xb,
        double band,
        double outOfBandPenalty = double.PositiveInfinity)
    {
        _inner = inner;
        _xa = xa;
        _xb = xb;
        _band = band;
        _penalty = outOfBandPenalty;
    }

    /// <inheritdoc />
    public double Cost(int i, int j) =>
        Math.Abs(_xa[i] - _xb[j]) <= _band ? _inner.Cost(i, j) : _inner.Cost(i, j) + _penalty;
}

/// <summary>Транспонирование стоимости: <c>Cost(i, j) = inner.Cost(j, i)</c> (обмен ролями последовательностей).</summary>
/// <typeparam name="TCost">Тип исходной стоимости.</typeparam>
public readonly ref struct TransposedCost<TCost> : IDtwCost
    where TCost : IDtwCost, allows ref struct
{
    private readonly TCost _inner;

    /// <summary>Создаёт обёртку.</summary>
    /// <param name="inner">Исходная стоимость.</param>
    public TransposedCost(in TCost inner)
    {
        _inner = inner;
    }

    /// <inheritdoc />
    public double Cost(int i, int j) => _inner.Cost(j, i);
}

/// <summary>
/// Угловая стоимость по заранее посчитанным единичным векторам направления <c>(cos θ, sin θ)</c>:
/// <c>1 − (cosθa·cosθb + sinθa·sinθb) = 1 − cos(θa − θb)</c> без вызова <see cref="Math.Cos(double)"/> в клетке.
/// Значения лежат в [0, 2].
/// </summary>
public readonly ref struct DirectionCost : IDtwCost
{
    private readonly ReadOnlySpan<Vec2> _a;
    private readonly ReadOnlySpan<Vec2> _b;

    /// <summary>Создаёт стоимость над двумя рядами единичных направлений.</summary>
    /// <param name="a">Направления первой последовательности.</param>
    /// <param name="b">Направления второй последовательности.</param>
    public DirectionCost(ReadOnlySpan<Vec2> a, ReadOnlySpan<Vec2> b)
    {
        _a = a;
        _b = b;
    }

    /// <inheritdoc />
    public double Cost(int i, int j) => Math.Max(0.0, 1.0 - Vec2.Dot(_a[i], _b[j]));
}

/// <summary>
/// Локальная стоимость выравнивания оценщика (ARCHITECTURE.md §6.4.4) без бонуса якорей:
/// <c>wPos·‖pᵢ − qⱼ‖² + wTheta·(1 − cos(θᵢ − θⱼ))</c>, направления — заранее посчитанные единичные векторы.
/// Бонус за совпадение ключевых точек в оценщике нужно выражать штрафом за несовпадение (стоимость ≥ 0).
/// </summary>
public readonly ref struct PositionDirectionCost : IDtwCost
{
    private readonly ReadOnlySpan<Vec2> _pa;
    private readonly ReadOnlySpan<Vec2> _pb;
    private readonly ReadOnlySpan<Vec2> _da;
    private readonly ReadOnlySpan<Vec2> _db;
    private readonly double _wPos;
    private readonly double _wTheta;

    /// <summary>Создаёт стоимость.</summary>
    /// <param name="positionsA">Точки первой последовательности.</param>
    /// <param name="positionsB">Точки второй последовательности.</param>
    /// <param name="directionsA">Единичные направления первой последовательности (параллельны точкам).</param>
    /// <param name="directionsB">Единичные направления второй последовательности.</param>
    /// <param name="positionWeight">Вес позиции, ≥ 0.</param>
    /// <param name="directionWeight">Вес направления, ≥ 0.</param>
    public PositionDirectionCost(
        ReadOnlySpan<Vec2> positionsA,
        ReadOnlySpan<Vec2> positionsB,
        ReadOnlySpan<Vec2> directionsA,
        ReadOnlySpan<Vec2> directionsB,
        double positionWeight,
        double directionWeight)
    {
        if (positionsA.Length != directionsA.Length || positionsB.Length != directionsB.Length)
        {
            throw new ArgumentException("Positions and directions must have the same length.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(positionWeight);
        ArgumentOutOfRangeException.ThrowIfNegative(directionWeight);
        _pa = positionsA;
        _pb = positionsB;
        _da = directionsA;
        _db = directionsB;
        _wPos = positionWeight;
        _wTheta = directionWeight;
    }

    /// <inheritdoc />
    public double Cost(int i, int j) =>
        (_wPos * Vec2.DistanceSquared(_pa[i], _pb[j])) + (_wTheta * Math.Max(0.0, 1.0 - Vec2.Dot(_da[i], _db[j])));
}
