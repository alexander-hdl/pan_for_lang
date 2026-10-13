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
        Assert.False(options.WpfPointer);
        Assert.Null(options.ExitAfter);
        Assert.Null(options.MonitorIndex);
        Assert.Null(options.ReportDirectory);
        Assert.Empty(options.UnknownArguments);
    }

    [Fact]
    public void Monitor_report_dir_and_wpf_pointer_are_parsed()
    {
        AppOptions options = AppOptions.Parse(["--monitor", "2", "--report-dir", "reports/v0", "--wpf-pointer"]);

        Assert.Equal(2, options.MonitorIndex);
        Assert.Equal("reports/v0", options.ReportDirectory);
        Assert.True(options.WpfPointer);
        Assert.True(options.KeepWpfStylus); // EnablePointerSupport работает только при включённом стилусе WPF
        Assert.Empty(options.UnknownArguments);
    }

    [Theory]
    [InlineData("--monitor")]
    [InlineData("--monitor", "0")]
    [InlineData("--monitor", "x")]
    [InlineData("--report-dir")]
    public void Bad_monitor_or_report_dir_is_reported_not_thrown(params string[] args)
    {
        AppOptions options = AppOptions.Parse(args);

        Assert.Null(options.MonitorIndex);
        Assert.Null(options.ReportDirectory);
        Assert.Contains(args[0], options.UnknownArguments);
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
    public void Project_does_not_enable_invariant_globalization_because_wpf_text_needs_culture_data()
    {
        // Наблюдалось: при InvariantGlobalization=true WPF падает на первом TextBlock (MS.Internal.FontCache.MajorLanguages).
        // F12: свойство не задаётся в проекте приложения, а Directory.Build.targets включает его только для не-WPF исполняемых.
        var csproj = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "PenForLang.App.Wpf.csproj"));

        Assert.DoesNotContain(csproj.Descendants("InvariantGlobalization"), e => e.Value.Trim() == "true");
        Assert.Equal("true", csproj.Descendants("UseWPF").Single().Value.Trim());
    }

    [Fact]
    public void Project_publishes_the_launcher_scripts_next_to_the_executable()
    {
        var csproj = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "PenForLang.App.Wpf.csproj"));

        XElement? launchers = csproj.Descendants("None")
            .SingleOrDefault(e => ((string?)e.Attribute("Include"))?.EndsWith("*.cmd", StringComparison.Ordinal) == true);

        Assert.NotNull(launchers);
        Assert.Equal("PreserveNewest", (string?)launchers.Attribute("CopyToPublishDirectory"));
    }
}
