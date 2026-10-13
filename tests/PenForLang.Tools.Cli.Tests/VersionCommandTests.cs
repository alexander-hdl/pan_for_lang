using System.Reflection;

namespace PenForLang.Tools.Cli.Tests;

public class VersionCommandTests
{
    [Fact]
    public void Version_option_prints_product_version_and_exits_with_zero()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        int exitCode = PflApp.Run(["--version"], output, error);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());

        string expected = typeof(PflApp).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.Equal(expected, output.ToString().Trim());
        Assert.Matches(@"^\d+\.\d+\.\d+", output.ToString().Trim());
    }

    [Fact]
    public void Help_lists_the_tool_description()
    {
        var output = new StringWriter();

        int exitCode = PflApp.Run(["--help"], output, new StringWriter());

        Assert.Equal(0, exitCode);
        Assert.Contains("PenForLang", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_option_returns_non_zero()
    {
        var error = new StringWriter();

        int exitCode = PflApp.Run(["--no-such-option"], new StringWriter(), error);

        Assert.NotEqual(0, exitCode);
        Assert.NotEqual(string.Empty, error.ToString());
    }
}
