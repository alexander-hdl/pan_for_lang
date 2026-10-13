using System.Collections.Immutable;
using PenForLang.Core;
using PenForLang.Glyphs;
using PenForLang.Glyphs.Loading;
using PenForLang.Ink;
using PenForLang.Testing.SynthInk;

namespace PenForLang.Tools.Cli.Tests;

/// <summary>Команда <c>pfl eval</c>: разбор записи пера оценщиком изолированной буквы.</summary>
public sealed class EvalCommandTests : IDisposable
{
    private const double XHeightPx = 112.5;
    private const double OriginXPx = 180;
    private const double BaselineYPx = 620;

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "pfl-eval-tests-" + Guid.NewGuid().ToString("N"));

    public EvalCommandTests()
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

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = PflApp.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    private static StylePack LoadPack() => StyleLoader.LoadOrThrow(new DirectoryContentSource(Path.Combine(RepoRoot, "content")), "styles/zaner-bloser");

    /// <summary>Записывает попытку (в координатах линейки) в лог пера: u, v → пиксели поверхности.</summary>
    private string WritePenLog(RulerInk ink, string target)
    {
        var header = new PenLogHeader(
            "0.7.0",
            "10.0.26100",
            new PenDeviceInfo("XPPen Artist 12 Pro", PenSourceKind.Win32Pointer, 200, 199.6, 1024, true, PenTimestampKind.Qpc),
            new PenScreenInfo(1920, 1080, 256, 144, 1.5, 7.5, PxPerMmSource.Calibrated),
            new GuideFrame(OriginXPx, BaselineYPx, XHeightPx, 7.5, 3.0, null, new StyleId("zaner-bloser")),
            new PenLogTask("t1", "E03", target, "L2", "trace", "right"),
            null,
            null,
            10_000_000);
        var entries = ImmutableArray.CreateBuilder<PenLogEntry>();
        foreach (RulerStroke stroke in ink.Strokes)
        {
            for (int i = 0; i < stroke.Points.Length; i++)
            {
                RulerPoint p = stroke.Points[i];
                PenPhase phase = i == 0 ? PenPhase.Down : (i == stroke.Points.Length - 1 ? PenPhase.Up : PenPhase.Move);
                var sample = new PenSample(p.TimeUs, OriginXPx + (p.U * XHeightPx), BaselineYPx - (p.V * XHeightPx), p.Pressure, 0, 0, phase, PenButtons.None, SampleFlags.None, 3);
                entries.Add(new PenLogSampleEntry(sample));
            }
        }

        string path = Path.Combine(_temp, target + ".penlog.jsonl");
        using FileStream stream = File.Create(path);
        PenLogWriter.Write(stream, new PenLog(header, entries.ToImmutable()));
        return path;
    }

    [Fact]
    public void A_clean_letter_is_recognized_and_printed_without_findings()
    {
        StylePack pack = LoadPack();
        Glyph glyph = pack.GetGlyph("t");
        string log = WritePenLog(new SynthInkGenerator(pack.Manifest).Ideal(glyph), "t");

        (int code, string output, string error) = Run("eval", log, "--content", Path.Combine(RepoRoot, "content"));

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, error);
        Assert.Contains("decision Target", output, StringComparison.Ordinal);
        Assert.Contains("process_ok yes", output, StringComparison.Ordinal);
        Assert.Contains("findings: none", output, StringComparison.Ordinal);
        Assert.Contains("deferred bar (Bar): Present, AfterWord", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wrong_start_is_reported_with_the_measured_value_and_the_template_span()
    {
        StylePack pack = LoadPack();
        Glyph glyph = pack.GetGlyph("a");
        SynthSample sample = new SynthInkGenerator(pack.Manifest, new SynthOptions { ScaleMin = 1, ScaleMax = 1, SlantRangeDeg = 0, RotationRangeDeg = 0, BaselineDrift = 0, StrokeOffsetSigma = 0, WobbleAmplitude = 0, TremorSigma = 0 })
            .Mutate(glyph, SynthMutation.WrongStart, 3)!;
        string log = WritePenLog(sample.Ink, "a");

        (int code, string output, _) = Run("eval", log, "--content", Path.Combine(RepoRoot, "content"));

        Assert.Equal(0, code);
        Assert.Contains("P1 [Error]", output, StringComparison.Ordinal);
        Assert.Contains("process_ok no", output, StringComparison.Ordinal);
        Assert.Contains("measured", output, StringComparison.Ordinal);
        Assert.Contains("template s1/", output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_glyph_option_overrides_the_target_of_the_log()
    {
        StylePack pack = LoadPack();
        string log = WritePenLog(new SynthInkGenerator(pack.Manifest).Ideal(pack.GetGlyph("i")), "phrase:cat sat");

        (int missing, _, string error) = Run("eval", log, "--content", Path.Combine(RepoRoot, "content"));
        (int code, string output, _) = Run("eval", log, "--glyph", "i", "--content", Path.Combine(RepoRoot, "content"));

        Assert.Equal(2, missing);
        Assert.Contains("--glyph", error, StringComparison.Ordinal);
        Assert.Equal(0, code);
        Assert.Contains("glyph zaner-bloser/i.default", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_files_and_unknown_glyphs_are_reported_with_exit_codes()
    {
        StylePack pack = LoadPack();
        string log = WritePenLog(new SynthInkGenerator(pack.Manifest).Ideal(pack.GetGlyph("i")), "i");

        (int noFile, _, string noFileError) = Run("eval", Path.Combine(_temp, "nope.jsonl"));
        (int unknown, _, string unknownError) = Run("eval", log, "--glyph", "z", "--content", Path.Combine(RepoRoot, "content"));

        Assert.Equal(2, noFile);
        Assert.Contains("not found", noFileError, StringComparison.Ordinal);
        Assert.Equal(1, unknown);
        Assert.Contains("glyph 'z'", unknownError, StringComparison.Ordinal);
    }

    // ---------------------------------- слова (V0.10) ----------------------------------

    [Fact]
    public void A_lift_on_the_join_is_printed_as_j1_with_the_boundaries_and_the_lift()
    {
        StylePack pack = LoadPack();
        WordTemplate word = WordTemplateBuilder.Build(pack, "at");
        SynthWordSample sample = new SynthInkGenerator(pack.Manifest).Mutate(word, SynthWordMutation.SkipJoin, 3)!;
        string log = WritePenLog(sample.Ink, "x");

        (int code, string output, string error) = Run("eval", log, "--word", "at", "--content", Path.Combine(RepoRoot, "content"));

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, error);
        Assert.Contains("word 'at'", output, StringComparison.Ordinal);
        Assert.Contains("decision Target", output, StringComparison.Ordinal);
        Assert.Contains("join 0 (0->1, JF1, tall): present no", output, StringComparison.Ordinal);
        Assert.Contains("J1 [Error]", output, StringComparison.Ordinal);
        Assert.Contains("Join 0 join0", output, StringComparison.Ordinal);
        Assert.Contains("template join0/Join", output, StringComparison.Ordinal);
        Assert.Contains("pen lifted", output, StringComparison.Ordinal);
        Assert.Contains("lift before stroke 1: on join 0", output, StringComparison.Ordinal);
        Assert.Contains("letter 1 't' (WordInner)", output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_word_comes_from_the_log_target_and_a_dot_put_immediately_is_d4_without_j1()
    {
        StylePack pack = LoadPack();
        WordTemplate word = WordTemplateBuilder.Build(pack, "it");
        SynthWordSample sample = new SynthInkGenerator(pack.Manifest).Mutate(word, SynthWordMutation.DeferredImmediately, 2)!;
        string log = WritePenLog(sample.Ink, "word:it");

        (int code, string output, _) = Run("eval", log, "--content", Path.Combine(RepoRoot, "content"));

        Assert.Equal(0, code);
        Assert.Contains("word 'it'", output, StringComparison.Ordinal);
        Assert.Contains("D4 [Info]", output, StringComparison.Ordinal);
        Assert.DoesNotContain("J1", output, StringComparison.Ordinal);
        Assert.Contains("join 0 (0->1, JF1, tall): present yes, lift expected yes", output, StringComparison.Ordinal);
        Assert.Contains("deferred dot of letter 0 (Dot): Present, Immediate", output, StringComparison.Ordinal);
        Assert.Contains("lift before stroke 2: on join 0, expected", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clean_word_is_printed_without_findings_and_an_unknown_letter_is_reported()
    {
        StylePack pack = LoadPack();
        string log = WritePenLog(new SynthInkGenerator(pack.Manifest).Ideal(WordTemplateBuilder.Build(pack, "at")), "word:at");

        (int code, string output, _) = Run("eval", log, "--content", Path.Combine(RepoRoot, "content"));
        (int unknown, _, string error) = Run("eval", log, "--word", "az", "--content", Path.Combine(RepoRoot, "content"));

        Assert.Equal(0, code);
        Assert.Contains("findings: none", output, StringComparison.Ordinal);
        Assert.Contains("segmentation confident", output, StringComparison.Ordinal);
        Assert.Equal(1, unknown);
        Assert.Contains("cannot build the word 'az'", error, StringComparison.Ordinal);
    }
}
