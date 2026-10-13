namespace PenForLang.Wpf.Controls;

/// <summary>Сводка внутренней задержки отрисовки (мс).</summary>
/// <param name="Frames">Сколько кадров с новыми точками измерено.</param>
/// <param name="MeanPointToRenderMs">Средняя задержка «метка новейшей точки кадра → <c>CompositionTarget.Rendering</c>».</param>
/// <param name="MaxPointToRenderMs">Наибольшая задержка «метка старейшей точки кадра → Rendering».</param>
/// <param name="LastPointToRenderMs">Задержка последнего кадра (новейшая точка).</param>
/// <param name="MeanArrivalToRenderMs">Средняя задержка «получение пачки программой → Rendering» (без части ОС и драйвера).</param>
/// <param name="MaxArrivalToRenderMs">Наибольшая задержка «получение → Rendering».</param>
public readonly record struct RenderLatency(
    long Frames,
    double MeanPointToRenderMs,
    double MaxPointToRenderMs,
    double LastPointToRenderMs,
    double MeanArrivalToRenderMs,
    double MaxArrivalToRenderMs)
{
    /// <summary>Измерений ещё не было.</summary>
    public bool IsEmpty => Frames == 0;
}

/// <summary>
/// Накопитель внутренней задержки «метка точки → Rendering» для dev-оверлея и отчёта сеанса (ARCHITECTURE.md §7.5). Метка точки —
/// время источника (QPC в мкс, <c>PenTimeBase.MonotonicAbsolute</c>); к ней добавляется время обработки в программе: от получения
/// пачки до кадра, в котором её показали. Ожидание кадра (до ~16,7 мс при 60 Гц) входит в задержку — это и есть внутренняя часть
/// «перо → пиксель». Часть после Rendering (композитор, DWM, панель) измеряется только камерой (V0-hardware.md).
/// </summary>
public sealed class RenderLatencyMeter
{
    private long _frames;
    private double _sumPointUs;
    private double _maxPointUs;
    private double _lastPointUs;
    private double _sumArrivalUs;
    private double _maxArrivalUs;

    /// <summary>Записывает измерение для кадра.</summary>
    /// <param name="nowUs">Момент Rendering, мкс.</param>
    /// <param name="newestPointUs">Метка новейшей точки кадра.</param>
    /// <param name="oldestPointUs">Метка старейшей точки кадра.</param>
    /// <param name="firstArrivalUs">Момент получения первой пачки кадра программой (0 — не измерялся).</param>
    public void Record(long nowUs, long newestPointUs, long oldestPointUs, long firstArrivalUs)
    {
        double newest = Math.Max(0, nowUs - newestPointUs);
        double oldest = Math.Max(0, nowUs - oldestPointUs);
        _frames++;
        _sumPointUs += newest;
        _lastPointUs = newest;
        if (oldest > _maxPointUs)
        {
            _maxPointUs = oldest;
        }

        if (firstArrivalUs > 0)
        {
            double arrival = Math.Max(0, nowUs - firstArrivalUs);
            _sumArrivalUs += arrival;
            if (arrival > _maxArrivalUs)
            {
                _maxArrivalUs = arrival;
            }
        }
    }

    /// <summary>Сбрасывает накопленное.</summary>
    public void Reset()
    {
        _frames = 0;
        _sumPointUs = 0;
        _maxPointUs = 0;
        _lastPointUs = 0;
        _sumArrivalUs = 0;
        _maxArrivalUs = 0;
    }

    /// <summary>Текущая сводка.</summary>
    public RenderLatency Snapshot() => _frames == 0
        ? default
        : new RenderLatency(
            _frames,
            _sumPointUs / _frames / 1000.0,
            _maxPointUs / 1000.0,
            _lastPointUs / 1000.0,
            _sumArrivalUs / _frames / 1000.0,
            _maxArrivalUs / 1000.0);
}
