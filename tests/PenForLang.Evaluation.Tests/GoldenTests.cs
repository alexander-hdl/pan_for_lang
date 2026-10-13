using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PenForLang.Evaluation.Tests.Support;
using PenForLang.Glyphs;
using PenForLang.Ink;
using PenForLang.Testing.SynthInk;

namespace PenForLang.Evaluation.Tests;

/// <summary>
/// Golden-снимки решений оценщика (ARCHITECTURE.md §10.2, A19): решения, коды и места находок сравниваются точно, числа
/// (<c>form</c>, тяжесть, измеренные значения, метрики) — с допусками из самого снимка, а не побитово: между машинами
/// плавающая точка расходится в последних разрядах. Входы — детерминированные синтетические образцы (SynthInk-lite).
/// <para>
/// Обновить снимки после намеренного изменения оценщика или чисел: <c>PFL_UPDATE_GOLDEN=1 dotnet test tests/PenForLang.Evaluation.Tests</c>
/// (в CI запрещено), просмотреть <c>git diff</c>. Принимает новые снимки оценщика только оркестратор или человек (A18).
/// </para>
/// </summary>
public class GoldenTests
{
    private const string UpdateVariable = "PFL_UPDATE_GOLDEN";
    private const string SchemaName = "pfl.test.evaluation-golden/1";

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static TheoryData<string> Letters => ["a", "i", "t"];

    private static bool UpdateRequested
    {
        get
        {
            if (Environment.GetEnvironmentVariable(UpdateVariable) != "1")
            {
                return false;
            }

            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS")))
            {
                throw new InvalidOperationException($"{UpdateVariable}=1 is not allowed in CI: golden snapshots of the evaluator are accepted by the orchestrator or a human.");
            }

            return true;
        }
    }

    private static string GoldenPath(string symbol) => Repo.Path("tests", "PenForLang.Evaluation.Tests", "golden", $"{symbol}.default.json");

    [Theory]
    [MemberData(nameof(Letters))]
    public void Decisions_codes_and_places_match_the_golden_snapshot_and_numbers_stay_in_tolerance(string symbol)
    {
        JsonObject actual = Snapshot(symbol);
        string path = GoldenPath(symbol);
        if (UpdateRequested)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual.ToJsonString(Pretty).ReplaceLineEndings("\n") + "\n");
        }

        Assert.True(File.Exists(path), $"Golden file {path} is missing. Create it by running the tests with {UpdateVariable}=1.");
        var expected = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var problems = new List<string>();
        Compare(expected, actual, problems);

        Assert.True(problems.Count == 0, $"{symbol} differs from {path}:\n  " + string.Join("\n  ", problems) + $"\nIf the change is intended, run with {UpdateVariable}=1, review the diff and let the orchestrator accept it.");
    }

    [Fact]
    public void Comparison_flags_a_changed_code_a_moved_place_and_a_number_out_of_tolerance()
    {
        JsonObject golden = Snapshot("t");
        var changed = (JsonObject)JsonNode.Parse(golden.ToJsonString())!;
        var cases = changed["cases"]!.AsArray();
        var wrongStart = cases.Select(c => c!.AsObject()).First(c => c["name"]!.GetValue<string>().Contains("WrongStart", StringComparison.Ordinal));
        var findings = wrongStart["findings"]!.AsArray();
        findings[0]!["code"] = "P2";
        findings[0]!["stroke"] = "elsewhere";
        wrongStart["form"] = wrongStart["form"]!.GetValue<double>() + 0.2;

        var problems = new List<string>();
        Compare(golden, changed, problems);

        Assert.Contains(problems, p => p.Contains("code", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("stroke", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("form", StringComparison.Ordinal));
    }

    [Fact]
    public void Comparison_accepts_numbers_within_tolerance()
    {
        JsonObject golden = Snapshot("a");
        var nudged = (JsonObject)JsonNode.Parse(golden.ToJsonString())!;
        foreach (JsonNode? c in nudged["cases"]!.AsArray())
        {
            c!["form"] = Math.Min(1.0, c["form"]!.GetValue<double>() + 0.004);
        }

        var problems = new List<string>();
        Compare(golden, nudged, problems);

        Assert.Empty(problems);
    }

    private static JsonObject Snapshot(string symbol)
    {
        Glyph glyph = Shipped.Glyph(symbol);
        var cases = new JsonArray { Describe("self", Tester.Evaluate(Tester.Synth.Ideal(glyph), glyph)) };
        foreach (ulong seed in new ulong[] { 1, 2 })
        {
            cases.Add(Describe($"clean.seed{seed}", Tester.Evaluate(Tester.Synth.Clean(glyph, seed), glyph)));
        }

        foreach (SynthMutation mutation in Enum.GetValues<SynthMutation>())
        {
            if (mutation == SynthMutation.None || Tester.Synth.Mutate(glyph, mutation, 1) is not { } sample)
            {
                continue;
            }

            cases.Add(Describe($"mutation.{mutation}.seed1", Tester.Evaluate(sample.Ink, glyph)));
        }

        return new JsonObject
        {
            ["schema"] = SchemaName,
            ["glyph"] = glyph.Id.ToString(),
            ["note"] = "Evaluator golden: decisions, codes and places exact; numbers within tolerances. Inputs: SynthInk-lite, seeds in case names.",
            ["evaluatorVersion"] = EvaluatorVersion.Current,
            ["tolerances"] = new JsonObject { ["form"] = 0.02, ["severity"] = 0.05, ["measured"] = 0.05, ["metric"] = 0.03 },
            ["cases"] = cases,
        };
    }

    private static JsonObject Describe(string name, EvaluationResult result)
    {
        EvaluationSummary s = result.Summary;
        var findings = new JsonArray();
        foreach (Finding f in s.Findings)
        {
            var node = new JsonObject
            {
                ["code"] = f.Code.ToString(),
                ["kind"] = f.Kind.ToString(),
                ["place"] = f.Where.Place.ToString(),
                ["stroke"] = f.Where.StrokeId,
                ["ink"] = f.Where.InkStrokeIndex,
                ["span"] = $"{f.Span.StrokeId}:{f.Span.Part}",
                ["severity"] = Round(f.Severity),
                ["measured"] = f.Measured is { } m ? Round(m) : null,
                ["threshold"] = f.Threshold is { } t ? Round(t) : null,
            };
            findings.Add(node);
        }

        var deferred = new JsonArray();
        foreach (DeferredSummary d in s.Deferred)
        {
            deferred.Add(new JsonObject { ["slot"] = d.StrokeId, ["status"] = d.Status.ToString(), ["timing"] = d.Timing.ToString() });
        }

        var metrics = new JsonObject();
        foreach (FormComponent c in result.Detail.FormComponents)
        {
            metrics[c.Name] = Round(c.Value);
        }

        return new JsonObject
        {
            ["name"] = name,
            ["decision"] = s.Decision.ToString(),
            ["confidence"] = s.Confidence.ToString(),
            ["form"] = Round(s.Form),
            ["processOk"] = s.Letters[0].ProcessOk,
            ["findings"] = findings,
            ["deferred"] = deferred,
            ["metrics"] = metrics,
        };
    }

    private static double Round(double value) => Math.Round(value, 4);

    private static void Compare(JsonObject expected, JsonObject actual, List<string> problems)
    {
        JsonObject tolerances = expected["tolerances"]!.AsObject();
        double formTolerance = tolerances["form"]!.GetValue<double>();
        double severityTolerance = tolerances["severity"]!.GetValue<double>();
        double measuredTolerance = tolerances["measured"]!.GetValue<double>();
        double metricTolerance = tolerances["metric"]!.GetValue<double>();

        var expectedCases = expected["cases"]!.AsArray().Select(c => c!.AsObject()).ToList();
        var actualCases = actual["cases"]!.AsArray().Select(c => c!.AsObject()).ToList();
        if (!expectedCases.Select(c => c["name"]!.GetValue<string>()).SequenceEqual(actualCases.Select(c => c["name"]!.GetValue<string>())))
        {
            problems.Add("case list differs: " + string.Join(",", actualCases.Select(c => c["name"]!.GetValue<string>())));
            return;
        }

        for (int i = 0; i < expectedCases.Count; i++)
        {
            JsonObject e = expectedCases[i];
            JsonObject a = actualCases[i];
            string name = e["name"]!.GetValue<string>();
            foreach (string key in new[] { "decision", "confidence" })
            {
                Exact(name, key, e[key], a[key], problems);
            }

            Exact(name, "processOk", e["processOk"], a["processOk"], problems);
            Within(name, "form", e["form"], a["form"], formTolerance, problems);

            JsonArray ef = e["findings"]!.AsArray();
            JsonArray af = a["findings"]!.AsArray();
            if (ef.Count != af.Count)
            {
                problems.Add($"{name}: {af.Count} findings instead of {ef.Count} ({string.Join(",", af.Select(f => f!["code"]!.GetValue<string>()))})");
            }
            else
            {
                for (int k = 0; k < ef.Count; k++)
                {
                    string label = $"{name}/finding{k}";
                    foreach (string key in new[] { "code", "kind", "place", "stroke", "ink", "span" })
                    {
                        Exact(label, key, ef[k]![key], af[k]![key], problems);
                    }

                    Within(label, "severity", ef[k]!["severity"], af[k]!["severity"], severityTolerance, problems);
                    Within(label, "measured", ef[k]!["measured"], af[k]!["measured"], measuredTolerance, problems);
                    Within(label, "threshold", ef[k]!["threshold"], af[k]!["threshold"], 1e-9, problems);
                }
            }

            if (e["deferred"]!.ToJsonString() != a["deferred"]!.ToJsonString())
            {
                problems.Add($"{name}: deferred differ: {a["deferred"]!.ToJsonString()} instead of {e["deferred"]!.ToJsonString()}");
            }

            JsonObject em = e["metrics"]!.AsObject();
            JsonObject am = a["metrics"]!.AsObject();
            if (!em.Select(kv => kv.Key).SequenceEqual(am.Select(kv => kv.Key)))
            {
                problems.Add($"{name}: metric set differs");
                continue;
            }

            foreach (KeyValuePair<string, JsonNode?> kv in em)
            {
                Within(name, "metric " + kv.Key, kv.Value, am[kv.Key], metricTolerance, problems);
            }
        }
    }

    private static void Exact(string where, string key, JsonNode? expected, JsonNode? actual, List<string> problems)
    {
        string e = expected?.ToJsonString() ?? "null";
        string a = actual?.ToJsonString() ?? "null";
        if (e != a)
        {
            problems.Add($"{where}: {key} is {a} instead of {e}");
        }
    }

    private static void Within(string where, string key, JsonNode? expected, JsonNode? actual, double tolerance, List<string> problems)
    {
        if (expected is null || actual is null)
        {
            if ((expected is null) != (actual is null))
            {
                problems.Add($"{where}: {key} presence differs");
            }

            return;
        }

        double e = expected.GetValue<double>();
        double a = actual.GetValue<double>();
        if (Math.Abs(e - a) > tolerance)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{where}: {key} is {a:0.####} instead of {e:0.####} (tolerance {tolerance})"));
        }
    }
}
