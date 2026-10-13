namespace PenForLang.Tools.Cli.Tests;

/// <summary>Команды <c>pfl validate</c> и <c>pfl render</c> на реальном пакете и на испорченной копии.</summary>
public sealed class StyleCommandsTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "pfl-cli-tests-" + Guid.NewGuid().ToString("N"));

    public StyleCommandsTests()
    {
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }

    private static string RepoRoot
    {
        get
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

    private static string StyleDirectory => Path.Combine(RepoRoot, "content", "styles", "zaner-bloser");

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = PflApp.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    private string CopyStyle(string name = "zaner-bloser")
    {
        string target = Path.Combine(_temp, name);
        foreach (string file in Directory.EnumerateFiles(StyleDirectory, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(target, Path.GetRelativePath(StyleDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        return target;
    }

    [Fact]
    public void Validate_accepts_the_shipped_style_package()
    {
        (int code, string output, string error) = Run("validate", StyleDirectory);

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, error);
        Assert.Contains("OK: style 'zaner-bloser'", output, StringComparison.Ordinal);
        Assert.Contains("3 glyph(s) (0 reviewed)", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_reports_every_error_with_file_field_path_and_fails()
    {
        string style = CopyStyle();
        string glyph = Path.Combine(style, "glyphs", "i.default.json");
        File.WriteAllText(glyph, File.ReadAllText(glyph).Replace("\"E-UP\"", "\"E-XX\"", StringComparison.Ordinal));
        File.Delete(Path.Combine(style, "labels", "en.json"));

        (int code, string output, string error) = Run("validate", style);

        Assert.Equal(1, code);
        Assert.DoesNotContain("OK:", output, StringComparison.Ordinal);
        Assert.Contains("error: glyphs/i.default.json: entry.class: unknown entry class 'E-XX'", error, StringComparison.Ordinal);
        Assert.Contains("labels/en.json", error, StringComparison.Ordinal);
        Assert.Contains("FAILED:", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_of_a_missing_directory_is_a_usage_error()
    {
        (int code, _, string error) = Run("validate", Path.Combine(_temp, "no-such-style"));

        Assert.Equal(2, code);
        Assert.Contains("not found", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_writes_the_svg_file_that_matches_the_review_copy()
    {
        string target = Path.Combine(_temp, "out", "a.svg");

        (int code, string output, string error) = Run("render", StyleDirectory, "a", "-o", target);

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, error);
        Assert.Contains("wrote", output, StringComparison.Ordinal);
        string svg = File.ReadAllText(target);
        Assert.StartsWith("<svg ", svg, StringComparison.Ordinal);
        string reviewCopy = Path.Combine(RepoRoot, "docs", "spikes", "glyphs-v0", "a.svg");
        Assert.Equal(File.ReadAllText(reviewCopy), svg);
    }

    [Fact]
    public void Render_writes_to_stdout_without_the_output_option_and_accepts_glyph_ids()
    {
        (int codeShort, string shortOutput, _) = Run("render", StyleDirectory, "t.default");
        (int codeFull, string fullOutput, _) = Run("render", StyleDirectory, "zaner-bloser/t.default");

        Assert.Equal(0, codeShort);
        Assert.Equal(0, codeFull);
        Assert.Equal(shortOutput, fullOutput);
        Assert.Contains("zaner-bloser/t.default", shortOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_finds_a_style_by_id_under_the_content_root_and_honours_layer_options()
    {
        string content = Path.Combine(RepoRoot, "content");

        (int code, string svg, _) = Run("render", "zaner-bloser", "i", "--content", content, "--keypoints", "--no-guides", "--scale", "100");

        Assert.Equal(0, code);
        Assert.Contains("id=\"keypoints\"", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"guides\"", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_of_an_unknown_glyph_lists_the_available_ones()
    {
        (int code, _, string error) = Run("render", StyleDirectory, "z");

        Assert.Equal(1, code);
        Assert.Contains("glyph 'z' is not in style 'zaner-bloser'", error, StringComparison.Ordinal);
        Assert.Contains("a.default", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_refuses_an_invalid_package()
    {
        string style = CopyStyle();
        File.WriteAllText(Path.Combine(style, "style.json"), "{ not json");

        (int code, string output, string error) = Run("render", style, "a");

        Assert.Equal(1, code);
        Assert.Equal(string.Empty, output);
        Assert.Contains("invalid JSON", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_lists_the_new_commands()
    {
        (int code, string output, _) = Run("--help");

        Assert.Equal(0, code);
        Assert.Contains("validate", output, StringComparison.Ordinal);
        Assert.Contains("render", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Directory_content_source_lists_sorted_relative_paths_and_rejects_bad_ones()
    {
        File.WriteAllText(Path.Combine(_temp, "b.txt"), "b");
        Directory.CreateDirectory(Path.Combine(_temp, "sub"));
        File.WriteAllText(Path.Combine(_temp, "sub", "a.txt"), "a");
        var source = new DirectoryContentSource(_temp);

        Assert.Equal(["b.txt", "sub/a.txt"], source.List(string.Empty));
        Assert.Equal(["sub/a.txt"], source.List("sub"));
        Assert.Empty(source.List("missing"));
        Assert.True(source.Exists("sub/a.txt"));
        Assert.False(source.Exists("sub/zzz.txt"));
        using (Stream stream = source.Open("b.txt"))
        {
            Assert.Equal(1, stream.Length);
        }

        Assert.Throws<FileNotFoundException>(() => source.Open("nope.txt"));
        Assert.Throws<ArgumentException>(() => source.Exists("../escape.txt"));
        Assert.Throws<ArgumentException>(() => source.Open("sub\\a.txt"));
        Assert.Throws<DirectoryNotFoundException>(() => new DirectoryContentSource(Path.Combine(_temp, "nope")));
    }
}
