using PenForLang.Wpf.Controls;

namespace PenForLang.App.Wpf.Tests;

public class SessionReportLiveInkTests
{
    [Fact]
    public void Report_contains_the_internal_latency_and_it_is_in_the_summary()
    {
        ReportInput input = SessionReportTests.Input() with
        {
            Latency = new RenderLatency(480, 11.37, 33.9, 9.0, 6.12, 21.5),
            LiveInkStrokes = 12,
            LiveInkSegments = 1530,
            GcLowLatencyActivations = 3,
            GcCollections = (7, 2, 1),
            GcPause = TimeSpan.FromMilliseconds(12.5),
        };

        string report = SessionReport.Build(input);

        Assert.Contains("## Живые чернила и задержка (V0.6)", report, StringComparison.Ordinal);
        Assert.Contains("среднее **11.4 мс**, максимум 33.9 мс", report, StringComparison.Ordinal);
        Assert.Contains("кадров с новыми точками 480", report, StringComparison.Ordinal);
        Assert.Contains("среднее 6.1 мс, максимум 21.5 мс", report, StringComparison.Ordinal);
        Assert.Contains("штрихов 12, отрезков 1530", report, StringComparison.Ordinal);
        Assert.Contains("включался 3 раз", report, StringComparison.Ordinal);
        Assert.Contains("7/2/1", report, StringComparison.Ordinal);

        // Строка с задержкой есть и в кратких выводах — раньше раздела подробностей.
        int summary = report.IndexOf("Внутренняя задержка «метка точки → Rendering»: среднее **11.4 мс**", StringComparison.Ordinal);
        Assert.InRange(summary, 0, report.IndexOf("## Среда", StringComparison.Ordinal));
        Assert.Contains("камерой 240 fps", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_without_measurements_says_so()
    {
        string report = SessionReport.Build(SessionReportTests.Input());

        Assert.Contains("Внутренняя задержка: нет данных", report, StringComparison.Ordinal);
        Assert.Contains("Запись penlog выключена", report, StringComparison.Ordinal);
        Assert.DoesNotContain("Внутренняя задержка «метка точки → Rendering»: среднее", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Recorded_attempts_are_listed_with_their_files()
    {
        ReportInput input = SessionReportTests.Input() with
        {
            DevMode = true,
            PenLogDirectory = @"C:\Users\x\AppData\Local\PenForLang\dev\penlogs",
            RecordedAttempts =
            [
                new RecordedAttempt(@"C:\p\20261014-120000-a-001.pfl.jsonl.gz", 412, 1, "done-button", "glyph:a"),
                new RecordedAttempt(@"C:\p\20261014-120100-t-002.pfl.jsonl.gz", 90, 0, "cancelled", "glyph:t"),
            ],
            PenLogError = "disk full",
        };

        string report = SessionReport.Build(input);

        Assert.Contains(@"Папка: `C:\Users\x\AppData\Local\PenForLang\dev\penlogs`; записано попыток: 2", report, StringComparison.Ordinal);
        Assert.Contains("| glyph:a | 412 | 1 | done-button | `C:\\p\\20261014-120000-a-001.pfl.jsonl.gz` |", report, StringComparison.Ordinal);
        Assert.Contains("| glyph:t | 90 | 0 | cancelled |", report, StringComparison.Ordinal);
        Assert.Contains("Ошибка записи penlog: disk full", report, StringComparison.Ordinal);
    }
}

public class AppOptionsDevTests
{
    [Fact]
    public void Dev_mode_is_on_by_default_and_the_new_options_have_defaults()
    {
        AppOptions options = AppOptions.Parse([]);

        Assert.True(options.Dev);
        Assert.Null(options.PenLogDirectory);
        Assert.Null(options.ContentDirectory);
        Assert.False(options.StartInLatencyMode);
        Assert.Equal(SandboxLayout.DefaultXHeightMm, options.XHeightMm);
    }

    [Fact]
    public void Dev_flags_and_paths_are_parsed()
    {
        AppOptions options = AppOptions.Parse(["--no-dev", "--penlog-dir", @"C:\logs", "--content-dir", @"C:\content", "--latency", "--xheight-mm", "9.5", "--mouse"]);

        Assert.False(options.Dev);
        Assert.Equal(@"C:\logs", options.PenLogDirectory);
        Assert.Equal(@"C:\content", options.ContentDirectory);
        Assert.True(options.StartInLatencyMode);
        Assert.Equal(9.5, options.XHeightMm);
        Assert.True(options.UseMouse);
        Assert.Empty(options.UnknownArguments);

        Assert.True(AppOptions.Parse(["--no-dev", "--dev"]).Dev);
    }

    [Theory]
    [InlineData("--penlog-dir")]
    [InlineData("--content-dir")]
    [InlineData("--xheight-mm")]
    [InlineData("--xheight-mm", "0")]
    [InlineData("--xheight-mm", "abc")]
    [InlineData("--xheight-mm", "100")]
    public void Bad_values_are_reported_not_thrown(params string[] args)
    {
        AppOptions options = AppOptions.Parse(args);

        Assert.Contains(args[0], options.UnknownArguments);
        Assert.Equal(SandboxLayout.DefaultXHeightMm, options.XHeightMm);
        Assert.Null(options.PenLogDirectory);
    }
}
