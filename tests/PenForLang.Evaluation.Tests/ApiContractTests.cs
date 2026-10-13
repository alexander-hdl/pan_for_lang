using System.Collections.Immutable;
using PenForLang.Evaluation.Tests.Support;
using PenForLang.Glyphs;
using PenForLang.Ink;
using PenForLang.Testing.SynthInk;

namespace PenForLang.Evaluation.Tests;

/// <summary>Контракт публичного API: граничные входы, версия, формат результата.</summary>
public class ApiContractTests
{
    private static readonly RulerInk Empty = new([], null, 10.0, false);

    [Fact]
    public void Empty_ink_is_unrecognized_without_findings_or_exceptions()
    {
        EvaluationResult result = Tester.Evaluate(Empty, "a");

        Assert.Equal(Decision.Unrecognized, result.Summary.Decision);
        Assert.Equal(Confidence.Low, result.Summary.Confidence);
        Assert.Empty(result.Summary.Findings);
        Assert.Equal(0.0, result.Summary.Form);
        Assert.Equal("a", result.Summary.TargetText);
        Assert.Equal(TargetKind.Letter, result.Summary.Kind);
    }

    [Fact]
    public void Strokes_without_points_and_with_non_finite_points_are_ignored()
    {
        Glyph glyph = Shipped.Glyph("i");
        RulerInk clean = Tester.Synth.Clean(glyph, 2);
        RulerStroke broken = new(7, [new RulerPoint(double.NaN, 0.0, 1, 0.5f), new RulerPoint(1.0, double.PositiveInfinity, 2, 0.5f)], false);
        RulerStroke empty = new(8, [], false);
        RulerInk noisy = clean with { Strokes = [.. clean.Strokes, broken, empty] };

        Assert.Equal(ResultText.Describe(Tester.Evaluate(clean, glyph)), ResultText.Describe(Tester.Evaluate(noisy, glyph)).Replace("\n", "\n"));
    }

    [Fact]
    public void A_single_tap_is_handled()
    {
        RulerInk tap = new([new RulerStroke(0, [new RulerPoint(0.5, 0.5, 100, 0.5f)], false)], null, 10.0, false);

        EvaluationResult result = Tester.Evaluate(tap, "t");

        Assert.Equal(Decision.Target, result.Summary.Decision);
        Assert.True(result.Summary.Has(ErrorCode.P5), "the stem is missing");
    }

    [Fact]
    public void Word_contexts_are_not_supported_in_v0()
    {
        Glyph glyph = Shipped.Glyph("a");
        var request = new LetterEvaluationRequest(Tester.Synth.Ideal(glyph), glyph, Shipped.Style, Context: LetterContext.WordInner);

        Assert.Throws<NotSupportedException>(() => Tester.Evaluator.Evaluate(request));
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => Tester.Evaluator.Evaluate(null!));
        Assert.Throws<ArgumentNullException>(() => new LetterEvaluator(null!));
    }

    [Fact]
    public void Summary_is_a_stable_facade_with_template_spans_and_no_ink_dependency()
    {
        Glyph glyph = Shipped.Glyph("t");
        EvaluationResult result = Tester.Evaluate(Tester.Synth.Mutate(glyph, SynthMutation.WrongStart, 1)!.Ink, glyph);

        EvaluationSummary summary = result.Summary;
        Assert.Equal(EvaluatorVersion.Current, summary.Info.Version);
        Assert.Equal(TargetKind.Letter, summary.Kind);
        Assert.Equal("t", summary.WrittenText);
        LetterSummary letter = Assert.Single(summary.Letters);
        Assert.Equal(LetterContext.Isolated, letter.Context);
        Assert.Null(letter.Identity);
        Assert.True(letter.SegmentationConfident);
        Assert.All(summary.Findings, f =>
        {
            Assert.NotNull(f.Span);
            Assert.InRange(f.Severity, 0.0, 1.0);
        });
    }

    [Fact]
    public void Clipped_strokes_lower_the_confidence_to_medium()
    {
        Glyph glyph = Shipped.Glyph("i");
        RulerInk clean = Tester.Synth.Clean(glyph, 1);
        RulerInk clipped = clean with { Strokes = [.. clean.Strokes.Select(s => s with { Clipped = true })] };

        Assert.Equal(Confidence.High, Tester.Evaluate(clean, glyph).Summary.Confidence);
        Assert.Equal(Confidence.Medium, Tester.Evaluate(clipped, glyph).Summary.Confidence);
    }

    [Fact]
    public void Findings_come_in_the_order_of_the_methodology_priorities()
    {
        // Старт (P1) раньше формы (F*) и отложенных (D*): ребёнку показывается первая находка.
        Glyph glyph = Shipped.Glyph("t");
        SynthSample sample = Tester.Synth.Mutate(glyph, SynthMutation.WrongStart, 5)!;
        ImmutableArray<Finding> findings = Tester.Evaluate(sample.Ink, glyph).Summary.Findings;

        Assert.Equal(ErrorCode.P1, findings[0].Code);
    }

    [Fact]
    public void Tolerance_profiles_scale_form_tolerances_but_not_process_findings()
    {
        Glyph glyph = Shipped.Glyph("t");
        RulerInk wobbly = new SynthInkGenerator(Shipped.Style, new SynthOptions { WobbleAmplitude = 0.12, TremorSigma = 0.012 }).Clean(glyph, 4);

        double learning = Tester.Evaluate(wobbly, glyph, ToleranceProfile.Learning).Summary.Form;
        double stable = Tester.Evaluate(wobbly, glyph, ToleranceProfile.Stable).Summary.Form;
        double sprint = Tester.Evaluate(wobbly, glyph, ToleranceProfile.Sprint).Summary.Form;

        Assert.True(stable < learning, $"stable {stable:F3} must be stricter than learning {learning:F3}");
        Assert.True(sprint >= learning, $"sprint {sprint:F3} must not be stricter than learning {learning:F3}");
        SynthSample wrongStart = Tester.Synth.Mutate(glyph, SynthMutation.WrongStart, 1)!;
        Assert.True(Tester.Evaluate(wrongStart.Ink, glyph, ToleranceProfile.Sprint).Summary.Has(ErrorCode.P1));
    }

    [Fact]
    public void Absolute_slant_mode_penalizes_the_slant_while_consistency_mode_does_not()
    {
        Glyph glyph = Shipped.Glyph("t");
        var upright = new SynthInkGenerator(Shipped.Style, new SynthOptions { SlantRangeDeg = 0.0, ScaleMin = 1.0, ScaleMax = 1.0, RotationRangeDeg = 0.0 });
        RulerInk ink = ShearInk(upright.Clean(glyph, 3), 12.0); // ещё на 12° правее наклона эталона

        StyleManifest absolute = Shipped.Style with { SlantScoring = SlantScoring.Absolute };
        EvaluationResult consistency = Tester.Evaluate(ink, glyph);
        EvaluationResult strict = Tester.Evaluate(ink, glyph, style: absolute);

        Assert.False(consistency.Summary.Has(ErrorCode.F6));
        Assert.DoesNotContain(consistency.Detail.FormComponents, c => c.Name == "slant");
        Assert.True(strict.Summary.Has(ErrorCode.F6), $"findings: {string.Join(", ", strict.Summary.Findings.Select(f => f.Code))}");
        Assert.Contains(strict.Detail.FormComponents, c => c.Name == "slant" && c.Score < 1.0);
        Assert.True(strict.Summary.Form < consistency.Summary.Form);
    }

    /// <summary>Наклон письма на <paramref name="degrees"/> относительно имеющегося (сдвиг x на v·tan).</summary>
    private static RulerInk ShearInk(RulerInk ink, double degrees)
    {
        double tan = Math.Tan(degrees * Math.PI / 180.0);
        return ink with
        {
            Strokes = [.. ink.Strokes.Select(s => s with { Points = [.. s.Points.Select(p => p with { U = p.U + (p.V * tan) })] })],
        };
    }
}
