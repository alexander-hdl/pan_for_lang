using PenForLang.Core;
using PenForLang.Testing;
using static PenForLang.Ink.Tests.TestData;

namespace PenForLang.Ink.Tests;

public class AttemptTimeRebaserTests
{
    [Fact]
    public void Rebasing_after_thirty_days_of_uptime_gives_exact_task_relative_microseconds()
    {
        var clock = new FakeMonotonicClock(); // 30 суток при 10 МГц: ticks * 1e6 переполнило бы long
        AttemptTimeRebaser rebaser = AttemptTimeRebaser.Start(clock);

        // Источник отдаёт абсолютное время: тики QPC → мкс (без переполнения), через 123,4 мс и 2,5 с после старта задания.
        clock.Advance(1_234_000);
        long first = AttemptTimeRebaser.ToAbsoluteMicroseconds(clock.Ticks, clock.Frequency);
        clock.Advance(23_766_000);
        long second = AttemptTimeRebaser.ToAbsoluteMicroseconds(clock.Ticks, clock.Frequency);

        Assert.Equal(123_400, rebaser.ToTaskTime(first));
        Assert.Equal(2_500_000, rebaser.ToTaskTime(second));
        Assert.True(first > 2_000_000_000_000); // абсолютное время действительно огромное (~30 суток в мкс)
    }

    [Fact]
    public async Task Pipeline_from_fake_source_to_assembled_attempt_uses_task_relative_time()
    {
        var clock = new FakeMonotonicClock();
        var source = new FakePenInputSource();
        AttemptTimeRebaser rebaser = AttemptTimeRebaser.Start(clock);
        var assembler = new StrokeAssembler();
        var buffer = new PenSample[16];
        source.SamplesReceived += batch =>
        {
            int n = rebaser.Rebase(batch.Span, buffer);
            assembler.Add(buffer.AsSpan(0, n));
        };
        Assert.Equal(PenTimeBase.MonotonicAbsolute, source.TimeBase);

        await source.StartAsync(CancellationToken.None);
        long Abs() => AttemptTimeRebaser.ToAbsoluteMicroseconds(clock.Ticks, clock.Frequency);
        clock.Advance(10_000);
        source.Push(Sample(Abs(), 1, 1, PenPhase.Down));
        clock.Advance(50_000);
        source.Push(Sample(Abs(), 2, 2, PenPhase.Up));

        var stroke = assembler.ToStrokes().Single();
        Assert.Equal([1_000L, 6_000L], stroke.Samples.Select(s => s.TimeUs));
    }

    [Fact]
    public void Samples_before_the_origin_are_dropped_by_batch_rebase_and_TryRebase_reports_them()
    {
        var rebaser = new AttemptTimeRebaser(1_000);
        PenSample[] source = [Sample(900, 0, 0, PenPhase.Hover), Sample(1_000, 1, 1, PenPhase.Down), Sample(1_500, 2, 2, PenPhase.Up)];
        var destination = new PenSample[3];

        int count = rebaser.Rebase(source, destination);

        Assert.Equal(2, count);
        Assert.Equal([0L, 500L], destination.Take(count).Select(s => s.TimeUs));
        Assert.False(rebaser.TryRebase(source[0], out PenSample early));
        Assert.Equal(0, early.TimeUs);
        Assert.True(rebaser.TryRebase(source[1], out _));
    }

    [Fact]
    public void Rebase_validates_arguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AttemptTimeRebaser(-1));
        Assert.Throws<ArgumentNullException>(() => AttemptTimeRebaser.Start(null!));
        Assert.Throws<ArgumentException>(() => new AttemptTimeRebaser(0).Rebase(new PenSample[2], new PenSample[1]));
    }
}
