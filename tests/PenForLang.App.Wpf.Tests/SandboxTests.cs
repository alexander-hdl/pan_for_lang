using System.IO;
using PenForLang.Core;
using PenForLang.Glyphs.Animation;
using PenForLang.Ink;
using PenForLang.Platform.Windows;
using PenForLang.Testing;
using PenForLang.Wpf.Controls;

namespace PenForLang.App.Wpf.Tests;

public class SandboxSessionTests
{
    private static SandboxSession Create(TempDirectory? dir, FakeMonotonicClock clock, string letter = "a") =>
        new(clock, dir?.Path, () => new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero), (_, attempt) => RecorderTestSupport.Header(), letter);

    private static void Touch(SandboxSession session, FakeMonotonicClock clock, int points = 5)
    {
        long t0 = RecorderTestSupport.Us(clock);
        var samples = new PenSample[points];
        for (int i = 0; i < points; i++)
        {
            PenPhase phase = i == 0 ? PenPhase.Down : (i == points - 1 ? PenPhase.Up : PenPhase.Move);
            samples[i] = RecorderTestSupport.Sample(t0 + ((i + 1) * 5000L), 100 + (i * 10), 200, phase);
        }

        session.OnSamples(samples);
        clock.Advance(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public void Done_saves_the_attempt_and_starts_the_next_one()
    {
        using var dir = new TempDirectory();
        var clock = new FakeMonotonicClock();
        using SandboxSession session = Create(dir, clock);
        Assert.Equal(1, session.AttemptNumber);
        Assert.Equal("glyph:a", session.Target);

        session.StimulusStarted();
        clock.Advance(TimeSpan.FromMilliseconds(200));
        session.StimulusEnded();
        Touch(session, clock);
        session.Restart();
        session.Restart();
        Assert.Equal(2, session.Restarts);
        Touch(session, clock, 8);

        RecordedAttempt? first = session.Done();

        Assert.NotNull(first);
        Assert.Equal(2, first.Restarts);
        Assert.Equal(13, first.Samples);
        Assert.Equal("done-button", first.CompletedBy);
        Assert.Equal(2, session.AttemptNumber);
        Assert.Equal(0, session.Restarts);
        Assert.Null(session.CurrentPath);
        Assert.Single(session.Recorded);

        PenLog log = RecorderTestSupport.Read(first.Path);
        InkAttempt attempt = log.ToInkAttempt();
        Assert.Equal(2, attempt.Restarts);
        Assert.Equal(InkCompletion.DoneButton, attempt.Completion);
        Assert.Single(attempt.Strokes); // штрихи до последнего «Заново» отброшены — отменённая попытка
        Assert.Equal(
            [InkEvent.Names.StimulusStart, InkEvent.Names.StimulusEnd, InkEvent.Names.RestartButton, InkEvent.Names.RestartButton, InkEvent.Names.Completed],
            log.Events.Select(e => e.Name));

        // Вторая попытка пишется в другой файл.
        Touch(session, clock);
        RecordedAttempt? second = session.Done();
        Assert.NotNull(second);
        Assert.NotEqual(first.Path, second.Path);
        Assert.Equal(2, session.Recorded.Count);
    }

    [Fact]
    public void Switching_the_letter_cancels_the_current_attempt_without_changing_the_attempt_number()
    {
        using var dir = new TempDirectory();
        var clock = new FakeMonotonicClock();
        using SandboxSession session = Create(dir, clock);
        Touch(session, clock);

        session.SelectLetter("t");

        Assert.Equal("t", session.Letter);
        Assert.Equal("glyph:t", session.Target);
        Assert.Equal(1, session.AttemptNumber);
        RecordedAttempt cancelled = Assert.Single(session.Recorded);
        Assert.Equal("cancelled", cancelled.CompletedBy);
        Assert.Equal("glyph:a", cancelled.Target);

        session.SelectLetter("t"); // та же буква — ничего не меняется
        Assert.Single(session.Recorded);
    }

    [Fact]
    public void Without_a_penlog_folder_nothing_is_recorded_but_counters_work()
    {
        var clock = new FakeMonotonicClock();
        using SandboxSession session = Create(null, clock);
        Assert.False(session.IsRecording);

        Touch(session, clock);
        session.StimulusStarted();
        session.Restart();
        Assert.Equal(1, session.Restarts);

        Assert.Null(session.Done());
        Assert.Equal(2, session.AttemptNumber);
        Assert.Empty(session.Recorded);
    }

    [Fact]
    public void Close_writes_an_unfinished_attempt_as_cancelled()
    {
        using var dir = new TempDirectory();
        var clock = new FakeMonotonicClock();
        SandboxSession session = Create(dir, clock);
        Touch(session, clock);

        session.Close();

        RecordedAttempt recorded = Assert.Single(session.Recorded);
        Assert.Equal("cancelled", recorded.CompletedBy);
        Assert.Equal(InkCompletion.Cancelled, RecorderTestSupport.Read(recorded.Path).ToInkAttempt().Completion);
        session.Dispose();
    }
}

public class SandboxLayoutTests
{
    private static readonly MonitorDescription PenPanel = new(2, @"\\.\DISPLAY2", new MonitorRect(2560, 0, 4480, 1080), false, 256, 144, 1.5);

    [Fact]
    public void Px_per_mm_comes_from_edid_size_or_falls_back_to_the_default()
    {
        (double edid, PxPerMmSource edidSource) = SandboxLayout.ResolvePxPerMm(PenPanel);
        Assert.Equal(7.5, edid, 6); // 1920 px / 256 мм
        Assert.Equal(PxPerMmSource.Edid, edidSource);

        (double none, PxPerMmSource noneSource) = SandboxLayout.ResolvePxPerMm(null);
        Assert.Equal(SurfaceMapper.DefaultPxPerMm, none);
        Assert.Equal(PxPerMmSource.Default, noneSource);

        var unknownSize = new MonitorDescription(3, "x", new MonitorRect(0, 0, 1920, 1080), false, null, null, 1.0);
        Assert.Equal(PxPerMmSource.Default, SandboxLayout.ResolvePxPerMm(unknownSize).Source);

        var absurd = new MonitorDescription(4, "x", new MonitorRect(0, 0, 1920, 1080), false, 5000, 3000, 1.0);
        Assert.Equal(PxPerMmSource.Default, SandboxLayout.ResolvePxPerMm(absurd).Source);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void Frames_are_sized_in_millimetres_independent_of_the_windows_scale(double dpi)
    {
        var mapper = new SurfaceMapper(new PenSurface(2560, 100, 1920, 900), 7.5, dpi);
        SandboxContent content = LoadContent();

        (GuideFrame demo, GuideFrame writing) = SandboxLayout.Frames(mapper, content.Style, 12);

        Assert.Equal(12 * 7.5, demo.XHeightPx, 6);
        Assert.Equal(12 * 7.5, writing.XHeightPx, 6);
        Assert.Equal(12.0, writing.XHeightMm, 6);
        Assert.Equal(demo.BaselineYPx, writing.BaselineYPx);
        Assert.True(writing.OriginXPx > demo.OriginXPx + (3 * demo.XHeightPx), "the writing row starts to the right of the demo");

        // Строка целиком помещается в холст с запасом: асцендер сверху, десцендер снизу.
        double ascenderTop = writing.BaselineYPx - (2 * writing.XHeightPx);
        double descenderBottom = writing.BaselineYPx + writing.XHeightPx;
        Assert.InRange(ascenderTop, 0, 900);
        Assert.InRange(descenderBottom, 0, 900);
    }

    [Fact]
    public void Frames_on_a_small_canvas_stay_inside_it_and_invalid_height_is_rejected()
    {
        var mapper = new SurfaceMapper(new PenSurface(0, 0, 700, 330), 7.5, 1.0);
        SandboxContent content = LoadContent();

        (GuideFrame demo, GuideFrame writing) = SandboxLayout.Frames(mapper, content.Style, 12);

        Assert.InRange(demo.BaselineYPx, 0, 330);
        Assert.InRange(writing.OriginXPx, 0, 700);
        Assert.Throws<ArgumentOutOfRangeException>(() => SandboxLayout.Frames(mapper, content.Style, 0));
    }

    [Fact]
    public void Header_describes_a_developer_adult_without_consent()
    {
        var guides = new GuideFrame(100, 300, 90, 7.5, 3, null, new StyleId("zaner-bloser"));

        PenLogHeader header = SandboxLayout.Header(
            "0.1.0",
            "10.0.19045",
            new PenDeviceInfo("Mouse", PenSourceKind.Mouse, null, null, 1, false, PenTimestampKind.Qpc),
            PenPanel,
            1.5,
            7.5,
            PxPerMmSource.Edid,
            guides,
            "i",
            4,
            10_000_000);

        Assert.Equal(PenSubjectKind.Adult, header.Subject!.Kind);
        Assert.Equal("dev", header.Subject.Anon);
        Assert.Null(header.Consent);
        Assert.Equal("glyph:i", header.Task!.Target);
        Assert.Equal("sandbox-004", header.Task.TaskId);
        Assert.Equal(1920, header.Screen.WidthPx);
        Assert.Equal(1.5, header.Screen.DpiScale);
        Assert.Equal(guides.PxPerMm, header.Screen.PxPerMm);

        // Заголовок проходит запись и чтение (в том числе проверку «ребёнок требует согласия»).
        using var stream = new MemoryStream();
        PenLogWriter.Write(stream, new PenLog(header, []));
        stream.Position = 0;
        Assert.Equal("glyph:i", PenLogReader.Read(stream).Header.Task!.Target);
    }

    internal static SandboxContent LoadContent()
    {
        string? root = SandboxContent.FindContentRoot(null, AppContext.BaseDirectory);
        Assert.NotNull(root);
        return SandboxContent.Load(new DirectoryContentSource(root));
    }
}

public class SandboxContentTests
{
    [Fact]
    public void Content_is_found_next_to_the_program_or_above_it_and_loads_all_three_letters()
    {
        string? root = SandboxContent.FindContentRoot(null, AppContext.BaseDirectory);
        Assert.NotNull(root);
        Assert.True(File.Exists(Path.Combine(root, "styles", "zaner-bloser", "style.json")));

        SandboxContent content = SandboxContent.Load(new DirectoryContentSource(root));
        foreach (string letter in SandboxContent.Letters)
        {
            AnimationScript script = content.GetScript(letter);
            Assert.Equal(letter, script.Glyph.Symbol);
            Assert.True(script.DurationMs > 1000);
        }

        Assert.Throws<KeyNotFoundException>(() => content.GetScript("z"));
    }

    [Fact]
    public void Build_copies_the_content_next_to_the_program()
    {
        // MSBuild-копирование content/styles в папку программы (и публикации): без него self-contained сборка не найдёт буквы.
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "content", "styles", "zaner-bloser", "style.json")));
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "content", "styles", "zaner-bloser", "glyphs", "a.default.json")));
    }

    [Fact]
    public void Explicit_directory_must_contain_the_style_and_missing_content_gives_null()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(dir.Path);
        Assert.Null(SandboxContent.FindContentRoot(dir.Path, AppContext.BaseDirectory));
        Assert.Null(SandboxContent.FindContentRoot(null, dir.Path));
    }

    [Fact]
    public void Directory_content_source_reads_lists_and_cannot_leave_its_root()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(dir.Path, "a", "b"));
        File.WriteAllText(Path.Combine(dir.Path, "a", "b", "x.txt"), "hello");
        File.WriteAllText(Path.Combine(dir.Path, "a", "y.txt"), "world");
        var source = new DirectoryContentSource(dir.Path);

        Assert.True(source.Exists("a/b/x.txt"));
        Assert.False(source.Exists("a/none.txt"));
        using (var reader = new StreamReader(source.Open("a/b/x.txt")))
        {
            Assert.Equal("hello", reader.ReadToEnd());
        }

        Assert.Equal(["a/b/x.txt", "a/y.txt"], source.List(string.Empty));
        Assert.Empty(source.List("missing"));
        Assert.Throws<ArgumentException>(() => source.Exists("../outside.txt"));
        Assert.Throws<FileNotFoundException>(() => source.Open("a/none.txt"));
        Assert.Throws<DirectoryNotFoundException>(() => new DirectoryContentSource(Path.Combine(dir.Path, "nope")));
    }
}

public class DevOverlayTextTests
{
    [Fact]
    public void Overlay_shows_rate_latency_strokes_dpi_monitor_and_gc()
    {
        string text = DevOverlayText.Build(new DevOverlayInput
        {
            SourceKind = "Win32Pointer",
            RateHz = 199.6,
            RateP5Hz = 180.2,
            Latency = new RenderLatency(250, 11.2, 31.0, 9.5, 6.4, 20.1),
            Strokes = 3,
            Segments = 421,
            PenDown = true,
            DpiScale = 1.5,
            MonitorNumber = 2,
            MonitorName = @"\\.\DISPLAY2",
            MonitorSizePx = (1920, 1080),
            MonitorSizeMm = (256, 144),
            PxPerMm = 7.5,
            PxPerMmSource = "edid",
            GcLowLatencyActive = true,
            GcCollections = (3, 1, 0),
            GcPause = TimeSpan.FromMilliseconds(4.2),
            Letter = "t",
            Attempt = 4,
            Restarts = 1,
            Recording = true,
            PenLogPath = @"C:\x\a.pfl.jsonl.gz",
            PenLogSamples = 120,
            Recorded = 3,
        });

        foreach (string expected in new[]
        {
            "199.6 Hz", "mean 11.2 ms", "max 31.0", "250 frames", "mean 6.4 ms", "3 strokes, 421 segments", "pen DOWN", "pressure width on",
            "150%", "7.50 px/mm (edid)", @"[2] \\.\DISPLAY2 1920x1080 px 256x144 mm", "SustainedLowLatency ON", "3/1/0", "4.2 ms",
            "Attempt #4: letter 't', restarts 1", @"C:\x\a.pfl.jsonl.gz (120 samples)", "saved attempts 3",
        })
        {
            Assert.Contains(expected, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Overlay_copes_with_missing_data()
    {
        string text = DevOverlayText.Build(new DevOverlayInput { SourceKind = "Mouse" });

        Assert.Contains("no data yet", text, StringComparison.Ordinal);
        Assert.Contains("Penlog: off", text, StringComparison.Ordinal);
        Assert.Contains("Monitor: -", text, StringComparison.Ordinal);
        Assert.Contains("rate: -", text, StringComparison.Ordinal);
    }
}
