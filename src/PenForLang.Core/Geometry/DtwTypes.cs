namespace PenForLang.Core.Geometry;

/// <summary>
/// Локальная стоимость соответствия элемента <c>i</c> первой последовательности элементу <c>j</c> второй.
/// Значение — неотрицательное конечное число или <see cref="double.PositiveInfinity"/> (пара запрещена).
/// Отрицательная стоимость и NaN недопустимы: DTW бросает <see cref="InvalidOperationException"/>. Это условие корректности
/// открытого начала и жадного выбора конца; поэтому якоря и бонусы оценщика (<c>anchorBonus</c>) учитываются
/// <b>штрафами</b> за их отсутствие (стоимость остаётся ≥ 0), а не отрицательными бонусами.
/// Реализации — структуры (в том числе <c>ref struct</c> над <see cref="ReadOnlySpan{T}"/>): общие методы DTW
/// специализируются под тип, вызов стоимости встраивается и не выделяет память.
/// </summary>
public interface IDtwCost
{
    /// <summary>Стоимость пары <paramref name="i"/> (первая последовательность) и <paramref name="j"/> (вторая).</summary>
    /// <param name="i">Индекс в первой последовательности.</param>
    /// <param name="j">Индекс во второй последовательности.</param>
    double Cost(int i, int j);
}

/// <summary>Клетка пути выравнивания: элемент <see cref="I"/> первой последовательности соответствует <see cref="J"/>-му второй.</summary>
/// <param name="I">Индекс в первой последовательности.</param>
/// <param name="J">Индекс во второй последовательности.</param>
public readonly record struct DtwCell(int I, int J);

/// <summary>
/// Параметры DTW. Вторая последовательность — «опорная» (эталон): открытые концы относятся к ней.
/// Чтобы открыть концы первой, поменяйте последовательности местами.
/// </summary>
public readonly record struct DtwOptions
{
    /// <summary>
    /// Открытое начало: путь может начинаться в любой клетке первой строки <c>(0, j)</c>, а не только в <c>(0, 0)</c>
    /// (первая последовательность выравнивается с подпоследовательностью второй, начало которой свободно).
    /// Корректность обеспечивает требование неотрицательных стоимостей (оно проверяется): продолжение первой строки
    /// никогда не выгоднее свежего старта.
    /// </summary>
    public bool OpenBegin { get; init; }

    /// <summary>
    /// Открытый конец: путь может заканчиваться в любой клетке последней строки <c>(n−1, j)</c>; выбирается минимум
    /// накопленной стоимости (при равенстве — наименьший <c>j</c>). Первая последовательность (рукопись) поглощается
    /// целиком при любом конце, поэтому сырой минимум — это минимум стоимости на элемент первой последовательности
    /// (<see cref="DtwResult.CostPerFirstElement"/> отличается лишь постоянным делителем). Делить на длину пути
    /// (<see cref="DtwResult.CostPerPathCell"/>) для выбора конца не следует: длинный путь с большим warping выглядел бы лучше.
    /// </summary>
    public bool OpenEnd { get; init; }

    /// <summary>
    /// Радиус индексной полосы Сакое–Тибы (<c>null</c> — полосы нет): допустимы клетки <c>|i − j| ≤ max(radius, |n − m|)</c>.
    /// Расширение до <c>|n − m|</c> гарантирует связность полосы и достижимость угла <c>(n−1, m−1)</c>.
    /// </summary>
    public int? IndexBandRadius { get; init; }

    /// <summary>
    /// Только для <see cref="Dtw.AlignXBand{TCost}"/>: конечный штраф (≥ 0), прибавляемый к стоимости пар вне полосы по x
    /// («мягкая» полоса). <c>null</c> или +∞ — жёсткая полоса: такие пары запрещены, и при разрыве по x результат может
    /// оказаться <see cref="DtwResult.Infeasible"/>.
    /// </summary>
    public double? OutOfBandCost { get; init; }
}

/// <summary>Итог выравнивания DTW. Для недостижимого конца (<see cref="IsFeasible"/> = <c>false</c>) стоимость бесконечна.</summary>
/// <param name="IsFeasible">Существует ли допустимый путь.</param>
/// <param name="Cost">Накопленная стоимость пути (сумма локальных стоимостей клеток) или +∞.</param>
/// <param name="PathLength">Число клеток пути (0 при недостижимости).</param>
/// <param name="Start">Первая клетка пути (или <c>(−1, −1)</c>).</param>
/// <param name="End">Последняя клетка пути (или <c>(−1, −1)</c>).</param>
/// <param name="FirstLength">Длина первой последовательности <c>n</c> (0 при недостижимости).</param>
public readonly record struct DtwResult(bool IsFeasible, double Cost, int PathLength, DtwCell Start, DtwCell End, int FirstLength)
{
    /// <summary>Результат «пути нет».</summary>
    public static DtwResult Infeasible { get; } =
        new(false, double.PositiveInfinity, 0, new DtwCell(-1, -1), new DtwCell(-1, -1), 0);

    /// <summary>
    /// Накопленная стоимость на элемент <b>первой</b> последовательности (рукописи): <c>Cost / n</c>. Это нормировка
    /// оценщика (ARCHITECTURE.md §6.4.6, A04): знаменатель не зависит от пути и длины эталона (+∞ при недостижимости).
    /// </summary>
    public double CostPerFirstElement => IsFeasible ? Cost / FirstLength : double.PositiveInfinity;

    /// <summary>
    /// Накопленная стоимость, делённая на число клеток пути. <b>Не используйте для сравнения гипотез:</b> длина пути растёт
    /// с warping и поощряет искажённые выравнивания (противоречит §6.4.6). Оставлена для диагностики.
    /// </summary>
    public double CostPerPathCell => IsFeasible ? Cost / PathLength : double.PositiveInfinity;
}

/// <summary>
/// Результат сравнения выравнивания с эталоном в прямом и обращённом направлении (детекция обратного направления штриха).
/// Путь обращённого выравнивания и индексы <see cref="DtwResult.Start"/>/<see cref="DtwResult.End"/> приведены к
/// индексам исходного (не обращённого) эталона: <c>J</c> убывает вдоль пути.
/// </summary>
/// <param name="Forward">Выравнивание с эталоном как есть.</param>
/// <param name="Reversed">Выравнивание с эталоном, пройденным в обратном порядке.</param>
public readonly record struct DtwDirectionComparison(DtwResult Forward, DtwResult Reversed)
{
    /// <summary>Обращённый эталон строго лучше (по накопленной стоимости), чем прямой.</summary>
    public bool ReversedIsBetter => Reversed.IsFeasible && (!Forward.IsFeasible || Reversed.Cost < Forward.Cost);

    /// <summary>Порог, ниже которого обе стоимости считаются нулевыми (идеальное совпадение в обоих направлениях).</summary>
    public const double ZeroCostEpsilon = 1e-12;

    /// <summary>
    /// Отношение <c>Reversed.Cost / Forward.Cost</c>. Если обе стоимости ≤ <see cref="ZeroCostEpsilon"/> (например, симметричная
    /// кривая), равно 1; +∞, если прямого пути нет, обращённого нет либо прямая стоимость нулевая при ненулевой обращённой.
    /// </summary>
    public double ReversedToForwardRatio
    {
        get
        {
            if (!Forward.IsFeasible || !Reversed.IsFeasible)
            {
                return double.PositiveInfinity;
            }

            if (Forward.Cost <= ZeroCostEpsilon && Reversed.Cost <= ZeroCostEpsilon)
            {
                return 1.0;
            }

            return Forward.Cost > ZeroCostEpsilon ? Reversed.Cost / Forward.Cost : double.PositiveInfinity;
        }
    }
}

/// <summary>
/// Переиспользуемые буферы для <see cref="Dtw"/>: повторные вызовы с тем же рабочим набором не выделяют память,
/// если размеры не растут (буферы растут геометрически, ×1,5). Не потокобезопасен: заводите <b>один экземпляр на экземпляр
/// оценщика и поток</b>. После вызова <see cref="Dtw"/> содержит накопленную матрицу последнего выравнивания; следующий вызов
/// (в том числе вторая половина <see cref="Dtw.CompareDirections{TForward, TReversed}"/>) её перезаписывает.
/// </summary>
public sealed class DtwWorkspace
{
    private const double GrowthFactor = 1.5;

    internal double[] Accumulated = [];
    internal byte[] Directions = [];
    internal int[] RowStart = [];
    internal int[] RowLow = [];
    internal int[] RowHigh = [];

    /// <summary>Число клеток, которое вмещают текущие буферы (растёт геометрически).</summary>
    public int CellCapacity => Accumulated.Length;

    /// <summary>Число строк последнего выравнивания (0 до первого вызова).</summary>
    public int Rows { get; internal set; }

    /// <summary>
    /// Последняя строка накопленной матрицы <c>D(n−1, ·)</c> — стоимость выравнивания всей первой последовательности с
    /// префиксами эталона (основа инкрементального монитора с открытым концом). Индекс 0 соответствует столбцу
    /// <see cref="LastRowFirstColumn"/>; недостижимые клетки равны +∞.
    /// </summary>
    public ReadOnlySpan<double> LastRow
    {
        get
        {
            if (Rows == 0)
            {
                return default;
            }

            int width = RowHigh[Rows - 1] >= RowLow[Rows - 1] ? RowHigh[Rows - 1] - RowLow[Rows - 1] + 1 : 0;
            return new ReadOnlySpan<double>(Accumulated, RowStart[Rows - 1], width);
        }
    }

    /// <summary>Столбец эталона, которому соответствует первый элемент <see cref="LastRow"/>.</summary>
    public int LastRowFirstColumn => Rows == 0 ? 0 : RowLow[Rows - 1];

    /// <summary>
    /// Накопленная стоимость <c>D(i, j)</c> последнего выравнивания. Возвращает <c>false</c>, если клетка вне матрицы или вне
    /// допустимого диапазона строки (полосы); недостижимая клетка внутри диапазона даёт <c>true</c> и +∞.
    /// </summary>
    /// <param name="i">Индекс в первой последовательности.</param>
    /// <param name="j">Индекс во второй последовательности.</param>
    /// <param name="value">Значение <c>D(i, j)</c>.</param>
    public bool TryGetAccumulated(int i, int j, out double value)
    {
        if (i >= 0 && i < Rows && j >= RowLow[i] && j <= RowHigh[i])
        {
            value = Accumulated[RowStart[i] + (j - RowLow[i])];
            return true;
        }

        value = double.PositiveInfinity;
        return false;
    }

    internal void EnsureRows(int rows)
    {
        if (RowStart.Length < rows)
        {
            int size = Math.Max(rows, (int)(RowStart.Length * GrowthFactor));
            RowStart = new int[size];
            RowLow = new int[size];
            RowHigh = new int[size];
        }
    }

    internal void EnsureCells(long cells)
    {
        if (cells > Array.MaxLength)
        {
            throw new InvalidOperationException("DTW matrix is too large; use a band.");
        }

        if (Accumulated.Length < cells)
        {
            long grown = Math.Min((long)Array.MaxLength, Math.Max(cells, (long)(Accumulated.Length * GrowthFactor)));
            Accumulated = new double[grown];
            Directions = new byte[grown];
        }
    }
}
