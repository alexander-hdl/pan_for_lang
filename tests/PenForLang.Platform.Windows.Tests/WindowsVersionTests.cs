namespace PenForLang.Platform.Windows.Tests;

public class WindowsVersionTests
{
    [Theory]
    [InlineData(10, 0, 19045, true)] // Windows 10 22H2
    [InlineData(10, 0, 22631, true)] // Windows 11 23H2
    [InlineData(10, 0, 26100, true)] // Windows 11 24H2 / Server 2025
    [InlineData(10, 0, 19044, false)] // Windows 10 21H2
    [InlineData(6, 3, 9600, false)] // Windows 8.1
    [InlineData(11, 0, 1, true)] // гипотетическая будущая версия
    public void IsSupported_follows_minimum_build(int major, int minor, int build, bool expected)
    {
        Assert.Equal(expected, WindowsVersion.IsSupported(major, minor, build));
    }

    [Fact]
    public void Current_machine_is_supported()
    {
        Assert.True(WindowsVersion.IsCurrentSupported());
    }
}
