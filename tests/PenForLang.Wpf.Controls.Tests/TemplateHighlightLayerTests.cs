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
}
