using System.Collections.Immutable;
using PenForLang.Core;
using PenForLang.Glyphs;
using PenForLang.Ink;
using PenForLang.Testing;
using PenForLang.Tools.InkLab.Annotation;
using PenForLang.Tools.InkLab.Content;
using PenForLang.Tools.InkLab.Protocol;
using PenForLang.Tools.InkLab.Recording;
using PenForLang.Tools.InkLab.Scene;
using PenForLang.Tools.InkLab.Tests.Support;
using PenForLang.Tools.InkLab.Views;

namespace PenForLang.Tools.InkLab.Tests;

public class LabOptionsTests
{
    [Fact]
    public void Parse_reads_known_options_and_collects_unknown_ones()
    {
        LabOptions o = LabOptions.Parse(["--mouse", "--corpus-dir", "C:\\c", "--monitor", "2", "--xheight-mm", "12.5", "--subject", "s1", "--annotator", "B", "--exit-after", "3", "--wat"]);

        Assert.True(o.UseMouse);
        Assert.Equal("C:\\c", o.CorpusDirectory);
        Assert.Equal(2, o.MonitorIndex);
        Assert.Equal(12.5, o.XHeightMm);
        Assert.Equal("s1", o.Subject);
        Assert.Equal("B", o.Annotator);
        Assert.Equal(TimeSpan.FromSeconds(3), o.ExitAfter);
        Assert.Equal<string>(["--wat"], o.UnknownArguments);
    }

    [Fact]
    public void Defaults_are_pen_mode_and_15_mm()
    {
        LabOptions o = LabOptions.Parse([]);

        Assert.False(o.UseMouse);
        Assert.Null(o.CorpusDirectory);
        Assert.Equal(15.0, o.XHeightMm);
        Assert.Equal("A", o.Annotator);
        Assert.Empty(o.UnknownArguments);
    }

    [Theory]
    [InlineData("--monitor", "0")]
    [InlineData("--monitor", "x")]
    [InlineData("--xheight-mm", "100")]
    [InlineData("--exit-after", "-1")]
    [InlineData("--corpus-dir", " ")]
    public void Bad_values_are_reported_as_unknown_arguments_instead_of_throwing(string name, string value)
    {
        LabOptions o = LabOptions.Parse([name, value]);

        Assert.Contains(name, o.UnknownArguments);
    }
}

public class AttemptCaptureTests
{
    private readonly FakeMonotonicClock _clock = new();

    private long NowUs() => MonotonicTime.ToMicroseconds(_clock.Ticks, _clock.Frequency);

    private PenSample Sample(long offsetUs, PenPhase phase) =>
        new(NowUs() + offsetUs, 100, 100, 0.5f, 0, 0, phase, PenButtons.None, SampleFlags.None, 1);

    [Fact]
    public void Samples_are_rebased_to_microseconds_from_the_start_of_the_attempt()
    {
        var capture = new AttemptCapture(_clock);
        _clock.Advance(TimeSpan.FromSeconds(5));
        capture.Begin();
        _clock.Advance(TimeSpan.FromMilliseconds(40));

        capture.AddSamples([Sample(0, PenPhase.Down), Sample(5000, PenPhase.Move), Sample(10_000, PenPhase.Up)]);
        PenLog log = capture.Complete(TestData.Header("inklab-v0.9/a-001", "glyph:a"))!;

        long[] times = [.. log.Samples.Select(s => s.TimeUs)];
        Assert.Equal([40_000, 45_000, 50_000], times);
        Assert.False(capture.IsActive);
    }

    [Fact]
    public void Points_before_the_start_and_hover_are_dropped_and_time_never_decreases()
    {
        var capture = new AttemptCapture(_clock);
        _clock.Advance(TimeSpan.FromSeconds(1));
        capture.Begin();

        capture.AddSamples([Sample(-100, PenPhase.Down), Sample(0, PenPhase.Hover)]);
        Assert.Equal(0, capture.ContactSamples);

        capture.AddSamples([Sample(2000, PenPhase.Down), Sample(1000, PenPhase.Move), Sample(3000, PenPhase.Up)]);
        PenLog log = capture.Complete(TestData.Header())!;

        Assert.Equal(3, log.Samples.Count());
        Assert.True(log.Samples.Zip(log.Samples.Skip(1), (a, b) => b.TimeUs >= a.TimeUs).All(x => x));
    }

    [Fact]
    public void An_attempt_without_contact_produces_no_log()
    {
        var capture = new AttemptCapture(_clock);
        capture.Begin();
        capture.AddSamples([Sample(0, PenPhase.Hover)]);

        Assert.Null(capture.Complete(TestData.Header()));
    }

    [Fact]
    public void Nothing_is_collected_before_begin()
    {
        var capture = new AttemptCapture(_clock);

        capture.AddSamples([Sample(0, PenPhase.Down)]);
        capture.RecordEvent("x");

        Assert.Equal(0, capture.ContactSamples);
        Assert.Null(capture.Complete(TestData.Header()));
    }

    [Fact]
    public void Restart_counts_and_records_the_event()
    {
        var capture = new AttemptCapture(_clock);
        capture.Begin();
        capture.AddSamples([Sample(0, PenPhase.Down), Sample(1000, PenPhase.Up)]);

        capture.Restart();

        Assert.Equal(1, capture.Restarts);
        PenLog log = capture.Complete(TestData.Header())!;
        Assert.Single(log.Events, e => e.Name == InkEvent.Names.RestartButton);
        Assert.Equal(InkCompletion.DoneButton, log.ToInkAttempt().Completion);
    }
}

public class RecordingHeadersTests
{
    [Fact]
    public void Task_ids_round_trip_and_expose_the_variant()
    {
        string id = RecordingHeaders.TaskId("inklab-v0.9", "at-lift-003");

        Assert.True(RecordingHeaders.TryParseTaskId(id, out string protocol, out string prompt, out string? variant));
        Assert.Equal("inklab-v0.9", protocol);
        Assert.Equal("at-lift-003", prompt);
        Assert.Equal("lift", variant);

        Assert.True(RecordingHeaders.TryParseTaskId("inklab-v0.9/a-007", out _, out _, out string? none));
        Assert.Null(none);
        Assert.False(RecordingHeaders.TryParseTaskId("sandbox-001", out _, out _, out _));
        Assert.False(RecordingHeaders.TryParseTaskId(null, out _, out _, out _));
    }

    [Fact]
    public void Header_is_adult_with_optional_consent()
    {
        RecordingPrompt prompt = RecordingProtocol.CreateDefault().Prompts[0];

        PenLogHeader withConsent = RecordingHeaders.Build(TestData.Context, new SubjectInfo(" x ", SubjectInfo.Left, 30, 5), "p1", prompt);
        PenLogHeader without = RecordingHeaders.Build(TestData.Context, new SubjectInfo("x"), "p1", prompt);

        Assert.Equal(PenSubjectKind.Adult, withConsent.Subject!.Kind);
        Assert.Equal("x", withConsent.Subject.Anon);
        Assert.Equal(5, withConsent.Consent!.Id);
        Assert.Equal("left", withConsent.Task!.Handedness);
        Assert.Null(without.Consent);
        Assert.Equal("right", without.Task!.Handedness);
        Assert.Equal("p1/a-001", without.Task.TaskId);
    }
}

public class LayoutAndContentTests
{
    private static readonly Lazy<LabContent?> Content = new(() =>
    {
        string? root = LabContent.FindContentRoot(null, AppContext.BaseDirectory);
        return root is null ? null : LabContent.Load(new DirectoryContentSource(root));
    });

    private static LabContent RequireContent() => Content.Value ?? throw new InvalidOperationException("content/styles/zaner-bloser not found");

    [Fact]
    public void Reference_for_a_letter_is_one_step_and_for_the_word_at_two_steps_in_order()
    {
        LabContent content = RequireContent();
        RecordingProtocol protocol = RecordingProtocol.CreateDefault();

        ReferencePlan letter = content.PlanFor(protocol.Prompts[0]);
        ReferencePlan word = content.PlanFor(protocol.Prompts.First(p => p.IsWord));

        Assert.Single(letter.Steps);
        Assert.Equal("a", letter.Steps[0].Symbol);
        Assert.Equal(0, letter.Steps[0].OffsetU);
        Assert.Equal<string>(["a", "t"], word.Steps.Select(s => s.Symbol));
        Assert.True(word.Steps[1].OffsetU > word.Steps[0].OffsetU);
        Assert.True(word.WidthXHeights > letter.WidthXHeights);
        Assert.Same(word, content.PlanFor(protocol.Prompts.First(p => p.IsWord))); // повторный запрос — из кэша
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void Ruler_frames_are_defined_in_millimetres_and_independent_of_the_windows_dpi_scale(double dpi)
    {
        LabContent content = RequireContent();
        var surface = new PenSurface(100, 200, 1800, 900);
        var mapper = new SurfaceMapper(surface, 7.5, dpi);

        (GuideFrame demo, GuideFrame writing) = RecordingLayout.Frames(mapper, content.Style, 15, 2.5);

        Assert.Equal(15 * 7.5, writing.XHeightPx, 6); // физические px = мм × px/мм, DPI не входит
        Assert.Equal(demo.BaselineYPx, writing.BaselineYPx);
        Assert.True(writing.OriginXPx > demo.OriginXPx + (2.5 * demo.XHeightPx)); // письмо правее эталона
        Assert.InRange(writing.BaselineYPx, 0, surface.HeightPx);
        Assert.True(writing.OriginXPx < surface.WidthPx);
    }

    [Fact]
    public void A_wider_reference_pushes_the_writing_area_further_right()
    {
        LabContent content = RequireContent();
        var mapper = new SurfaceMapper(new PenSurface(0, 0, 2400, 900), 7.5, 1.0);

        (_, GuideFrame narrow) = RecordingLayout.Frames(mapper, content.Style, 15, 0.5);
        (_, GuideFrame wide) = RecordingLayout.Frames(mapper, content.Style, 15, 4.0);

        Assert.True(wide.OriginXPx > narrow.OriginXPx);
    }

    [Fact]
    public void Pxpermm_comes_from_the_monitor_size_when_known_else_the_default()
    {
        var monitor = new PenForLang.Platform.Windows.MonitorDescription(
            0, "m", new PenForLang.Platform.Windows.MonitorRect(0, 0, 1920, 1080), true, 256, 144);

        Assert.Equal((7.5, PxPerMmSource.Edid), RecordingLayout.ResolvePxPerMm(monitor));
        Assert.Equal((SurfaceMapper.DefaultPxPerMm, PxPerMmSource.Default), RecordingLayout.ResolvePxPerMm(null));
    }

    [Fact]
    public void Zero_x_height_is_rejected()
    {
        LabContent content = RequireContent();
        var mapper = new SurfaceMapper(new PenSurface(0, 0, 1800, 900), 7.5, 1.0);

        Assert.Throws<ArgumentOutOfRangeException>(() => RecordingLayout.Frames(mapper, content.Style, 0, 1));
    }
}

public class HiddenSurfaceTests
{
    [Fact]
    public void The_hidden_surface_is_accepted_by_the_ownership_tracker_and_owns_nothing_on_screen()
    {
        PenSurface hidden = PenSurfaces.Hidden;
        var ownership = new PointerOwnership(100, 100);

        ownership.SetSurface(hidden); // нулевой размер здесь выбросил бы исключение — из-за этого окно падало при запуске

        // Точка экрана (500, 500) в координатах холста — далеко за его краем.
        var down = new PenSample(0, 500 - hidden.OriginXPx, 500 - hidden.OriginYPx, 0.5f, 0, 0, PenPhase.Down, PenButtons.None, SampleFlags.None, 1);
        Assert.False(ownership.TryProcess(in down, out _));
        Assert.Equal(0, ownership.OwnedCount);
    }
}

public class SceneAndViewTests
{
    [Fact]
    public void Nearest_finds_the_closest_sample_and_honours_the_stroke_filter()
    {
        ImmutableArray<InkStroke> strokes = TestData.Strokes(TestData.AtWordLifted());

        (StrokePoint Point, double Distance)? hit = SceneBuilder.Nearest(strokes, TestData.Frame, 0.5, 0.52);
        (StrokePoint Point, double Distance)? onlyLater = SceneBuilder.Nearest(strokes, TestData.Frame, 0.5, 0.52, s => s >= 1);

        Assert.Equal(new StrokePoint(0, 10), hit!.Value.Point);
        Assert.InRange(hit.Value.Distance, 0.019, 0.021);
        Assert.NotEqual(0, onlyLater!.Value.Point.Stroke);
        Assert.Null(SceneBuilder.Nearest(strokes, TestData.Frame, 0, 0, _ => false));
    }

    [Fact]
    public void Group_of_a_point_follows_the_junction_order_with_a_join_zone()
    {
        Junction[] junctions =
        [
            new(0, JunctionKind.Connected, new StrokePoint(0, 10), new StrokePoint(0, 14)),
            new(1, JunctionKind.Connected, new StrokePoint(0, 30), new StrokePoint(0, 30)),
        ];

        Assert.Equal(0, SceneBuilder.GroupOf(junctions, new StrokePoint(0, 3)));
        Assert.Equal(0, SceneBuilder.GroupOf(junctions, new StrokePoint(0, 10)));
        Assert.Equal(SceneBuilder.JoinGroup, SceneBuilder.GroupOf(junctions, new StrokePoint(0, 12)));
        Assert.Equal(1, SceneBuilder.GroupOf(junctions, new StrokePoint(0, 14)));
        Assert.Equal(1, SceneBuilder.GroupOf(junctions, new StrokePoint(0, 30)));
        Assert.Equal(2, SceneBuilder.GroupOf(junctions, new StrokePoint(1, 0)));
        Assert.Equal(SceneBuilder.NeutralGroup, SceneBuilder.GroupOf([], new StrokePoint(0, 0)));
    }

    [Fact]
    public void Polylines_split_at_junctions_share_the_boundary_point_so_the_line_is_continuous()
    {
        ImmutableArray<InkStroke> strokes = TestData.Strokes(TestData.AtWordJoined());
        Junction[] junctions = [new(0, JunctionKind.Connected, new StrokePoint(0, 20), new StrokePoint(0, 20))];

        InkScene scene = SceneBuilder.FromAnnotation(strokes, TestData.Frame, SceneGuides.Default, "at", junctions, []);

        ScenePolyline[] stroke0 = [.. scene.Polylines.Where(p => p.StrokeIndex == 0)];
        Assert.Equal(2, stroke0.Length);
        Assert.Equal(21, stroke0[0].Points.Length);
        Assert.Equal(stroke0[0].Points[^1], stroke0[1].Points[0]);
        Assert.Equal(41, stroke0[0].Points.Length + stroke0[1].Points.Length - 1);
    }

    [Fact]
    public void View_transform_keeps_aspect_and_maps_back_and_forth()
    {
        InkScene scene = SceneBuilder.FromStrokes(TestData.Strokes(TestData.AtWordLifted()), TestData.Frame, SceneGuides.Default);

        ViewTransform t = ViewTransform.Fit(scene, 800, 400);

        Assert.Equal(1.25, t.U(t.X(1.25)), 9);
        Assert.Equal(-0.4, t.V(t.Y(-0.4)), 9);
        Assert.True(t.Y(0) > t.Y(1)); // v растёт вверх
        // Вся линейка помещается по высоте: от десцендера до асцендера.
        Assert.InRange(t.Y(SceneGuides.Default.Ascender), 0, 400);
        Assert.InRange(t.Y(SceneGuides.Default.Descender), 0, 400);
        Assert.InRange(t.X(1.6), 0, 800);
    }

    [Fact]
    public void An_empty_scene_still_fits_the_guides_into_the_view()
    {
        ViewTransform t = ViewTransform.Fit(InkScene.Empty, 600, 300);

        Assert.True(t.Scale > 0);
        Assert.InRange(t.Y(2.0), 0, 300);
        Assert.InRange(t.Y(-1.0), 0, 300);
    }

    [Fact]
    public void Replay_metadata_describes_the_recording_in_russian()
    {
        PenLog log = TestData.AtWordLifted();

        IReadOnlyList<string> lines = PenForLang.Tools.InkLab.ViewModels.ReplayMetadata.Describe("f.pfl.jsonl.gz", log, log.ToInkAttempt());

        Assert.Equal("Файл: f.pfl.jsonl.gz", lines[0]);
        Assert.Contains(lines, l => l.StartsWith("Устройство: Test pen", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("Экран: 1920×1080 px", StringComparison.Ordinal));
    }
}
