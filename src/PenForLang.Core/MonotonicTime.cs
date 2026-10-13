namespace PenForLang.Core;

/// <summary>
/// Чистые функции перевода тиков монотонных часов в микросекунды без переполнения.
/// Наивное <c>ticks * 1_000_000 / frequency</c> переполняет <see cref="long"/> уже при ~9.2e12 тиков
/// (при 10 МГц это около 10 суток аптайма); здесь целая и дробная части секунды считаются раздельно.
/// </summary>
public static class MonotonicTime
{
    private const long MicrosPerSecond = 1_000_000;

    /// <summary>Тики → микросекунды (округление вниз) для неотрицательных тиков.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Тики отрицательны или частота не положительна.</exception>
    public static long ToMicroseconds(long ticks, long frequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ticks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        long seconds = ticks / frequency;
        long remainder = ticks % frequency;

        // 128-битное умножение: remainder * 1e6 не должно переполняться ни при какой частоте.
        long fractionUs = (long)(((UInt128)(ulong)remainder * MicrosPerSecond) / (ulong)frequency);
        return checked((seconds * MicrosPerSecond) + fractionUs);
    }

    /// <summary>
    /// Микросекунды между <paramref name="originTicks"/> и <paramref name="ticks"/> (округление вниз). Разность берётся в тиках,
    /// поэтому большие абсолютные значения счётчика (недели аптайма) не влияют на точность.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ticks"/> раньше <paramref name="originTicks"/> или частота не положительна.</exception>
    public static long ElapsedMicroseconds(long ticks, long originTicks, long frequency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ticks, originTicks);
        return ToMicroseconds(ticks - originTicks, frequency);
    }
}
