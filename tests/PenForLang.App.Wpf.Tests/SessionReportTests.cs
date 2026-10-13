using System.IO;
using PenForLang.Ink;
using PenForLang.Platform.Windows;

namespace PenForLang.App.Wpf.Tests;

public class SessionReportTests
{
    private static readonly MonitorDescription Main = new(1, @"\\.\DISPLAY1", new MonitorRect(0, 0, 2560, 1440), true, 597, 336, 1.0);
    private static readonly MonitorDescription PenPanel = new(2, @"\\.\DISPLAY2", new MonitorRect(2560, 0, 4480, 1080), false, 256, 144, 1.5);

    private static ReportInput Input(
        PointerDiagnostics? pointer = null,
        PenStats? stats = null,
        long wpfMouseDowns = 0,
        long buttonClicks = 0,
        IReadOnlyList<TargetResult>? targets = null,
        IReadOnlyList<ReportSnapshot>? snapshots = null) =>
        new()
        {
            Reason = "exit",
            Now = new DateTimeOffset(2026, 10, 14, 12, 30, 0, TimeSpan.Zero),
            Started = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero),
            Arguments = ["--windowed"],
            OsDescription = "Microsoft Windows 10.0.19045",
            OsVersion = "10.0.19045",
            OsSupported = true,
            FrameworkDescription = ".NET 10.0.12",
            Architecture = "X64",
            AppVersion = "0.1.0.0",
            StylusDisabledSwitch = true,
            PointerSupportSwitch = false,
            SourceKind = "Win32Pointer",
            DeviceProduct = "XPPen Artist",
            TiltReported = true,
            Monitors = [Main, PenPanel],
            Choice = new PenMonitorChoice(PenPanel, PenMonitorChoiceSource.PointerDevice, new PointerDeviceDescription(7, PointerDeviceKind.IntegratedPen, 2, "XPPen Artist"), 1),
            Devices = [new PointerDeviceDescription(7, PointerDeviceKind.IntegratedPen, 2, "XPPen Artist")],
            DpiScale = 1.5,
            PointerStats = pointer,
            WpfMouseDowns = wpfMouseDowns,
            ButtonClicks = buttonClicks,
            Stats = stats ?? new PenStatsMeter().Snapshot(),
            Targets = targets ?? [],
            Snapshots = snapshots ?? [],
            Errors = [],
            ReportPath = @"C:\Users\x\Desktop\PenForLang-V0-report.md",
        };

    private static PointerDiagnostics Diagnostics(long pen = 10, long handled = 8, long nonPen = 0, int lastError = 0, long fallback = 0, long timeFallback = 0) =>
        new(pen + nonPen, pen, nonPen, handled, lastError == 0 ? 0 : 3, 0, 100, 40, 2, timeFallback, 5, 1, fallback, lastError);

    private static PenStats StatsWith(Action<PenStatsMeter> feed)
    {
        var meter = new PenStatsMeter();
        feed(meter);
        return meter.Snapshot();
    }

    [Fact]
    public void Report_has_all_sections_and_the_key_numbers()
    {
        PenStats stats = StatsWith(m =>
        {
            m.Add(new PenSample(1_000_000, 1, 1, 0.2f, 5, -3, PenPhase.Down, PenButtons.None, SampleFlags.None, 1));
            for (int i = 1; i <= 100; i++)
            {
                m.Add(new PenSample(1_000_000 + (i * 5_000L), 1, 1, 0.9f, 5, -3, PenPhase.Move, i == 50 ? PenButtons.Barrel : PenButtons.None, SampleFlags.Coalesced, 1));
            }

            m.Add(new PenSample(1_505_000, 1, 1, 0f, 0, 0, PenPhase.Up, PenButtons.None, SampleFlags.None, 1));
        });

        string report = SessionReport.Build(Input(Diagnostics(), stats, buttonClicks: 3));

        foreach (string section in new[]
        {
            "# PenForLang V0.5", "## Краткие выводы", "## Среда", "## Мониторы и устройства", "## Сообщения и события",
            "## Частота в контакте", "## Давление и наклон", "## Кнопки пера и тестовая кнопка", "## Мишени точности",
            "## Снимки (клавиша S)", "## Ошибки и предупреждения",
        })
        {
            Assert.Contains(section, report, StringComparison.Ordinal);
        }

        Assert.Contains("Microsoft Windows 10.0.19045", report, StringComparison.Ordinal);
        Assert.Contains(@"\\.\DISPLAY2", report, StringComparison.Ordinal);
        Assert.Contains("PointerDevice", report, StringComparison.Ordinal);
        Assert.Contains("XPPen Artist", report, StringComparison.Ordinal);
        Assert.Contains("200.0 Гц", report, StringComparison.Ordinal);
        Assert.Contains("barrel да", report, StringComparison.Ordinal);
        Assert.Contains("ДА", report, StringComparison.Ordinal);
        Assert.Contains("да (3)", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Pointer_that_never_arrived_is_called_out_with_the_wpf_counters()
    {
        string report = SessionReport.Build(Input(Diagnostics(pen: 0, handled: 0), wpfMouseDowns: 12));

        Assert.Contains("доходят до окна: **НЕТ**", report, StringComparison.Ordinal);
        Assert.Contains("НЕ через WM_POINTER", report, StringComparison.Ordinal);
    }

    [Fact]
    public void History_error_is_reported_with_the_win32_code()
    {
        string report = SessionReport.Build(Input(Diagnostics(lastError: 232, fallback: 4)));

        Assert.Contains("код Win32 232", report, StringComparison.Ordinal);
        Assert.Contains("спасено запасным GetPointerPenInfo 4", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_performance_count_is_flagged()
    {
        string report = SessionReport.Build(Input(Diagnostics(timeFallback: 30)));

        Assert.Contains("нет у 30 из 100 точек", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Mouse_mode_has_no_pointer_counters()
    {
        string report = SessionReport.Build(Input(pointer: null));

        Assert.Contains("Режим мыши", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Targets_and_snapshots_are_listed()
    {
        var targets = new[] { new TargetResult(5, 3.5, -2.0, 0.47, -0.27, 2), new TargetResult(1, 0.5, 0.5, 0.07, 0.07, 1) };
        var snapshots = new[] { new ReportSnapshot(1, new DateTimeOffset(2026, 10, 14, 12, 10, 0, TimeSpan.Zero), new PenStatsMeter().Snapshot(), Diagnostics(), 2, 5) };

        string report = SessionReport.Build(Input(Diagnostics(), targets: targets, snapshots: snapshots));

        Assert.Contains("| 5 | 3.5 | -2.0 | 0.47 | -0.27 | 2 |", report, StringComparison.Ordinal);
        Assert.True(
            report.IndexOf("| 1 | 0.5 |", StringComparison.Ordinal) < report.IndexOf("| 5 | 3.5 |", StringComparison.Ordinal),
            "targets are sorted by number");
        Assert.Contains("| 1 | 12:10:00 |", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Numbers_use_the_invariant_culture()
    {
        var old = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            string report = SessionReport.Build(Input(Diagnostics(), targets: [new TargetResult(5, 3.5, -2.0, 0.47, -0.27, 1)]));

            Assert.Contains("| 5 | 3.5 | -2.0 |", report, StringComparison.Ordinal);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = old;
        }
    }

    [Fact]
    public void Monitor_table_numbers_match_the_monitor_option_and_mark_the_chosen_one()
    {
        string report = SessionReport.Build(Input(Diagnostics()));

        Assert.Contains(@"| 1 | \\.\DISPLAY1 | 2560x1440 |", report, StringComparison.Ordinal);
        Assert.Contains(@"| 2 | \\.\DISPLAY2 | 1920x1080 | (2560,0) | 256x144 | 150% |  | **да** |", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Writer_writes_to_the_first_working_directory_and_overwrites_the_same_file()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pfl-report-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string[] directories = [Path.Combine(dir, "a"), Path.Combine(dir, "b")];
            string name = ReportWriter.FileName(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));

            Assert.True(ReportWriter.TryWrite(directories, name, "first", out string? path, out string? error));
            Assert.Null(error);
            Assert.Equal(Path.Combine(dir, "a", name), path);
            Assert.True(ReportWriter.TryWrite(directories, name, "second", out string? again, out _));
            Assert.Equal(path, again);
            Assert.Equal("second", File.ReadAllText(path!));
            Assert.Equal("PenForLang-V0-report-20261014-120000.md", name);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Writer_falls_back_to_the_next_directory_and_reports_the_failure()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pfl-report-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Первая «папка» — на деле файл: создать в ней каталог/файл нельзя.
            string blocker = Path.Combine(dir, "blocker");
            File.WriteAllText(blocker, "x");

            Assert.True(ReportWriter.TryWrite([Path.Combine(blocker, "sub"), Path.Combine(dir, "ok")], "r.md", "text", out string? path, out string? error));
            Assert.Equal(Path.Combine(dir, "ok", "r.md"), path);
            Assert.NotNull(error);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Writer_fails_cleanly_when_no_directory_works()
    {
        Assert.False(ReportWriter.TryWrite([], "r.md", "text", out string? path, out _));
        Assert.Null(path);
    }

    [Fact]
    public void Candidate_directories_use_the_option_or_the_desktop_first_then_documents_and_the_app_folder()
    {
        IReadOnlyList<string> withOption = ReportWriter.CandidateDirectories("custom-dir");
        IReadOnlyList<string> withoutOption = ReportWriter.CandidateDirectories(null);

        Assert.Equal("custom-dir", withOption[0]);
        Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), withOption);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), withoutOption[0]);
        Assert.Contains(AppContext.BaseDirectory, withoutOption);
        Assert.Equal(withoutOption.Count, withoutOption.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
