using PenForLang.Core;

namespace PenForLang.Testing;

/// <summary>
/// Управляемые монотонные часы для тестов: тики растут только при <see cref="Advance"/>.
/// По умолчанию частота 10 МГц (как у QPC на типичном Windows) и начало отсчёта, близкое к месяцу аптайма, —
/// чтобы тесты ловили переполнение при переводе в микросекунды.
/// </summary>
public sealed class FakeMonotonicClock : IMonotonicClock
{
    /// <summary>Частота по умолчанию, Гц.</summary>
    public const long DefaultFrequency = 10_000_000;

    /// <summary>Начальное значение по умолчанию: 30 суток аптайма.</summary>
    public const long DefaultStartTicks = 30L * 24 * 3600 * DefaultFrequency;

    /// <summary>Создаёт часы.</summary>
    /// <param name="startTicks">Начальное значение счётчика; не отрицательно.</param>
    /// <param name="frequency">Тиков в секунду; положительна.</param>
    public FakeMonotonicClock(long startTicks = DefaultStartTicks, long frequency = DefaultFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startTicks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        Ticks = startTicks;
        Frequency = frequency;
    }

    /// <inheritdoc />
    public long Frequency { get; }

    /// <inheritdoc />
    public long Ticks { get; private set; }

    /// <summary>Сдвигает счётчик вперёд на <paramref name="ticks"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Сдвиг отрицателен.</exception>
    public void Advance(long ticks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ticks);
        Ticks += ticks;
    }

    /// <summary>Сдвигает счётчик вперёд на интервал (с округлением вниз до тика).</summary>
    public void Advance(TimeSpan delta)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delta, TimeSpan.Zero);
        Advance((long)((double)delta.Ticks / TimeSpan.TicksPerSecond * Frequency));
    }
}
