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

    /// <summary>Угловой признак в радианах: <c>1 − cos d</c> (не зависит от обёртки угла на 2π).</summary>
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

/// <summary>Ограничение полосой по x: пары с <c>|xa[i] − xb[j]| &gt; band</c> запрещены (стоимость +∞).</summary>
/// <typeparam name="TCost">Тип исходной стоимости.</typeparam>
public readonly ref struct XBandedCost<TCost> : IDtwCost
    where TCost : IDtwCost, allows ref struct
{
    private readonly TCost _inner;
    private readonly ReadOnlySpan<double> _xa;
    private readonly ReadOnlySpan<double> _xb;
    private readonly double _band;

    /// <summary>Создаёт обёртку.</summary>
    /// <param name="inner">Исходная стоимость.</param>
    /// <param name="xa">Координаты x первой последовательности.</param>
    /// <param name="xb">Координаты x второй последовательности.</param>
    /// <param name="band">Полуширина полосы, ≥ 0.</param>
    public XBandedCost(in TCost inner, ReadOnlySpan<double> xa, ReadOnlySpan<double> xb, double band)
    {
        _inner = inner;
        _xa = xa;
        _xb = xb;
        _band = band;
    }

    /// <inheritdoc />
    public double Cost(int i, int j) =>
        Math.Abs(_xa[i] - _xb[j]) <= _band ? _inner.Cost(i, j) : double.PositiveInfinity;
}
