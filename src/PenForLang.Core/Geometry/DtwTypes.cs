namespace PenForLang.Core.Geometry;

/// <summary>
/// Локальная стоимость соответствия элемента <c>i</c> первой последовательности элементу <c>j</c> второй.
/// Значение — неотрицательное конечное число или <see cref="double.PositiveInfinity"/> (пара запрещена).
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
    /// Стоимости должны быть неотрицательны (тогда продолжение первой строки никогда не выгоднее свежего старта).
    /// </summary>
    public bool OpenBegin { get; init; }

    /// <summary>
    /// Открытый конец: путь может заканчиваться в любой клетке последней строки <c>(n−1, j)</c>; выбирается минимум
    /// накопленной стоимости (при равенстве — наименьший <c>j</c>). Для сравнения префиксов разной длины нормируйте
    /// стоимость на <see cref="DtwResult.PathLength"/> самостоятельно.
    /// </summary>
    public bool OpenEnd { get; init; }

    /// <summary>
    /// Радиус индексной полосы Сакое–Тибы (<c>null</c> — полосы нет): допустимы клетки <c>|i − j| ≤ max(radius, |n − m|)</c>.
    /// Расширение до <c>|n − m|</c> гарантирует связность полосы и достижимость угла <c>(n−1, m−1)</c>.
    /// </summary>
    public int? IndexBandRadius { get; init; }
}

/// <summary>Итог выравнивания DTW. Для недостижимого конца (<see cref="IsFeasible"/> = <c>false</c>) стоимость бесконечна.</summary>
/// <param name="IsFeasible">Существует ли допустимый путь.</param>
/// <param name="Cost">Накопленная стоимость пути (сумма локальных стоимостей клеток) или +∞.</param>
/// <param name="PathLength">Число клеток пути (0 при недостижимости).</param>
/// <param name="Start">Первая клетка пути (или <c>(−1, −1)</c>).</param>
/// <param name="End">Последняя клетка пути (или <c>(−1, −1)</c>).</param>
public readonly record struct DtwResult(bool IsFeasible, double Cost, int PathLength, DtwCell Start, DtwCell End)
{
    /// <summary>Накопленная стоимость, делённая на длину пути (+∞ при недостижимости).</summary>
    public double NormalizedCost => IsFeasible ? Cost / PathLength : double.PositiveInfinity;

    /// <summary>Результат «пути нет».</summary>
    public static DtwResult Infeasible { get; } =
        new(false, double.PositiveInfinity, 0, new DtwCell(-1, -1), new DtwCell(-1, -1));
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

    /// <summary>Отношение <c>Reversed.Cost / Forward.Cost</c>; +∞, если прямого пути нет или его стоимость 0.</summary>
    public double ReversedToForwardRatio =>
        Forward.IsFeasible && Forward.Cost > 0.0 ? Reversed.Cost / Forward.Cost : double.PositiveInfinity;
}

/// <summary>
/// Переиспользуемые буферы для <see cref="Dtw"/>: повторные вызовы с тем же рабочим набором не выделяют память,
/// если размеры не растут. Не потокобезопасен; один экземпляр — один вызывающий поток.
/// </summary>
public sealed class DtwWorkspace
{
    internal double[] Accumulated = [];
    internal byte[] Directions = [];
    internal int[] RowStart = [];
    internal int[] RowLow = [];
    internal int[] RowHigh = [];

    internal void EnsureRows(int rows)
    {
        if (RowStart.Length < rows)
        {
            RowStart = new int[rows];
            RowLow = new int[rows];
            RowHigh = new int[rows];
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
            Accumulated = new double[cells];
            Directions = new byte[cells];
        }
    }
}
