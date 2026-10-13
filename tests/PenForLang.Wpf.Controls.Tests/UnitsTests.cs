namespace PenForLang.Wpf.Controls.Tests;

public class UnitsTests
{
    [Fact]
    public void One_inch_is_96_dips()
    {
        Assert.Equal(96.0, Units.MmToDip(25.4), 10);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7.5)]
    [InlineData(123.456)]
    public void Mm_to_dip_round_trips(double mm)
    {
        Assert.Equal(mm, Units.DipToMm(Units.MmToDip(mm)), 10);
    }
}
