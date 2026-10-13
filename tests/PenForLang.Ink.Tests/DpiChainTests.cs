using PenForLang.Core;

namespace PenForLang.Ink.Tests;

/// <summary>
/// Сквозные проверки A12: линейка в мм или DIP → <see cref="GuideFrame"/> (px) → точка пера в физических экранных px →
/// (u, v) линейки. Ожидаемые (u, v) вычислены в миллиметрах и не зависят от DPI, поэтому тест падает, если где-либо в цепочке
/// DPI-масштаб убрать, удвоить или применить не туда.
/// </summary>
public class DpiChainTests
{
    private const double PxPerMm = 7.5;

    // Линейка на холсте (мм от его угла): x0 = 10, baseline = 40, x-height = 15 (7,5 px/мм → 112,5 px).
    private const double X0Mm = 10;
    private const double BaselineMm = 40;
    private const double XHeightMm = 15;

    private static readonly StyleId Style = new("zaner-bloser");

    /// <summary>Фейковый WPF/ОС: PointToScreen отдаёт ФИЗИЧЕСКИЕ px — начало монитора плюс DIP, умноженный на DPI монитора.</summary>
    private sealed record FakeMonitor(double OriginXPx, double OriginYPx, double DpiScale)
    {
        public (double X, double Y) PointToScreen(double windowLeftDip, double windowTopDip, double canvasXDip, double canvasYDip) =>
            (OriginXPx + ((windowLeftDip + canvasXDip) * DpiScale), OriginYPx + ((windowTopDip + canvasYDip) * DpiScale));
    }

    public static TheoryData<double, double, double> Monitors => new()
    {
        { 1.0, 0, 0 },
        { 1.25, 0, 0 },
        { 1.5, 0, 0 },
        { 1.5, -1920, -200 }, // монитор пера слева от основного, с отрицательным началом
        { 1.25, -2560, 0 },
        { 1.0, 3840, -300 }, // справа и выше
    };

    private const double WindowLeftDip = 120.5;
    private const double WindowTopDip = 40.25;
    private const double CanvasWidthMm = 150;
    private const double CanvasHeightMm = 90;

    private static SurfaceMapper CreateMapper(FakeMonitor monitor)
    {
        double wDip = CanvasWidthMm * PxPerMm / monitor.DpiScale;
        double hDip = CanvasHeightMm * PxPerMm / monitor.DpiScale;
        (double x0, double y0) = monitor.PointToScreen(WindowLeftDip, WindowTopDip, 0, 0);
        (double x1, double y1) = monitor.PointToScreen(WindowLeftDip, WindowTopDip, wDip, hDip);
        return new SurfaceMapper(PenSurface.FromScreenCorners(x0, y0, x1, y1), PxPerMm, monitor.DpiScale);
    }

    /// <summary>Что вернёт ОС для пера, стоящего в точке холста (мм от его угла).</summary>
    private static (double X, double Y) PenAtScreenPx(FakeMonitor monitor, double xMm, double yMm) =>
        monitor.PointToScreen(
            WindowLeftDip,
            WindowTopDip,
            xMm * PxPerMm / monitor.DpiScale,
            yMm * PxPerMm / monitor.DpiScale);

    [Theory]
    [MemberData(nameof(Monitors))]
    public void Guides_in_mm_and_pen_in_screen_px_give_dpi_independent_ruler_coordinates(double dpi, double originX, double originY)
    {
        var monitor = new FakeMonitor(originX, originY, dpi);
        SurfaceMapper mapper = CreateMapper(monitor);
        GuideFrame guides = GuideFrame.FromMm(mapper, Style, X0Mm, BaselineMm, XHeightMm, 3.0);

        // Линейка в px не зависит от DPI Windows: 10 мм = 75 px, 40 мм = 300 px, 15 мм = 112,5 px.
        Assert.Equal(75, guides.OriginXPx, 9);
        Assert.Equal(300, guides.BaselineYPx, 9);
        Assert.Equal(112.5, guides.XHeightPx, 9);

        foreach ((double xMm, double yMm, double u, double v) in new[]
        {
            (10.0, 40.0, 0.0, 0.0), // начало строки на baseline
            (17.5, 25.0, 0.5, 1.0), // 7,5 мм правее начала и 15 мм выше baseline
            (25.0, 40.0, 1.0, 0.0),
            (10.0, 47.5, 0.0, -0.5), // ниже baseline (хвост буквы)
            (85.0, 10.0, 5.0, 2.0),
        })
        {
            (double sx, double sy) = PenAtScreenPx(monitor, xMm, yMm);
            (double x, double y) = mapper.ScreenToSurface(sx, sy);
            (double actualU, double actualV) = RulerProjection.ToRuler(guides, x, y);

            Assert.Equal(u, actualU, 9);
            Assert.Equal(v, actualV, 9);
        }
    }

    [Theory]
    [MemberData(nameof(Monitors))]
    public void Guides_in_dip_scale_by_dpi_and_reach_the_same_ruler_coordinates(double dpi, double originX, double originY)
    {
        var monitor = new FakeMonitor(originX, originY, dpi);
        SurfaceMapper mapper = CreateMapper(monitor);

        // Те же 10 / 40 / 15 мм, но заданные в DIP разметки WPF этого монитора.
        double toDip = PxPerMm / dpi;
        GuideFrame guides = GuideFrame.FromDip(mapper, Style, X0Mm * toDip, BaselineMm * toDip, XHeightMm * toDip, 3.0);

        Assert.Equal(75, guides.OriginXPx, 9);
        Assert.Equal(300, guides.BaselineYPx, 9);
        Assert.Equal(112.5, guides.XHeightPx, 9);

        (double sx, double sy) = PenAtScreenPx(monitor, 17.5, 25.0);
        (double x, double y) = mapper.ScreenToSurface(sx, sy);
        (double u, double v) = RulerProjection.ToRuler(guides, x, y);
        Assert.Equal(0.5, u, 9);
        Assert.Equal(1.0, v, 9);
    }

    [Theory]
    [MemberData(nameof(Monitors))]
    public void Full_attempt_chain_gives_the_same_ruler_ink_at_every_dpi(double dpi, double originX, double originY)
    {
        var monitor = new FakeMonitor(originX, originY, dpi);
        SurfaceMapper mapper = CreateMapper(monitor);
        GuideFrame guides = GuideFrame.FromMm(mapper, Style, X0Mm, BaselineMm, XHeightMm, 3.0);

        (double X, double Y)[] penMm = [(10.0, 40.0), (17.5, 25.0), (25.0, 40.0)];
        PenSample[] samples =
        [
            .. penMm.Select((p, i) =>
            {
                (double sx, double sy) = PenAtScreenPx(monitor, p.X, p.Y);
                (double x, double y) = mapper.ScreenToSurface(sx, sy);
                PenPhase phase = i == 0 ? PenPhase.Down : i == penMm.Length - 1 ? PenPhase.Up : PenPhase.Move;
                return TestData.Sample(1000 * i, x, y, phase);
            }),
        ];
        var attempt = new InkAttempt([new InkStroke(0, [.. samples])], guides, TestData.Device(), null, InkCompletion.Pause, 0, []);

        RulerInk ink = RulerProjection.Project(attempt);

        Assert.Equal(XHeightMm, ink.MmPerUnit, 9);
        Assert.Equal([0.0, 0.5, 1.0], ink.Strokes[0].Points.Select(p => Math.Round(p.U, 9)));
        Assert.Equal([0.0, 1.0, 0.0], ink.Strokes[0].Points.Select(p => Math.Round(p.V, 9)));
    }

    [Theory]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void Control_a_pipeline_with_a_doubled_or_missing_dpi_factor_is_detected_by_the_same_expectations(double dpi)
    {
        var monitor = new FakeMonitor(-1920, 0, dpi);
        SurfaceMapper mapper = CreateMapper(monitor);
        GuideFrame guides = GuideFrame.FromMm(mapper, Style, X0Mm, BaselineMm, XHeightMm, 3.0);
        (double sx, double sy) = PenAtScreenPx(monitor, 17.5, 25.0);
        (double x, double y) = mapper.ScreenToSurface(sx, sy);

        // Ошибочные варианты: лишнее ×dpi у точки или у линейки, деление на dpi.
        (double uDoubled, _) = RulerProjection.ToRuler(guides, x * dpi, y * dpi);
        (double uDivided, _) = RulerProjection.ToRuler(guides, x / dpi, y / dpi);
        GuideFrame scaledGuides = new(guides.OriginXPx * dpi, guides.BaselineYPx * dpi, guides.XHeightPx * dpi, guides.PxPerMm, 3.0, null, Style);
        (double uGuidesDoubled, _) = RulerProjection.ToRuler(scaledGuides, x, y);

        Assert.NotEqual(0.5, uDoubled, 3);
        Assert.NotEqual(0.5, uDivided, 3);
        Assert.NotEqual(0.5, uGuidesDoubled, 3);
    }

    [Fact]
    public void FromMm_ignores_dpi_and_FromDip_uses_it()
    {
        var surface = new PenSurface(0, 0, 1000, 1000);
        var at100 = new SurfaceMapper(surface, PxPerMm, 1.0);
        var at150 = new SurfaceMapper(surface, PxPerMm, 1.5);

        Assert.Equal(
            GuideFrame.FromMm(at100, Style, 10, 40, 15, 3.0),
            GuideFrame.FromMm(at150, Style, 10, 40, 15, 3.0));
        Assert.Equal(75, GuideFrame.FromDip(at100, Style, 75, 300, 112.5, 3.0).OriginXPx, 9);
        Assert.Equal(112.5, GuideFrame.FromDip(at150, Style, 50, 200, 75, 3.0).XHeightPx, 9);
        Assert.Equal(7.5, GuideFrame.FromDip(at150, Style, 50, 200, 75, 3.0).PxPerMm);
    }

    [Fact]
    public void FromMm_and_FromDip_validate_arguments_and_keep_slant_and_spacing()
    {
        var mapper = new SurfaceMapper(new PenSurface(0, 0, 100, 100), PxPerMm, 1.0);

        GuideFrame guides = GuideFrame.FromMm(mapper, Style, 1, 2, 3, 2.5, 12);

        Assert.Equal(2.5, guides.LineSpacing);
        Assert.Equal(12, guides.SlantDeg);
        Assert.Equal(Style, guides.Style);
        Assert.Throws<ArgumentException>(() => GuideFrame.FromMm(mapper, Style, 1, 2, 0, 3));
        Assert.Throws<ArgumentException>(() => GuideFrame.FromDip(mapper, Style, 1, 2, -1, 3));
        Assert.Throws<ArgumentException>(() => GuideFrame.FromMm(mapper, default, 1, 2, 3, 3));
        Assert.Throws<ArgumentNullException>(() => GuideFrame.FromMm(null!, Style, 1, 2, 3, 3));
    }
}
