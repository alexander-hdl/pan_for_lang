using PenForLang.Ink;

namespace PenForLang.App.Wpf;

/// <summary>Снимок статистики потока точек для панели диагностики.</summary>
/// <param name="TotalSamples">Всего точек.</param>
/// <param name="ContactSamples">Точек в контакте.</param>
/// <param name="Strokes">Начато штрихов (фаз Down).</param>
/// <param name="CoalescedSamples">Точек из истории (coalesced).</param>
/// <param name="ClippedSamples">Точек за краем холста.</param>
/// <param name="RateHz">Частота отчётов за последнюю секунду по меткам времени точек, Гц (0 — мало данных).</param>
/// <param name="MedianDtMs">Медианный интервал между соседними точками, мс.</param>
/// <param name="MaxDtMs">Наибольший интервал (без пауз длиннее порога пропуска), мс.</param>
/// <param name="GapsOverThreshold">Сколько интервалов превысили порог потери (по умолчанию 12 мс при номинальных 5 мс).</param>
/// <param name="LastPointerId">pointerId последней точки.</param>
/// <param name="LastPressure">Давление последней точки.</param>
/// <param name="MaxPressure">Наибольшее давление за сеанс.</param>
/// <param name="LastTiltX">Наклон X последней точки, градусы.</param>
/// <param name="LastTiltY">Наклон Y последней точки, градусы.</param>
/// <param name="LastButtons">Кнопки последней точки.</param>
/// <param name="LastPhase">Фаза последней точки.</param>
/// <param name="LastX">X последней точки на поверхности, px.</param>
/// <param name="LastY">Y последней точки на поверхности, px.</param>
public readonly record struct PenStats(
    long TotalSamples,
    long ContactSamples,
    long Strokes,
    long CoalescedSamples,
    long ClippedSamples,
    double RateHz,
    double MedianDtMs,
    double MaxDtMs,
    long GapsOverThreshold,
    uint LastPointerId,
    float LastPressure,
    float MaxPressure,
    float LastTiltX,
    float LastTiltY,
    PenButtons LastButtons,
    PenPhase LastPhase,
    double LastX,
    double LastY);

/// <summary>
/// Измеритель потока точек для протокола V0 на железе (200 Гц без потерь): считает частоту, интервалы и пропуски по меткам
/// времени точек, а не по часам окна, поэтому показывает частоту ЦИФРАТОРА (с учётом coalesced-точек истории).
/// Скользящие буферы заранее выделены — добавление точек не аллоцирует.
/// </summary>
public sealed class PenStatsMeter
{
    private const int RingSize = 2048;
    private const long RateWindowUs = 1_000_000;

    private readonly long[] _times = new long[RingSize];
    private readonly double[] _dtMs = new double[RingSize];
    private readonly double _gapThresholdMs;
    private readonly double _pauseThresholdMs;

    private int _timeCount;
    private int _timeHead;
    private int _dtCount;
    private int _dtHead;
    private long _lastTimeUs = long.MinValue;
    private uint _lastPointerId = uint.MaxValue;
    private PenStats _stats;

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
        _stats = _stats with
        {
            TotalSamples = _stats.TotalSamples + 1,
            ContactSamples = _stats.ContactSamples + (s.IsContact ? 1 : 0),
            Strokes = _stats.Strokes + (s.Phase == PenPhase.Down ? 1 : 0),
            CoalescedSamples = _stats.CoalescedSamples + ((s.Flags & SampleFlags.Coalesced) != 0 ? 1 : 0),
            ClippedSamples = _stats.ClippedSamples + (s.IsClipped ? 1 : 0),
            LastPointerId = s.PointerId,
            LastPressure = s.Pressure,
            MaxPressure = Math.Max(_stats.MaxPressure, s.Pressure),
            LastTiltX = s.TiltXDeg,
            LastTiltY = s.TiltYDeg,
            LastButtons = s.Buttons,
            LastPhase = s.Phase,
            LastX = s.X,
            LastY = s.Y,
        };

        if (s.PointerId == _lastPointerId && _lastTimeUs != long.MinValue)
        {
            double dtMs = (s.TimeUs - _lastTimeUs) / 1000.0;
            if (dtMs >= 0 && dtMs < _pauseThresholdMs)
            {
                _dtMs[_dtHead] = dtMs;
                _dtHead = (_dtHead + 1) % RingSize;
                _dtCount = Math.Min(_dtCount + 1, RingSize);
                if (dtMs > _gapThresholdMs)
                {
                    _stats = _stats with { GapsOverThreshold = _stats.GapsOverThreshold + 1 };
                }
            }
        }

        _times[_timeHead] = s.TimeUs;
        _timeHead = (_timeHead + 1) % RingSize;
        _timeCount = Math.Min(_timeCount + 1, RingSize);
        _lastTimeUs = s.TimeUs;
        _lastPointerId = s.PointerId;
    }

    /// <summary>Снимок статистики (расчёт медианы и частоты выделяет память; вызывать из таймера панели, не из горячего пути).</summary>
    public PenStats Snapshot()
    {
        double median = 0;
        double max = 0;
        if (_dtCount > 0)
        {
            double[] dts = new double[_dtCount];
            for (int i = 0; i < _dtCount; i++)
            {
                dts[i] = _dtMs[(_dtHead - 1 - i + RingSize) % RingSize];
            }

            Array.Sort(dts);
            median = dts[dts.Length / 2];
            max = dts[^1];
        }

        return _stats with { RateHz = ComputeRateHz(), MedianDtMs = median, MaxDtMs = max };
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
