using PenForLang.Core;
using PenForLang.Core.Geometry;
using PenForLang.Evaluation;
using PenForLang.Glyphs;
using PenForLang.Glyphs.Loading;
using PenForLang.Ink;
using PenForLang.Testing.SynthInk;

namespace PenForLang.Testing.Tests;

/// <summary>SynthInk-lite (ARCHITECTURE.md §10.3): детерминизм, соответствие глифу, разметка мутаций.</summary>
public class SynthInkTests
{
    private static readonly StylePack Pack = LoadPack();

    private static readonly SynthInkGenerator Generator = new(Pack.Manifest);

    private static readonly SynthOptions NoVariation = new()
    {
        ScaleMin = 1.0,
        ScaleMax = 1.0,
        SlantRangeDeg = 0.0,
        RotationRangeDeg = 0.0,
        OffsetU = 0.0,
        BaselineDrift = 0.0,
        StrokeOffsetSigma = 0.0,
        WobbleAmplitude = 0.0,
        TremorSigma = 0.0,
        SpeedMin = 1.0,
        SpeedMax = 1.0,
        TimeJitterUs = 0.0,
    };

    public static TheoryData<string> Letters => ["a", "i", "t"];

    [Theory]
    [MemberData(nameof(Letters))]
    public void Ideal_ink_lies_on_the_glyph_paths(string symbol)
    {
        Glyph glyph = Pack.GetGlyph(symbol);
        RulerInk ink = Generator.Ideal(glyph);
        Vec2[] main = glyph.Strokes.SelectMany(s => s.Path.Flatten(0.002)).Concat(glyph.LeadIn!.Flatten(0.002)).Concat(glyph.LeadOut!.Flatten(0.002)).ToArray();

        Assert.Equal(glyph.PenStrokes.Length, ink.Strokes.Length);
        foreach (RulerStroke stroke in ink.Strokes)
        {
            Vec2[] reference = stroke.Index < glyph.Strokes.Length
                ? main
                : glyph.Deferred[stroke.Index - glyph.Strokes.Length].Path.Flatten(0.002);
            foreach (RulerPoint p in stroke.Points)
            {
                Assert.True(PolylineMath.DistanceToPolyline(new Vec2(p.U, p.V), reference) < 0.01, $"{symbol} stroke {stroke.Index}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Letters))]
    public void Ink_is_sampled_at_200_hz_and_strokes_do_not_overlap_in_time(string symbol)
    {
        Glyph glyph = Pack.GetGlyph(symbol);
        RulerInk ink = Generator.Ideal(glyph);

        foreach (RulerStroke stroke in ink.Strokes)
        {
            for (int i = 1; i < stroke.Points.Length - 1; i++)
            {
                Assert.Equal(5000, stroke.Points[i].TimeUs - stroke.Points[i - 1].TimeUs);
            }
        }

        for (int s = 1; s < ink.Strokes.Length; s++)
        {
            Assert.True(ink.Strokes[s].Points[0].TimeUs > ink.Strokes[s - 1].Points[^1].TimeUs, "strokes do not overlap in time");
        }
    }

    [Theory]
    [MemberData(nameof(Letters))]
    public void Noisy_samples_are_deterministic_by_seed_and_vary_between_seeds(string symbol)
    {
        Glyph glyph = Pack.GetGlyph(symbol);

        RulerInk a = Generator.Clean(glyph, 12);
        RulerInk b = Generator.Clean(glyph, 12);
        RulerInk c = Generator.Clean(glyph, 13);

        Assert.True(a.Strokes.Select(s => s.Points).Zip(b.Strokes.Select(s => s.Points)).All(pair => pair.First.SequenceEqual(pair.Second)));
        Assert.False(a.Strokes[0].Points.SequenceEqual(c.Strokes[0].Points));
        Assert.All(a.Strokes.SelectMany(s => s.Points), p => Assert.True(double.IsFinite(p.U) && double.IsFinite(p.V)));
    }

    [Fact]
    public void Without_variation_a_clean_sample_equals_the_ideal_ink()
    {
        var plain = new SynthInkGenerator(Pack.Manifest, NoVariation);
        Glyph glyph = Pack.GetGlyph("a");
        RulerInk ideal = plain.Ideal(glyph);
        RulerInk clean = plain.Clean(glyph, 5);

        // Остаётся только положение буквы на строке (начало в u = 1) и задержка начала по времени.
        Assert.Equal(ideal.Strokes[0].Points.Select(p => (p.U + 1.0, p.V)), clean.Strokes[0].Points.Select(p => (p.U, p.V)));
    }

    [Fact]
    public void Variation_stays_within_the_configured_child_like_limits()
    {
        Glyph glyph = Pack.GetGlyph("i");
        var options = new SynthOptions();
        Vec2[] ideal = Generator.Ideal(glyph).Strokes[0].Points.Select(p => new Vec2(p.U, p.V)).ToArray();
        double idealHeight = ideal.Max(p => p.Y) - ideal.Min(p => p.Y);

        for (ulong seed = 1; seed <= 40; seed++)
        {
            Vec2[] sample = Generator.Clean(glyph, seed).Strokes[0].Points.Select(p => new Vec2(p.U, p.V)).ToArray();
            double height = sample.Max(p => p.Y) - sample.Min(p => p.Y);

            Assert.InRange(height / idealHeight, options.ScaleMin - 0.1, options.ScaleMax + 0.1);
        }
    }

    [Fact]
    public void Mutation_matrix_has_labels_and_changes_the_ink()
    {
        foreach (string symbol in new[] { "a", "i", "t" })
        {
            Glyph glyph = Pack.GetGlyph(symbol);
            RulerInk clean = Generator.Clean(glyph, 4);
            foreach (SynthMutation mutation in Enum.GetValues<SynthMutation>().Where(m => m is not (SynthMutation.None or SynthMutation.Scribble)))
            {
                SynthSample? sample = Generator.Mutate(glyph, mutation, 4);
                if (sample is null)
                {
                    continue;
                }

                Assert.Equal(mutation, sample.Mutation);
                Assert.NotEmpty(sample.Expected);
                Assert.All(sample.Expected, e => Assert.True(
                    e.StrokeId is null || glyph.PenStrokes.Any(p => p.Id == e.StrokeId),
                    $"{symbol} {mutation}: unknown stroke {e.StrokeId}"));
                Assert.False(
                    sample.Ink.Strokes.Length == clean.Strokes.Length
                    && sample.Ink.Strokes.Zip(clean.Strokes).All(pair => pair.First.Points.SequenceEqual(pair.Second.Points)),
                    $"{symbol} {mutation} left the ink unchanged");
            }
        }
    }

    [Fact]
    public void Mutation_labels_use_the_taxonomy_codes()
    {
        Glyph t = Pack.GetGlyph("t");

        Assert.Equal([ErrorCode.P1], Generator.Mutate(t, SynthMutation.WrongStart, 1)!.Expected.Select(e => e.Code));
        Assert.Equal([ErrorCode.P2], Generator.Mutate(t, SynthMutation.ReversedStroke, 1)!.Expected.Select(e => e.Code));
        Assert.Equal([ErrorCode.P4], Generator.Mutate(t, SynthMutation.ExtraStroke, 1)!.Expected.Select(e => e.Code));
        Assert.Equal([ErrorCode.D1], Generator.Mutate(t, SynthMutation.MissingDeferred, 1)!.Expected.Select(e => e.Code));
        Assert.Equal([ErrorCode.D4], Generator.Mutate(t, SynthMutation.DeferredImmediately, 1)!.Expected.Select(e => e.Code));
        Assert.Equal([ErrorCode.D2], Generator.Mutate(t, SynthMutation.MisplacedDeferred, 1)!.Expected.Select(e => e.Code));
        Assert.Equal([ErrorCode.D3], Generator.Mutate(t, SynthMutation.ExtraDeferred, 1)!.Expected.Select(e => e.Code));
        Assert.Equal([ErrorCode.P6], Generator.Mutate(t, SynthMutation.ParallelRetrace, 1)!.Expected.Select(e => e.Code));
        Assert.Equal([ErrorCode.F2], Generator.Mutate(Pack.GetGlyph("a"), SynthMutation.OpenOval, 1)!.Expected.Select(e => e.Code));
    }

    [Fact]
    public void Deferred_immediately_puts_the_deferred_stroke_first_in_time()
    {
        Glyph glyph = Pack.GetGlyph("i");
        RulerInk ink = Generator.Mutate(glyph, SynthMutation.DeferredImmediately, 2)!.Ink;
        RulerInk late = Generator.Clean(glyph, 2);

        // Первым в хронологии — короткий штрих-точка; у образца без мутации он последний.
        Assert.True(ink.Strokes[0].Points.Min(p => p.V) > 1.0, "dot first");
        Assert.True(late.Strokes[^1].Points.Min(p => p.V) > 1.0, "dot last");
        Assert.True(ink.Strokes[^1].Points.Min(p => p.V) < 0.5, "the stem is written after the dot");
    }

    [Fact]
    public void Scribbles_are_deterministic()
    {
        Glyph glyph = Pack.GetGlyph("a");
        RulerInk a = Generator.Mutate(glyph, SynthMutation.Scribble, 3)!.Ink;
        RulerInk b = Generator.Mutate(glyph, SynthMutation.Scribble, 3)!.Ink;

        Assert.True(a.Strokes.Select(s => s.Points).Zip(b.Strokes.Select(s => s.Points)).All(pair => pair.First.SequenceEqual(pair.Second)));
        Assert.InRange(a.Strokes.Length, 3, 5);
    }

    private static StylePack LoadPack()
    {
        string content = Path.Combine(FindRoot(), "content");
        var source = new InMemoryContentSource();
        foreach (string file in Directory.EnumerateFiles(content, "*", SearchOption.AllDirectories))
        {
            source.Add(Path.GetRelativePath(content, file).Replace('\\', '/'), File.ReadAllBytes(file));
        }

        return StyleLoader.LoadOrThrow(source, "styles/zaner-bloser");
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

        throw new InvalidOperationException("PenForLang.sln not found above " + AppContext.BaseDirectory);
    }
}
