using System.Text.Json.Nodes;
using PenForLang.Core;
using PenForLang.Evaluation.Config;
using PenForLang.Evaluation.Tests.Support;

namespace PenForLang.Evaluation.Tests;

/// <summary>Загрузка и проверка <c>content/config/scoring.json</c>: числа оценщика — в файле, не в коде (CLAUDE.md, ARCHITECTURE.md §3.6).</summary>
public class ScoringConfigTests
{
    private static JsonNode Original() => JsonNode.Parse(Shipped.ConfigText)!;

    private static ScoringConfig Parse(JsonNode node) => ScoringConfigLoader.Parse(System.Text.Encoding.UTF8.GetBytes(node.ToJsonString()));

    private static ScoringConfigException Reject(JsonNode node) => Assert.Throws<ScoringConfigException>(() => Parse(node));

    [Fact]
    public void Shipped_config_loads_through_the_content_source()
    {
        ScoringConfig config = ScoringConfigLoader.Load(Shipped.Source);

        Assert.Equal("0.1.0", config.Version);
        Assert.Equal(0.04, config.ResampleStep);
        Assert.Equal(0.55, config.Process.StartTolerance);
        Assert.Equal(DeferredAcceptPolicy.Either, config.Deferred.AcceptTiming);
        Assert.Equal(Enum.GetValues<ToleranceProfile>().Length, config.Profiles.Count);
        Assert.Equal(64, config.ConfigHash.Length);
    }

    [Fact]
    public void Every_form_weight_has_a_score_function_and_every_function_a_weight()
    {
        ScoringConfig config = Shipped.Config;

        Assert.Equal(
            config.Form.Weights.Select(w => w.Key).Order(StringComparer.Ordinal),
            config.Form.Scores.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Missing_file_in_the_content_source_is_reported()
    {
        Assert.Throws<FileNotFoundException>(() => ScoringConfigLoader.Load(new InMemoryContentSource()));
    }

    [Fact]
    public void Hash_is_stable_changes_with_numbers_but_not_with_meta_or_formatting()
    {
        string hash = Shipped.Config.ConfigHash;
        JsonNode reformatted = Original();
        reformatted["meta"]!["status"] = "something else";
        JsonNode changed = Original();
        changed["process"]!["startTolerance"] = 0.36;

        Assert.Equal(hash, ScoringConfigLoader.Parse(System.Text.Encoding.UTF8.GetBytes(Shipped.ConfigText)).ConfigHash);
        Assert.Equal(hash, Parse(reformatted).ConfigHash);
        Assert.NotEqual(hash, Parse(changed).ConfigHash);
    }

    [Fact]
    public void Evaluator_info_carries_the_version_and_the_hash()
    {
        Assert.Equal(EvaluatorVersion.Current, Tester.Evaluator.Info.Version);
        Assert.Equal(Shipped.Config.ConfigHash, Tester.Evaluator.Info.ConfigHash);
    }

    [Fact]
    public void A_changed_number_changes_the_decision_without_touching_code()
    {
        // Порог старта из файла: с очень малым допуском даже верный образец получит P1.
        JsonNode strict = Original();
        strict["process"]!["startTolerance"] = 0.01;
        strict["process"]!["approach"]!["tolerance"] = 0.01;
        var evaluator = new LetterEvaluator(Parse(strict));
        var glyph = Shipped.Glyph("t");

        EvaluationResult tight = evaluator.Evaluate(new LetterEvaluationRequest(Tester.Synth.Clean(glyph, 1), glyph, Shipped.Style));
        EvaluationResult normal = Tester.Evaluate(Tester.Synth.Clean(glyph, 1), glyph);

        Assert.True(tight.Summary.Has(ErrorCode.P1));
        Assert.False(normal.Summary.Has(ErrorCode.P1));
        Assert.NotEqual(tight.Summary.Info.ConfigHash, normal.Summary.Info.ConfigHash);
    }

    [Fact]
    public void Unknown_keys_are_rejected()
    {
        JsonNode node = Original();
        node["process"]!["startTolerence"] = 0.3;

        Assert.Contains(Reject(node).Issues, i => i.StartsWith("process.startTolerence", StringComparison.Ordinal) && i.Contains("unknown", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_keys_are_rejected_with_their_path()
    {
        JsonNode node = Original();
        node["deferred"]!.AsObject().Remove("nearU");

        Assert.Contains(Reject(node).Issues, i => i.StartsWith("deferred.nearU", StringComparison.Ordinal) && i.Contains("missing", StringComparison.Ordinal));
    }

    [Fact]
    public void Findings_without_a_threshold_need_their_severity_in_the_file()
    {
        JsonNode node = Original();
        node["findings"]!["fixedSeverity"]!.AsObject().Remove("P2");
        node["findings"]!["fixedSeverity"]!["ZZ"] = 0.5;

        IReadOnlyList<string> issues = Reject(node).Issues;
        Assert.Contains(issues, i => i.StartsWith("findings.fixedSeverity.P2", StringComparison.Ordinal) && i.Contains("missing", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.StartsWith("findings.fixedSeverity.ZZ", StringComparison.Ordinal));
    }

    [Fact]
    public void Numbers_outside_their_range_are_rejected()
    {
        JsonNode node = Original();
        node["resampleStep"] = 0.0;
        node["confidence"]!["unrecognizedCoverage"] = 1.5;

        IReadOnlyList<string> issues = Reject(node).Issues;
        Assert.Contains(issues, i => i.StartsWith("resampleStep", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.StartsWith("confidence.unrecognizedCoverage", StringComparison.Ordinal));
    }

    [Fact]
    public void Bad_score_functions_are_rejected()
    {
        JsonNode decreasingX = Original();
        decreasingX["form"]!["scores"]!["dtwMean"]!["points"] = JsonNode.Parse("[[0.2, 1.0], [0.1, 0.0]]");
        JsonNode scoreAboveOne = Original();
        scoreAboveOne["form"]!["scores"]!["frechet"]!["points"] = JsonNode.Parse("[[0.0, 2.0], [1.0, 0.0]]");
        JsonNode orphanWeight = Original();
        orphanWeight["form"]!["weights"]!["mystery"] = 0.1;

        Assert.Contains(Reject(decreasingX).Issues, i => i.StartsWith("form.scores.dtwMean.points", StringComparison.Ordinal));
        Assert.Contains(Reject(scoreAboveOne).Issues, i => i.StartsWith("form.scores.frechet.points", StringComparison.Ordinal));
        Assert.Contains(Reject(orphanWeight).Issues, i => i.StartsWith("form.weights.mystery", StringComparison.Ordinal));
    }

    [Fact]
    public void Wrong_schema_and_bad_policy_and_bad_json_are_rejected()
    {
        JsonNode schema = Original();
        schema["schema"] = "pfl.config.scoring/2";
        JsonNode policy = Original();
        policy["deferred"]!["acceptTiming"] = "never";

        Assert.Contains(Reject(schema).Issues, i => i.StartsWith("schema", StringComparison.Ordinal));
        Assert.Contains(Reject(policy).Issues, i => i.StartsWith("deferred.acceptTiming", StringComparison.Ordinal));
        Assert.Throws<ScoringConfigException>(() => ScoringConfigLoader.Parse("{ not json"u8));
    }

    [Fact]
    public void Even_smoothing_window_is_rejected()
    {
        JsonNode node = Original();
        node["smoothing"]!["window"] = 6;

        Assert.Contains(Reject(node).Issues, i => i.StartsWith("smoothing", StringComparison.Ordinal));
    }

    [Fact]
    public void Score_function_is_piecewise_linear_with_a_dead_zone_and_tolerance_scaling()
    {
        ScoreFunction mean = Shipped.Config.Form.Scores["dtwMean"];

        Assert.Equal(1.0, mean.Evaluate(0.0, 1.0));
        Assert.Equal(1.0, mean.Evaluate(0.05, 1.0));
        Assert.Equal(0.5, mean.Evaluate(0.175, 1.0), 1e-12);
        Assert.Equal(0.0, mean.Evaluate(0.5, 1.0));
        Assert.True(mean.Evaluate(0.175, 0.75) < mean.Evaluate(0.175, 1.0), "a stricter profile scores the same deviation lower");
        Assert.True(mean.Evaluate(0.175, 1.2) > mean.Evaluate(0.175, 1.0), "a looser profile scores the same deviation higher");
    }
}
