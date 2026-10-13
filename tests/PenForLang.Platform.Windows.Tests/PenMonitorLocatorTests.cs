namespace PenForLang.Platform.Windows.Tests;

public class PenMonitorLocatorTests
{
    [Fact]
    public void Monitor_enumeration_returns_sane_values_when_the_session_has_a_display()
    {
        IReadOnlyList<MonitorDescription> monitors = PenMonitorLocator.EnumerateMonitors();
        if (monitors.Count == 0)
        {
            Assert.Skip("The session has no display (headless runner).");
        }

        Assert.All(monitors, m =>
        {
            Assert.True(m.BoundsPx.Width > 0 && m.BoundsPx.Height > 0);
            Assert.False(string.IsNullOrEmpty(m.DeviceName));
            Assert.True(m.DpiScale >= 1.0);
        });
        Assert.Contains(monitors, m => m.IsPrimary);
    }

    [Fact]
    public void Locate_always_picks_a_monitor_when_one_exists_and_never_throws()
    {
        PenMonitorChoice choice = PenMonitorLocator.Locate();

        if (PenMonitorLocator.EnumerateMonitors().Count == 0)
        {
            Assert.Null(choice.Monitor);
            return;
        }

        Assert.NotNull(choice.Monitor);
    }

    [Fact]
    public void Pointer_device_enumeration_does_not_throw()
    {
        IReadOnlyList<PointerDeviceDescription> devices = PenMonitorLocator.EnumeratePointerDevices();

        Assert.NotNull(devices);
    }
}
