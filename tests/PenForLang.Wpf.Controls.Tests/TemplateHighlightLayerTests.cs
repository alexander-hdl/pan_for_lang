using PenForLang.Core.Geometry;
using PenForLang.Ink;
using PenForLang.Testing;
using PenForLang.Wpf.Controls.Tests.Support;

namespace PenForLang.Wpf.Controls.Tests;

public class TemplateHighlightLayerTests
{
    private const double PxPerMm = 7.5;

    private static GuideFrame Frame(double dpi)
    {
        var mapper = new SurfaceMapper(new PenSurface(0, 0, 1920, 1080), PxPerMm, dpi);
        return GuideFrame.FromMm(mapper, Shipped.Style.Id, 30, 50, 15, 3.0);
    }

    [Fact]
    public void The_pulse_curve_is_smooth_periodic_and_bounded()
    {
        Assert.Equal(0.0, PulseCurve.Phase(0, 800), 9);
        Assert.Equal(1.0, PulseCurve.Phase(400, 800), 9);
        Assert.Equal(0.0, PulseCurve.Phase(800, 800), 9);
        Assert.Equal(PulseCurve.Phase(130, 800), PulseCurve.Phase(930, 800), 9);
        for (double t = 0; t <= 1600; t += 37)
        {
            Assert.InRange(PulseCurve.Phase(t, 800), 0.0, 1.0);
        }

        // Без резких скачков: за 20 мс фаза меняется мало.
        for (double t = 0; t < 1600; t += 20)
        {
            Assert.True(Math.Abs(PulseCurve.Phase(t + 20, 800) - PulseCurve.Phase(t, 800)) < 0.2);
        }

        Assert.Equal(0.0, PulseCurve.Phase(100, 0));
        Assert.Equal(0.0, PulseCurve.Phase(double.NaN, 800));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void A_highlighted_polyline_is_drawn_on_the_template_and_nowhere_else(double dpi)
    {
        Sta.Run(() =>
        {
            GuideFrame frame = Frame(dpi);
            var layer = new TemplateHighlightLayer(new FakeMonotonicClock()) { Guides = frame, DpiScale = dpi };
            layer.Show([[new Vec2(0, 0), new Vec2(2, 0)]], null, HighlightColor.Warm, 800, 0);

            (byte[] data, int width, _) = Pixels.Render(layer, 1920 / dpi, 1080 / dpi, dpi);

            // Линия по baseline от u = 0 до 2 (x-height = 15 мм): середина закрашена, далеко от неё — нет, и точек старта нет.
            double midX = frame.OriginXPx + frame.XHeightPx;
            Assert.True(Pixels.Alpha(data, width, (int)midX, (int)frame.BaselineYPx) > 0x40);
            Assert.Equal(0, Pixels.Alpha(data, width, (int)midX, (int)(frame.BaselineYPx - (3 * frame.XHeightPx))));
            Assert.Equal(0, Pixels.Alpha(data, width, (int)(frame.OriginXPx - (2 * frame.XHeightPx)), (int)frame.BaselineYPx));
            Assert.False(layer.IsPulsing);
            Assert.True(layer.HasContent);
        });
    }

    [Fact]
    public void The_start_point_pulses_for_the_given_time_then_disappears_without_a_highlight()
    {
        Sta.Run(() =>
        {
            var clock = new FakeMonotonicClock();
            GuideFrame frame = Frame(1.0);
            var layer = new TemplateHighlightLayer(clock) { Guides = frame, DpiScale = 1.0 };
            layer.Show([], new Vec2(0.5, 0.5), HighlightColor.Warm, 800, 1600);

            Assert.True(layer.IsPulsing);
            (byte[] shown, int width, _) = Pixels.Render(layer, 1920, 1080, 1.0);
            double x = frame.OriginXPx + (0.5 * frame.XHeightPx);
            double y = frame.BaselineYPx - (0.5 * frame.XHeightPx);
            Assert.True(Pixels.Alpha(shown, width, (int)x, (int)y) > 0x40);

            clock.Advance(TimeSpan.FromMilliseconds(1500));
            layer.FrameTick();
            Assert.True(layer.IsPulsing);

            clock.Advance(TimeSpan.FromMilliseconds(200));
            layer.FrameTick();

            // Мягкий сигнал закончился сам: точки нет, слоя «ничего» не остаётся.
            Assert.False(layer.IsPulsing);
            Assert.False(layer.HasContent);
            (byte[] gone, _, _) = Pixels.Render(layer, 1920, 1080, 1.0);
            Assert.Equal(0, Pixels.CountAlpha(gone));
        });
    }

    [Fact]
    public void With_zero_duration_the_pulse_stays_until_cleared_and_the_color_is_not_red()
    {
        Sta.Run(() =>
        {
            var clock = new FakeMonotonicClock();
            GuideFrame frame = Frame(1.0);
            var layer = new TemplateHighlightLayer(clock) { Guides = frame, DpiScale = 1.0 };
            layer.Show([[new Vec2(0, 0), new Vec2(1, 0)]], new Vec2(0, 0), HighlightColor.Positive, 800, 0);

            clock.Advance(TimeSpan.FromSeconds(30));
            layer.FrameTick();
            Assert.True(layer.IsPulsing);
            Assert.Equal(HighlightColor.Positive, layer.Tone);

            // Любой закрашенный пиксель подсветки зелёный или янтарный: зелёного канала не меньше половины красного (для красного цвета это не так).
            (byte[] data, _, _) = Pixels.Render(layer, 1920, 1080, 1.0);
            int painted = 0;
            for (int i = 0; i < data.Length; i += 4)
            {
                if (data[i + 3] > 0x40)
                {
                    painted++;
                    Assert.True(data[i + 1] * 2 >= data[i + 2], "the highlight must not be red (BGRA: [1] is green, [2] is red)");
                }
            }

            Assert.True(painted > 100);

            layer.Clear();
            Assert.False(layer.IsPulsing);
            Assert.False(layer.HasContent);
        });
    }

    [Fact]
    public void The_start_point_stays_green_and_only_the_ring_is_amber_then_the_ring_freezes_after_the_pulses()
    {
        Sta.Run(() =>
        {
            var clock = new FakeMonotonicClock();
            GuideFrame frame = Frame(1.0);
            var layer = new TemplateHighlightLayer(clock) { Guides = frame, DpiScale = 1.0 };
            var point = new Vec2(0.5, 0.5);
            layer.Show([], point, HighlightColor.Warm, 800, 2400, keepStatic: true);

            Assert.True(layer.IsPulsing);
            Assert.True(layer.IsListeningToFrames);
            (byte[] pulsing, int width, _) = Pixels.Render(layer, 1920, 1080, 1.0);
            int cx = (int)(frame.OriginXPx + (0.5 * frame.XHeightPx));
            int cy = (int)(frame.BaselineYPx - (0.5 * frame.XHeightPx));
            AssertGreenDot(pulsing, width, cx, cy);

            // Кольцо вокруг точки янтарное: найдём закрашенный пиксель за краем точки, он красный/зелёный как янтарь (R заметно больше B).
            Assert.True(HasAmber(pulsing, width, cx, cy, frame.PxPerMm));

            clock.Advance(TimeSpan.FromMilliseconds(2500)); // три периода прошли
            layer.FrameTick();

            Assert.False(layer.IsPulsing);
            Assert.False(layer.IsListeningToFrames); // на кадры композитора больше не подписан
            Assert.True(layer.HasContent);
            (byte[] frozen, _, _) = Pixels.Render(layer, 1920, 1080, 1.0);
            AssertGreenDot(frozen, width, cx, cy);
            Assert.True(HasAmber(frozen, width, cx, cy, frame.PxPerMm)); // статичное кольцо осталось
        });
    }

    [Fact]
    public void The_layer_listens_to_frames_only_while_pulsing_and_a_plain_dot_has_no_ring()
    {
        Sta.Run(() =>
        {
            var clock = new FakeMonotonicClock();
            GuideFrame frame = Frame(1.0);
            var layer = new TemplateHighlightLayer(clock) { Guides = frame, DpiScale = 1.0 };

            Assert.False(layer.IsListeningToFrames);
            layer.Show([[new Vec2(0, 0), new Vec2(1, 0)]], null, HighlightColor.Warm, 800, 0); // без точки: мигать нечему
            Assert.False(layer.IsListeningToFrames);

            layer.Show([], new Vec2(0.5, 0.5), HighlightColor.Warm, 800, 1600);
            Assert.True(layer.IsListeningToFrames);
            layer.Clear();
            Assert.False(layer.IsListeningToFrames);

            layer.ShowDot(new Vec2(0.5, 0.5));
            Assert.False(layer.IsListeningToFrames);
            Assert.False(layer.IsPulsing);
            Assert.True(layer.HasContent);
            (byte[] data, int width, _) = Pixels.Render(layer, 1920, 1080, 1.0);
            int cx = (int)(frame.OriginXPx + (0.5 * frame.XHeightPx));
            int cy = (int)(frame.BaselineYPx - (0.5 * frame.XHeightPx));
            AssertGreenDot(data, width, cx, cy);
            Assert.False(HasAmber(data, width, cx, cy, frame.PxPerMm));
        });
    }

    private static void AssertGreenDot(byte[] data, int width, int x, int y)
    {
        int i = (((y * width) + x) * 4);
        Assert.True(data[i + 3] > 0xE0, "the dot is opaque");
        Assert.True(data[i + 1] > data[i + 2] && data[i + 1] > data[i], "BGRA: green must dominate in the dot");
    }

    /// <summary>Есть ли на кольце (за краем точки радиусом ~2,6 мм) заметно янтарный пиксель: красного заметно больше синего.</summary>
    private static bool HasAmber(byte[] data, int width, int cx, int cy, double pxPerMm)
    {
        int from = (int)(3.0 * pxPerMm);
        int to = (int)(9.0 * pxPerMm);
        for (int dx = from; dx <= to; dx++)
        {
            foreach ((int ox, int oy) in new[] { (dx, 0), (-dx, 0), (0, dx), (0, -dx) })
            {
                int i = ((((cy + oy) * width) + cx + ox) * 4);
                if (data[i + 3] > 0x20 && data[i + 2] > data[i] + 0x30 && data[i + 2] > data[i + 1])
                {
                    return true;
                }
            }
        }

        return false;
    }
}
