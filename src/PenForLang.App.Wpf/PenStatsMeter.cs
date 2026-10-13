using PenForLang.Ink;

namespace PenForLang.App.Wpf;

/// <summary>Частота отчётов в одном штрихе (по интервалам между соседними точками контакта).</summary>
/// <param name="Index">Номер штриха с 1.</param>
/// <param name="Samples">Точек контакта в штрихе (без синтетических).</param>
/// <param name="MedianHz">Медианная мгновенная частота (1000 / интервал в мс), Гц.</param>
/// <param name="P5Hz">5-й процентиль мгновенной частоты — «медленный хвост»: провалы частоты из-за потерь отчётов, Гц.</param>
/// <param name="DurationMs">Длительность штриха, мс.</param>
/// <param name="CoalescedShare">Доля coalesced-точек (из истории) в штрихе, 0..1.</param>
public readonly record struct StrokeRate(int Index, int Samples, double MedianHz, double P5Hz, double DurationMs, double CoalescedShare);

/// <summary>Снимок статистики потока точек для панели диагностики и отчёта.</summary>
/// <param name="TotalSamples">Всего точек.</param>
/// <param name="ContactSamples">Точек в контакте.</param>
/// <param name="HoverSamples">Точек наведения.</param>
/// <param name="Strokes">Начато штрихов (фаз Down).</param>
/// <param name="CoalescedSamples">Точек из истории (coalesced).</param>
/// <param name="ClippedSamples">Точек за краем холста.</param>
/// <param name="SyntheticTimeSamples">Точек с синтетическим временем (<see cref="SampleFlags.InterpolatedTime"/>): в частоту не входят.</param>
/// <param name="RateHz">Частота отчётов в КОНТАКТЕ за последнюю секунду по меткам времени точек, Гц (0 — мало данных).</param>
/// <param name="MedianDtMs">Медианный интервал между соседними точками контакта, мс.</param>
/// <param name="MaxDtMs">Наибольший интервал (без пауз длиннее порога пропуска), мс.</param>
/// <param name="GapsOverThreshold">Сколько интервалов превысили порог потери (по умолчанию 12 мс при номинальных 5 мс).</param>
/// <param name="OverallMedianHz">Медианная мгновенная частота по последним интервалам, Гц.</param>
/// <param name="OverallP5Hz">5-й процентиль мгновенной частоты по последним интервалам, Гц.</param>
/// <param name="StrokeRates">Частота по каждому штриху (законченные и текущий).</param>
/// <param name="LastPointerId">pointerId последней точки.</param>
/// <param name="LastPressure">Давление последней точки.</param>
/// <param name="MinPressure">Наименьшее давление в контакте (Down/Move), 0 — не было контакта.</param>
/// <param name="MaxPressure">Наибольшее давление за сеанс.</param>
/// <param name="PressureLevels">Сколько различных значений давления (в шагах 1/1024) встречено.</param>
/// <param name="TiltXMin">Наименьший наклон X, градусы.</param>
/// <param name="TiltXMax">Наибольший наклон X, градусы.</param>
/// <param name="TiltYMin">Наименьший наклон Y, градусы.</param>
/// <param name="TiltYMax">Наибольший наклон Y, градусы.</param>
/// <param name="LastTiltX">Наклон X последней точки, градусы.</param>
/// <param name="LastTiltY">Наклон Y последней точки, градусы.</param>
/// <param name="BarrelSamples">Точек с нажатой барабанной кнопкой.</param>
/// <param name="EraserSamples">Точек с ластиком (перевёрнутое перо или кнопка ластика).</param>
/// <param name="LastButtons">Кнопки последней точки.</param>
/// <param name="LastPhase">Фаза последней точки.</param>
/// <param name="LastX">X последней точки на поверхности, px.</param>
/// <param name="LastY">Y последней точки на поверхности, px.</param>
public readonly record struct PenStats(
    long TotalSamples,
    long ContactSamples,
    long HoverSamples,
    long Strokes,
    long CoalescedSamples,
    long ClippedSamples,
    long SyntheticTimeSamples,
    double RateHz,
    double MedianDtMs,
    double MaxDtMs,
    long GapsOverThreshold,
    double OverallMedianHz,
    double OverallP5Hz,
    IReadOnlyList<StrokeRate> StrokeRates,
    uint LastPointerId,
    float LastPressure,
    float MinPressure,
    float MaxPressure,
    int PressureLevels,
    float TiltXMin,
    float TiltXMax,
    float TiltYMin,
    float TiltYMax,
    float LastTiltX,
    float LastTiltY,
    long BarrelSamples,
    long EraserSamples,
    PenButtons LastButtons,
    PenPhase LastPhase,
    double LastX,
    double LastY);

/// <summary>
/// Измеритель потока точек для протокола V0 на железе (200 Гц без потерь): считает частоту, интервалы и пропуски по меткам
/// времени точек, а не по часам окна, поэтому показывает частоту ЦИФРАТОРА (с учётом coalesced-точек истории).
/// </summary>
/// <remarks>
/// <para>Частота и интервалы считаются ТОЛЬКО по точкам контакта (<see cref="PenSample.IsContact"/>) с настоящими метками
/// времени: наведение, синтетические точки (закрытие штриха при потере захвата) и точки с распределённым временем
/// (<see cref="SampleFlags.InterpolatedTime"/>) в расчёт не входят (F13). Интервал не считается через начало штриха (Down) и
/// через смену указателя.</para>
/// <para>Скользящие буферы заранее выделены — добавление точек не аллоцирует (кроме закрытия штриха).</para>
/// </remarks>
public sealed class PenStatsMeter
{
    private const int RingSize = 2048;
    private const long RateWindowUs = 1_000_000;
    private const int MaxStrokeSummaries = 500;

    private readonly long[] _times = new long[RingSize];
    private readonly double[] _dtMs = new double[RingSize];
    private readonly double _gapThresholdMs;
    private readonly double _pauseThresholdMs;
    private readonly List<StrokeRate> _strokes = [];
    private readonly List<double> _strokeRates = [];
    private readonly HashSet<int> _pressureLevels = [];

    private int _timeCount;
    private int _timeHead;
    private int _dtCount;
    private int _dtHead;
    private long _lastContactTimeUs = long.MinValue;
    private uint _lastContactPointer = uint.MaxValue;
    private bool _strokeOpen;
    private long _strokeStartUs;
    private long _strokeSamples;
    private long _strokeCoalesced;
    private int _strokeCounter;
    private PenStats _stats;
    private bool _seenTilt;
    private float _minPressure = float.MaxValue;
    private long _lastStrokeEndUs;

    /// <summary>Создаёт измеритель.</summary>
    /// <param name="gapThresholdMs">Интервал, начиная с которого считается потеря отчёта (при 200 Гц — 12 мс).</param>
    /// <param name="pauseThresholdMs">Интервал, после которого поток считается новой серией (выход из зоны), в статистику не идёт.</param>
    public PenStatsMeter(double gapThresholdMs = 12.0, double pauseThresholdMs = 100.0)
    {
        _gapThresholdMs = gapThresholdMs;
        _pauseThresholdMs = pauseThresholdMs;
    }

    /// <summary>Добавляет пачку точек в хронологическом порядке.</summary>
    public void Add(ReadOnlySpan<PenSample> samples)
    {
        foreach (PenSample s in samples)
        {
            Add(in s);
        }
    }

    /// <summary>Добавляет точку.</summary>
    public void Add(in PenSample s)
    {
        bool synthetic = (s.Flags & SampleFlags.InterpolatedTime) != 0;
        bool contactPressure = s.Phase is PenPhase.Down or PenPhase.Move;

        _stats = _stats with
        {
            TotalSamples = _stats.TotalSamples + 1,
            ContactSamples = _stats.ContactSamples + (s.IsContact ? 1 : 0),
            HoverSamples = _stats.HoverSamples + (s.Phase == PenPhase.Hover ? 1 : 0),
            Strokes = _stats.Strokes + (s.Phase == PenPhase.Down ? 1 : 0),
            CoalescedSamples = _stats.CoalescedSamples + ((s.Flags & SampleFlags.Coalesced) != 0 ? 1 : 0),
            ClippedSamples = _stats.ClippedSamples + (s.IsClipped ? 1 : 0),
            SyntheticTimeSamples = _stats.SyntheticTimeSamples + (synthetic ? 1 : 0),
            LastPointerId = s.PointerId,
            LastPressure = s.Pressure,
            MaxPressure = Math.Max(_stats.MaxPressure, s.Pressure),
            LastTiltX = s.TiltXDeg,
            LastTiltY = s.TiltYDeg,
            BarrelSamples = _stats.BarrelSamples + ((s.Buttons & PenButtons.Barrel) != 0 ? 1 : 0),
            EraserSamples = _stats.EraserSamples + ((s.Buttons & PenButtons.Eraser) != 0 ? 1 : 0),
            LastButtons = s.Buttons,
            LastPhase = s.Phase,
            LastX = s.X,
            LastY = s.Y,
        };

        if (contactPressure)
        {
            _minPressure = Math.Min(_minPressure, s.Pressure);
            _pressureLevels.Add((int)((s.Pressure * 1024f) + 0.5f));
        }

        if (s.TiltXDeg != 0 || s.TiltYDeg != 0 || _seenTilt)
        {
            _stats = _seenTilt
                ? _stats with
                {
                    TiltXMin = Math.Min(_stats.TiltXMin, s.TiltXDeg),
                    TiltXMax = Math.Max(_stats.TiltXMax, s.TiltXDeg),
                    TiltYMin = Math.Min(_stats.TiltYMin, s.TiltYDeg),
                    TiltYMax = Math.Max(_stats.TiltYMax, s.TiltYDeg),
                }
                : _stats with { TiltXMin = s.TiltXDeg, TiltXMax = s.TiltXDeg, TiltYMin = s.TiltYDeg, TiltYMax = s.TiltYDeg };
            _seenTilt = true;
        }

        if (!s.IsContact || synthetic)
        {
            // F13: наведение и синтетические точки в частоту не входят. Синтетический Up всё же закрывает штрих.
            if (s.Phase == PenPhase.Up && _strokeOpen)
            {
                FinishStroke();
            }

            return;
        }

        if (s.Phase == PenPhase.Down)
        {
            if (_strokeOpen)
            {
                FinishStroke();
            }

            _strokeOpen = true;
            _strokeStartUs = s.TimeUs;
            _strokeSamples = 0;
            _strokeCoalesced = 0;
            _strokeRates.Clear();
            _lastContactTimeUs = long.MinValue;
        }

        if (s.PointerId == _lastContactPointer && _lastContactTimeUs != long.MinValue)
        {
            double dtMs = (s.TimeUs - _lastContactTimeUs) / 1000.0;
            if (dtMs >= 0 && dtMs < _pauseThresholdMs)
            {
                _dtMs[_dtHead] = dtMs;
                _dtHead = (_dtHead + 1) % RingSize;
                _dtCount = Math.Min(_dtCount + 1, RingSize);
                if (dtMs > _gapThresholdMs)
                {
                    _stats = _stats with { GapsOverThreshold = _stats.GapsOverThreshold + 1 };
                }

                if (dtMs > 0)
                {
                    _strokeRates.Add(1000.0 / dtMs);
                }
            }
        }

        _times[_timeHead] = s.TimeUs;
        _timeHead = (_timeHead + 1) % RingSize;
        _timeCount = Math.Min(_timeCount + 1, RingSize);
        _lastContactTimeUs = s.TimeUs;
        _lastContactPointer = s.PointerId;

        if (_strokeOpen)
        {
            _strokeSamples++;
            if ((s.Flags & SampleFlags.Coalesced) != 0)
            {
                _strokeCoalesced++;
            }

            _lastStrokeEndUs = s.TimeUs;
            if (s.Phase == PenPhase.Up)
            {
                FinishStroke();
            }
        }
    }

    /// <summary>Снимок статистики (расчёт медианы и частоты выделяет память; вызывать из таймера панели, не из горячего пути).</summary>
    public PenStats Snapshot()
    {
        double median = 0;
        double max = 0;
        double overallMedianHz = 0;
        double overallP5Hz = 0;
        if (_dtCount > 0)
        {
            double[] dts = new double[_dtCount];
            var rates = new List<double>(_dtCount);
            for (int i = 0; i < _dtCount; i++)
            {
                dts[i] = _dtMs[(_dtHead - 1 - i + RingSize) % RingSize];
                if (dts[i] > 0)
                {
                    rates.Add(1000.0 / dts[i]);
                }
            }

            Array.Sort(dts);
            median = dts[dts.Length / 2];
            max = dts[^1];
            if (rates.Count > 0)
            {
                rates.Sort();
                overallMedianHz = rates[rates.Count / 2];
                overallP5Hz = rates[(int)Math.Floor(0.05 * (rates.Count - 1))];
            }
        }

        var strokeRates = new List<StrokeRate>(_strokes);
        if (_strokeOpen && _strokeRates.Count >= 1)
        {
            strokeRates.Add(Summarize(_strokeCounter + 1));
        }

        return _stats with
        {
            RateHz = ComputeRateHz(),
            MedianDtMs = median,
            MaxDtMs = max,
            OverallMedianHz = overallMedianHz,
            OverallP5Hz = overallP5Hz,
            StrokeRates = strokeRates,
            PressureLevels = _pressureLevels.Count,
            MinPressure = _minPressure == float.MaxValue ? 0f : _minPressure,
        };
    }

    private void FinishStroke()
    {
        _strokeOpen = false;
        if (_strokeRates.Count >= 1)
        {
            _strokeCounter++;
            _strokes.Add(Summarize(_strokeCounter));
            if (_strokes.Count > MaxStrokeSummaries)
            {
                _strokes.RemoveAt(0);
            }
        }

        _strokeRates.Clear();
    }

    private StrokeRate Summarize(int index)
    {
        double[] rates = [.. _strokeRates];
        Array.Sort(rates);
        double median = rates[rates.Length / 2];
        double p5 = rates[(int)Math.Floor(0.05 * (rates.Length - 1))];
        double duration = (_lastStrokeEndUs - _strokeStartUs) / 1000.0;
        double coalescedShare = _strokeSamples > 0 ? (double)_strokeCoalesced / _strokeSamples : 0;
        return new StrokeRate(index, (int)_strokeSamples, median, p5, duration, coalescedShare);
    }

    private double ComputeRateHz()
    {
        if (_timeCount < 2)
        {
            return 0;
        }

        long newest = _times[(_timeHead - 1 + RingSize) % RingSize];
        long oldestInWindow = newest;
        int inWindow = 0;
        for (int i = 0; i < _timeCount; i++)
        {
            long t = _times[(_timeHead - 1 - i + RingSize) % RingSize];
            if (newest - t > RateWindowUs)
            {
                break;
            }

            oldestInWindow = t;
            inWindow++;
        }

        long spanUs = newest - oldestInWindow;
        return inWindow < 2 || spanUs <= 0 ? 0 : (inWindow - 1) * 1_000_000.0 / spanUs;
    }
}
