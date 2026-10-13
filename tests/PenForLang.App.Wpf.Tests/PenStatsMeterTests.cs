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

    [Fact]
    public void Hover_and_synthetic_points_do_not_enter_the_rate()
    {
        var meter = new PenStatsMeter();
        // Наведение с интервалом 1 мс не должно «разгонять» частоту; контакт идёт с интервалом 5 мс.
        for (int i = 0; i < 300; i++)
        {
            meter.Add(Sample(1_000_000 + (i * 1_000L), PenPhase.Hover, pressure: 0f));
        }

        meter.Add(Sample(2_000_000, PenPhase.Down));
        for (int i = 1; i <= 100; i++)
        {
            meter.Add(Sample(2_000_000 + (i * 5_000L)));
            // Синтетическая точка с «сейчас»-временем между настоящими: в частоту не входит.
            meter.Add(Sample(2_000_000 + (i * 5_000L) + 1, flags: SampleFlags.InterpolatedTime));
        }

        PenStats stats = meter.Snapshot();

        Assert.InRange(stats.RateHz, 199.0, 201.0);
        Assert.Equal(5.0, stats.MedianDtMs, precision: 3);
        Assert.Equal(0, stats.GapsOverThreshold);
        Assert.Equal(300, stats.HoverSamples);
        Assert.Equal(100, stats.SyntheticTimeSamples);
    }

    [Fact]
    public void Rate_is_reported_per_stroke_with_median_and_p5()
    {
        var meter = new PenStatsMeter();

        // Штрих 1: чистые 200 Гц (интервал 5 мс).
        meter.Add(Sample(1_000_000, PenPhase.Down));
        for (int i = 1; i <= 100; i++)
        {
            meter.Add(Sample(1_000_000 + (i * 5_000L)));
        }

        meter.Add(Sample(1_505_000, PenPhase.Up, pressure: 0f));

        // Штрих 2: 200 Гц, но каждый 10-й интервал вдвое длиннее (потеря отчёта) -> p5 = 100 Гц.
        long t = 3_000_000;
        meter.Add(Sample(t, PenPhase.Down));
        for (int i = 1; i <= 100; i++)
        {
            t += i % 10 == 0 ? 10_000 : 5_000;
            meter.Add(Sample(t));
        }

        meter.Add(Sample(t + 5_000, PenPhase.Up, pressure: 0f));

        IReadOnlyList<StrokeRate> strokes = meter.Snapshot().StrokeRates;

        Assert.Equal(2, strokes.Count);
        Assert.Equal(1, strokes[0].Index);
        Assert.Equal(200.0, strokes[0].MedianHz, precision: 1);
        Assert.Equal(200.0, strokes[0].P5Hz, precision: 1);
        Assert.Equal(200.0, strokes[1].MedianHz, precision: 1);
        Assert.Equal(100.0, strokes[1].P5Hz, precision: 1);
        Assert.InRange(strokes[0].Samples, 100, 103);
    }

    [Fact]
    public void Coalesced_share_is_counted_per_stroke()
    {
        var meter = new PenStatsMeter();
        meter.Add(Sample(1_000_000, PenPhase.Down));
        for (int i = 1; i <= 10; i++)
        {
            meter.Add(Sample(1_000_000 + (i * 5_000L), flags: i <= 8 ? SampleFlags.Coalesced : SampleFlags.None));
        }

        meter.Add(Sample(1_055_000, PenPhase.Up, pressure: 0f));

        StrokeRate stroke = Assert.Single(meter.Snapshot().StrokeRates);
        Assert.InRange(stroke.CoalescedShare, 0.66, 0.68); // 8 из 12 точек контакта (Down + 10 Move + Up)
    }

    [Fact]
    public void Pressure_range_levels_tilt_and_buttons_are_tracked()
    {
        var meter = new PenStatsMeter();
        meter.Add(new PenSample(1_000, 1, 1, 0.25f, -10, 5, PenPhase.Down, PenButtons.None, SampleFlags.None, 1));
        meter.Add(new PenSample(2_000, 1, 1, 0.75f, 20, -8, PenPhase.Move, PenButtons.Barrel, SampleFlags.None, 1));
        meter.Add(new PenSample(3_000, 1, 1, 0.75f, 20, -8, PenPhase.Move, PenButtons.Eraser, SampleFlags.None, 1));
        meter.Add(new PenSample(4_000, 1, 1, 0f, 0, 0, PenPhase.Up, PenButtons.None, SampleFlags.None, 1));

        PenStats stats = meter.Snapshot();

        Assert.Equal(0.25f, stats.MinPressure);
        Assert.Equal(0.75f, stats.MaxPressure);
        Assert.Equal(2, stats.PressureLevels);
        Assert.Equal(-10f, stats.TiltXMin);
        Assert.Equal(20f, stats.TiltXMax);
        Assert.Equal(-8f, stats.TiltYMin);
        Assert.Equal(5f, stats.TiltYMax);
        Assert.Equal(1, stats.BarrelSamples);
        Assert.Equal(1, stats.EraserSamples);
    }
}
