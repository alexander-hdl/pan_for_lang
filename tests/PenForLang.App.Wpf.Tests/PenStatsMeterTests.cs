using PenForLang.Ink;

namespace PenForLang.App.Wpf.Tests;

public class PenStatsMeterTests
{
    private static PenSample Sample(long timeUs, PenPhase phase = PenPhase.Move, uint id = 1, float pressure = 0.5f, SampleFlags flags = SampleFlags.None) =>
        new(timeUs, 10, 20, pressure, 0, 0, phase, PenButtons.None, flags, id);

    [Fact]
    public void Steady_200_hz_stream_is_measured_as_200_hz_with_no_gaps()
    {
        var meter = new PenStatsMeter();
        for (int i = 0; i < 400; i++)
        {
            meter.Add(Sample(5_000_000_000L + (i * 5_000L)));
        }

        PenStats stats = meter.Snapshot();

        Assert.InRange(stats.RateHz, 199.0, 201.0);
        Assert.Equal(5.0, stats.MedianDtMs, precision: 3);
        Assert.Equal(5.0, stats.MaxDtMs, precision: 3);
        Assert.Equal(0, stats.GapsOverThreshold);
        Assert.Equal(400, stats.TotalSamples);
    }

    [Fact]
    public void Lost_reports_show_up_as_gaps_and_lower_rate()
    {
        var meter = new PenStatsMeter();
        long t = 1_000_000;
        for (int i = 0; i < 200; i++)
        {
            // Каждый 10-й отчёт теряется: интервал 10 мс вместо 5; каждый 50-й теряется дважды: 15 мс.
            t += i % 50 == 49 ? 15_000 : (i % 10 == 9 ? 10_000 : 5_000);
            meter.Add(Sample(t));
        }

        PenStats stats = meter.Snapshot();

        Assert.Equal(4, stats.GapsOverThreshold); // только интервалы 15 мс превышают порог 12 мс
        Assert.Equal(15.0, stats.MaxDtMs, precision: 3);
        Assert.InRange(stats.RateHz, 150.0, 195.0);
    }

    [Fact]
    public void Long_pauses_and_pointer_changes_are_not_counted_as_gaps()
    {
        var meter = new PenStatsMeter();
        meter.Add(Sample(1_000_000, id: 1));
        meter.Add(Sample(1_005_000, id: 1));
        meter.Add(Sample(3_000_000, id: 1)); // пауза 2 с: перо вышло из зоны
        meter.Add(Sample(3_005_000, id: 2)); // другой указатель

        Assert.Equal(0, meter.Snapshot().GapsOverThreshold);
    }

    [Fact]
    public void Counters_and_last_values_track_the_stream()
    {
        var meter = new PenStatsMeter();
        meter.Add(Sample(1_000, PenPhase.Hover, pressure: 0f));
        meter.Add(Sample(2_000, PenPhase.Down, pressure: 0.3f));
        meter.Add(Sample(3_000, PenPhase.Move, pressure: 0.9f, flags: SampleFlags.Coalesced));
        meter.Add(Sample(4_000, PenPhase.Up, pressure: 0.1f, flags: SampleFlags.Clipped));

        PenStats stats = meter.Snapshot();

        Assert.Equal(4, stats.TotalSamples);
        Assert.Equal(3, stats.ContactSamples);
        Assert.Equal(1, stats.Strokes);
        Assert.Equal(1, stats.CoalescedSamples);
        Assert.Equal(1, stats.ClippedSamples);
        Assert.Equal(0.9f, stats.MaxPressure);
        Assert.Equal(PenPhase.Up, stats.LastPhase);
        Assert.Equal(0.1f, stats.LastPressure);
    }

    [Fact]
    public void Empty_meter_reports_zeros()
    {
        PenStats stats = new PenStatsMeter().Snapshot();

        Assert.Equal(0, stats.RateHz);
        Assert.Equal(0, stats.MedianDtMs);
        Assert.Equal(0, stats.TotalSamples);
    }
}
