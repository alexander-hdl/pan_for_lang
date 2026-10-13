namespace PenForLang.Core.Geometry;

/// <summary>
/// Динамическая трансформация времени (DTW) двух последовательностей длиной <c>n</c> и <c>m</c> с локальной стоимостью
/// <see cref="IDtwCost"/> и шагами <c>(i−1, j)</c>, <c>(i, j−1)</c>, <c>(i−1, j−1)</c>:
/// <c>D(i,j) = c(i,j) + min(D(i−1,j), D(i,j−1), D(i−1,j−1))</c>.
/// <para>Варианты: полный; индексная полоса Сакое–Тибы (<see cref="DtwOptions.IndexBandRadius"/>); полоса по x
/// (<see cref="AlignXBand{TCost}"/>, ограничение <c>|xa[i] − xb[j]| ≤ bandX</c>, ARCHITECTURE.md §6.4.4); открытые начало и конец
/// (<see cref="DtwOptions"/>); многопризнаковая стоимость (<see cref="WeightedFeatureCost"/> или любая <see cref="IDtwCost"/>);
/// путь выравнивания; сравнение с обращённым эталоном (<see cref="CompareWithReversedReference{TCost}"/>).</para>
/// <para>Память и время пропорциональны числу допустимых клеток (полоса экономит оба). При равенстве стоимостей предпочтение:
/// диагональ, затем шаг по <c>i</c>, затем по <c>j</c>, поэтому путь детерминирован. Запрещённая пара — стоимость
/// <see cref="double.PositiveInfinity"/>; если конец недостижим, результат <see cref="DtwResult.Infeasible"/>.</para>
/// </summary>
public static class Dtw
{
    private const byte DirStart = 0;
    private const byte DirDiagonal = 1;
    private const byte DirUp = 2;
    private const byte DirLeft = 3;
    private const byte DirNone = 255;

    /// <summary>Выравнивание (полное или с индексной полосой, с открытыми концами или без).</summary>
    /// <typeparam name="TCost">Тип стоимости (структура — для встраивания вызова).</typeparam>
    /// <param name="n">Длина первой последовательности, ≥ 1.</param>
    /// <param name="m">Длина второй (опорной) последовательности, ≥ 1.</param>
    /// <param name="cost">Локальная стоимость пар.</param>
    /// <param name="options">Открытые концы и индексная полоса.</param>
    /// <param name="workspace">Буферы для повторного использования; <c>null</c> — выделить новые.</param>
    /// <param name="path">Если не <c>null</c> — очищается и заполняется путём от начала к концу.</param>
    public static DtwResult Align<TCost>(
        int n,
        int m,
        in TCost cost,
        DtwOptions options = default,
        DtwWorkspace? workspace = null,
        List<DtwCell>? path = null)
        where TCost : IDtwCost, allows ref struct
    {
        CheckSizes(n, m);
        workspace ??= new DtwWorkspace();
        workspace.EnsureRows(n);
        for (int i = 0; i < n; i++)
        {
            workspace.RowLow[i] = 0;
            workspace.RowHigh[i] = m - 1;
        }

        ApplyIndexBand(n, m, options.IndexBandRadius, workspace);
        return Run(n, m, in cost, options, workspace, path);
    }

    /// <summary>
    /// Выравнивание с полосой по x: клетка <c>(i, j)</c> допустима, только если <c>|xa[i] − xb[j]| ≤ bandX</c>
    /// (после нормализации и деслантинга; ARCHITECTURE.md §6.4.4). Полоса может комбинироваться с индексной
    /// (<see cref="DtwOptions.IndexBandRadius"/>) и открытыми концами. Если <paramref name="xb"/> не убывает (монотонный эталон), диапазоны
    /// допустимых <c>j</c> в строках находятся бинарным поиском и работа пропорциональна ширине полосы; иначе строки сканируются полностью.
    /// При жёсткой полосе (по умолчанию) и отсутствии допустимого пути (например, разрыв по x больше полосы)
    /// возвращается <see cref="DtwResult.Infeasible"/> — вызывающий код обязан его обработать (штраф или «не распознано»).
    /// Мягкая полоса: <see cref="DtwOptions.OutOfBandCost"/> задаёт конечный штраф, прибавляемый к стоимости пар вне полосы;
    /// тогда путь существует всегда, но отсечения диапазонов нет и работа пропорциональна <c>n·m</c>.
    /// </summary>
    /// <typeparam name="TCost">Тип стоимости.</typeparam>
    /// <param name="xa">Координаты x первой последовательности (<c>n = xa.Length</c>).</param>
    /// <param name="xb">Координаты x второй последовательности (<c>m = xb.Length</c>).</param>
    /// <param name="bandX">Полуширина полосы по x, ≥ 0.</param>
    /// <param name="cost">Локальная стоимость пар.</param>
    /// <param name="options">Открытые концы и индексная полоса.</param>
    /// <param name="workspace">Буферы для повторного использования.</param>
    /// <param name="path">Если не <c>null</c> — очищается и заполняется путём.</param>
    public static DtwResult AlignXBand<TCost>(
        ReadOnlySpan<double> xa,
        ReadOnlySpan<double> xb,
        double bandX,
        in TCost cost,
        DtwOptions options = default,
        DtwWorkspace? workspace = null,
        List<DtwCell>? path = null)
        where TCost : IDtwCost, allows ref struct
    {
        int n = xa.Length;
        int m = xb.Length;
        CheckSizes(n, m);
        if (!(bandX >= 0.0))
        {
            throw new ArgumentOutOfRangeException(nameof(bandX), "Band must be >= 0.");
        }

        workspace ??= new DtwWorkspace();
        workspace.EnsureRows(n);
        double penalty = options.OutOfBandCost ?? double.PositiveInfinity;
        if (!(penalty >= 0.0))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "OutOfBandCost must be >= 0.");
        }

        if (double.IsPositiveInfinity(penalty))
        {
            FillXBandRanges(xa, xb, bandX, workspace);
        }
        else
        {
            // Мягкая полоса: допустимы все клетки (диапазоны строк полные), вне полосы добавляется штраф.
            for (int i = 0; i < n; i++)
            {
                workspace.RowLow[i] = 0;
                workspace.RowHigh[i] = m - 1;
            }
        }

        ApplyIndexBand(n, m, options.IndexBandRadius, workspace);
        var banded = new XBandedCost<TCost>(in cost, xa, xb, bandX, penalty);
        return Run(n, m, in banded, options, workspace, path);
    }

    /// <summary>
    /// Выравнивает последовательность и с эталоном, и с эталоном, пройденным в обратном порядке (вторая последовательность
    /// переиндексируется <c>j → m−1−j</c>). Подходит, когда признаки зависят только от положения; направленные признаки
    /// (θ) нужно пересчитать — для этого есть <see cref="CompareDirections{TForward,TReversed}"/>.
    /// Открытые концы в <paramref name="options"/> относятся к эталону в порядке обхода (для обращённого — к его началу и концу после обращения).
    /// </summary>
    /// <typeparam name="TCost">Тип стоимости.</typeparam>
    /// <param name="n">Длина первой последовательности.</param>
    /// <param name="m">Длина эталона.</param>
    /// <param name="cost">Стоимость для эталона как есть.</param>
    /// <param name="options">Опции DTW.</param>
    /// <param name="workspace">Буферы для повторного использования.</param>
    /// <param name="forwardPath">Путь прямого выравнивания (опционально).</param>
    /// <param name="reversedPath">Путь обращённого выравнивания в индексах исходного эталона (опционально).</param>
    public static DtwDirectionComparison CompareWithReversedReference<TCost>(
        int n,
        int m,
        in TCost cost,
        DtwOptions options = default,
        DtwWorkspace? workspace = null,
        List<DtwCell>? forwardPath = null,
        List<DtwCell>? reversedPath = null)
        where TCost : IDtwCost, allows ref struct
    {
        CheckSizes(n, m);
        var reversedCost = new ReversedReferenceCost<TCost>(in cost, m);
        return CompareDirections(n, m, in cost, in reversedCost, options, workspace, forwardPath, reversedPath);
    }

    /// <summary>
    /// Выравнивает последовательность с эталоном и с обращённым эталоном, где стоимость обращённого случая задана отдельно
    /// (например, признаки θ пересчитаны для обратного обхода). <paramref name="reversed"/> получает индексы <c>j</c> уже в
    /// обращённом порядке; в результатах и пути они приводятся к индексам исходного эталона (<c>m − 1 − j</c>).
    /// </summary>
    /// <typeparam name="TForward">Тип стоимости для прямого эталона.</typeparam>
    /// <typeparam name="TReversed">Тип стоимости для обращённого эталона.</typeparam>
    /// <param name="n">Длина первой последовательности.</param>
    /// <param name="m">Длина эталона.</param>
    /// <param name="forward">Стоимость для эталона как есть.</param>
    /// <param name="reversed">Стоимость для эталона в обратном порядке.</param>
    /// <param name="options">Опции DTW.</param>
    /// <param name="workspace">Буферы для повторного использования.</param>
    /// <param name="forwardPath">Путь прямого выравнивания (опционально).</param>
    /// <param name="reversedPath">Путь обращённого выравнивания в индексах исходного эталона (опционально).</param>
    public static DtwDirectionComparison CompareDirections<TForward, TReversed>(
        int n,
        int m,
        in TForward forward,
        in TReversed reversed,
        DtwOptions options = default,
        DtwWorkspace? workspace = null,
        List<DtwCell>? forwardPath = null,
        List<DtwCell>? reversedPath = null)
        where TForward : IDtwCost, allows ref struct
        where TReversed : IDtwCost, allows ref struct
    {
        workspace ??= new DtwWorkspace();
        DtwResult fwd = Align(n, m, in forward, options, workspace, forwardPath);
        DtwResult rev = Align(n, m, in reversed, options, workspace, reversedPath);

        if (reversedPath is not null)
        {
            for (int k = 0; k < reversedPath.Count; k++)
            {
                DtwCell c = reversedPath[k];
                reversedPath[k] = new DtwCell(c.I, m - 1 - c.J);
            }
        }

        if (rev.IsFeasible)
        {
            rev = rev with
            {
                Start = new DtwCell(rev.Start.I, m - 1 - rev.Start.J),
                End = new DtwCell(rev.End.I, m - 1 - rev.End.J),
            };
        }

        return new DtwDirectionComparison(fwd, rev);
    }

    private static void CheckSizes(int n, int m)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(m, 1);
    }

    private static void ApplyIndexBand(int n, int m, int? radius, DtwWorkspace ws)
    {
        if (radius is null)
        {
            return;
        }

        ArgumentOutOfRangeException.ThrowIfNegative(radius.Value, nameof(DtwOptions.IndexBandRadius));
        long r = Math.Max(radius.Value, Math.Abs(n - m));
        for (int i = 0; i < n; i++)
        {
            ws.RowLow[i] = (int)Math.Max(ws.RowLow[i], i - r);
            ws.RowHigh[i] = (int)Math.Min(ws.RowHigh[i], i + r);
        }
    }

    private static void FillXBandRanges(ReadOnlySpan<double> xa, ReadOnlySpan<double> xb, double band, DtwWorkspace ws)
    {
        int n = xa.Length;
        int m = xb.Length;
        bool sorted = true;
        for (int j = 1; j < m; j++)
        {
            if (!(xb[j - 1] <= xb[j]))
            {
                sorted = false;
                break;
            }
        }

        for (int i = 0; i < n; i++)
        {
            double x = xa[i];
            int lo;
            int hi;
            if (double.IsNaN(x))
            {
                lo = 0;
                hi = -1;
            }
            else if (sorted)
            {
                // p — первая позиция с xb[p] >= x. Слева от p допустимость растёт с j (ложь…истина), справа — убывает (истина…ложь).
                int p = 0;
                int right = m;
                while (p < right)
                {
                    int mid = p + ((right - p) / 2);
                    if (xb[mid] < x)
                    {
                        p = mid + 1;
                    }
                    else
                    {
                        right = mid;
                    }
                }

                int a = 0;
                int b = p;
                while (a < b)
                {
                    int mid = a + ((b - a) / 2);
                    if (Math.Abs(x - xb[mid]) <= band)
                    {
                        b = mid;
                    }
                    else
                    {
                        a = mid + 1;
                    }
                }

                lo = a;

                int c = p;
                int d = m;
                while (c < d)
                {
                    int mid = c + ((d - c) / 2);
                    if (Math.Abs(x - xb[mid]) <= band)
                    {
                        c = mid + 1;
                    }
                    else
                    {
                        d = mid;
                    }
                }

                hi = c - 1;
            }
            else
            {
                lo = 0;
                hi = -1;
                for (int j = 0; j < m; j++)
                {
                    if (Math.Abs(x - xb[j]) <= band)
                    {
                        if (hi < 0)
                        {
                            lo = j;
                        }

                        hi = j;
                    }
                }
            }

            ws.RowLow[i] = lo;
            ws.RowHigh[i] = hi;
        }
    }

    private static DtwResult Run<TCost>(
        int n,
        int m,
        in TCost cost,
        DtwOptions options,
        DtwWorkspace ws,
        List<DtwCell>? path)
        where TCost : IDtwCost, allows ref struct
    {
        int[] rowLow = ws.RowLow;
        int[] rowHigh = ws.RowHigh;
        int[] rowStart = ws.RowStart;

        long total = 0;
        for (int i = 0; i < n; i++)
        {
            rowStart[i] = (int)Math.Min(total, int.MaxValue);
            int width = rowHigh[i] >= rowLow[i] ? rowHigh[i] - rowLow[i] + 1 : 0;
            total += width;
        }

        ws.EnsureCells(total);
        ws.Rows = n;
        double[] acc = ws.Accumulated;
        byte[] dir = ws.Directions;
        bool openBegin = options.OpenBegin;

        for (int i = 0; i < n; i++)
        {
            int lo = rowLow[i];
            int hi = rowHigh[i];
            int start = rowStart[i];
            int prevStart = i > 0 ? rowStart[i - 1] : 0;
            int prevLo = i > 0 ? rowLow[i - 1] : 0;
            int prevHi = i > 0 ? rowHigh[i - 1] : -1;

            for (int j = lo; j <= hi; j++)
            {
                double c = cost.Cost(i, j);
                if (!(c >= 0.0))
                {
                    throw new InvalidOperationException("DTW cost must be >= 0 or +Infinity (negative and NaN are not allowed).");
                }

                double best;
                byte code;
                if (i == 0)
                {
                    if (openBegin || j == 0)
                    {
                        best = 0.0;
                        code = DirStart;
                    }
                    else
                    {
                        best = j > lo ? acc[start + (j - 1 - lo)] : double.PositiveInfinity;
                        code = DirLeft;
                    }
                }
                else
                {
                    best = double.PositiveInfinity;
                    code = DirNone;
                    if (j > 0 && j - 1 >= prevLo && j - 1 <= prevHi)
                    {
                        best = acc[prevStart + (j - 1 - prevLo)];
                        code = DirDiagonal;
                    }

                    if (j >= prevLo && j <= prevHi)
                    {
                        double up = acc[prevStart + (j - prevLo)];
                        if (up < best)
                        {
                            best = up;
                            code = DirUp;
                        }
                    }

                    if (j > lo)
                    {
                        double left = acc[start + (j - 1 - lo)];
                        if (left < best)
                        {
                            best = left;
                            code = DirLeft;
                        }
                    }
                }

                int idx = start + (j - lo);
                acc[idx] = best + c;
                dir[idx] = code;
            }
        }

        int endJ = -1;
        int lastLo = rowLow[n - 1];
        int lastHi = rowHigh[n - 1];
        int lastStart = rowStart[n - 1];
        if (options.OpenEnd)
        {
            double bestEnd = double.PositiveInfinity;
            for (int j = lastLo; j <= lastHi; j++)
            {
                double v = acc[lastStart + (j - lastLo)];
                if (v < bestEnd)
                {
                    bestEnd = v;
                    endJ = j;
                }
            }
        }
        else if (m - 1 >= lastLo && m - 1 <= lastHi)
        {
            double v = acc[lastStart + (m - 1 - lastLo)];
            if (!double.IsPositiveInfinity(v))
            {
                endJ = m - 1;
            }
        }

        path?.Clear();
        if (endJ < 0)
        {
            return DtwResult.Infeasible;
        }

        double totalCost = acc[lastStart + (endJ - lastLo)];
        int ci = n - 1;
        int cj = endJ;
        int length = 0;
        while (true)
        {
            length++;
            path?.Add(new DtwCell(ci, cj));
            byte code = dir[rowStart[ci] + (cj - rowLow[ci])];
            if (code == DirStart)
            {
                break;
            }

            switch (code)
            {
                case DirDiagonal:
                    ci--;
                    cj--;
                    break;
                case DirUp:
                    ci--;
                    break;
                case DirLeft:
                    cj--;
                    break;
                default:
                    throw new InvalidOperationException("DTW backtrace reached an unreachable cell.");
            }
        }

        path?.Reverse();
        return new DtwResult(true, totalCost, length, new DtwCell(ci, cj), new DtwCell(n - 1, endJ), n);
    }
}
