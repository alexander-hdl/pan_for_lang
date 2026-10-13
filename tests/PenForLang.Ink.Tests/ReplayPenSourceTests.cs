using System.Collections.Immutable;
using PenForLang.Testing;
using static PenForLang.Ink.Tests.TestData;

namespace PenForLang.Ink.Tests;

public class ReplayPenSourceTests
{
    private static readonly TimeSpan Millisecond = TimeSpan.FromMilliseconds(1);

    private static long ElapsedUs(FakeClock clock, DateTimeOffset start) => (clock.UtcNow - start).Ticks / 10;

    /// <summary>Лог из трёх штрихов по 200 Гц с событиями; времена не кратны миллисекунде.</summary>
    private static PenLog TwoHundredHertzLog()
    {
        var entries = new List<object> { Event(0, "stimulus-start"), Event(640_000, "stimulus-end") };
        entries.AddRange(Stroke(1_000_123, 40).Cast<object>());
        entries.AddRange(Stroke(1_700_777, 25, x0: 400).Cast<object>());
        entries.Add(Event(2_400_000, "completed", ("by", "done-button")));
        return Log(FullHeader(), [.. entries]);
    }

    [Fact]
    public async Task Each_record_is_emitted_within_one_millisecond_of_its_log_time_on_a_one_ms_clock()
    {
        PenLog log = TwoHundredHertzLog();
        var clock = new FakeClock();
        var source = new ReplayPenSource(log, clock);
        var emittedAt = new List<(long LogTimeUs, long ClockUs)>();
        DateTimeOffset start = clock.UtcNow;
        source.SamplesReceived += batch =>
        {
            foreach (PenSample s in batch.Span)
            {
                emittedAt.Add((s.TimeUs, ElapsedUs(clock, start)));
            }
        };
        source.LogEventRaised += ev => emittedAt.Add((ev.TimeUs, ElapsedUs(clock, start)));

        await source.StartAsync(CancellationToken.None);
        for (int ms = 0; ms <= 3000; ms++)
        {
            source.Poll();
            clock.Advance(Millisecond);
        }

        Assert.True(source.IsCompleted);
        Assert.Equal(log.Entries.Length, emittedAt.Count);
        Assert.All(emittedAt, e =>
        {
            long lateness = e.ClockUs - e.LogTimeUs;
            Assert.InRange(lateness, 0, 999);
        });
    }

    [Fact]
    public async Task RunToEnd_with_a_clock_advancing_delay_emits_every_record_exactly_on_time_and_in_order()
    {
        PenLog log = TwoHundredHertzLog();
        var clock = new FakeClock(new DateTimeOffset(2030, 5, 6, 7, 8, 9, TimeSpan.Zero)); // старт не с нуля
        var source = new ReplayPenSource(log, clock);
        DateTimeOffset start = clock.UtcNow;
        var order = new List<PenLogEntry>();
        var lateness = new List<long>();
        source.SamplesReceived += batch =>
        {
            foreach (PenSample s in batch.Span)
            {
                order.Add(new PenLogSampleEntry(s));
                lateness.Add(ElapsedUs(clock, start) - s.TimeUs);
            }
        };
        source.LogEventRaised += ev =>
        {
            order.Add(new PenLogEventEntry(ev));
            lateness.Add(ElapsedUs(clock, start) - ev.TimeUs);
        };

        await source.StartAsync(CancellationToken.None);
        await source.RunToEndAsync(
            (wait, _) =>
            {
                clock.Advance(wait);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(source.IsCompleted);
        Assert.Equal(log.Entries, order);
        Assert.All(lateness, l => Assert.Equal(0, l));
    }

    [Fact]
    public async Task Replayed_samples_assemble_into_the_same_attempt_as_the_log()
    {
        PenLog log = TwoHundredHertzLog();
        var clock = new FakeClock();
        var source = new ReplayPenSource(log, clock);
        var assembler = new StrokeAssembler();
        source.SamplesReceived += batch => assembler.Add(batch.Span);

        await source.StartAsync(CancellationToken.None);
        await source.RunToEndAsync((wait, _) =>
        {
            clock.Advance(wait);
            return ValueTask.CompletedTask;
        }, CancellationToken.None);

        ImmutableArray<InkStroke> strokes = assembler.ToStrokes();
        InkAttempt expected = log.ToInkAttempt();
        Assert.Equal(2, strokes.Length);
        for (int i = 0; i < strokes.Length; i++)
        {
            Assert.True(expected.Strokes[i].Samples.SequenceEqual(strokes[i].Samples));
        }
    }

    [Fact]
    public async Task Nothing_is_emitted_before_start_after_stop_or_until_the_time_comes()
    {
        PenLog log = Log(FullHeader(), Sample(1_000, 1, 1, PenPhase.Down), Sample(2_000, 2, 2, PenPhase.Up));
        var clock = new FakeClock();
        var source = new ReplayPenSource(log, clock);
        int batches = 0;
        source.SamplesReceived += _ => batches++;

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(0, source.Poll()); // не запущен
        Assert.Null(source.TimeUntilNext);

        await source.StartAsync(CancellationToken.None);
        Assert.Equal(0, source.Poll()); // t = 0 < 1000 мкс
        Assert.Equal(TimeSpan.FromTicks(10_000), source.TimeUntilNext);

        clock.Advance(TimeSpan.FromTicks(9_999)); // 999.9 мкс
        Assert.Equal(0, source.Poll());
        clock.Advance(TimeSpan.FromTicks(1)); // ровно 1000 мкс
        Assert.Equal(1, source.Poll());
        Assert.Equal(1, source.EmittedCount);

        await source.StopAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, source.Poll());
        Assert.False(source.IsRunning);
        Assert.Equal(1, batches);
    }

    [Fact]
    public async Task A_long_pause_between_polls_delivers_all_due_points_as_one_ordered_batch_split_by_events()
    {
        PenLog log = Log(
            Header(),
            Sample(100, 0, 0, PenPhase.Down),
            Sample(200, 1, 1),
            Event(250, "mark"),
            Sample(300, 2, 2, PenPhase.Up));
        var clock = new FakeClock();
        var source = new ReplayPenSource(log, clock);
        var trace = new List<string>();
        source.SamplesReceived += batch => trace.Add("batch:" + string.Join(",", batch.ToArray().Select(s => s.TimeUs)));
        source.LogEventRaised += ev => trace.Add("event:" + ev.Name);

        await source.StartAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(4, source.Poll());

        Assert.Equal(["batch:100,200", "event:mark", "batch:300"], trace);
        Assert.True(source.IsCompleted);
        Assert.Null(source.TimeUntilNext);
    }

    [Fact]
    public async Task Start_connects_the_device_and_restart_replays_from_the_beginning()
    {
        PenLog log = Log(FullHeader(), Sample(10, 1, 1, PenPhase.Down), Sample(20, 2, 2, PenPhase.Up));
        var clock = new FakeClock();
        await using var source = new ReplayPenSource(log, clock);
        var events = new List<PenDeviceEvent>();
        int total = 0;
        source.DeviceChanged += events.Add;
        source.SamplesReceived += b => total += b.Length;

        Assert.Equal(PenSourceKind.Replay, source.Kind);
        Assert.Equal(log.Header.Device, source.Device);

        await source.StartAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        source.Poll();
        await source.StartAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        source.Poll();

        Assert.Equal(4, total);
        Assert.Equal([PenDeviceEventKind.Connected, PenDeviceEventKind.Connected], events.Select(e => e.Kind));
        Assert.Equal(log.Header.Device, events[0].Device);
    }

    [Fact]
    public async Task Unsorted_logs_are_replayed_in_time_order_with_ties_kept_stable()
    {
        var unsorted = new PenLog(
            Header(),
            [
                new PenLogSampleEntry(Sample(300, 3, 3, PenPhase.Up)),
                new PenLogSampleEntry(Sample(100, 1, 1, PenPhase.Down)),
                new PenLogSampleEntry(Sample(200, 2, 2)),
                new PenLogSampleEntry(Sample(200, 2.5, 2.5)),
            ]);
        var clock = new FakeClock();
        var source = new ReplayPenSource(unsorted, clock);
        var xs = new List<double>();
        source.SamplesReceived += b => xs.AddRange(b.Span.ToArray().Select(s => s.X));

        await source.StartAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        source.Poll();

        Assert.Equal([1.0, 2.0, 2.5, 3.0], xs);
    }

    [Fact]
    public async Task SetSurface_is_remembered_but_does_not_change_the_points()
    {
        PenLog log = Log(Header(), Sample(0, 5, 6, PenPhase.Down));
        var clock = new FakeClock();
        var source = new ReplayPenSource(log, clock);
        PenSample? received = null;
        source.SamplesReceived += b => received = b.Span[0];

        source.SetSurface(new PenSurface(1000, 500, 800, 600));
        await source.StartAsync(CancellationToken.None);
        source.Poll();

        Assert.Equal(new PenSurface(1000, 500, 800, 600), source.Surface);
        Assert.Equal(5, received?.X);
    }

    [Fact]
    public async Task Empty_log_completes_immediately_and_cancelled_run_stops()
    {
        var clock = new FakeClock();
        var empty = new ReplayPenSource(new PenLog(Header(), []), clock);
        await empty.StartAsync(CancellationToken.None);
        Assert.True(empty.IsCompleted);
        await empty.RunToEndAsync((_, _) => ValueTask.CompletedTask, CancellationToken.None);

        var source = new ReplayPenSource(Log(Header(), Sample(1_000_000, 0, 0)), clock);
        await source.StartAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await source.RunToEndAsync((_, _) => ValueTask.CompletedTask, cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await source.StartAsync(cts.Token));
    }

    [Fact]
    public async Task Clock_moved_backwards_does_not_emit_and_does_not_throw()
    {
        PenLog log = Log(Header(), Sample(0, 0, 0, PenPhase.Down), Sample(5_000, 1, 1, PenPhase.Up));
        var clock = new FakeClock();
        var source = new ReplayPenSource(log, clock);
        await source.StartAsync(CancellationToken.None);
        Assert.Equal(1, source.Poll());

        clock.Set(clock.UtcNow - TimeSpan.FromSeconds(1));

        Assert.Equal(0, source.Poll());
        Assert.Equal(TimeSpan.FromMilliseconds(1005), source.TimeUntilNext);
    }

    [Fact]
    public async Task StopAsync_from_a_sample_handler_interrupts_the_current_poll()
    {
        PenLog log = Log(
            Header(),
            Sample(100, 0, 0, PenPhase.Down),
            Event(150, "mark"),
            Sample(200, 1, 1),
            Sample(300, 2, 2, PenPhase.Up));
        var clock = new FakeClock();
        var source = new ReplayPenSource(log, clock);
        var received = new List<long>();
        var events = new List<string>();
        source.SamplesReceived += batch =>
        {
            received.AddRange(batch.ToArray().Select(s => s.TimeUs));
            source.StopAsync().AsTask().GetAwaiter().GetResult();
        };
        source.LogEventRaised += ev => events.Add(ev.Name);

        await source.StartAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        int emitted = source.Poll();

        Assert.Equal([100L], received);
        Assert.Empty(events); // событие после остановки не доставлено
        Assert.Equal(1, emitted);
        Assert.False(source.IsRunning);
        Assert.Equal(0, source.Poll());
    }

    [Fact]
    public async Task StopAsync_from_an_event_handler_stops_before_the_remaining_samples()
    {
        PenLog log = Log(Header(), Event(100, "stop-here"), Sample(200, 1, 1, PenPhase.Down), Sample(300, 2, 2, PenPhase.Up));
        var clock = new FakeClock();
        var source = new ReplayPenSource(log, clock);
        int batches = 0;
        source.SamplesReceived += _ => batches++;
        source.LogEventRaised += _ => source.StopAsync().AsTask().GetAwaiter().GetResult();

        await source.StartAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        int emitted = source.Poll();

        Assert.Equal(1, emitted);
        Assert.Equal(0, batches);
    }

    [Fact]
    public void Replay_declares_task_relative_time()
    {
        var source = new ReplayPenSource(new PenLog(Header(), []), new FakeClock());

        Assert.Equal(PenTimeBase.TaskRelative, source.TimeBase);
    }

    [Fact]
    public void Constructor_rejects_nulls()
    {
        Assert.Throws<ArgumentNullException>(() => new ReplayPenSource(null!, new FakeClock()));
        Assert.Throws<ArgumentNullException>(() => new ReplayPenSource(new PenLog(Header(), []), null!));
    }
}
