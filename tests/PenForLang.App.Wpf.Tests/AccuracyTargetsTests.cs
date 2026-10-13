namespace PenForLang.App.Wpf.Tests;

public class AccuracyTargetsTests
{
    [Fact]
    public void Grid_has_nine_targets_at_10_50_90_percent_row_by_row()
    {
        IReadOnlyList<(double X, double Y)> grid = AccuracyTargets.Grid(1920, 1080);

        Assert.Equal(9, grid.Count);
        Assert.Equal((192.0, 108.0), grid[0]);
        Assert.Equal((960.0, 108.0), grid[1]);
        Assert.Equal((1728.0, 108.0), grid[2]);
        Assert.Equal((960.0, 540.0), grid[4]);
        Assert.Equal((1728.0, 972.0), grid[8]);
    }

    [Fact]
    public void Grid_is_in_physical_pixels_so_it_does_not_depend_on_dpi_scale()
    {
        // 1920x1080 физических px при 150% — это 1280x720 DIP, но мишени считаются от физического размера (A12).
        Assert.Equal(AccuracyTargets.Grid(1920, 1080)[4], (960.0, 540.0));
    }

    [Fact]
    public void Nearest_returns_the_target_number_and_the_offset_from_its_centre()
    {
        IReadOnlyList<(double X, double Y)> grid = AccuracyTargets.Grid(1920, 1080);

        (int number, double dx, double dy) = AccuracyTargets.Nearest(grid, 965.5, 536.0);

        Assert.Equal(5, number);
        Assert.Equal(5.5, dx);
        Assert.Equal(-4.0, dy);
    }

    [Fact]
    public void Nearest_rejects_an_empty_list()
    {
        Assert.Throws<ArgumentException>(() => AccuracyTargets.Nearest([], 0, 0));
    }
}
