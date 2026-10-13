using System.Runtime;
using PenForLang.Core;
using PenForLang.Ink;
using PenForLang.Testing;
using PenForLang.Wpf.Controls.Tests.Support;

namespace PenForLang.Wpf.Controls.Tests;

[Collection(AllocationSensitive.Name)]
public class LiveInkLayerTests
{
    private sealed class RecordingGcMode : IGcLatencyMode
    {
        private GCLatencyMode _mode = GCLatencyMode.Interactive;

        public GCLatencyMode Mode
        {
            get => _mode;
            set
            {
                if (ThrowOnSet)
                {
                    throw new InvalidOperationException("not supported");
                }

                Sets.Add(value);
                _mode = value;
            }
        }

        public List<GCLatencyMode> Sets { get; } = [];

        public bool ThrowOnSet { get; set; }

        public void Preset(GCLatencyMode mode) => _mode = mode;
    }

    private static LiveInkLayer Create(IMonotonicClock? clock = null, IGcLatencyMode? gc = null, int width = 600, int height = 300)
    {
        var layer = new LiveInkLayer(clock, gc) { DpiScale = 1.0 };
        layer.ConfigureSurface(width, height, 1.0);
        return layer;
    }

    [Fact]
    public void Attached_source_batches_become_strokes_and_clear_resets_them()
    {
        Sta.Run(() =>
        {
            LiveInkLayer layer = Create();
            var source = new FakePenInputSource();
            layer.Attach(source);
            source.StartAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();

            source.Push(TestSamples.Stroke(50, 100, 12, 0, 10));
            source.Push(TestSamples.Stroke(50, 180, 12, 0, 10, startUs: 3_000_000));

            Assert.Equal(2, layer.StrokeCount);
            Assert.Equal(18, layer.SegmentCount);

            layer.Clear();
            Assert.Equal(0, layer.StrokeCount);
            Assert.Equal(0, layer.SegmentCount);

            layer.Detach();
            source.Push(TestSamples.Stroke(50, 100, 12, 0, 10, startUs: 9_000_000));
            Assert.Equal(0, layer.StrokeCount);
        });
    }

    [Fact]
    public void Frame_tick_makes_the_ink_visible_in_the_rendered_layer_and_clear_removes_it()
    {
        Sta.Run(() =>
        {
            LiveInkLayer layer = Create(width: 400, height: 200);
            layer.AddSamples(TestSamples.Stroke(50, 100, 10, 0, 21));
            layer.FrameTick();

            (byte[] data, int width, _) = Pixels.Render(layer, 400, 200, 1.0);
            Assert.True(Pixels.Alpha(data, width, 150, 100) > 200, "ink must reach the WPF bitmap on the frame tick");
            Assert.Equal(0, Pixels.Alpha(data, width, 150, 150));

            layer.Clear();
            layer.FrameTick();
            (byte[] cleared, _, _) = Pixels.Render(layer, 400, 200, 1.0);
            Assert.Equal(0, Pixels.CountAlpha(cleared));
        });
    }

    [Theory]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void Raster_is_one_to_one_in_physical_pixels_at_any_dpi(double dpi)
    {
        Sta.Run(() =>
        {
            // Слой 480×240 DIP при dpi → буфер (480·dpi)×(240·dpi) физических px; штрих по y = 120 px, x = 100…300 px.
            var layer = new LiveInkLayer { DpiScale = dpi };
            int widthPx = (int)Math.Round(480 * dpi);
            int heightPx = (int)Math.Round(240 * dpi);
            layer.ConfigureSurface(widthPx, heightPx, dpi);
            layer.AddSamples(TestSamples.Stroke(100, 120, 10, 0, 21));
            layer.FrameTick();

            (byte[] data, int width, int height) = Pixels.Render(layer, 480, 240, dpi);
            Assert.Equal(widthPx, width);
            Assert.Equal(heightPx, height);
            Assert.True(Pixels.Alpha(data, width, 200, 120) > 200);
            Assert.Equal(0, Pixels.Alpha(data, width, 200, 160));
            Assert.Equal(0, Pixels.Alpha(data, width, 50, 120));
        });
    }

    [Fact]
    public void Sustained_low_latency_is_enabled_on_pen_down_and_restored_after_the_attempt()
    {
        Sta.Run(() =>
        {
            var gc = new RecordingGcMode();
            gc.Preset(GCLatencyMode.Batch);
            LiveInkLayer layer = Create(gc: gc);

            layer.AddSamples([TestSamples.Hover(100, 100)]);
            Assert.False(layer.IsGcLatencyModeActive);
            Assert.Empty(gc.Sets);

            PenSample[] stroke = TestSamples.Stroke(50, 100, 12, 0, 10);
            layer.AddSamples(stroke.AsSpan(0, 3));
            Assert.True(layer.IsGcLatencyModeActive);
            Assert.Equal(GCLatencyMode.SustainedLowLatency, gc.Mode);

            layer.AddSamples(stroke.AsSpan(3)); // отрыв пера внутри попытки режим не меняет
            layer.AddSamples(TestSamples.Stroke(50, 150, 12, 0, 10, startUs: 3_000_000));
            Assert.Single(gc.Sets);

            layer.Clear(); // «Заново» — та же попытка
            Assert.True(layer.IsGcLatencyModeActive);

            layer.EndAttempt();
            Assert.False(layer.IsGcLatencyModeActive);
            Assert.Equal(GCLatencyMode.Batch, gc.Mode);
            Assert.Equal([GCLatencyMode.SustainedLowLatency, GCLatencyMode.Batch], gc.Sets);

            layer.EndAttempt(); // повторный вызов безопасен
            Assert.Equal(2, gc.Sets.Count);
        });
    }

    [Fact]
    public void Unsupported_gc_mode_does_not_break_drawing()
    {
        Sta.Run(() =>
        {
            var gc = new RecordingGcMode { ThrowOnSet = true };
            LiveInkLayer layer = Create(gc: gc);

            layer.AddSamples(TestSamples.Stroke(50, 100, 12, 0, 10));

            Assert.False(layer.IsGcLatencyModeActive);
            Assert.Equal(1, layer.StrokeCount);
            layer.EndAttempt();
        });
    }

    [Fact]
    public void Latency_is_measured_from_the_point_stamp_to_the_frame()
    {
        Sta.Run(() =>
        {
            var clock = new FakeMonotonicClock();
            LiveInkLayer layer = Create(clock);
            long Now() => MonotonicTime.ToMicroseconds(clock.Ticks, clock.Frequency);

            // Кадр 1: точка поставлена устройством 3 мс назад, пачка получена «сейчас», кадр — через 8 мс.
            layer.AddSamples([TestSamples.Contact(50, 100, Now() - 3000, PenPhase.Down)]);
            clock.Advance(TimeSpan.FromMilliseconds(8));
            layer.FrameTick();

            RenderLatency first = layer.Latency;
            Assert.Equal(1, first.Frames);
            Assert.Equal(11.0, first.MeanPointToRenderMs, 1);
            Assert.Equal(11.0, first.MaxPointToRenderMs, 1);
            Assert.Equal(8.0, first.MeanArrivalToRenderMs, 1);

            // Кадр без новых точек задержку не добавляет.
            clock.Advance(TimeSpan.FromMilliseconds(16));
            layer.FrameTick();
            Assert.Equal(1, layer.Latency.Frames);

            // Кадр 2: две пачки между кадрами. Среднее считается по новейшей точке (5 мс), максимум — по старейшей (10 мс).
            long start = Now();
            layer.AddSamples([TestSamples.Contact(60, 100, start - 2000)]);
            clock.Advance(TimeSpan.FromMilliseconds(4));
            layer.AddSamples([TestSamples.Contact(70, 100, start + 3000)]);
            clock.Advance(TimeSpan.FromMilliseconds(4));
            layer.FrameTick();

            RenderLatency second = layer.Latency;
            Assert.Equal(2, second.Frames);
            Assert.Equal(8.0, second.MeanPointToRenderMs, 1); // (11 + 5) / 2
            Assert.Equal(11.0, second.MaxPointToRenderMs, 1);
            Assert.Equal(5.0, second.LastPointToRenderMs, 1);
            Assert.Equal(6.0, second.MeanArrivalToRenderMs, 1); // (8 + 4) / 2: по пачке с той же новейшей точкой (4 мс до кадра)
            Assert.Equal(8.0, second.MaxArrivalToRenderMs, 1); // по пачке со старейшей точкой

            layer.ResetLatency();
            Assert.True(layer.Latency.IsEmpty);
        });
    }

    [Fact]
    public void Frames_with_only_hover_points_are_not_counted_in_the_latency()
    {
        Sta.Run(() =>
        {
            var clock = new FakeMonotonicClock();
            LiveInkLayer layer = Create(clock);
            long Now() => MonotonicTime.ToMicroseconds(clock.Ticks, clock.Frequency);

            layer.AddSamples([TestSamples.Hover(50, 100, Now() - 2000)]);
            clock.Advance(TimeSpan.FromMilliseconds(5));
            layer.FrameTick();
            Assert.True(layer.Latency.IsEmpty);

            layer.AddSamples([TestSamples.Contact(50, 100, Now() - 2000, PenPhase.Down)]);
            clock.Advance(TimeSpan.FromMilliseconds(5));
            layer.FrameTick();
            Assert.Equal(1, layer.Latency.Frames);
            Assert.Equal(7.0, layer.Latency.MeanPointToRenderMs, 1);
            Assert.Equal(5.0, layer.Latency.MeanArrivalToRenderMs, 1);
        });
    }

    [Fact]
    public void Latency_is_not_measured_for_task_relative_sources_or_without_a_clock()
    {
        Sta.Run(() =>
        {
            var clock = new FakeMonotonicClock();
            LiveInkLayer replay = Create(clock);
            var source = new FakePenInputSource(PenSourceKind.Replay, null, PenTimeBase.TaskRelative);
            replay.Attach(source);
            source.StartAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
            source.Push(TestSamples.Stroke(50, 100, 12, 0, 4));
            clock.Advance(TimeSpan.FromMilliseconds(8));
            replay.FrameTick();
            Assert.True(replay.Latency.IsEmpty);

            LiveInkLayer noClock = Create();
            noClock.AddSamples(TestSamples.Stroke(50, 100, 12, 0, 4));
            noClock.FrameTick();
            Assert.True(noClock.Latency.IsEmpty);
        });
    }

    [Fact]
    public void Pressure_width_is_off_by_default_and_the_raster_is_not_smoothed_by_the_scaler()
    {
        Sta.Run(() =>
        {
            var layer = new LiveInkLayer();

            Assert.False(layer.PressureEnabled);
            Assert.Equal(System.Windows.Media.BitmapScalingMode.NearestNeighbor, System.Windows.Media.RenderOptions.GetBitmapScalingMode(layer));
            layer.PressureEnabled = true;
            Assert.True(layer.PressureEnabled);
        });
    }

    [Fact]
    public void Same_size_and_dpi_do_not_recreate_the_bitmap()
    {
        Sta.Run(() =>
        {
            LiveInkLayer layer = Create(width: 400, height: 200);
            int generation = layer.BitmapGeneration;
            layer.AddSamples(TestSamples.Stroke(50, 100, 10, 0, 21));

            layer.ConfigureSurface(400, 200, 1.0);
            layer.ConfigureSurface(400, 200, 1.0);

            Assert.Equal(generation, layer.BitmapGeneration);
            Assert.Equal(1, layer.StrokeCount);

            layer.ConfigureSurface(500, 200, 1.0);
            Assert.Equal(generation + 1, layer.BitmapGeneration);
        });
    }

    [Fact]
    public void Clear_before_next_stroke_is_forwarded_to_the_surface()
    {
        Sta.Run(() =>
        {
            LiveInkLayer layer = Create(width: 400, height: 200);
            layer.AddSamples(TestSamples.Stroke(50, 100, 10, 0, 15));
            layer.ClearBeforeNextStroke = true;
            layer.AddSamples([TestSamples.Hover(10, 10)]);
            Assert.Equal(1, layer.StrokeCount);

            layer.AddSamples(TestSamples.Stroke(50, 30, 10, 0, 6, startUs: 3_000_000));

            Assert.Equal(1, layer.StrokeCount);
            Assert.False(layer.ClearBeforeNextStroke);
        });
    }

    [Fact]
    public void Samples_before_the_surface_exists_are_ignored()
    {
        Sta.Run(() =>
        {
            var layer = new LiveInkLayer();
            layer.AddSamples(TestSamples.Stroke(50, 100, 12, 0, 4));
            layer.FrameTick();

            Assert.Equal(0, layer.StrokeCount);
            Assert.Null(layer.Surface);
        });
    }

    [Fact]
    public void Reconfiguring_the_surface_keeps_the_ink()
    {
        Sta.Run(() =>
        {
            LiveInkLayer layer = Create(width: 400, height: 200);
            layer.DpiScale = 1.5;
            layer.AddSamples(TestSamples.Stroke(50, 100, 10, 0, 21));

            layer.ConfigureSurface(800, 400, 1.5);
            layer.FrameTick();

            Assert.Equal(1, layer.StrokeCount);
            Assert.Equal(1.5, layer.RasterDpiScale);
            Assert.Equal(800, layer.Surface!.WidthPx);
            (byte[] data, int width, _) = Pixels.Render(layer, 800 / 1.5, 400 / 1.5, 1.5);
            Assert.True(Pixels.Alpha(data, width, 150, 100) > 200);
        });
    }

    [Fact]
    public void Layer_hot_path_does_not_allocate_over_1000_batches()
    {
        long delta = long.MaxValue;
        Sta.Run(() =>
        {
            var clock = new FakeMonotonicClock();
            var gc = new RecordingGcMode();
            LiveInkLayer layer = Create(clock, gc, 1920, 1080);
            layer.PressureEnabled = true;
            var batch = new PenSample[4];

            for (int i = 0; i < 400; i++)
            {
                Fill(batch, i, clock);
                layer.AddSamples(batch);
            }

            // Второй прогрев — целое окно измерения, включая очистку и путь кадра (flush в WriteableBitmap, задержка, маркер).
            for (int i = 0; i < 1000; i++)
            {
                Fill(batch, i, clock);
                layer.AddSamples(batch);
            }

            layer.FrameTick();
            layer.Clear();
            layer.FrameTick();

            // Окно: очистка и 1000 пачек; ровно 0 байт в окне без сборок мусора (см. Allocation).
            delta = Allocation.MeasureQuiet(() =>
            {
                layer.Clear();
                for (int i = 0; i < 1000; i++)
                {
                    Fill(batch, i, clock);
                    layer.AddSamples(batch);
                }
            });
        });

        Assert.Equal(0, delta);
    }

    [Fact]
    public void Frame_upload_to_the_bitmap_allocates_only_a_few_dozen_bytes_per_frame()
    {
        long delta = long.MaxValue;
        Sta.Run(() =>
        {
            var clock = new FakeMonotonicClock();
            LiveInkLayer layer = Create(clock, new RecordingGcMode(), 1920, 1080);
            var batch = new PenSample[4];
            for (int i = 0; i < 400; i++)
            {
                Fill(batch, i, clock);
                layer.AddSamples(batch);
                layer.FrameTick();
            }

            layer.Clear();
            layer.FrameTick();

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                Fill(batch, i, clock);
                layer.AddSamples(batch);
                if (i % 4 == 0)
                {
                    layer.FrameTick();
                }
            }

            delta = GC.GetAllocatedBytesForCurrentThread() - before;
        });

        // 250 кадров. WriteableBitmap.WritePixels внутри WPF выделяет ~40 байт на вызов (не горячий путь: раз за кадр, а не за пачку).
        Assert.True(delta <= 250 * 128, $"frame upload allocated {delta} bytes over 250 frames");
    }

    private static void Fill(PenSample[] batch, int index, FakeMonotonicClock clock)
    {
        long nowUs = MonotonicTime.ToMicroseconds(clock.Ticks, clock.Frequency);
        int inStroke = index % 50;
        for (int k = 0; k < batch.Length; k++)
        {
            int step = (inStroke * batch.Length) + k;
            PenPhase phase = PenPhase.Move;
            if (inStroke == 0 && k == 0)
            {
                phase = PenPhase.Down;
            }
            else if (inStroke == 49 && k == batch.Length - 1)
            {
                phase = PenPhase.Up;
            }

            batch[k] = new PenSample(
                nowUs,
                100 + (step * 7.0),
                540 + (Math.Sin(step * 0.09) * 200) + ((index / 50) * 3),
                0.3f + (0.6f * (float)(0.5 + (0.5 * Math.Sin(step * 0.05)))),
                0,
                0,
                phase,
                PenButtons.None,
                SampleFlags.None,
                1);
        }

        clock.Advance(50_000); // 5 мс при 10 МГц
    }
}
