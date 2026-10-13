using System.Windows;
using PenForLang.Ink;
using PenForLang.Wpf.Controls.Tests.Support;

namespace PenForLang.Wpf.Controls.Tests;

public class InkSurfaceTests
{
    private static uint At(InkSurface s, int x, int y) => s.Pixels[(y * s.WidthPx) + x];

    private static int Covered(InkSurface s)
    {
        int n = 0;
        foreach (uint p in s.Pixels)
        {
            if ((p >> 24) != 0)
            {
                n++;
            }
        }

        return n;
    }

    [Fact]
    public void Horizontal_stroke_inks_the_path_and_not_the_far_background()
    {
        var surface = new InkSurface(400, 200);
        surface.Add(TestSamples.Stroke(50, 100, 10, 0, 21));

        Assert.True((At(surface, 150, 100) >> 24) == 255, "the center of the stroke is fully opaque");
        Assert.True((At(surface, 150, 130) >> 24) == 0, "far from the stroke is empty");
        Assert.True((At(surface, 20, 100) >> 24) == 0, "before the start is empty");
        Assert.True(Covered(surface) > 20 * 10 * 3, "a 200 px stroke of ~6 px width covers more than 3600 pixels");
    }

    [Fact]
    public void Stroke_and_segment_counts_match_the_input()
    {
        var surface = new InkSurface(600, 300);

        // Два штриха по 10 точек, шаг 12 px (сглаживание шаг не обнуляет): 2 штриха, по 9 отрезков.
        surface.Add(TestSamples.Stroke(50, 100, 12, 0, 10));
        surface.Add(TestSamples.Stroke(50, 200, 12, 0, 10, startUs: 2_000_000));

        Assert.Equal(2, surface.StrokeCount);
        Assert.Equal(18, surface.SegmentCount);
        Assert.False(surface.IsPenDown);
    }

    [Fact]
    public void Batches_split_arbitrarily_give_the_same_picture()
    {
        PenSample[] stroke = TestSamples.Stroke(30, 40, 9, 5, 30);
        var whole = new InkSurface(500, 300);
        whole.Add(stroke);

        var pieces = new InkSurface(500, 300);
        for (int i = 0; i < stroke.Length; i += 7)
        {
            pieces.Add(stroke.AsSpan(i, Math.Min(7, stroke.Length - i)));
        }

        Assert.True(whole.Pixels.SequenceEqual(pieces.Pixels));
        Assert.Equal(whole.SegmentCount, pieces.SegmentCount);
    }

    [Fact]
    public void Last_point_is_reached_even_though_smoothing_lags()
    {
        var surface = new InkSurface(400, 200);
        PenSample[] stroke = TestSamples.Stroke(50, 100, 15, 0, 11); // последняя точка (200, 100)
        surface.Add(stroke);

        Assert.True((At(surface, 200, 100) >> 24) == 255, "the stroke must be completed up to the real pen-up point");
        Assert.True((At(surface, 215, 100) >> 24) < 255, "and not far beyond it");
    }

    [Fact]
    public void Move_without_down_hover_and_auxiliary_points_draw_nothing()
    {
        var surface = new InkSurface(300, 200);
        PenSample[] orphan = TestSamples.Stroke(50, 100, 10, 0, 10);
        surface.Add(orphan.AsSpan(1)); // нет Down: указатель не принадлежит холсту

        surface.Add([TestSamples.Hover(100, 100), TestSamples.Hover(110, 100)]);

        PenSample[] barrel = TestSamples.Stroke(50, 150, 10, 0, 10);
        for (int i = 0; i < barrel.Length; i++)
        {
            barrel[i] = barrel[i] with { Buttons = PenButtons.Barrel };
        }

        surface.Add(barrel);

        Assert.Equal(0, Covered(surface));
        Assert.Equal(0, surface.StrokeCount);
        Assert.True(surface.HasTip);
    }

    [Fact]
    public void Higher_pressure_gives_a_thicker_line_within_limits()
    {
        var thin = new InkSurface(400, 200) { PressureEnabled = true };
        var normal = new InkSurface(400, 200) { PressureEnabled = true };
        var thick = new InkSurface(400, 200) { PressureEnabled = true };
        thin.Add(TestSamples.Stroke(50, 100, 10, 0, 21, pressure: 0.05f));
        normal.Add(TestSamples.Stroke(50, 100, 10, 0, 21, pressure: 0.5f));
        thick.Add(TestSamples.Stroke(50, 100, 10, 0, 21, pressure: 1.0f));

        int a = Covered(thin);
        int b = Covered(normal);
        int c = Covered(thick);
        Assert.True(a < b && b < c, $"thin {a} < normal {b} < thick {c}");

        // Ограничение: полный нажим не толще MaxPressureFactor / MinPressureFactor от слабого — не в разы.
        Assert.True(c < a * 3.0, $"thickness is limited ({c} vs {a})");
    }

    [Fact]
    public void Pressure_width_is_off_by_default_the_line_does_not_react_to_pressure()
    {
        // Методика: никакой видимой обратной связи по давлению (D15/§9.1/§7.5); ширина от давления — только для разработчика.
        Assert.False(new InkSurface(10, 10).PressureEnabled);
        var off = new InkSurface(400, 200);
        var offHard = new InkSurface(400, 200);
        off.Add(TestSamples.Stroke(50, 100, 10, 0, 21, pressure: 0.1f));
        offHard.Add(TestSamples.Stroke(50, 100, 10, 0, 21, pressure: 1.0f));
        Assert.Equal(Covered(off), Covered(offHard));
        Assert.True(off.Pixels.SequenceEqual(offHard.Pixels));
    }

    [Fact]
    public void Zero_pressure_uses_the_default_pressure_when_the_width_follows_pressure()
    {
        var zero = new InkSurface(400, 200) { PressureEnabled = true };
        var half = new InkSurface(400, 200) { PressureEnabled = true };
        zero.Add(TestSamples.Stroke(50, 100, 10, 0, 21, pressure: 0f));
        half.Add(TestSamples.Stroke(50, 100, 10, 0, 21, pressure: 0.5f));
        Assert.Equal(Covered(half), Covered(zero));
    }

    [Fact]
    public void Radius_changes_gradually_between_points()
    {
        var surface = new InkSurface(500, 200) { PressureEnabled = true };
        PenSample[] stroke = TestSamples.Stroke(50, 100, 10, 0, 30, pressure: 0.05f);
        for (int i = 15; i < stroke.Length; i++)
        {
            stroke[i] = stroke[i] with { Pressure = 1.0f }; // скачок давления посередине
        }

        surface.Add(stroke);

        // Высота линии (число закрашенных пикселей в столбце) растёт плавно, без ступеньки больше пикселя за шаг в 10 px.
        int previous = ColumnHeight(surface, 60);
        for (int x = 70; x < 330; x += 10)
        {
            int height = ColumnHeight(surface, x);
            Assert.True(Math.Abs(height - previous) <= 2, $"thickness jumped by {height - previous} px at x={x}");
            previous = height;
        }
    }

    private static int ColumnHeight(InkSurface s, int x)
    {
        int n = 0;
        for (int y = 0; y < s.HeightPx; y++)
        {
            if ((At(s, x, y) >> 24) > 127)
            {
                n++;
            }
        }

        return n;
    }

    [Fact]
    public void Down_with_zero_pressure_does_not_start_the_stroke_thin()
    {
        // Давление в Down у части устройств ещё нулевое: сглаженное давление стартует со значения по умолчанию, а не с нуля.
        var surface = new InkSurface(500, 200) { PressureEnabled = true };
        PenSample[] stroke = TestSamples.Stroke(50, 100, 10, 0, 30, pressure: 0.5f);
        stroke[0] = stroke[0] with { Pressure = 0f };

        surface.Add(stroke);

        int early = ColumnHeight(surface, 70);
        int late = ColumnHeight(surface, 250);
        Assert.True(Math.Abs(early - late) <= 1, $"the start ({early} px) must be as thick as the rest ({late} px)");
    }

    [Fact]
    public void Clear_before_next_stroke_keeps_the_ink_until_the_next_touch()
    {
        var surface = new InkSurface(400, 200);
        surface.Add(TestSamples.Stroke(50, 100, 10, 0, 15));
        surface.ClearBeforeNextStroke = true;

        surface.Add([TestSamples.Hover(80, 150, 2_000_000)]); // наведение чернила не стирает
        Assert.NotEqual(0, Covered(surface));
        Assert.Equal(1, surface.StrokeCount);

        surface.Add(TestSamples.Stroke(50, 30, 10, 0, 6, startUs: 3_000_000));

        Assert.False(surface.ClearBeforeNextStroke);
        Assert.Equal(1, surface.StrokeCount); // счётчики начались заново
        Assert.True((At(surface, 75, 30) >> 24) == 255, "the new stroke is drawn");
        Assert.True((At(surface, 120, 100) >> 24) == 0, "the old stroke is gone");
    }

    [Fact]
    public void Pending_arrival_times_belong_to_the_same_points_as_the_pending_stamps()
    {
        var surface = new InkSurface(400, 200);
        surface.Add([TestSamples.Contact(50, 100, 1_000, PenPhase.Down)], arrivalUs: 5_000);
        surface.Add([TestSamples.Contact(60, 100, 9_000)], arrivalUs: 12_000);

        Assert.Equal(1_000, surface.PendingOldestPointUs);
        Assert.Equal(5_000, surface.PendingFirstArrivalUs);
        Assert.Equal(9_000, surface.PendingNewestPointUs);
        Assert.Equal(12_000, surface.PendingLatestArrivalUs);
    }

    [Fact]
    public void Clear_erases_the_ink_and_resets_the_counters()
    {
        var surface = new InkSurface(300, 200);
        surface.Add(TestSamples.Stroke(50, 100, 10, 0, 15));
        Assert.NotEqual(0, Covered(surface));

        surface.Clear();

        Assert.Equal(0, Covered(surface));
        Assert.Equal(0, surface.StrokeCount);
        Assert.Equal(0, surface.SegmentCount);
        Assert.Equal(0, surface.PointCount);
        Assert.True(surface.TryTakeDirty(out Int32Rect rect));
        Assert.Equal(new Int32Rect(0, 0, 300, 200), rect);

        // После очистки можно рисовать снова.
        surface.Add(TestSamples.Stroke(50, 100, 10, 0, 15));
        Assert.Equal(1, surface.StrokeCount);
    }

    [Fact]
    public void Dirty_rectangle_covers_every_changed_pixel_and_is_reset_when_taken()
    {
        var surface = new InkSurface(500, 300);
        Assert.False(surface.TryTakeDirty(out _));

        surface.Add(TestSamples.Stroke(100, 80, 8, 6, 20));
        Assert.True(surface.TryTakeDirty(out Int32Rect rect));
        Assert.False(surface.TryTakeDirty(out _));

        for (int y = 0; y < surface.HeightPx; y++)
        {
            for (int x = 0; x < surface.WidthPx; x++)
            {
                if ((At(surface, x, y) >> 24) != 0)
                {
                    Assert.True(x >= rect.X && x < rect.X + rect.Width && y >= rect.Y && y < rect.Y + rect.Height, $"pixel ({x},{y}) is outside {rect}");
                }
            }
        }

        // Прямоугольник небольшой: не весь буфер.
        Assert.True(rect.Width * rect.Height < 500 * 300 / 2);
    }

    [Fact]
    public void Resize_redraws_the_stored_strokes()
    {
        var surface = new InkSurface(300, 200);
        surface.Add(TestSamples.Stroke(50, 100, 10, 0, 15));
        int before = Covered(surface);

        surface.Resize(600, 400);

        Assert.Equal(600, surface.WidthPx);
        Assert.Equal(before, Covered(surface));
        Assert.True((At(surface, 120, 100) >> 24) == 255);
        Assert.True(surface.TryTakeDirty(out Int32Rect rect));
        Assert.Equal(new Int32Rect(0, 0, 600, 400), rect);
    }

    [Fact]
    public void Points_outside_the_surface_are_clipped_without_errors()
    {
        var surface = new InkSurface(200, 100);
        surface.Add(TestSamples.Stroke(-80, 50, 20, 0, 20)); // от −80 до +300 по X

        Assert.True((At(surface, 100, 50) >> 24) == 255);
        Assert.Equal(1, surface.StrokeCount);
    }

    [Fact]
    public void Hot_path_does_not_allocate_over_1000_batches()
    {
        var surface = new InkSurface(1920, 1080) { PressureEnabled = true };
        var batch = new PenSample[4];

        // Прогрев: JIT, таблицы, первая запись в буфер.
        for (int i = 0; i < 400; i++)
        {
            FillBatch(batch, i);
            surface.Add(batch, i);
        }

        surface.Clear();
        surface.TryTakeDirty(out _);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            FillBatch(batch, i);
            surface.Add(batch, i);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
        Assert.True(surface.SegmentCount > 3000, "the batches really were drawn");
    }

    /// <summary>Пачка из 4 точек волнистой траектории: 50 пачек — один штрих (Down в первой, Up в последней).</summary>
    private static void FillBatch(PenSample[] batch, int index)
    {
        int inStroke = index % 50;
        for (int k = 0; k < batch.Length; k++)
        {
            int step = (inStroke * batch.Length) + k;
            double x = 100 + (step * 7.0);
            double y = 540 + (Math.Sin(step * 0.09) * 200) + ((index / 50) * 3);
            float pressure = 0.3f + (0.6f * (float)(0.5 + (0.5 * Math.Sin(step * 0.05))));
            PenPhase phase = PenPhase.Move;
            if (inStroke == 0 && k == 0)
            {
                phase = PenPhase.Down;
            }
            else if (inStroke == 49 && k == batch.Length - 1)
            {
                phase = PenPhase.Up;
            }

            batch[k] = new PenSample((index * 20000L) + (k * 5000L), x, y, pressure, 0, 0, phase, PenButtons.None, SampleFlags.None, 1);
        }
    }
}
