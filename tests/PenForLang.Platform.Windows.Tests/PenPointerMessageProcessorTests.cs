using System.Runtime.InteropServices;
using PenForLang.Core;
using PenForLang.Ink;
using PenForLang.Platform.Windows.Native;
using PenForLang.Testing;

namespace PenForLang.Platform.Windows.Tests;

public class PenPointerMessageProcessorTests
{
    // Холст 800x600 с левым верхним углом на экране в (100, 50).
    private static readonly PenSurface Surface = new(100, 50, 800, 600);

    private readonly FakePointerApi _api = new();
    private readonly FakeMonotonicClock _clock = new();
    private readonly PenPointerMessageProcessor _processor;

    public PenPointerMessageProcessorTests()
    {
        _processor = new PenPointerMessageProcessor(_api, _clock);
        _processor.SetSurface(Surface);
    }

    private bool Process(uint message, uint pointerId, out PenSample[] samples, params NativePointerPenInfo[] chronologicalHistory)
    {
        _api.SetHistoryChronological(chronologicalHistory);
        bool handled = _processor.TryProcess(message, (nint)pointerId, out ReadOnlyMemory<PenSample> memory);
        samples = memory.ToArray();
        return handled;
    }

    [Fact]
    public void History_is_reversed_to_chronological_order_and_older_entries_are_marked_coalesced()
    {
        Assert.True(Process(NativeConstants.WmPointerDown, 1, out _, Pen.Down(1, 300, 200, perf: 1_000_000)));

        // ОС отдаёт [t3, t2, t1] — новейшая первая; на выходе обязано быть t1, t2, t3.
        bool handled = Process(
            NativeConstants.WmPointerUpdate,
            1,
            out PenSample[] samples,
            Pen.Move(1, 310, 200, perf: 1_050_000),
            Pen.Move(1, 320, 200, perf: 1_100_000),
            Pen.Move(1, 330, 200, perf: 1_150_000));

        Assert.True(handled);
        Assert.Equal([210.0, 220.0, 230.0], samples.Select(s => s.X));
        // 10 МГц: 1_050_000 тиков = 105_000 мкс и т. д.
        Assert.Equal([105_000L, 110_000L, 115_000L], samples.Select(s => s.TimeUs));
        Assert.Equal(SampleFlags.Coalesced, samples[0].Flags);
        Assert.Equal(SampleFlags.Coalesced, samples[1].Flags);
        Assert.Equal(SampleFlags.None, samples[2].Flags);
    }

    [Fact]
    public void Time_is_converted_from_qpc_ticks_to_absolute_microseconds_without_overflow()
    {
        // ~30 суток аптайма на 10 МГц: наивное ticks * 1_000_000 переполнило бы long.
        ulong ticks = (ulong)FakeMonotonicClock.DefaultStartTicks + 12_345;
        Process(NativeConstants.WmPointerDown, 1, out PenSample[] samples, Pen.Down(1, 300, 200, perf: ticks));

        Assert.Equal(MonotonicTime.ToMicroseconds((long)ticks, FakeMonotonicClock.DefaultFrequency), samples[0].TimeUs);
        Assert.True(samples[0].TimeUs > 2_000_000_000_000);
    }

    [Fact]
    public void Missing_performance_count_falls_back_to_the_clock_and_marks_the_sample()
    {
        Process(NativeConstants.WmPointerDown, 1, out PenSample[] samples, Pen.Down(1, 300, 200, perf: 0));

        Assert.Equal(MonotonicTime.ToMicroseconds(_clock.Ticks, _clock.Frequency), samples[0].TimeUs);
        Assert.Equal(SampleFlags.InterpolatedTime, samples[0].Flags);
        Assert.Equal(1, _processor.Diagnostics.TimeFallbackSamples);
    }

    [Fact]
    public void Down_inside_is_handled_and_up_releases_ownership()
    {
        Assert.True(Process(NativeConstants.WmPointerDown, 7, out PenSample[] down, Pen.Down(7, 300, 200)));
        Assert.Equal(PenPhase.Down, down.Single().Phase);
        Assert.Equal(7u, down.Single().PointerId);
        Assert.Equal(1, _processor.OwnedCount);

        Assert.True(Process(NativeConstants.WmPointerUpdate, 7, out PenSample[] move, Pen.Move(7, 310, 210, 1_010_000)));
        Assert.Equal(PenPhase.Move, move.Single().Phase);

        Assert.True(Process(NativeConstants.WmPointerUp, 7, out PenSample[] up, Pen.Up(7, 310, 210, 1_020_000)));
        Assert.Equal(PenPhase.Up, up.Single().Phase);
        Assert.Equal(0, _processor.OwnedCount);
    }

    [Fact]
    public void Down_outside_the_canvas_is_not_handled_and_its_moves_are_dropped()
    {
        // Левее холста (экранный X = 50 < origin 100): нажатие на кнопку интерфейса — оно уходит в WPF.
        Assert.False(Process(NativeConstants.WmPointerDown, 3, out PenSample[] down, Pen.Down(3, 50, 200)));
        Assert.Empty(down);

        // Указатель вошёл на холст уже после Down вне его — холсту не принадлежит.
        Assert.False(Process(NativeConstants.WmPointerUpdate, 3, out PenSample[] move, Pen.Move(3, 300, 200, 1_010_000)));
        Assert.Empty(move);
        Assert.Equal(0, _processor.OwnedCount);
    }

    [Fact]
    public void Owned_pointer_leaving_the_canvas_keeps_being_handled_with_the_clipped_flag()
    {
        Process(NativeConstants.WmPointerDown, 1, out _, Pen.Down(1, 300, 200));

        bool handled = Process(NativeConstants.WmPointerUpdate, 1, out PenSample[] samples, Pen.Move(1, 1500, 200, 1_010_000));

        Assert.True(handled);
        Assert.True(samples.Single().IsClipped);
        Assert.True(samples.Single().X > Surface.WidthPx);
        Assert.Equal(1, _processor.Diagnostics.ClippedSamples);
    }

    [Fact]
    public void Non_pen_pointer_is_not_handled_and_not_read()
    {
        _api.Types[9] = 4; // PT_MOUSE

        bool handled = Process(NativeConstants.WmPointerDown, 9, out PenSample[] samples, Pen.Down(9, 300, 200));

        Assert.False(handled);
        Assert.Empty(samples);
        Assert.Equal(0, _api.HistoryCalls);
        Assert.Equal(1, _processor.Diagnostics.NonPenMessages);
        Assert.Equal(0, _processor.Diagnostics.PenMessages);
    }

    [Fact]
    public void Hover_inside_is_handled_hover_outside_is_not()
    {
        Assert.True(Process(NativeConstants.WmPointerUpdate, 1, out PenSample[] inside, Pen.Hover(1, 400, 300, 1_000_000)));
        Assert.Equal(PenPhase.Hover, inside.Single().Phase);
        Assert.Equal(0, _processor.OwnedCount);

        Assert.False(Process(NativeConstants.WmPointerUpdate, 1, out _, Pen.Hover(1, 5, 5, 1_010_000)));
    }

    [Fact]
    public void Lost_up_is_closed_by_an_implicit_up_on_hover_of_an_owned_pointer()
    {
        Process(NativeConstants.WmPointerDown, 1, out _, Pen.Down(1, 300, 200));

        bool handled = Process(NativeConstants.WmPointerUpdate, 1, out PenSample[] samples, Pen.Hover(1, 305, 205, 1_030_000));

        Assert.True(handled);
        Assert.Equal(PenPhase.Up, samples.Single().Phase);
        Assert.Equal(0, _processor.OwnedCount);
    }

    [Fact]
    public void Pointer_leave_while_owned_closes_the_stroke()
    {
        Process(NativeConstants.WmPointerDown, 1, out _, Pen.Down(1, 300, 200));

        // WM_POINTERLEAVE: контакта нет, флагов DOWN/UP нет.
        bool handled = Process(NativeConstants.WmPointerLeave, 1, out PenSample[] samples, Pen.Hover(1, 305, 205, 1_030_000));

        Assert.True(handled);
        Assert.Equal(PenPhase.Up, samples.Single().Phase);
    }

    [Fact]
    public void Capture_changed_releases_ownership_closes_the_open_stroke_and_is_not_handled()
    {
        Process(NativeConstants.WmPointerDown, 1, out _, Pen.Down(1, 300, 200, perf: 1_000_000));
        Process(NativeConstants.WmPointerUpdate, 1, out _, Pen.Move(1, 320, 210, perf: 1_010_000));
        _clock.Advance(TimeSpan.FromMilliseconds(30));

        bool handled = _processor.TryProcess(NativeConstants.WmPointerCaptureChanged, 1, out ReadOnlyMemory<PenSample> memory);

        Assert.False(handled);
        PenSample up = memory.Span[0];
        Assert.Equal(PenPhase.Up, up.Phase);
        Assert.Equal(220.0, up.X);
        Assert.Equal(0, _processor.OwnedCount);

        // После потери захвата движения указателя холсту не принадлежат.
        Assert.False(Process(NativeConstants.WmPointerUpdate, 1, out _, Pen.Move(1, 330, 210, perf: 1_020_000)));
    }

    [Fact]
    public void Capture_changed_for_a_pointer_that_is_not_owned_does_nothing()
    {
        bool handled = _processor.TryProcess(NativeConstants.WmPointerCaptureChanged, 5, out ReadOnlyMemory<PenSample> memory);

        Assert.False(handled);
        Assert.True(memory.IsEmpty);
    }

    [Fact]
    public void History_longer_than_the_buffer_grows_the_buffer_and_keeps_every_entry_in_order()
    {
        Process(NativeConstants.WmPointerDown, 1, out _, Pen.Down(1, 200, 200, perf: 1_000_000));

        const int Count = 500; // больше начального буфера (128)
        NativePointerPenInfo[] chronological = Enumerable.Range(0, Count)
            .Select(i => Pen.Move(1, 200 + i, 200, perf: 1_010_000UL + (ulong)(i * 2_000)))
            .ToArray();

        bool handled = Process(NativeConstants.WmPointerUpdate, 1, out PenSample[] samples, chronological);

        Assert.True(handled);
        Assert.Equal(Count, samples.Length);
        Assert.Equal(Enumerable.Range(0, Count).Select(i => 100.0 + i), samples.Select(s => s.X));
        Assert.Equal(Count - 1, samples.Count(s => (s.Flags & SampleFlags.Coalesced) != 0));
        Assert.Equal(1, _processor.Diagnostics.HistoryBufferGrowths);

        // Второе такое же сообщение растить буфер не должно.
        Process(NativeConstants.WmPointerUpdate, 1, out PenSample[] again, chronological);
        Assert.Equal(Count, again.Length);
        Assert.Equal(1, _processor.Diagnostics.HistoryBufferGrowths);
    }

    [Fact]
    public void History_failure_is_counted_and_the_message_is_left_to_wpf()
    {
        _api.FailHistory = true;

        bool handled = Process(NativeConstants.WmPointerDown, 1, out PenSample[] samples, Pen.Down(1, 300, 200));

        Assert.False(handled);
        Assert.Empty(samples);
        Assert.Equal(1, _processor.Diagnostics.HistoryFailures);
    }

    [Fact]
    public void Without_a_surface_nothing_is_handled()
    {
        var processor = new PenPointerMessageProcessor(_api, _clock);
        _api.SetHistoryChronological(Pen.Down(1, 300, 200));

        Assert.False(processor.TryProcess(NativeConstants.WmPointerDown, 1, out ReadOnlyMemory<PenSample> samples));
        Assert.True(samples.IsEmpty);
    }

    [Fact]
    public void Unrelated_messages_are_ignored_without_touching_the_api()
    {
        Assert.False(_processor.TryProcess(0x0200 /* WM_MOUSEMOVE */, 1, out ReadOnlyMemory<PenSample> samples));
        Assert.True(samples.IsEmpty);
        Assert.Equal(0, _api.HistoryCalls);
        Assert.Equal(0, _processor.Diagnostics.PointerMessages);
    }

    [Fact]
    public void Pointer_id_is_the_low_word_of_wparam()
    {
        Assert.Equal(0x1234u, PenPointerMessageProcessor.PointerIdOf((nint)0x00AB1234));
    }

    [Fact]
    public void Pressure_tilt_and_reporting_flags_are_tracked()
    {
        Assert.False(_processor.TiltReported);
        NativePointerPenInfo info = Pen.Down(1, 300, 200, pressure: 1024);
        info.TiltX = -30;
        info.TiltY = 45;

        Process(NativeConstants.WmPointerDown, 1, out PenSample[] samples, info);

        Assert.Equal(1f, samples[0].Pressure);
        Assert.Equal(-30f, samples[0].TiltXDeg);
        Assert.Equal(45f, samples[0].TiltYDeg);
        Assert.True(_processor.TiltReported);
        Assert.True(_processor.PressureReported);
    }

    [Fact]
    public void Whole_pipeline_with_the_stroke_assembler_gives_one_closed_stroke_with_all_points()
    {
        var assembler = new StrokeAssembler();
        void Feed(uint message, params NativePointerPenInfo[] history)
        {
            Process(message, 1, out PenSample[] samples, history);
            assembler.Add(samples);
        }

        Feed(NativeConstants.WmPointerDown, Pen.Down(1, 300, 200, perf: 1_000_000));
        Feed(
            NativeConstants.WmPointerUpdate,
            Pen.Move(1, 310, 200, 1_020_000),
            Pen.Move(1, 320, 200, 1_040_000),
            Pen.Move(1, 330, 200, 1_060_000));
        Feed(NativeConstants.WmPointerUp, Pen.Up(1, 330, 200, 1_080_000));

        Assert.Equal(0, assembler.OpenStrokeCount);
        Assert.Equal(0, assembler.DroppedNonMonotonicCount);
        var stroke = Assert.Single(assembler.ToStrokes());
        Assert.Equal(5, stroke.Samples.Length);
        Assert.Equal(PenPhase.Down, stroke.Samples[0].Phase);
        Assert.Equal(PenPhase.Up, stroke.Samples[^1].Phase);
    }

    [Fact]
    public void Native_structs_match_the_documented_layout_on_x64()
    {
        if (!Environment.Is64BitProcess)
        {
            return;
        }

        // Размеры считаны с POINTER_INFO / POINTER_PEN_INFO из WinUser.h (x64): 96 и 120; POINTER_TYPE_INFO — 8 + 144 (POINTER_TOUCH_INFO).
        Assert.Equal(96, Marshal.SizeOf<NativePointerInfo>());
        Assert.Equal(120, Marshal.SizeOf<NativePointerPenInfo>());
        Assert.Equal(152, Marshal.SizeOf<NativePointerTypeInfoPen>());
        Assert.Equal(8, (int)Marshal.OffsetOf<NativePointerTypeInfoPen>(nameof(NativePointerTypeInfoPen.PenInfo)));
        Assert.Equal(16, (int)Marshal.OffsetOf<NativePointerInfo>(nameof(NativePointerInfo.SourceDevice)));
        Assert.Equal(32, (int)Marshal.OffsetOf<NativePointerInfo>(nameof(NativePointerInfo.PtPixelLocation)));
        Assert.Equal(64, (int)Marshal.OffsetOf<NativePointerInfo>(nameof(NativePointerInfo.DwTime)));
        Assert.Equal(80, (int)Marshal.OffsetOf<NativePointerInfo>(nameof(NativePointerInfo.PerformanceCount)));
        Assert.Equal(96, (int)Marshal.OffsetOf<NativePointerPenInfo>(nameof(NativePointerPenInfo.PenFlags)));
    }

    [Fact]
    public void Message_and_flag_constants_match_the_windows_sdk_headers()
    {
        // WinUser.h (SDK 10.0.26100) и страницы learn.microsoft.com; защита от опечаток в шестнадцатеричных значениях.
        Assert.Equal(0x0246u, NativeConstants.WmPointerDown);
        Assert.Equal(0x0245u, NativeConstants.WmPointerUpdate);
        Assert.Equal(0x0247u, NativeConstants.WmPointerUp);
        Assert.Equal(0x0249u, NativeConstants.WmPointerEnter);
        Assert.Equal(0x024Au, NativeConstants.WmPointerLeave);
        Assert.Equal(0x024Cu, NativeConstants.WmPointerCaptureChanged);
        Assert.Equal(0x00010000u, NativeConstants.PointerFlagDown);
        Assert.Equal(0x00020000u, NativeConstants.PointerFlagUpdate);
        Assert.Equal(0x00040000u, NativeConstants.PointerFlagUp);
        Assert.Equal(0x00000004u, NativeConstants.PointerFlagInContact);
        Assert.Equal(0x00000002u, NativeConstants.PointerFlagInRange);
        Assert.Equal(3u, NativeConstants.PtPen);
    }
}
