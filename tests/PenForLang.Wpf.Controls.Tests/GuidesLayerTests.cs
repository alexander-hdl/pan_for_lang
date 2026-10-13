namespace PenForLang.Wpf.Controls.Tests;

public class GuidesLayerTests
{
    [Fact]
    public void CountLines_uses_spacing_in_millimeters()
    {
        // WPF-объекты требуют STA-потока; xunit по умолчанию запускает тесты в MTA.
        int count = 0;
        double spacing = 0;
        RunSta(() =>
        {
            var layer = new GuidesLayer { LineSpacingMm = 25.4 }; // 25,4 мм = 96 DIP
            count = layer.CountLines(192);
            spacing = layer.LineSpacingMm;
        });

        Assert.Equal(25.4, spacing);
        Assert.Equal(3, count); // линии на 0, 96 и 192 DIP
    }

    [Fact]
    public void Non_positive_spacing_is_rejected()
    {
        Exception? caught = null;
        RunSta(() =>
        {
            try
            {
                _ = new GuidesLayer { LineSpacingMm = 0 };
            }
            catch (ArgumentException ex)
            {
                caught = ex;
            }
        });

        Assert.NotNull(caught);
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new InvalidOperationException("STA action failed.", failure);
        }
    }
}
