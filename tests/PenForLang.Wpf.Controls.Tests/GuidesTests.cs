using PenForLang.Ink;
using PenForLang.Wpf.Controls.Tests.Support;

namespace PenForLang.Wpf.Controls.Tests;

public class GuidesTests
{
    private const double PxPerMm = 7.5;

    private static GuideFrame FrameFor(double dpiScale, double xHeightMm = 15, double originMm = 20, double baselineMm = 60, double? slant = null)
    {
        var mapper = new SurfaceMapper(new PenSurface(0, 0, 1920, 1080), PxPerMm, dpiScale);
        return GuideFrame.FromMm(mapper, Shipped.Style.Id, originMm, baselineMm, xHeightMm, 3.0, slant);
    }

    private static GuideSegment Single(IReadOnlyList<GuideSegment> segments, GuideKind kind) => segments.Single(s => s.Kind == kind);

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void Row_size_in_physical_pixels_does_not_depend_on_dpi_scale(double dpi)
    {
        GuideFrame frame = FrameFor(dpi);
        IReadOnlyList<GuideSegment> segments = GuidesGeometry.Compute(frame, Shipped.Style, dpi, 1600, 800);

        double baselinePx = Single(segments, GuideKind.Baseline).Y1 * dpi;
        double xHeightPx = Single(segments, GuideKind.XHeight).Y1 * dpi;
        double ascenderPx = Single(segments, GuideKind.Ascender).Y1 * dpi;
        double descenderPx = Single(segments, GuideKind.Descender).Y1 * dpi;

        // 15 мм × 7,5 px/мм = 112,5 физических px при любом масштабе Windows.
        Assert.Equal(60 * PxPerMm, baselinePx, 6);
        Assert.Equal(15 * PxPerMm, baselinePx - xHeightPx, 6);
        Assert.Equal(2 * 15 * PxPerMm, baselinePx - ascenderPx, 6);
        Assert.Equal(-15 * PxPerMm, baselinePx - descenderPx, 6);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void Dip_geometry_is_physical_divided_by_dpi_once(double dpi)
    {
        GuideFrame frame = FrameFor(dpi);
        IReadOnlyList<GuideSegment> segments = GuidesGeometry.Compute(frame, Shipped.Style, dpi, 1600, 800);

        // Двойное масштабирование дало бы 60·7,5·dpi / dpi = 450 при любом dpi; верно — 450 / dpi.
        Assert.Equal(60 * PxPerMm / dpi, Single(segments, GuideKind.Baseline).Y1, 6);
        Assert.Equal(15 * PxPerMm / dpi, Single(segments, GuideKind.Baseline).Y1 - Single(segments, GuideKind.XHeight).Y1, 6);
    }

    [Fact]
    public void Horizontal_lines_span_the_whole_layer()
    {
        IReadOnlyList<GuideSegment> segments = GuidesGeometry.Compute(FrameFor(1.0), Shipped.Style, 1.0, 1234, 800);

        foreach (GuideSegment s in segments.Where(s => s.Kind != GuideKind.Slant))
        {
            Assert.Equal(0, s.X1);
            Assert.Equal(1234, s.X2);
            Assert.Equal(s.Y1, s.Y2);
        }
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void Slant_lines_lean_right_by_the_style_angle(double dpi)
    {
        GuideFrame frame = FrameFor(dpi); // slantDeg берётся из стиля (20°)
        IReadOnlyList<GuideSegment> slants = GuidesGeometry.Compute(frame, Shipped.Style, dpi, 1600, 800).Where(s => s.Kind == GuideKind.Slant).ToList();

        Assert.NotEmpty(slants);
        double expectedTan = Math.Tan(Shipped.Style.SlantDeg!.Value * Math.PI / 180);
        foreach (GuideSegment s in slants)
        {
            Assert.True(s.Y2 < s.Y1, "the line must go from the descender (low) to the ascender (high)");
            Assert.Equal(expectedTan, (s.X2 - s.X1) / (s.Y1 - s.Y2), 6);
            Assert.Equal(3 * 15 * PxPerMm / dpi, s.Y1 - s.Y2, 6); // от десцендера (−1) до асцендера (2) = 3 x-height
        }
    }

    [Fact]
    public void Slant_lines_are_spaced_by_the_requested_step_in_x_heights()
    {
        GuideFrame frame = FrameFor(1.0);
        List<GuideSegment> slants = GuidesGeometry.Compute(frame, Shipped.Style, 1.0, 1600, 800, slantSpacingXHeights: 0.5)
            .Where(s => s.Kind == GuideKind.Slant).ToList();

        for (int i = 1; i < slants.Count; i++)
        {
            Assert.Equal(0.5 * 15 * PxPerMm, slants[i].X1 - slants[i - 1].X1, 6);
        }
    }

    [Fact]
    public void Frame_slant_overrides_the_style_slant_and_zero_slant_has_no_slant_lines()
    {
        GuideFrame vertical = FrameFor(1.0, slant: 0);
        Assert.DoesNotContain(GuidesGeometry.Compute(vertical, Shipped.Style, 1.0, 1600, 800), s => s.Kind == GuideKind.Slant);

        GuideFrame steep = FrameFor(1.0, slant: 30);
        GuideSegment slant = GuidesGeometry.Compute(steep, Shipped.Style, 1.0, 1600, 800).First(s => s.Kind == GuideKind.Slant);
        Assert.Equal(Math.Tan(30 * Math.PI / 180), (slant.X2 - slant.X1) / (slant.Y1 - slant.Y2), 6);
    }

    [Fact]
    public void Repeated_rows_add_lines_at_the_line_spacing()
    {
        GuideFrame frame = FrameFor(1.0, baselineMm: 40); // межстрочный интервал 3 x-height = 45 мм = 337,5 px
        IReadOnlyList<GuideSegment> rows = GuidesGeometry.Compute(frame, Shipped.Style, 1.0, 1600, 1000, repeatRows: true);

        double[] baselines = rows.Where(s => s.Kind == GuideKind.Baseline).Select(s => s.Y1).Order().ToArray();
        Assert.True(baselines.Length >= 3);
        for (int i = 1; i < baselines.Length; i++)
        {
            Assert.Equal(3 * 15 * PxPerMm, baselines[i] - baselines[i - 1], 6);
        }

        Assert.Single(GuidesGeometry.Compute(frame, Shipped.Style, 1.0, 1600, 1000), s => s.Kind == GuideKind.Baseline);
    }

    [Fact]
    public void Empty_layer_and_invalid_arguments()
    {
        Assert.Empty(GuidesGeometry.Compute(FrameFor(1.0), Shipped.Style, 1.0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => GuidesGeometry.Compute(FrameFor(1.0), Shipped.Style, 0, 100, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => GuidesGeometry.Compute(FrameFor(1.0), Shipped.Style, 1.0, 100, 100, slantSpacingXHeights: 0));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void Layer_draws_the_baseline_on_the_expected_physical_pixel_row(double dpi)
    {
        double baselinePx = 0;
        int rowsWithInk = 0;
        Sta.Run(() =>
        {
            GuideFrame frame = FrameFor(dpi, slant: 0); // без наклонных линий: между горизонталями пусто
            baselinePx = frame.BaselineYPx;
            var layer = new GuidesLayer { Guides = frame, Manifest = Shipped.Style, DpiScale = dpi };
            (byte[] data, int width, int height) = Pixels.Render(layer, 1200 / dpi, 800 / dpi, dpi);
            int y0 = (int)Math.Round(baselinePx);
            foreach (int y in new[] { y0 - 2, y0 - 1, y0, y0 + 1 })
            {
                if (Pixels.Alpha(data, width, 600, y) > 40)
                {
                    rowsWithInk++;
                }
            }

            // Между линиями (в двух строках пикселей над базовой линией, но ниже x-height) в этом столбце пусто.
            Assert.Equal(0, Pixels.Alpha(data, width, 600, (int)(baselinePx - 60)));
            Assert.True(height > baselinePx);
        });

        Assert.True(rowsWithInk >= 1, "baseline pixels must be on the physical row of the guide frame");
    }

    [Fact]
    public void Layer_segments_follow_its_properties()
    {
        Sta.Run(() =>
        {
            GuideFrame frame = FrameFor(1.5);
            var layer = new GuidesLayer { Guides = frame, Manifest = Shipped.Style, DpiScale = 1.5 };
            layer.Measure(new System.Windows.Size(900, 600));
            layer.Arrange(new System.Windows.Rect(0, 0, 900, 600));

            Assert.Equal(1.5, layer.EffectiveDpiScale);
            Assert.Equal(GuidesGeometry.Compute(frame, Shipped.Style, 1.5, 900, 600), layer.Segments);

            layer.Guides = null;
            Assert.Empty(layer.Segments);
        });
    }
}
