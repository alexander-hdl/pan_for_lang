namespace PenForLang.App.Wpf.Tests;

public class SystemClockTests
{
    [Fact]
    public void UtcNow_is_close_to_the_real_time_and_in_utc()
    {
        var clock = new SystemClock();

        DateTimeOffset before = DateTimeOffset.UtcNow;
        DateTimeOffset value = clock.UtcNow;
        DateTimeOffset after = DateTimeOffset.UtcNow;

        Assert.InRange(value, before, after);
        Assert.Equal(TimeSpan.Zero, value.Offset);
    }
}
