using System.IO;
using System.Xml.Linq;

namespace PenForLang.App.Wpf.Tests;

public class AppOptionsTests
{
    [Fact]
    public void Defaults_are_pen_mode_fullscreen_with_wpf_stylus_disabled()
    {
        AppOptions options = AppOptions.Parse([]);

        Assert.False(options.UseMouse);
        Assert.False(options.KeepWpfStylus);
        Assert.False(options.Windowed);
        Assert.Null(options.ExitAfter);
        Assert.Empty(options.UnknownArguments);
    }

    [Fact]
    public void Flags_are_recognised_case_insensitively()
    {
        AppOptions options = AppOptions.Parse(["--MOUSE", "--wpf-stylus", "--Windowed", "--exit-after", "2.5"]);

        Assert.True(options.UseMouse);
        Assert.True(options.KeepWpfStylus);
        Assert.True(options.Windowed);
        Assert.Equal(TimeSpan.FromSeconds(2.5), options.ExitAfter);
        Assert.Empty(options.UnknownArguments);
    }

    [Theory]
    [InlineData("--exit-after")]
    [InlineData("--exit-after", "abc")]
    [InlineData("--exit-after", "-3")]
    [InlineData("--exit-after", "0")]
    public void Bad_exit_after_is_reported_not_thrown(params string[] args)
    {
        AppOptions options = AppOptions.Parse(args);

        Assert.Null(options.ExitAfter);
        Assert.Contains("--exit-after", options.UnknownArguments);
    }

    [Fact]
    public void Unknown_arguments_are_collected()
    {
        AppOptions options = AppOptions.Parse(["--mouse", "--bogus", "x"]);

        Assert.True(options.UseMouse);
        Assert.Equal(["--bogus", "x"], options.UnknownArguments);
    }

    [Fact]
    public void Project_declares_the_disable_stylus_switch_in_runtimeconfig()
    {
        // ADR-003 / §7.1: переключатель идёт в runtimeconfig; --wpf-stylus переопределяет его в Program.Main.
        var csproj = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "PenForLang.App.Wpf.csproj"));

        XElement? option = csproj.Descendants("RuntimeHostConfigurationOption")
            .SingleOrDefault(e => (string?)e.Attribute("Include") == AppOptions.DisableStylusSwitch);

        Assert.NotNull(option);
        Assert.Equal("true", (string?)option.Attribute("Value"));
    }

    [Fact]
    public void Project_turns_off_invariant_globalization_because_wpf_text_needs_culture_data()
    {
        // Наблюдалось: при InvariantGlobalization=true WPF падает на первом TextBlock (MS.Internal.FontCache.MajorLanguages).
        var csproj = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "PenForLang.App.Wpf.csproj"));

        Assert.Equal("false", csproj.Descendants("InvariantGlobalization").Single().Value.Trim());
    }
}
