namespace PenForLang.Core.Geometry;

/// <summary>
/// Фильтр Савицкого–Голея: сглаживание и производные равномерно дискретизированного сигнала локальной
/// аппроксимацией многочленом по методу наименьших квадратов.
/// Ядро считается один раз в конструкторе (решение нормальных уравнений методом Гаусса в <c>double</c>),
/// применение не выделяет память. Краевые точки (первые и последние <c>h = (window−1)/2</c>) вычисляются по многочлену,
/// подогнанному к первому или последнему окну (аналог режима <c>interp</c> в SciPy), поэтому многочлен степени
/// ≤ <c>order</c> воспроизводится точно на всём сигнале, включая края. Если сигнал короче окна, окно и порядок
/// уменьшаются автоматически; сокращённые фильтры создаются один раз и кешируются (память выделяется только при первом
/// обращении к данной длине). Для двух точек производная первого порядка — конечная разность. Экземпляр можно
/// использовать из нескольких потоков (кеш заполняется атомарно, одинаковыми объектами).
/// </summary>
public sealed class SavitzkyGolayFilter
{
    private readonly double[] _kernel; // window x window, строка r — веса для позиции r внутри окна
    private readonly SavitzkyGolayFilter?[] _reduced; // кеш фильтров для сигналов короче окна, индекс = длина сигнала

    /// <summary>Создаёт фильтр.</summary>
    /// <param name="window">Размер окна: нечётное число ≥ 1.</param>
    /// <param name="order">Степень многочлена, от 0 до <c>window − 1</c>.</param>
    /// <param name="derivative">Порядок производной (0 — сглаживание), ≥ 0.</param>
    /// <param name="spacing">Шаг дискретизации по аргументу (производная делится на <c>spacing^derivative</c>), &gt; 0.</param>
    public SavitzkyGolayFilter(int window, int order, int derivative = 0, double spacing = 1.0)
    {
        if (window < 1 || (window % 2) == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(window), "Window must be an odd number >= 1.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(order);
        if (order > window - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(order), "Order must not exceed window - 1.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(derivative);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(spacing);

        Window = window;
        Order = order;
        Derivative = derivative;
        Spacing = spacing;
        _kernel = BuildKernel(window, order, derivative, spacing);
        _reduced = new SavitzkyGolayFilter?[window];
    }

    /// <summary>Размер окна.</summary>
    public int Window { get; }

    /// <summary>Степень локального многочлена.</summary>
    public int Order { get; }

    /// <summary>Порядок производной.</summary>
    public int Derivative { get; }

    /// <summary>Шаг дискретизации по аргументу.</summary>
    public double Spacing { get; }

    /// <summary>Веса центральной точки (длина <see cref="Window"/>), для проверок и внешнего использования.</summary>
    public ReadOnlySpan<double> CenterWeights
    {
        get
        {
            int h = (Window - 1) / 2;
            return new ReadOnlySpan<double>(_kernel, h * Window, Window);
        }
    }

    /// <summary>Применяет фильтр к скалярному сигналу. Буферы не должны пересекаться.</summary>
    /// <param name="source">Сигнал.</param>
    /// <param name="destination">Результат, длиной не меньше сигнала.</param>
    public void Apply(ReadOnlySpan<double> source, Span<double> destination)
    {
        CheckBuffers(source.Length, destination.Length, source.Overlaps(destination));
        int n = source.Length;
        if (n == 0)
        {
            return;
        }

        if (n < Window)
        {
            if (n == 2 && Derivative == 1)
            {
                double slope = (source[1] - source[0]) / Spacing;
                destination[0] = slope;
                destination[1] = slope;
                return;
            }

            Reduced(n).Apply(source, destination);
            return;
        }

        int h = (Window - 1) / 2;
        for (int p = 0; p < n; p++)
        {
            int start = Math.Clamp(p - h, 0, n - Window);
            ReadOnlySpan<double> row = new(_kernel, (p - start) * Window, Window);
            double acc = 0.0;
            for (int k = 0; k < Window; k++)
            {
                acc += row[k] * source[start + k];
            }

            destination[p] = acc;
        }
    }

    /// <summary>Применяет фильтр к каждой координате ломаной. Буферы не должны пересекаться.</summary>
    /// <param name="source">Точки.</param>
    /// <param name="destination">Результат, длиной не меньше числа точек. Для <c>Derivative &gt; 0</c> — вектор производной.</param>
    public void Apply(ReadOnlySpan<Vec2> source, Span<Vec2> destination)
    {
        CheckBuffers(source.Length, destination.Length, source.Overlaps(destination));
        int n = source.Length;
        if (n == 0)
        {
            return;
        }

        if (n < Window)
        {
            if (n == 2 && Derivative == 1)
            {
                Vec2 slope = (source[1] - source[0]) / Spacing;
                destination[0] = slope;
                destination[1] = slope;
                return;
            }

            Reduced(n).Apply(source, destination);
            return;
        }

        int h = (Window - 1) / 2;
        for (int p = 0; p < n; p++)
        {
            int start = Math.Clamp(p - h, 0, n - Window);
            ReadOnlySpan<double> row = new(_kernel, (p - start) * Window, Window);
            double ax = 0.0;
            double ay = 0.0;
            for (int k = 0; k < Window; k++)
            {
                Vec2 v = source[start + k];
                ax += row[k] * v.X;
                ay += row[k] * v.Y;
            }

            destination[p] = new Vec2(ax, ay);
        }
    }

    private static void CheckBuffers(int sourceLength, int destinationLength, bool overlaps)
    {
        if (destinationLength < sourceLength)
        {
            throw new ArgumentException("Destination is shorter than the source.");
        }

        if (overlaps)
        {
            throw new ArgumentException("Source and destination must not overlap.");
        }
    }

    private SavitzkyGolayFilter Reduced(int n)
    {
        SavitzkyGolayFilter? cached = Volatile.Read(ref _reduced[n]);
        if (cached is not null)
        {
            return cached;
        }

        int window = (n % 2) == 0 ? n - 1 : n;
        var created = new SavitzkyGolayFilter(window, Math.Min(Order, window - 1), Derivative, Spacing);
        return Interlocked.CompareExchange(ref _reduced[n], created, null) ?? created;
    }

    private static double[] BuildKernel(int window, int order, int derivative, double spacing)
    {
        int h = (window - 1) / 2;
        int dim = order + 1;
        double scale = Math.Max(h, 1);

        // Z = (AᵀA)⁻¹ Aᵀ, A[k][j] = xs_k^j, xs_k = (k − h) / scale — масштаб ради обусловленности.
        var a = new double[window, dim];
        for (int k = 0; k < window; k++)
        {
            double x = (k - h) / scale;
            double pow = 1.0;
            for (int j = 0; j < dim; j++)
            {
                a[k, j] = pow;
                pow *= x;
            }
        }

        var gram = new double[dim, dim + window];
        for (int r = 0; r < dim; r++)
        {
            for (int c = 0; c < dim; c++)
            {
                double s = 0.0;
                for (int k = 0; k < window; k++)
                {
                    s += a[k, r] * a[k, c];
                }

                gram[r, c] = s;
            }

            for (int k = 0; k < window; k++)
            {
                gram[r, dim + k] = a[k, r];
            }
        }

        SolveInPlace(gram, dim, window);

        var kernel = new double[window * window];
        if (derivative > order)
        {
            return kernel; // производная многочлена степени order порядка > order тождественно равна 0
        }

        double outScale = Math.Pow(scale * spacing, derivative);
        for (int row = 0; row < window; row++)
        {
            double xt = (row - h) / scale;
            for (int k = 0; k < window; k++)
            {
                double w = 0.0;
                for (int j = derivative; j <= order; j++)
                {
                    // d^derivative/dx^derivative x^j = j!/(j−derivative)! x^(j−derivative)
                    double coef = 1.0;
                    for (int q = 0; q < derivative; q++)
                    {
                        coef *= j - q;
                    }

                    w += gram[j, dim + k] * coef * Math.Pow(xt, j - derivative);
                }

                kernel[(row * window) + k] = w / outScale;
            }
        }

        return kernel;
    }

    // Метод Гаусса–Жордана с выбором главного элемента: приводит левый блок dim×dim к единичной матрице,
    // правый блок dim×rhs превращается в решение.
    private static void SolveInPlace(double[,] m, int dim, int rhs)
    {
        int cols = dim + rhs;
        for (int col = 0; col < dim; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < dim; r++)
            {
                if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col]))
                {
                    pivot = r;
                }
            }

            if (pivot != col)
            {
                for (int c = 0; c < cols; c++)
                {
                    (m[col, c], m[pivot, c]) = (m[pivot, c], m[col, c]);
                }
            }

            double diag = m[col, col];
            for (int c = 0; c < cols; c++)
            {
                m[col, c] /= diag;
            }

            for (int r = 0; r < dim; r++)
            {
                if (r == col)
                {
                    continue;
                }

                double f = m[r, col];
                if (f == 0.0)
                {
                    continue;
                }

                for (int c = 0; c < cols; c++)
                {
                    m[r, c] -= f * m[col, c];
                }
            }
        }
    }
}
