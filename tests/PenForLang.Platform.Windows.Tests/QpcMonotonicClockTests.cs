using PenForLang.Core;

namespace PenForLang.Platform.Windows.Tests;

public class QpcMonotonicClockTests
{
    [Fact]
    public void Frequency_is_positive_and_constant()
    {
        var clock = new QpcMonotonicClock();

        Assert.True(clock.Frequency > 0);
        Assert.Equal(clock.Frequency, new QpcMonotonicClock().Frequency);
    }

    [Fact]
    public void Ticks_never_decrease_and_convert_to_a_sane_number_of_microseconds()
    {
        var clock = new QpcMonotonicClock();
        long previous = clock.Ticks;
        for (int i = 0; i < 10_000; i++)
        {
            long now = clock.Ticks;
            Assert.True(now >= previous);
            previous = now;
        }

        Assert.True(MonotonicTime.ToMicroseconds(previous, clock.Frequency) >= 0);
    }
}
