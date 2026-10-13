using System.Collections.Concurrent;
using System.IO;
using PenForLang.Ink;
using PenForLang.Platform.Windows;
using PenForLang.Testing;

namespace PenForLang.App.Wpf.Tests;

/// <summary>Приёмник записи, фиксирующий потоки вызовов и держащий запись на «воротах» — чтобы доказать, что поток UI не ждёт ввода-вывода.</summary>
internal sealed class GatedSink : IAttemptLogSink
{
    private readonly ManualResetEventSlim _gate;

    public GatedSink(ManualResetEventSlim gate, ConcurrentBag<int> threads)
    {
        _gate = gate;
        Threads = threads;
    }

    public ConcurrentBag<int> Threads { get; }

    public ConcurrentQueue<PenSample> Samples { get; } = new();

    public ConcurrentQueue<string> Events { get; } = new();

    public bool Closed { get; private set; }

    public bool Discarded { get; private set; }

    public void WriteSample(in PenSample sample)
    {
        _gate.Wait();
        Threads.Add(Environment.CurrentManagedThreadId);
        Samples.Enqueue(sample);
    }

    public void WriteEvent(InkEvent ev)
    {
        _gate.Wait();
        Threads.Add(Environment.CurrentManagedThreadId);
        Events.Enqueue(ev.Name);
    }

    public void Close(bool discard)
    {
        Threads.Add(Environment.CurrentManagedThreadId);
        Closed = true;
        Discarded = discard;
    }
}

public class BackgroundWritingTests
{
    [Fact]
    public void The_ui_thread_never_waits_for_the_log_writer_and_all_io_happens_on_another_thread()
    {
        using var gate = new ManualResetEventSlim(false);
        var threads = new ConcurrentBag<int>();
        GatedSink? sink = null;
        string? openedPath = null;
        using var dir = new TempDirectory();
        var clock = new FakeMonotonicClock();
        int uiThread = Environment.CurrentManagedThreadId;

        using var recorder = new AttemptRecorder(
            dir.Path,
            clock,
            (path, header) =>
            {
                threads.Add(Environment.CurrentManagedThreadId); // открытие файла — тоже в фоне
                openedPath = path;
                sink = new GatedSink(gate, threads);
                return sink;
            });

        // Запись «зависла» (ворота закрыты): всё, что делает поток UI, обязано вернуться немедленно.
        var started = System.Diagnostics.Stopwatch.StartNew();
        recorder.Begin(() => RecorderTestSupport.Header(), "gated", "glyph:a");
        recorder.RecordEvent(InkEvent.Names.StimulusStart);
        long t0 = RecorderTestSupport.Us(clock);
        for (int i = 0; i < 200; i++)
        {
            recorder.AddSamples([RecorderTestSupport.Sample(t0 + (i * 5000L), i, i, i == 0 ? PenPhase.Down : PenPhase.Move)]);
        }

        recorder.Restart();
        RecordedAttempt? result = recorder.Complete("done-button");
        started.Stop();

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2), "the UI path must not block on the stalled writer");
        Assert.NotNull(result);
        Assert.Equal(200, result.Samples);
        Assert.DoesNotContain(uiThread, threads);
        Assert.True(sink is null || sink.Samples.IsEmpty, "nothing was written while the gate was closed");

        // Ворота открыты — фоновый поток дописывает всё.
        gate.Set();
        Assert.True(recorder.Drain(TimeSpan.FromSeconds(10)));

        Assert.NotNull(sink);
        Assert.NotNull(openedPath);
        Assert.EndsWith("gated.pfl.jsonl.gz", openedPath, StringComparison.Ordinal);
        Assert.Equal(200, sink.Samples.Count);
        Assert.Equal(
            [InkEvent.Names.StimulusStart, InkEvent.Names.RestartButton, InkEvent.Names.Completed],
            sink.Events);
        Assert.True(sink.Closed);
        Assert.False(sink.Discarded);
        Assert.DoesNotContain(uiThread, threads);
        Assert.Single(threads.Distinct().Where(t => t != uiThread).Take(2)); // один фоновый поток записи
    }

    [Fact]
    public void Samples_are_written_in_order_and_an_attempt_without_touches_is_discarded()
    {
        using var gate = new ManualResetEventSlim(true);
        var threads = new ConcurrentBag<int>();
        var sinks = new List<GatedSink>();
        using var dir = new TempDirectory();
        var clock = new FakeMonotonicClock();
        using var recorder = new AttemptRecorder(dir.Path, clock, (_, _) =>
        {
            var sink = new GatedSink(gate, threads);
            sinks.Add(sink);
            return sink;
        });

        recorder.Begin(() => RecorderTestSupport.Header(), "first", "glyph:a");
        long t0 = RecorderTestSupport.Us(clock);
        recorder.AddSamples(
        [
            RecorderTestSupport.Sample(t0 + 1000, 1, 1, PenPhase.Down),
            RecorderTestSupport.Sample(t0 + 6000, 2, 2, PenPhase.Move),
            RecorderTestSupport.Sample(t0 + 11000, 3, 3, PenPhase.Up),
        ]);
        recorder.Complete("done-button");

        recorder.Begin(() => RecorderTestSupport.Header(), "second", "glyph:a");
        Assert.Null(recorder.Complete("done-button"));
        Assert.True(recorder.Drain(TimeSpan.FromSeconds(10)));

        Assert.Equal(2, sinks.Count);
        Assert.Equal([1.0, 2.0, 3.0], sinks[0].Samples.Select(s => s.X));
        Assert.False(sinks[0].Discarded);
        Assert.True(sinks[1].Discarded, "an attempt without touches is removed");
    }

    [Fact]
    public void Dispose_waits_for_the_pending_writes_and_closes_the_attempt()
    {
        using var dir = new TempDirectory();
        var clock = new FakeMonotonicClock();
        string path;
        var recorder = new AttemptRecorder(dir.Path, clock);
        recorder.Begin(() => RecorderTestSupport.Header(), "exit", "glyph:a");
        recorder.AddSamples([RecorderTestSupport.Sample(RecorderTestSupport.Us(clock) + 1000, 1, 1, PenPhase.Down)]);
        path = recorder.CurrentPath!;

        recorder.Dispose(); // выход приложения: незавершённая попытка закрывается как отменённая, очередь дописана

        PenLog log = RecorderTestSupport.Read(path);
        Assert.Equal(InkCompletion.Cancelled, log.ToInkAttempt().Completion);
        Assert.Single(log.Samples);
    }
}

public class SandboxSessionBehaviourTests
{
    private static SandboxSession Create(TempDirectory dir, FakeMonotonicClock clock) =>
        new(clock, dir.Path, () => new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero), (_, _) => RecorderTestSupport.Header(), "a");

    [Fact]
    public void Stimulus_end_is_written_once_per_showing_also_when_the_letter_changes_or_done_is_pressed()
    {
        using var dir = new TempDirectory();
        var clock = new FakeMonotonicClock();
        using SandboxSession session = Create(dir, clock);

        session.StimulusEnded(); // без показа ничего не пишется

        session.StimulusStarted();
        session.StimulusEnded(); // доиграло или прервано касанием
        session.StimulusEnded(); // повтор игнорируется
        session.StimulusStarted();
        session.Restart();
        session.StimulusStarted(); // новый показ закрывает прежний
        long t = RecorderTestSupport.Us(clock);
        session.OnSamples([RecorderTestSupport.Sample(t + 1000, 1, 1, PenPhase.Down)]);
        RecordedAttempt? done = session.Done(); // «Готово» во время показа — stimulus-end пишется
        Assert.True(session.Drain(TimeSpan.FromSeconds(10)));

        Assert.NotNull(done);
        Assert.Equal(
            [
                InkEvent.Names.StimulusStart, InkEvent.Names.StimulusEnd,
                InkEvent.Names.StimulusStart, InkEvent.Names.RestartButton,
                InkEvent.Names.StimulusEnd, InkEvent.Names.StimulusStart,
                InkEvent.Names.StimulusEnd, InkEvent.Names.Completed,
            ],
            RecorderTestSupport.Read(done.Path).Events.Select(e => e.Name));
    }

    [Fact]
    public void Switching_the_letter_during_a_showing_writes_the_stimulus_end_into_the_cancelled_attempt()
    {
        using var dir = new TempDirectory();
        var clock = new FakeMonotonicClock();
        using SandboxSession session = Create(dir, clock);
        session.StimulusStarted();
        session.OnSamples([RecorderTestSupport.Sample(RecorderTestSupport.Us(clock) + 1000, 1, 1, PenPhase.Down)]);

        session.SelectLetter("t");
        Assert.True(session.Drain(TimeSpan.FromSeconds(10)));

        RecordedAttempt cancelled = Assert.Single(session.Recorded);
        Assert.Equal(
            [InkEvent.Names.StimulusStart, InkEvent.Names.StimulusEnd, InkEvent.Names.Completed],
            RecorderTestSupport.Read(cancelled.Path).Events.Select(e => e.Name));
    }

    [Fact]
    public void Cancel_attempt_closes_the_recording_as_cancelled_and_starts_a_fresh_one()
    {
        using var dir = new TempDirectory();
        var clock = new FakeMonotonicClock();
        using SandboxSession session = Create(dir, clock);
        session.OnSamples([RecorderTestSupport.Sample(RecorderTestSupport.Us(clock) + 1000, 1, 1, PenPhase.Down)]);
        session.Restart();

        session.CancelAttempt();

        Assert.Equal(1, session.AttemptNumber);
        Assert.Equal(0, session.Restarts);
        RecordedAttempt cancelled = Assert.Single(session.Recorded);
        Assert.Equal("cancelled", cancelled.CompletedBy);
        Assert.Null(session.CurrentPath);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void Pen_targets_and_gaps_are_sized_in_millimetres_through_px_per_mm(double dpi)
    {
        const double pxPerMm = 7.5;
        double target = SandboxLayout.MmToDip(SandboxLayout.TargetMinMm, pxPerMm, dpi);
        double gap = SandboxLayout.MmToDip(SandboxLayout.TargetGapMm, pxPerMm, dpi);

        Assert.Equal(12.0 * pxPerMm, target * dpi, 9); // 90 физических px при любом масштабе
        Assert.Equal(4.0 * pxPerMm, gap * dpi, 9);
        Assert.True(SandboxLayout.TargetMinMm >= 12.0 && SandboxLayout.TargetGapMm >= 4.0);
    }
}
