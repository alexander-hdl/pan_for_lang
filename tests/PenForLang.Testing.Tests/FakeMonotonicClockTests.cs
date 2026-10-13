using PenForLang.Core;

namespace PenForLang.Testing.Tests;

public class FakeMonotonicClockTests
{
    [Fact]
    public void Ticks_advance_only_on_request_and_start_near_a_month_of_uptime_by_default()
    {
        var clock = new FakeMonotonicClock();

        Assert.Equal(FakeMonotonicClock.DefaultStartTicks, clock.Ticks);
        Assert.Equal(clock.Ticks, clock.Ticks);
        clock.Advance(25);
        Assert.Equal(FakeMonotonicClock.DefaultStartTicks + 25, clock.Ticks);
        Assert.Equal(FakeMonotonicClock.DefaultFrequency, clock.Frequency);
        Assert.True(clock.Ticks > 9_223_372_036_854_775_807L / 1_000_000); // наивное ticks * 1e6 здесь переполнилось бы
    }

    [Fact]
    public void Advance_by_timespan_follows_the_frequency()
    {
        var clock = new FakeMonotonicClock(0, 1000);

        clock.Advance(TimeSpan.FromSeconds(2.5));

        Assert.Equal(2500, clock.Ticks);
        Assert.Equal(2_500_000, MonotonicTime.ToMicroseconds(clock.Ticks, clock.Frequency));
    }

    [Fact]
    public void Invalid_arguments_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FakeMonotonicClock(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FakeMonotonicClock(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FakeMonotonicClock().Advance(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FakeMonotonicClock().Advance(TimeSpan.FromTicks(-1)));
    }
}
