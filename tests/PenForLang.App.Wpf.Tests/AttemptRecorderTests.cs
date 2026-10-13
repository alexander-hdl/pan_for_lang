using System.IO;
using System.IO.Compression;
using PenForLang.Core;
using PenForLang.Ink;
using PenForLang.Platform.Windows;
using PenForLang.Testing;

namespace PenForLang.App.Wpf.Tests;

/// <summary>Временная папка, удаляемая после теста.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pfl-app-tests-" + Guid.NewGuid().ToString("N"));
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

internal static class RecorderTestSupport
{
    public static readonly StyleId Style = new StyleId("zaner-bloser");

    public static GuideFrame Guides() => new(600, 540, 90, 7.5, 3.0, null, Style);

    public static PenLogHeader Header(PenSubjectKind kind = PenSubjectKind.Adult, PenLogConsent? consent = null) => new(
        "0.1.0",
        "10.0.19045",
        new PenDeviceInfo("Test pen", PenSourceKind.Win32Pointer, 200, null, 1024, true, PenTimestampKind.Qpc),
        new PenScreenInfo(1920, 1080, 256, 144, 1.5, 7.5, PxPerMmSource.Default),
        Guides(),
        new PenLogTask("sandbox-001", "SANDBOX", "glyph:a", "L0", "sandbox", "right"),
        new PenLogSubject("dev", null, kind),
        consent,
        10_000_000);

    public static long Us(FakeMonotonicClock clock) => MonotonicTime.ToMicroseconds(clock.Ticks, clock.Frequency);

    public static PenSample Sample(long absoluteUs, double x, double y, PenPhase phase, float pressure = 0.5f) =>
        new(absoluteUs, x, y, pressure, 0, 0, phase, PenButtons.None, SampleFlags.None, 1);

    public static PenLog Read(string path)
    {
        using var file = File.OpenRead(path);
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        return PenLogReader.Read(gz);
    }
}

public class AttemptRecorderTests
{
    [Fact]
    public void Attempt_is_written_as_gzip_penlog_that_reads_back_with_rebased_time_and_events()
    {
        using var dir = new TempDirectory();
        var clock = new FakeMonotonicClock();
        using var recorder = new AttemptRecorder(dir.Path, clock);

        recorder.Begin(() => RecorderTestSupport.Header(), "20261014-120000-a-001", "glyph:a");
        Assert.True(recorder.IsActive);
        Assert.Null(recorder.CurrentPath); // файла нет до первого касания

        clock.Advance(TimeSpan.FromMilliseconds(100));
        recorder.RecordEvent(InkEvent.Names.StimulusStart, new Dictionary<string, string> { ["key"] = "glyph/a" });
        clock.Advance(TimeSpan.FromMilliseconds(400));
        recorder.RecordEvent(InkEvent.Names.StimulusEnd);

        long t0 = RecorderTestSupport.Us(clock);
        recorder.AddSamples(
        [
            RecorderTestSupport.Sample(t0 + 1000, 100, 200, PenPhase.Hover, 0),
            RecorderTestSupport.Sample(t0 + 5000, 100, 200, PenPhase.Down),
            RecorderTestSupport.Sample(t0 + 10000, 110, 205, PenPhase.Move),
            RecorderTestSupport.Sample(t0 + 15000, 120, 210, PenPhase.Up, 0),
        ]);
        Assert.NotNull(recorder.CurrentPath);
        Assert.Equal(3, recorder.SamplesWritten); // наведение не пишется

        clock.Advance(TimeSpan.FromMilliseconds(300));
        recorder.Restart();
        clock.Advance(TimeSpan.FromMilliseconds(50));
        RecordedAttempt? result = recorder.Complete("done-button");

        Assert.NotNull(result);
        Assert.False(recorder.IsActive);
        Assert.EndsWith(".pfl.jsonl.gz", result.Path, StringComparison.Ordinal);
        Assert.Equal(3, result.Samples);
        Assert.Equal(1, result.Restarts);
        Assert.Equal("glyph:a", result.Target);

        PenLog log = RecorderTestSupport.Read(result.Path);
        Assert.Equal("glyph:a", log.Header.Task!.Target);
        Assert.Equal(PenSubjectKind.Adult, log.Header.Subject!.Kind);
        Assert.Null(log.Header.Consent);

        // События стимула, записанные до первого касания, попали в файл первыми и в порядке времени.
        Assert.Equal(
            [InkEvent.Names.StimulusStart, InkEvent.Names.StimulusEnd, InkEvent.Names.RestartButton, InkEvent.Names.Completed],
            log.Events.Select(e => e.Name));
        Assert.InRange(log.Events.First().TimeUs, 99_000, 101_000); // 100 мс от начала задания
        long[] times = [.. log.Entries.Select(e => e.TimeUs)];
        Assert.Equal(times.Order(), times);

        PenSample[] samples = [.. log.Samples];
        Assert.Equal(3, samples.Length);
        Assert.InRange(samples[0].TimeUs, 504_000, 506_000); // 500 мс от Begin до t0 + 5 мс
        Assert.Equal(PenPhase.Down, samples[0].Phase);
        Assert.Equal(PenPhase.Up, samples[2].Phase);

        InkAttempt attempt = log.ToInkAttempt();
        Assert.Equal(InkCompletion.DoneButton, attempt.Completion);
        Assert.Equal(1, attempt.Restarts);
        Assert.InRange(attempt.StimulusEndUs!.Value, 499_000, 501_000);
    }

    [Fact]
    public void Attempt_without_touches_leaves_no_file()
    {
        using var dir = new TempDirectory();
        var clock = new FakeMonotonicClock();
        var recorder = new AttemptRecorder(dir.Path, clock);

        recorder.Begin(() => RecorderTestSupport.Header(), "x", "glyph:a");
        recorder.RecordEvent(InkEvent.Names.StimulusStart);
        recorder.AddSamples([RecorderTestSupport.Sample(RecorderTestSupport.Us(clock) + 1000, 1, 1, PenPhase.Hover, 0)]);

        Assert.Null(recorder.Complete("done-button"));
        Assert.False(Directory.Exists(dir.Path) && Directory.EnumerateFiles(dir.Path).Any());
    }

    [Fact]
    public void Header_is_requested_at_the_first_touch_and_missing_header_postpones_recording()
    {
        using var dir = new TempDirectory();
        var clock = new FakeMonotonicClock();
        using var recorder = new AttemptRecorder(dir.Path, clock);
        PenLogHeader? header = null;
        int calls = 0;
        recorder.Begin(
            () =>
            {
                calls++;
                return header;
            },
            "x",
            "glyph:a");

        long t0 = RecorderTestSupport.Us(clock);
        recorder.AddSamples([RecorderTestSupport.Sample(t0 + 1000, 1, 1, PenPhase.Down)]);
        Assert.Equal(1, calls);
        Assert.Null(recorder.CurrentPath);
        Assert.Equal(0, recorder.SamplesWritten);

        header = RecorderTestSupport.Header();
        recorder.AddSamples([RecorderTestSupport.Sample(t0 + 6000, 2, 2, PenPhase.Move)]);
        Assert.NotNull(recorder.CurrentPath);
        Assert.Equal(1, recorder.SamplesWritten);
    }

    [Fact]
    public void Samples_older_than_the_attempt_start_are_dropped_and_skewed_times_never_go_backwards()
    {
        using var dir = new TempDirectory();
        var clock = new FakeMonotonicClock();
        using var recorder = new AttemptRecorder(dir.Path, clock);
        long beforeBegin = RecorderTestSupport.Us(clock) - 5000;
        recorder.Begin(() => RecorderTestSupport.Header(), "x", "glyph:a");

        clock.Advance(TimeSpan.FromMilliseconds(50));
        recorder.RecordEvent("mark"); // событие «сейчас» = 50 мс
        long t0 = RecorderTestSupport.Us(clock);
        recorder.AddSamples(
        [
            RecorderTestSupport.Sample(beforeBegin, 1, 1, PenPhase.Down),
            RecorderTestSupport.Sample(t0 - 3000, 1, 1, PenPhase.Down), // метка устройства на 3 мс раньше события
            RecorderTestSupport.Sample(t0 + 2000, 2, 2, PenPhase.Up, 0),
        ]);
        RecordedAttempt? result = recorder.Complete("done-button");

        Assert.NotNull(result);
        Assert.Equal(2, result.Samples);
        PenLog log = RecorderTestSupport.Read(result.Path);
        long[] times = [.. log.Entries.Select(e => e.TimeUs)];
        Assert.Equal(times.Order(), times);
    }

    [Fact]
    public void Same_name_gets_a_numeric_suffix()
    {
        using var dir = new TempDirectory();
        var clock = new FakeMonotonicClock();
        using var recorder = new AttemptRecorder(dir.Path, clock);
        string?[] paths = new string?[2];
        for (int i = 0; i < 2; i++)
        {
            recorder.Begin(() => RecorderTestSupport.Header(), "same-name", "glyph:a");
            recorder.AddSamples([RecorderTestSupport.Sample(RecorderTestSupport.Us(clock) + 1000, 1, 1, PenPhase.Down)]);
            paths[i] = recorder.Complete("done-button")?.Path;
        }

        Assert.NotNull(paths[0]);
        Assert.NotNull(paths[1]);
        Assert.NotEqual(paths[0], paths[1]);
        Assert.EndsWith("same-name.pfl.jsonl.gz", paths[0], StringComparison.Ordinal);
        Assert.EndsWith("same-name-2.pfl.jsonl.gz", paths[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Child_recording_without_consent_is_refused_and_writes_nothing()
    {
        using var dir = new TempDirectory();
        var clock = new FakeMonotonicClock();
        using var recorder = new AttemptRecorder(dir.Path, clock);
        recorder.Begin(() => RecorderTestSupport.Header(PenSubjectKind.Child), "child", "glyph:a");

        recorder.AddSamples([RecorderTestSupport.Sample(RecorderTestSupport.Us(clock) + 1000, 1, 1, PenPhase.Down)]);

        Assert.NotNull(recorder.LastError);
        Assert.Null(recorder.CurrentPath);
        Assert.Null(recorder.Complete("done-button"));
    }

    [Fact]
    public void Unwritable_folder_is_reported_not_thrown()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(dir.Path);
        string blocker = Path.Combine(dir.Path, "file-instead-of-folder");
        File.WriteAllText(blocker, "x");
        var clock = new FakeMonotonicClock();
        using var recorder = new AttemptRecorder(Path.Combine(blocker, "sub"), clock);
        recorder.Begin(() => RecorderTestSupport.Header(), "x", "glyph:a");

        recorder.AddSamples([RecorderTestSupport.Sample(RecorderTestSupport.Us(clock) + 1000, 1, 1, PenPhase.Down)]);

        Assert.NotNull(recorder.LastError);
        Assert.Null(recorder.Complete("done-button"));
    }

    [Fact]
    public void Begin_closes_an_unfinished_attempt_as_cancelled()
    {
        using var dir = new TempDirectory();
        var clock = new FakeMonotonicClock();
        using var recorder = new AttemptRecorder(dir.Path, clock);
        recorder.Begin(() => RecorderTestSupport.Header(), "first", "glyph:a");
        recorder.AddSamples([RecorderTestSupport.Sample(RecorderTestSupport.Us(clock) + 1000, 1, 1, PenPhase.Down)]);
        string path = recorder.CurrentPath!;

        recorder.Begin(() => RecorderTestSupport.Header(), "second", "glyph:i");

        PenLog log = RecorderTestSupport.Read(path);
        Assert.Equal(InkCompletion.Cancelled, log.ToInkAttempt().Completion);
        Assert.Equal("cancelled", log.Events.Last().Data!["by"]);
    }
}
