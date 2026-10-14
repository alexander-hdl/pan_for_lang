using System.Text.Json.Nodes;
using PenForLang.Evaluation.Config;
using PenForLang.Evaluation.Tests.Support;
using PenForLang.Glyphs;
using PenForLang.Ink;

namespace PenForLang.Evaluation.Tests;

/// <summary>Числа слова и соединений — в <c>scoring.json</c> (ключи <c>word</c>, <c>joins</c>), загрузчик строгий; контракт слова.</summary>
public class WordConfigTests
{
    private static JsonNode Original() => JsonNode.Parse(Shipped.ConfigText)!;

    private static ScoringConfigException Reject(JsonNode node) =>
        Assert.Throws<ScoringConfigException>(() => ScoringConfigLoader.Parse(System.Text.Encoding.UTF8.GetBytes(node.ToJsonString())));

    [Fact]
    public void Shipped_config_has_the_word_and_join_numbers()
    {
        ScoringConfig config = Shipped.Config;

        Assert.Equal(0.8, config.Word.Aligner.BandU);
        Assert.Equal(0.3, config.Word.Boundaries.ShiftU);
        Assert.True(config.Word.Aligner.LiftInLetterPenalty > config.Word.Aligner.LiftJoinPenalty, "a lift inside a letter is dearer than a lift on a join");
        Assert.True(config.Joins.HighMinHeight > 0.0, "joins.highMinHeight is the only place of the high-join number (A25)");
        Assert.True(config.Findings.FixedSeverity.ContainsKey(ErrorCode.J1));
    }

    [Theory]
    [InlineData("word", "contextToleranceScale")]
    [InlineData("word.aligner", "bandU")]
    [InlineData("word.aligner", "joinSlackU")]
    [InlineData("word.aligner", "bandMarginU")]
    [InlineData("word.aligner", "breakCorridorU")]
    [InlineData("word.aligner", "elasticXScale")]
    [InlineData("word.aligner", "elasticMaxShiftU")]
    [InlineData("word.aligner", "elasticMinSpreadU")]
    [InlineData("word.unrecognized", "costPerPoint")]
    [InlineData("word.unrecognized", "coverageTauScale")]
    [InlineData("word", "markAdjacencyU")]
    [InlineData("joins", "entryAngleDeg")]
    [InlineData("word.boundaries", "minRiseCost")]
    [InlineData("word.boundaries", "liftSnapU")]
    [InlineData("word.unrecognized", "meanDtw")]
    [InlineData("joins", "highMinHeight")]
    [InlineData("joins", "entryErr")]
    [InlineData("joins.corner", "excessDeg")]
    public void Missing_word_and_join_keys_are_rejected_with_their_path(string section, string key)
    {
        JsonNode node = Original();
        JsonNode target = node;
        foreach (string part in section.Split('.'))
        {
            target = target[part]!;
        }

        target.AsObject().Remove(key);

        Assert.Contains(Reject(node).Issues, i => i.StartsWith(section + "." + key, StringComparison.Ordinal) && i.Contains("missing", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_sections_unknown_keys_and_bad_ranges_are_rejected()
    {
        JsonNode noWord = Original();
        noWord.AsObject().Remove("word");
        JsonNode unknown = Original();
        unknown["joins"]!["mystery"] = 1;
        JsonNode range = Original();
        range["word"]!["aligner"]!["bandU"] = 0.0;
        JsonNode noJ1 = Original();
        noJ1["findings"]!["fixedSeverity"]!.AsObject().Remove("J1");

        Assert.Contains(Reject(noWord).Issues, i => i.StartsWith("word", StringComparison.Ordinal));
        Assert.Contains(Reject(unknown).Issues, i => i.StartsWith("joins.mystery", StringComparison.Ordinal) && i.Contains("unknown", StringComparison.Ordinal));
        Assert.Contains(Reject(range).Issues, i => i.StartsWith("word.aligner.bandU", StringComparison.Ordinal));
        Assert.Contains(Reject(noJ1).Issues, i => i.StartsWith("findings.fixedSeverity.J1", StringComparison.Ordinal));
    }

    [Fact]
    public void The_word_numbers_are_part_of_the_config_hash()
    {
        JsonNode changed = Original();
        changed["joins"]!["entryErr"] = 0.5;

        string hash = ScoringConfigLoader.Parse(System.Text.Encoding.UTF8.GetBytes(changed.ToJsonString())).ConfigHash;

        Assert.NotEqual(Shipped.Config.ConfigHash, hash);
    }

    [Fact]
    public void Word_thresholds_come_from_the_file_a_stricter_entry_tolerance_makes_j3()
    {
        JsonNode strict = Original();
        strict["joins"]!["entryErr"] = 0.001;
        var evaluator = new WordEvaluator(ScoringConfigLoader.Parse(System.Text.Encoding.UTF8.GetBytes(strict.ToJsonString())));
        RulerInk ink = Tester.Synth.Clean(Tester.Word("at"), 3);

        EvaluationSummary strictResult = evaluator.Evaluate(new WordEvaluationRequest(ink, Tester.Word("at"))).Summary;

        Assert.True(strictResult.Has(ErrorCode.J3));
        Assert.False(Tester.EvaluateWord(ink, "at").Summary.Has(ErrorCode.J3));
    }

    // ---------------------------------- контракт слова ----------------------------------

    [Fact]
    public void Null_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => Tester.Words.Evaluate(null!));
        Assert.Throws<ArgumentNullException>(() => Tester.Words.Evaluate(new WordEvaluationRequest(null!, Tester.Word("at"))));
        Assert.Throws<ArgumentNullException>(() => Tester.Words.Evaluate(new WordEvaluationRequest(new RulerInk([], null, 10.0, false), null!)));
        Assert.Throws<ArgumentNullException>(() => new WordEvaluator(null!));
    }

    [Fact]
    public void The_word_result_has_the_facade_fields_for_letters_and_joins_and_the_detail_for_the_word()
    {
        EvaluationResult result = Tester.EvaluateWord(Tester.Synth.Ideal(Tester.Word("tat")), "tat");
        EvaluationSummary s = result.Summary;

        Assert.Equal(Tester.Words.Info, s.Info);
        Assert.Equal(Shipped.Config.ConfigHash, s.Info.ConfigHash);
        Assert.Equal([LetterContext.WordFirst, LetterContext.WordInner, LetterContext.WordInner], s.Letters.Select(l => l.Context));
        Assert.Equal(["t", "a", "t"], s.Letters.Select(l => l.Target));
        Assert.Equal([(0, 1), (1, 2)], s.Joins.Select(j => (j.Left, j.Right)));
        Assert.Equal(["JF2", "JF1"], s.Joins.Select(j => j.Family)); // t→a: перекат к овалу; a→t: подъём к верху t
        Assert.Equal([false, true], s.Joins.Select(j => j.Tall));
        Assert.Equal(2, result.Detail.Word!.Boundaries.Length);
        Assert.Equal(3, result.Detail.Word.LetterResults.Length);
        Assert.Equal(2, result.Detail.Word.Joins.Length);
        Assert.Equal(s.Joins.Select(j => j.Present), result.Detail.Word.Joins.Select(j => j.Present));
    }

    [Fact]
    public void A_letter_result_has_no_joins_and_no_word_detail()
    {
        Glyph glyph = Shipped.Glyph("a");

        EvaluationResult result = Tester.Evaluate(Tester.Synth.Ideal(glyph), glyph);

        Assert.Empty(result.Summary.Joins);
        Assert.Null(result.Detail.Word);
    }

    [Fact]
    public void Word_findings_are_ordered_by_priority_then_by_letter_and_place()
    {
        var sample = Tester.Synth.Mutate(Tester.Word("tit"), PenForLang.Testing.SynthInk.SynthWordMutation.LiftAtJoin, 2)!;

        IReadOnlyList<Finding> findings = Tester.EvaluateWord(sample.Ink, "tit").Summary.Findings;

        Assert.Equal(2, findings.Count(f => f.Code == ErrorCode.J1));
        int[] letters = [.. findings.Where(f => f.Code == ErrorCode.J1).Select(f => f.Where.LetterIndex)];
        Assert.Equal([0, 1], letters);
        Assert.Equal(findings.OrderBy(f => f, Comparer<Finding>.Create(Compare)).Select(f => f.Code), findings.Select(f => f.Code));
    }

    private static int Compare(Finding x, Finding y) => Rank(x).CompareTo(Rank(y));

    private static int Rank(Finding f) => f.Code switch
    {
        ErrorCode.P1 or ErrorCode.P2 => 30,
        ErrorCode.P6 => 43,
        ErrorCode.J1 => 50,
        ErrorCode.J2 or ErrorCode.J3 or ErrorCode.J4 => 52,
        ErrorCode.F2 or ErrorCode.F3 => 60,
        ErrorCode.D1 or ErrorCode.D2 or ErrorCode.D3 => 70,
        ErrorCode.F4 or ErrorCode.F5 => 80,
        ErrorCode.F1 => 90,
        ErrorCode.D4 => 200,
        _ => 150,
    };
}
