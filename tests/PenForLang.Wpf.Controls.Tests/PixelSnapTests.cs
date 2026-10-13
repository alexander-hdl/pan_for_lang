using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PenForLang.Ink;
using PenForLang.Wpf.Controls.Tests.Support;

namespace PenForLang.Wpf.Controls.Tests;

public class PixelSnapTests
{
    [Theory]
    [InlineData(37.3, 1.25)]
    [InlineData(37.3, 1.5)]
    [InlineData(40.33, 1.5)]
    [InlineData(10.07, 1.25)]
    public void Correction_moves_a_position_to_a_whole_physical_pixel(double dip, double dpi)
    {
        double corrected = dip + PixelSnap.Correction(dip, dpi);

        double physical = corrected * dpi;
        Assert.Equal(Math.Round(physical), physical, 9);
        Assert.True(Math.Abs(PixelSnap.Correction(dip, dpi)) <= 0.5 / dpi + 1e-12);
    }

    /// <summary>Контейнер с полосой дробной высоты над слоем — как полоса кнопок над холстом при 125% и 150%.</summary>
    private static Grid Host(FrameworkElement layer, double stripDip)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(stripDip) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(layer, 1);
        grid.Children.Add(layer);
        grid.Measure(new Size(900, 600));
        grid.Arrange(new Rect(0, 0, 900, 600));
        grid.UpdateLayout();
        return grid;
    }

    private static double PhysicalTop(FrameworkElement layer, Grid host, double dpi) =>
        layer.TransformToAncestor(host).Transform(new Point(0, 0)).Y * dpi;

    [Theory]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void Live_ink_layer_below_a_fractional_strip_lands_on_a_whole_physical_pixel(double dpi)
    {
        Sta.Run(() =>
        {
            var layer = new LiveInkLayer { DpiScale = dpi, UseLayoutRounding = false };
            Grid host = Host(layer, 37.3);

            // Номинальная позиция (без сдвига страховки) дробная — воспроизведение F1: 37,3 DIP × 1,25/1,5 не целое число физических px.
            double nominal = PhysicalTop(layer, host, dpi) - (layer.SnapOffset.Y * dpi);
            Assert.NotEqual(Math.Round(nominal), nominal, 3);

            layer.SnapToPixelGrid();

            double after = PhysicalTop(layer, host, dpi);
            Assert.Equal(Math.Round(after), after, 6);
            Assert.True(Math.Abs(layer.SnapOffset.Y) <= 0.5 / dpi + 1e-9);

            // Повторный вызов ничего не меняет.
            layer.SnapToPixelGrid();
            Assert.Equal(after, PhysicalTop(layer, host, dpi), 9);
        });
    }

    [Theory]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void Guides_and_animator_layers_are_snapped_too(double dpi)
    {
        Sta.Run(() =>
        {
            var guides = new GuidesLayer { DpiScale = dpi, UseLayoutRounding = false };
            var animator = new GlyphAnimatorLayer { DpiScale = dpi, UseLayoutRounding = false };
            Grid hostA = Host(guides, 37.3);
            Grid hostB = Host(animator, 37.3);

            guides.SnapToPixelGrid();
            animator.SnapToPixelGrid();

            Assert.Equal(Math.Round(PhysicalTop(guides, hostA, dpi)), PhysicalTop(guides, hostA, dpi), 6);
            Assert.Equal(Math.Round(PhysicalTop(animator, hostB, dpi)), PhysicalTop(animator, hostB, dpi), 6);
        });
    }

    [Fact]
    public void Layout_updates_snap_the_layer_automatically_and_whole_pixel_positions_need_no_correction()
    {
        Sta.Run(() =>
        {
            var layer = new LiveInkLayer { DpiScale = 1.5, UseLayoutRounding = false };
            Grid host = Host(layer, 37.3);
            layer.UpdateLayout();
            Assert.Equal(Math.Round(PhysicalTop(layer, host, 1.5)), PhysicalTop(layer, host, 1.5), 6);

            var aligned = new LiveInkLayer { DpiScale = 1.5, UseLayoutRounding = false };
            Host(aligned, 40); // 40 DIP × 1,5 = 60 физических px — целое
            aligned.SnapToPixelGrid();
            Assert.Equal(0, aligned.SnapOffset.Y, 9);
        });
    }

    [Fact]
    public void A_layer_without_a_parent_is_left_alone()
    {
        Sta.Run(() =>
        {
            var layer = new LiveInkLayer { DpiScale = 1.5 };
            var transform = new TranslateTransform();

            Assert.False(PixelSnap.Apply(layer, transform, 1.5));
            Assert.False(PixelSnap.Apply(layer, transform, 0));
            Assert.Equal(0, transform.X);
        });
    }

    [Fact]
    public void Snapped_layer_renders_the_ink_on_the_same_physical_row_as_requested()
    {
        Sta.Run(() =>
        {
            // Слой в строке со смещением 37,3 DIP при 150%: после страховки растр не размыт — центр линии полностью непрозрачен.
            var layer = new LiveInkLayer { DpiScale = 1.5, UseLayoutRounding = false };
            layer.ConfigureSurface(450, 300, 1.5);
            Grid host = Host(layer, 37.3);
            layer.SnapToPixelGrid();
            layer.AddSamples(TestSamples.Stroke(50, 100, 10, 0, 21));
            layer.FrameTick();

            int dpiPx = (int)Math.Round(900 * 1.5);
            int heightPx = (int)Math.Round(600 * 1.5);
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(dpiPx, heightPx, 144, 144, PixelFormats.Pbgra32);
            bitmap.Render(host);
            var data = new byte[dpiPx * heightPx * 4];
            bitmap.CopyPixels(data, dpiPx * 4, 0);

            int top = (int)Math.Round(PhysicalTop(layer, host, 1.5));
            int inkRow = top + 100;
            Assert.True(Pixels.Alpha(data, dpiPx, 150, inkRow) > 250, "the stroke centre row must be fully opaque (no half-pixel blur)");
        });
    }
}
