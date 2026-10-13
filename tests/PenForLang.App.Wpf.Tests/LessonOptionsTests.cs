using System.IO;

namespace PenForLang.App.Wpf.Tests;

public class LessonOptionsTests
{
    [Fact]
    public void The_lesson_is_the_main_screen_and_the_sandbox_is_a_separate_mode()
    {
        AppOptions defaults = AppOptions.Parse([]);
        AppOptions sandbox = AppOptions.Parse(["--sandbox", "--mouse", "--windowed"]);

        Assert.False(defaults.Sandbox);
        Assert.Equal("ru", defaults.Language);
        Assert.Equal("a", defaults.Letter);
        Assert.True(sandbox.Sandbox);
        Assert.True(sandbox.UseMouse);
        Assert.Empty(sandbox.UnknownArguments);
    }

    [Fact]
    public void Language_and_letter_are_parsed_and_bad_values_are_reported_not_thrown()
    {
        AppOptions ok = AppOptions.Parse(["--lang", "EN", "--letter", "T"]);
        Assert.Equal("en", ok.Language);
        Assert.Equal("t", ok.Letter);
        Assert.Empty(ok.UnknownArguments);

        foreach (string[] args in new[] { new[] { "--lang" }, new[] { "--lang", "de" }, new[] { "--letter" }, new[] { "--letter", "z" } })
        {
            AppOptions bad = AppOptions.Parse(args);
            Assert.Contains(args[0], bad.UnknownArguments);
            Assert.Equal("ru", bad.Language);
            Assert.Equal("a", bad.Letter);
        }
    }

    [Fact]
    public void Hardware_protocol_launchers_open_the_sandbox_and_the_lesson_launchers_do_not()
    {
        string root = FindRoot();
        foreach (string launcher in Directory.EnumerateFiles(Path.Combine(root, "build", "v0"), "*.cmd"))
        {
            Assert.Contains("--sandbox", File.ReadAllText(launcher), StringComparison.Ordinal);
        }

        string[] lessonLaunchers = [.. Directory.EnumerateFiles(Path.Combine(root, "build", "lesson"), "*.cmd")];
        Assert.Equal(3, lessonLaunchers.Length);
        foreach (string launcher in lessonLaunchers)
        {
            Assert.DoesNotContain("--sandbox", File.ReadAllText(launcher), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_child_launcher_records_nothing_and_the_adult_launchers_record()
    {
        string lessonDir = Path.Combine(FindRoot(), "build", "lesson");

        string child = File.ReadAllText(Path.Combine(lessonDir, "lesson-child-pen.cmd"));
        string adultPen = File.ReadAllText(Path.Combine(lessonDir, "lesson-adult-pen.cmd"));
        string adultMouse = File.ReadAllText(Path.Combine(lessonDir, "lesson-adult-mouse.cmd"));

        // Запись детских штрихов без согласия запрещена (ARCHITECTURE.md §3.4): у ребёнка нет ни --dev, ни записи.
        Assert.DoesNotContain("--dev", child.Replace("no --dev", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("--dev", adultPen, StringComparison.Ordinal);
        Assert.Contains("--dev", adultMouse, StringComparison.Ordinal);
        Assert.Contains("--mouse", adultMouse, StringComparison.Ordinal);
    }

    [Fact]
    public void The_project_ships_the_lesson_data_and_launchers_next_to_the_program()
    {
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "content", "config", "lesson.json")));
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "content", "config", "scoring.json")));
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "content", "locales", "ru.json")));
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "content", "locales", "en.json")));
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "lesson-child-pen.cmd")));
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "0-mouse.cmd")));
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PenForLang.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("PenForLang.sln not found.");
    }
}
