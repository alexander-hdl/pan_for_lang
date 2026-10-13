using PenForLang.Glyphs;
using PenForLang.Ink;
using PenForLang.Testing.SynthInk;

namespace PenForLang.Evaluation.Tests.Support;

/// <summary>Общий оценщик и генератор для тестов (оба потокобезопасны / детерминированы).</summary>
internal static class Tester
{
    public static readonly LetterEvaluator Evaluator = Shipped.NewEvaluator();

    public static readonly SynthInkGenerator Synth = new(Shipped.Style);

    public static readonly WordEvaluator Words = new(Shipped.Config);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, WordTemplate> Templates = new(StringComparer.Ordinal);

    /// <summary>Эталон слова (кеш: оценщик кеширует подготовленный эталон по экземпляру).</summary>
    public static WordTemplate Word(string text) => Templates.GetOrAdd(text, t => WordTemplateBuilder.Build(Shipped.Pack, t));

    public static EvaluationResult EvaluateWord(
        RulerInk ink,
        string text,
        ToleranceProfile tolerance = ToleranceProfile.Learning,
        DeferredAcceptPolicy? policy = null) =>
        Words.Evaluate(new WordEvaluationRequest(ink, Word(text), tolerance, policy));

    /// <summary>Буквы пакета v0.</summary>
    public static readonly string[] Letters = ["a", "i", "t"];

    public static EvaluationResult Evaluate(
        RulerInk ink,
        Glyph glyph,
        ToleranceProfile tolerance = ToleranceProfile.Learning,
        DeferredAcceptPolicy? policy = null,
        StyleManifest? style = null) =>
        Evaluator.Evaluate(new LetterEvaluationRequest(ink, glyph, style ?? Shipped.Style, tolerance, policy));

    public static EvaluationResult Evaluate(RulerInk ink, string symbol) => Evaluate(ink, Shipped.Glyph(symbol));

    /// <summary>Находки, которые методика считает ошибками процесса и отложенных штрихов (кроме информационных).</summary>
    public static readonly ErrorCode[] ProcessAndDeferred =
    [
        ErrorCode.P1, ErrorCode.P2, ErrorCode.P3, ErrorCode.P4, ErrorCode.P5, ErrorCode.P6,
        ErrorCode.D1, ErrorCode.D2, ErrorCode.D3,
    ];

    /// <summary>Сдвигает попытку по u и v (линейка).</summary>
    public static RulerInk Shift(RulerInk ink, double du, double dv) =>
        ink with
        {
            Strokes =
            [
                .. ink.Strokes.Select(s => s with
                {
                    Points = [.. s.Points.Select(p => p with { U = p.U + du, V = p.V + dv })],
                }),
            ],
        };
}
