using PenForLang.Ink;

namespace PenForLang.Testing.Tests;

public class FakePenInputSourceTests
{
    private static PenSample Sample(long t) =>
        new(t, 1, 2, 0.5f, 0, 0, PenPhase.Move, PenButtons.None, SampleFlags.None, 1);

    [Fact]
    public async Task Pushed_batches_reach_subscribers_only_while_started()
    {
        await using var source = new FakePenInputSource();
        var received = new List<PenSample>();
        source.SamplesReceived += batch => received.AddRange(batch.Span);

        Assert.False(source.Push(Sample(1)));
        await source.StartAsync(CancellationToken.None);
        Assert.True(source.Push(Sample(2), Sample(3)));
        await source.StopAsync();
        Assert.False(source.Push(Sample(4)));

        Assert.Equal([2L, 3L], received.Select(s => s.TimeUs));
        Assert.Equal(1, source.StartCount);
        Assert.Equal(1, source.StopCount);
    }

    [Fact]
    public void Device_events_and_surface_are_passed_through()
    {
        var source = new FakePenInputSource(PenSourceKind.Win32Pointer);
        PenDeviceEvent? seen = null;
        source.DeviceChanged += e => seen = e;

        source.RaiseDeviceEvent(new PenDeviceEvent(PenDeviceEventKind.Lost));
        source.SetSurface(new PenSurface(10, 20, 800, 600));

        Assert.Equal(PenDeviceEventKind.Lost, seen?.Kind);
        Assert.Equal(new PenSurface(10, 20, 800, 600), source.Surface);
        Assert.Equal(PenSourceKind.Win32Pointer, source.Kind);
    }
}
