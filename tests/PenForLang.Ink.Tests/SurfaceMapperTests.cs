namespace PenForLang.Ink.Tests;

/// <summary>
/// Проверки A12 на фейковом WPF: <c>PointToScreen</c> возвращает ФИЗИЧЕСКИЕ пиксели (экранное начало монитора плюс DIP, умноженный
/// на DPI-масштаб этого монитора), и SurfaceMapper не умножает на DPI повторно.
/// </summary>
public class SurfaceMapperTests
{
    private const double Eps = 1e-9;

    private static readonly double[] DpiScales = [1.0, 1.25, 1.5];

    /// <summary>Фейковый монитор: его начало в физических px виртуального экрана и DPI-масштаб.</summary>
    private sealed record FakeMonitor(double OriginXPx, double OriginYPx, double DpiScale)
    {
        /// <summary>Что вернул бы <c>canvas.PointToScreen</c> для точки холста (DIP) при окне в DIP-позиции (leftDip, topDip) монитора.</summary>
        public (double X, double Y) PointToScreen(double windowLeftDip, double windowTopDip, double canvasXDip, double canvasYDip) =>
            (OriginXPx + ((windowLeftDip + canvasXDip) * DpiScale), OriginYPx + ((windowTopDip + canvasYDip) * DpiScale));
    }

    private static SurfaceMapper MapperFor(FakeMonitor monitor, double left, double top, double wDip, double hDip, double pxPerMm)
    {
        (double x0, double y0) = monitor.PointToScreen(left, top, 0, 0);
        (double x1, double y1) = monitor.PointToScreen(left, top, wDip, hDip);
        return new SurfaceMapper(PenSurface.FromScreenCorners(x0, y0, x1, y1), pxPerMm, monitor.DpiScale);
    }

    private static void AssertPoint((double X, double Y) expected, (double X, double Y) actual)
    {
        Assert.Equal(expected.X, actual.X, Eps);
        Assert.Equal(expected.Y, actual.Y, Eps);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void Surface_size_is_physical_and_screen_to_surface_does_not_scale_by_dpi(double dpi)
    {
        var monitor = new FakeMonitor(0, 0, dpi);
        SurfaceMapper mapper = MapperFor(monitor, 100, 50, 800, 600, 7.5);

        // Прямоугольник холста уже в физических px: 800 DIP * dpi.
        Assert.Equal(800 * dpi, mapper.Surface.WidthPx, Eps);
        Assert.Equal(600 * dpi, mapper.Surface.HeightPx, Eps);

        // Перо коснулось центра холста (400, 300 DIP); ОС отдала физические экранные px.
        (double sx, double sy) = monitor.PointToScreen(100, 50, 400, 300);
        AssertPoint((400 * dpi, 300 * dpi), mapper.ScreenToSurface(sx, sy));

        // Двойное масштабирование дало бы 400 * dpi * dpi — убеждаемся, что это не так (для dpi != 1 значения различаются).
        if (dpi != 1.0)
        {
            Assert.NotEqual(400 * dpi * dpi, mapper.ScreenToSurface(sx, sy).X, 3);
        }

        // В DIP получаем исходные 400, 300.
        (double xs, double ys) = mapper.ScreenToSurface(sx, sy);
        AssertPoint((400, 300), mapper.SurfaceToDip(xs, ys));
    }

    [Theory]
    [InlineData(1.0, 7.5)]
    [InlineData(1.25, 7.5)]
    [InlineData(1.5, 7.5)]
    [InlineData(1.5, 5.0)]
    [InlineData(1.0, 9.75)]
    public void Screen_surface_mm_and_dip_round_trip(double dpi, double pxPerMm)
    {
        var monitor = new FakeMonitor(1920, -120, dpi);
        SurfaceMapper mapper = MapperFor(monitor, 33.5, 12.25, 640, 480, pxPerMm);
        (double sx, double sy) = monitor.PointToScreen(33.5, 12.25, 123.4, 56.7);

        (double x, double y) = mapper.ScreenToSurface(sx, sy);
        AssertPoint((sx, sy), mapper.SurfaceToScreen(x, y));

        (double mx, double my) = mapper.SurfaceToMm(x, y);
        AssertPoint((x, y), mapper.MmToSurface(mx, my));
        AssertPoint((x / pxPerMm, y / pxPerMm), (mx, my));
        AssertPoint((sx, sy), mapper.MmToScreen(mx, my));
        AssertPoint((mx, my), mapper.ScreenToMm(sx, sy));

        (double dx, double dy) = mapper.SurfaceToDip(x, y);
        AssertPoint((123.4, 56.7), (dx, dy));
        AssertPoint((x, y), mapper.DipToSurface(dx, dy));
    }

    [Fact]
    public void The_same_physical_canvas_gives_identical_surface_coordinates_at_any_dpi()
    {
        // Монитор 1920x1080 физических px; холст 1200x800 физических px на (200, 100). Меняется только DPI Windows:
        // в DIP холст другой, физические координаты пера — те же. Поверхность не должна зависеть от DPI.
        (double X, double Y)[] results = [.. DpiScales.Select(dpi =>
        {
            var monitor = new FakeMonitor(0, 0, dpi);
            SurfaceMapper mapper = MapperFor(monitor, 200 / dpi, 100 / dpi, 1200 / dpi, 800 / dpi, 7.5);
            Assert.Equal(1200, mapper.Surface.WidthPx, 6);
            return mapper.ScreenToSurface(500, 400);
        })];

        Assert.All(results, r => AssertPoint((300, 300), r));
    }

    [Fact]
    public void Window_moved_to_a_monitor_with_other_dpi_needs_a_new_mapper_and_keeps_dip_consistent()
    {
        var primary = new FakeMonitor(0, 0, 1.0);
        var penMonitor = new FakeMonitor(1920, 0, 1.5);

        SurfaceMapper onPrimary = MapperFor(primary, 100, 100, 800, 600, 5.0);
        SurfaceMapper onPen = onPrimary.WithSurface(MapperFor(penMonitor, 100, 100, 800, 600, 7.5).Surface);

        // WithSurface не меняет PxPerMm и DPI — осознанно: смена монитора требует нового экземпляра.
        Assert.Equal(1.0, onPen.DpiScale);
        SurfaceMapper proper = MapperFor(penMonitor, 100, 100, 800, 600, 7.5);
        Assert.Equal(1.5, proper.DpiScale);

        // Одна и та же DIP-точка холста (400, 300) на мониторах с разным DPI даёт разные физические px, но тот же DIP.
        (double sx1, double sy1) = primary.PointToScreen(100, 100, 400, 300);
        (double sx2, double sy2) = penMonitor.PointToScreen(100, 100, 400, 300);
        AssertPoint((400, 300), onPrimary.SurfaceToDip(onPrimary.ScreenToSurface(sx1, sy1).X, onPrimary.ScreenToSurface(sx1, sy1).Y));
        AssertPoint((600, 450), proper.ScreenToSurface(sx2, sy2));
        AssertPoint((400, 300), proper.SurfaceToDip(proper.ScreenToSurface(sx2, sy2).X, proper.ScreenToSurface(sx2, sy2).Y));

        // Миллиметры берутся из PxPerMm монитора, а не из DPI.
        AssertPoint((600 / 7.5, 450 / 7.5), proper.ScreenToMm(sx2, sy2));
    }

    [Fact]
    public void Millimetres_follow_PxPerMm_independently_of_dpi()
    {
        var surface = new PenSurface(0, 0, 1920, 1080);
        var a = new SurfaceMapper(surface, 7.5, 1.0);
        var b = new SurfaceMapper(surface, 7.5, 1.5);

        AssertPoint(a.SurfaceToMm(75, 150), b.SurfaceToMm(75, 150));
        AssertPoint((10, 20), a.SurfaceToMm(75, 150));
    }

    [Fact]
    public void ContainsScreenPoint_includes_borders_and_excludes_outside()
    {
        var mapper = new SurfaceMapper(new PenSurface(100, 200, 50, 40), 7.5, 1.0);

        Assert.True(mapper.ContainsScreenPoint(100, 200));
        Assert.True(mapper.ContainsScreenPoint(150, 240));
        Assert.False(mapper.ContainsScreenPoint(99.9, 220));
        Assert.False(mapper.ContainsScreenPoint(120, 240.1));
    }

    [Fact]
    public void Invalid_arguments_are_rejected()
    {
        var surface = new PenSurface(0, 0, 10, 10);
        Assert.Throws<ArgumentException>(() => new SurfaceMapper(surface, 0, 1));
        Assert.Throws<ArgumentException>(() => new SurfaceMapper(surface, 7.5, 0));
        Assert.Throws<ArgumentException>(() => new SurfaceMapper(surface, double.NaN, 1));
        Assert.Throws<ArgumentException>(() => new SurfaceMapper(surface, 7.5, double.PositiveInfinity));
        Assert.Throws<ArgumentException>(() => new SurfaceMapper(new PenSurface(double.NaN, 0, 10, 10), 7.5, 1));
        Assert.Throws<ArgumentException>(() => PenSurface.FromScreenCorners(10, 10, 5, 20));
        Assert.Throws<ArgumentException>(() => PenSurface.FromScreenCorners(0, 0, double.NaN, 20));
    }

    [Fact]
    public void Default_PxPerMm_matches_the_architecture_value()
    {
        Assert.Equal(7.5, SurfaceMapper.DefaultPxPerMm);
    }
}
