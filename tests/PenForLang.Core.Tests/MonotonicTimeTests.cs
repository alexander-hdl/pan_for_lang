namespace PenForLang.Core.Tests;

public class MonotonicTimeTests
{
    private const long TenMhz = 10_000_000;

    [Theory]
    [InlineData(0, TenMhz, 0)]
    [InlineData(10, TenMhz, 1)]
    [InlineData(9, TenMhz, 0)]
    [InlineData(TenMhz, TenMhz, 1_000_000)]
    [InlineData(2_000_000, 3_000_000, 666_666)]
    public void Small_values_convert_exactly_rounding_down(long ticks, long frequency, long expectedUs)
    {
        Assert.Equal(expectedUs, MonotonicTime.ToMicroseconds(ticks, frequency));
    }

    [Fact]
    public void Thirty_days_of_uptime_at_10_MHz_does_not_overflow_and_naive_formula_does()
    {
        long ticks = 30L * 24 * 3600 * TenMhz + 1234; // 2.592e13 тиков
        long expectedUs = (30L * 24 * 3600 * 1_000_000) + 123; // 1234 тика = 123.4 мкс

        Assert.Equal(expectedUs, MonotonicTime.ToMicroseconds(ticks, TenMhz));

        // Контроль: наивная формула здесь переполняется, т.е. тест действительно ловит ошибку.
        long naive = unchecked(ticks * 1_000_000 / TenMhz);
        Assert.NotEqual(expectedUs, naive);
    }

    [Fact]
    public void Elapsed_microseconds_uses_tick_difference_so_large_origins_are_harmless()
    {
        long origin = (30L * 24 * 3600 * TenMhz) + 7;
        long now = origin + 2_500_000; // 250 мс

        Assert.Equal(250_000, MonotonicTime.ElapsedMicroseconds(now, origin, TenMhz));
        Assert.Equal(0, MonotonicTime.ElapsedMicroseconds(origin, origin, TenMhz));
    }

    [Fact]
    public void Extreme_tick_counts_and_frequencies_are_handled()
    {
        // 3 ГГц (TSC-подобная частота), 1 год аптайма.
        const long Ghz3 = 3_000_000_000;
        long ticks = 365L * 24 * 3600 * Ghz3;

        Assert.Equal(365L * 24 * 3600 * 1_000_000, MonotonicTime.ToMicroseconds(ticks, Ghz3));
        Assert.Equal(1, MonotonicTime.ToMicroseconds(1, 1) / 1_000_000);
    }

    [Fact]
    public void Invalid_arguments_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MonotonicTime.ToMicroseconds(-1, TenMhz));
        Assert.Throws<ArgumentOutOfRangeException>(() => MonotonicTime.ToMicroseconds(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => MonotonicTime.ToMicroseconds(1, -5));
        Assert.Throws<ArgumentOutOfRangeException>(() => MonotonicTime.ElapsedMicroseconds(5, 6, TenMhz));
    }
}
