namespace PenForLang.Platform.Windows.Tests;

public class PenMonitorSelectorTests
{
    private static MonitorDescription Monitor(
        int handle,
        int left,
        int width,
        int height,
        bool primary = false,
        double? mmW = null,
        double? mmH = null,
        double dpi = 1.0) =>
        new(handle, $@"\\.\DISPLAY{handle}", new MonitorRect(left, 0, left + width, height), primary, mmW, mmH, dpi);

    private static PointerDeviceDescription Device(int handle, PointerDeviceKind kind, int monitorHandle, string product = "Pen") =>
        new(handle, kind, monitorHandle, product);

    private static readonly MonitorDescription Main = Monitor(1, 0, 2560, 1440, primary: true, mmW: 597, mmH: 336);
    private static readonly MonitorDescription PenPanel = Monitor(2, 2560, 1920, 1080, mmW: 256, mmH: 144, dpi: 1.5);

    [Fact]
    public void Pointer_device_wins_and_its_monitor_is_chosen()
    {
        var devices = new[]
        {
            Device(10, PointerDeviceKind.TouchPad, monitorHandle: 1, "Touchpad"),
            Device(11, PointerDeviceKind.IntegratedPen, monitorHandle: 2, "XPPen Artist"),
        };

        PenMonitorChoice choice = PenMonitorSelector.Select(devices, [Main, PenPanel]);

        Assert.Equal(PenMonitorChoiceSource.PointerDevice, choice.Source);
        Assert.Same(PenPanel, choice.Monitor);
        Assert.Equal("XPPen Artist", choice.Device!.Product);
        Assert.False(choice.NeedsParentConfirmation);
    }

    [Fact]
    public void External_pen_device_counts_as_a_pen()
    {
        PenMonitorChoice choice = PenMonitorSelector.Select(
            [Device(11, PointerDeviceKind.ExternalPen, monitorHandle: 1)], [Main, PenPanel]);

        Assert.Equal(PenMonitorChoiceSource.PointerDevice, choice.Source);
        Assert.Same(Main, choice.Monitor);
    }

    [Fact]
    public void Pen_device_wins_over_the_size_heuristic()
    {
        // Перо отображено на основной монитор, хотя рядом есть экран размером 256x144: верим устройству.
        PenMonitorChoice choice = PenMonitorSelector.Select(
            [Device(11, PointerDeviceKind.IntegratedPen, monitorHandle: 1)], [Main, PenPanel]);

        Assert.Same(Main, choice.Monitor);
        Assert.Equal(PenMonitorChoiceSource.PointerDevice, choice.Source);
    }

    [Fact]
    public void Pen_devices_mapped_to_different_monitors_are_ambiguous()
    {
        PenMonitorChoice choice = PenMonitorSelector.Select(
            [
                Device(11, PointerDeviceKind.IntegratedPen, monitorHandle: 2),
                Device(12, PointerDeviceKind.ExternalPen, monitorHandle: 1),
            ],
            [Main, PenPanel]);

        Assert.Same(PenPanel, choice.Monitor);
        Assert.Equal(2, choice.CandidateCount);
        Assert.True(choice.NeedsParentConfirmation);
    }

    [Fact]
    public void Pen_device_with_an_unknown_monitor_falls_through_to_the_size_heuristic()
    {
        PenMonitorChoice choice = PenMonitorSelector.Select(
            [Device(11, PointerDeviceKind.IntegratedPen, monitorHandle: 999)], [Main, PenPanel]);

        Assert.Equal(PenMonitorChoiceSource.PhysicalSizeHeuristic, choice.Source);
        Assert.Same(PenPanel, choice.Monitor);
    }

    [Fact]
    public void Without_pen_devices_the_256_by_144_mm_full_hd_monitor_is_chosen()
    {
        PenMonitorChoice choice = PenMonitorSelector.Select(
            [Device(10, PointerDeviceKind.Touch, monitorHandle: 1)], [Main, PenPanel]);

        Assert.Equal(PenMonitorChoiceSource.PhysicalSizeHeuristic, choice.Source);
        Assert.Same(PenPanel, choice.Monitor);
        Assert.Null(choice.Device);
    }

    [Theory]
    [InlineData(256.8, 144.5, true)] // реальная панель 11,6"
    [InlineData(265.0, 149.0, true)] // на границе допуска 4%
    [InlineData(300.0, 170.0, false)] // 13,3" ноутбук — не наш экран
    [InlineData(null, null, false)] // ОС размера не сообщила
    public void Size_heuristic_tolerance(double? widthMm, double? heightMm, bool expected)
    {
        MonitorDescription monitor = Monitor(2, 0, 1920, 1080, mmW: widthMm, mmH: heightMm);

        Assert.Equal(expected, PenMonitorSelector.MatchesPenPanelSize(monitor));
    }

    [Fact]
    public void Size_heuristic_also_requires_1920_by_1080_pixels()
    {
        MonitorDescription qhd = Monitor(2, 0, 2560, 1440, mmW: 256, mmH: 144);

        Assert.False(PenMonitorSelector.MatchesPenPanelSize(qhd));
    }

    [Fact]
    public void Size_heuristic_prefers_a_non_primary_match()
    {
        MonitorDescription primaryFullHd = Monitor(1, 0, 1920, 1080, primary: true, mmW: 256, mmH: 144);

        PenMonitorChoice choice = PenMonitorSelector.Select([], [primaryFullHd, PenPanel]);

        Assert.Same(PenPanel, choice.Monitor);
        Assert.Equal(2, choice.CandidateCount);
        Assert.True(choice.NeedsParentConfirmation);
    }

    [Fact]
    public void Otherwise_the_primary_monitor_is_chosen_and_flagged_for_parent_confirmation()
    {
        MonitorDescription other = Monitor(3, 5000, 1920, 1200, mmW: 518, mmH: 324);

        PenMonitorChoice choice = PenMonitorSelector.Select([], [other, Main]);

        Assert.Equal(PenMonitorChoiceSource.PrimaryFallback, choice.Source);
        Assert.Same(Main, choice.Monitor);
        Assert.True(choice.NeedsParentConfirmation);
    }

    [Fact]
    public void Without_a_primary_flag_the_first_monitor_is_used()
    {
        MonitorDescription a = Monitor(3, 0, 1280, 1024);
        MonitorDescription b = Monitor(4, 1280, 1280, 1024);

        PenMonitorChoice choice = PenMonitorSelector.Select([], [a, b]);

        Assert.Same(a, choice.Monitor);
    }

    [Fact]
    public void No_monitors_gives_no_choice()
    {
        PenMonitorChoice choice = PenMonitorSelector.Select([Device(1, PointerDeviceKind.IntegratedPen, 1)], []);

        Assert.Null(choice.Monitor);
        Assert.Equal(PenMonitorChoiceSource.None, choice.Source);
    }

    [Fact]
    public void Forced_monitor_index_wins_over_everything_else()
    {
        PenMonitorChoice choice = PenMonitorSelector.SelectForced(
            [Main, PenPanel], 1, [Device(11, PointerDeviceKind.IntegratedPen, monitorHandle: 2)]);

        Assert.Same(Main, choice.Monitor);
        Assert.Equal(PenMonitorChoiceSource.CommandLine, choice.Source);
        Assert.False(choice.NeedsParentConfirmation);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-1)]
    public void Forced_index_out_of_range_falls_back_to_the_automatic_choice(int index)
    {
        PenMonitorChoice choice = PenMonitorSelector.SelectForced(
            [Main, PenPanel], index, [Device(11, PointerDeviceKind.IntegratedPen, monitorHandle: 2)]);

        Assert.Same(PenPanel, choice.Monitor);
        Assert.Equal(PenMonitorChoiceSource.PointerDevice, choice.Source);
    }
}
