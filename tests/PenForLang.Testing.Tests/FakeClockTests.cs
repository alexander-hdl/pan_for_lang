namespace PenForLang.Testing.Tests;

public class FakeClockTests
{
    [Fact]
    public void Time_is_fixed_until_advanced()
    {
        var clock = new FakeClock();

        Assert.Equal(FakeClock.DefaultStart, clock.UtcNow);
        Assert.Equal(clock.UtcNow, clock.UtcNow);

        clock.Advance(TimeSpan.FromMinutes(90));
        Assert.Equal(FakeClock.DefaultStart.AddMinutes(90), clock.UtcNow);
    }

    [Fact]
    public void Advance_rejects_negative_delta()
    {
        var clock = new FakeClock();
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Set_normalizes_to_utc()
    {
        var clock = new FakeClock();
        clock.Set(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.FromHours(3)));

        Assert.Equal(TimeSpan.Zero, clock.UtcNow.Offset);
        Assert.Equal(new DateTimeOffset(2026, 5, 1, 9, 0, 0, TimeSpan.Zero), clock.UtcNow);
    }
}
