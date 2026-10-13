using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PenForLang.Core;
using PenForLang.Core.Geometry;

namespace PenForLang.Evaluation.Config;

/// <summary>Ошибка чтения <c>scoring.json</c>: все найденные замечания с путями полей.</summary>
public sealed class ScoringConfigException : Exception
{
    /// <summary>Создаёт исключение.</summary>
    /// <param name="issues">Замечания в формате «путь: сообщение».</param>
    public ScoringConfigException(IReadOnlyList<string> issues)
        : base("Invalid scoring config:" + Environment.NewLine + string.Join(Environment.NewLine, issues))
    {
        Issues = issues;
    }

    /// <summary>Замечания в формате «путь: сообщение».</summary>
    public IReadOnlyList<string> Issues { get; }
}

/// <summary>
/// Загрузка <c>scoring.json</c> (схема <c>pfl.config.scoring/1</c>) через <see cref="IContentSource"/>. Строгая:
/// неизвестные ключи, пропущенные ключи и числа вне допустимых диапазонов — замечания (значений по умолчанию в коде нет,
/// ARCHITECTURE.md §3.6, A25: единственная точка истины — файл).
/// </summary>
public static class ScoringConfigLoader
{
    /// <summary>Имя схемы файла.</summary>
    public const string SchemaName = "pfl.config.scoring/1";

    /// <summary>Коды находок без порога, тяжесть которых обязана быть задана в файле.</summary>
    private static readonly ErrorCode[] RequiredFixedSeverity =
    [
        ErrorCode.P2, ErrorCode.P3, ErrorCode.P5, ErrorCode.P6, ErrorCode.F3, ErrorCode.D1, ErrorCode.D3,
    ];

    /// <summary>Загружает конфигурацию из источника контента.</summary>
    /// <param name="source">Источник контента.</param>
    /// <param name="path">Относительный путь файла.</param>
    /// <exception cref="ScoringConfigException">Файл не разобран или содержит недопустимые значения.</exception>
    public static ScoringConfig Load(IContentSource source, string path = ScoringConfig.DefaultPath)
    {
        ArgumentNullException.ThrowIfNull(source);
        byte[] bytes;
        using (Stream stream = source.Open(path))
        using (var buffer = new MemoryStream())
        {
            stream.CopyTo(buffer);
            bytes = buffer.ToArray();
        }

        return Parse(bytes);
    }

    /// <summary>Разбирает конфигурацию из UTF-8 JSON.</summary>
    /// <param name="utf8Json">Содержимое файла.</param>
    /// <exception cref="ScoringConfigException">Файл не разобран или содержит недопустимые значения.</exception>
    public static ScoringConfig Parse(ReadOnlySpan<byte> utf8Json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8Json.ToArray(), new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (JsonException ex)
        {
            throw new ScoringConfigException([$"(file): {ex.Message}"]);
        }

        using (document)
        {
            var issues = new List<string>();
            ScoringConfig? config = ReadRoot(document.RootElement, ComputeHash(document.RootElement), issues);
            if (issues.Count > 0 || config is null)
            {
                throw new ScoringConfigException(issues);
            }

            return config;
        }
    }

    /// <summary>
    /// Хеш канонического JSON: ключи объектов отсортированы по <see cref="StringComparer.Ordinal"/>, пробелов нет, блок
    /// <c>meta</c> верхнего уровня исключён (описание не влияет на результат). Числа записываются как в файле.
    /// </summary>
    internal static string ComputeHash(JsonElement root)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, root, topLevel: true);
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element, bool topLevel)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty p in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (topLevel && p.Name == "meta")
                    {
                        continue;
                    }

                    writer.WritePropertyName(p.Name);
                    WriteCanonical(writer, p.Value, topLevel: false);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item, topLevel: false);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }

    private static ScoringConfig? ReadRoot(JsonElement root, string hash, List<string> issues)
    {
        if (ConfigObject.Open(root, string.Empty, issues) is not { } o)
        {
            return null;
        }

        string? schema = o.String("schema");
        if (schema is not null && schema != SchemaName)
        {
            issues.Add($"schema: expected '{SchemaName}' but found '{schema}'");
        }

        string version = o.String("version") ?? "?";
        o.Ignore("meta");
        double step = o.Double("resampleStep", 0.005, 0.5);
        var smoothing = new SmoothingConfig(0, 0);
        if (o.Obj("smoothing") is { } sm)
        {
            smoothing = new SmoothingConfig(sm.Int("window", 1, 99), sm.Int("order", 0, 8));
            if (smoothing.Window % 2 == 0 || smoothing.Order >= smoothing.Window)
            {
                issues.Add("smoothing: window must be odd and order < window");
            }

            sm.Done();
        }

        PreprocessConfig? pre = null;
        if (o.Obj("preprocess") is { } pp)
        {
            pre = new PreprocessConfig(pp.Double("spikeJumpMedianSteps", 1, 1000), pp.Double("dotMaxLength", 0, 1), pp.Double("dotMaxDurationMs", 0, 10000), pp.Double("minPointDistance", 0, 0.5), pp.Double("smoothHalfWindowMs", 0, 500), pp.Int("maxResamplePoints", 20, 100000), pp.Double("stepScaleMax", 1, 20));
            pp.Done();
        }

        NormalizationConfig? norm = ReadNormalization(o.Obj("normalization"), issues);
        DtwConfig? dtw = null;
        if (o.Obj("dtw") is { } d)
        {
            dtw = new DtwConfig(d.Double("positionWeight", 0, 1000), d.Double("directionWeight", 0, 1000));
            d.Done();
        }

        MatchingConfig? matching = null;
        if (o.Obj("matching") is { } mt)
        {
            matching = new MatchingConfig(
                mt.Double("extraStrokeCost", 0, 1000),
                mt.Double("missingStrokeCost", 0, 1000),
                mt.Double("minLengthRatio", 0, 1),
                mt.Double("maxLengthRatio", 1, 1000));
            mt.Done();
        }

        ImmutableDictionary<ToleranceProfile, ProfileConfig> profiles = ReadProfiles(o.Obj("profiles"), issues);
        ProcessConfig? process = ReadProcess(o.Obj("process"));
        FormConfig? form = ReadForm(o.Obj("form"), issues);
        DeferredConfig? deferred = ReadDeferred(o.Obj("deferred"), issues);
        FindingsConfig? findings = ReadFindings(o.Obj("findings"), issues);
        ConfidenceConfig? confidence = null;
        if (o.Obj("confidence") is { } c)
        {
            confidence = new ConfidenceConfig(c.Double("unrecognizedMeanDtw", 0, 100), c.Double("unrecognizedCoverage", 0, 1), c.Double("unrecognizedInkShare", 0, 1), c.Double("mediumMeanDtw", 0, 100));
            c.Done();
        }

        o.Done();
        if (issues.Count > 0 || pre is null || norm is null || dtw is null || matching is null || process is null
            || form is null || deferred is null || findings is null || confidence is null)
        {
            return null;
        }

        return new ScoringConfig(version, hash, step, smoothing, pre, norm, dtw, matching, profiles, process, form, deferred, findings, confidence);
    }

    private static NormalizationConfig? ReadNormalization(ConfigObject? n, List<string> issues)
    {
        if (n is null)
        {
            return null;
        }

        double share = n.Double("dominantStrokeShare", 0, 1);
        DeslantConfig? deslant = null;
        if (n.Obj("deslant") is { } ds)
        {
            deslant = new DeslantConfig(ds.Bool("enabled"), ds.Double("deadbandDeg", 0, 90), ds.Double("maxDeg", 0, 80), ds.Double("minRunLength", 0.01, 10), ds.Double("maxDeviationDeg", 1, 89), ds.Double("maxSpreadDeg", 0, 90));
            ds.Done();
        }

        ScaleConfig? scale = null;
        if (n.Obj("scale") is { } sc)
        {
            scale = new ScaleConfig(sc.Bool("enabled"), sc.Double("min", 0.1, 1), sc.Double("max", 1, 10), sc.Double("lowPercentile", 0, 0.49), sc.Double("highPercentile", 0.51, 1));
            sc.Done();
        }

        RefineConfig? refine = null;
        if (n.Obj("refine") is { } r)
        {
            refine = new RefineConfig(
                r.Bool("enabled"),
                r.Int("iterations", 1, 20),
                r.Double("keepShare", 0.1, 1),
                r.Double("scaleMin", 0.1, 1),
                r.Double("scaleMax", 1, 10),
                r.Double("maxShiftV", 0, 5),
                r.Double("minChange", 0, 1),
                r.Int("rounds", 1, 5),
                r.Double("rerunAbove", 0, 5));
            r.Done();
        }

        double baselineMax = n.Double("baselineShiftMax", 0, 10);
        n.Done();
        _ = issues;
        return deslant is null || scale is null || refine is null ? null : new NormalizationConfig(share, deslant, scale, baselineMax, refine);
    }

    private static ImmutableDictionary<ToleranceProfile, ProfileConfig> ReadProfiles(ConfigObject? p, List<string> issues)
    {
        ImmutableDictionary<ToleranceProfile, ProfileConfig>.Builder builder = ImmutableDictionary.CreateBuilder<ToleranceProfile, ProfileConfig>();
        if (p is null)
        {
            return builder.ToImmutable();
        }

        foreach (ToleranceProfile profile in Enum.GetValues<ToleranceProfile>())
        {
            string key = char.ToLowerInvariant(profile.ToString()[0]) + profile.ToString()[1..];
            if (p.Obj(key) is { } entry)
            {
                builder[profile] = new ProfileConfig(entry.Double("toleranceScale", 0.1, 10));
                entry.Done();
            }
        }

        p.Done();
        _ = issues;
        return builder.ToImmutable();
    }

    private static ProcessConfig? ReadProcess(ConfigObject? p)
    {
        if (p is null)
        {
            return null;
        }

        double start = p.Double("startTolerance", 0.01, 10);
        double startArc = p.Double("startArcTolerance", 0, 50);
        double startOnPath = p.Double("startOnPathTolerance", 0, 5);
        DirectionConfig? direction = null;
        if (p.Obj("direction") is { } d)
        {
            direction = new DirectionConfig(
                d.Double("reverseCostRatioMax", 0, 10),
                d.Double("strongReverseCostRatio", 0, 10),
                d.Double("firstSegmentAngleMaxDeg", 0, 180),
                d.Double("firstSegmentLength", 0.01, 5),
                d.Double("minStrokeLength", 0, 10),
                d.Double("barMinDirectionalShare", 0, 1),
                d.Bool("barReverseIsError"),
                d.Double("rangeAreaMinShare", 0, 1));
            d.Done();
        }

        StrokeCountConfig? count = null;
        if (p.Obj("strokeCount") is { } s)
        {
            count = new StrokeCountConfig(s.Int("extraForP4", 1, 100), s.Int("extraForStop", 1, 100));
            s.Done();
        }

        RetraceConfig? retrace = null;
        if (p.Obj("retrace") is { } r)
        {
            retrace = new RetraceConfig(r.Double("tolerance", 0.001, 5), r.Double("minShareOfReference", 0, 1), r.Int("minPriorPoints", 0, 1000), r.Int("minRangePoints", 0, 1000), r.Double("coveredByExtraShare", 0, 1));
            r.Done();
        }

        ApproachConfig? approach = null;
        if (p.Obj("approach") is { } ap)
        {
            approach = new ApproachConfig(
                ap.Bool("extendLeft"),
                ap.Double("extendRight", 0, 5),
                ap.Double("tolerance", 0.01, 10),
                ap.Double("maxAngleDeg", 0, 180),
                ap.Double("minElevationDeg", -90, 90),
                ap.Double("overlapMax", 0, 1),
                ap.Double("overlapTolerance", 0.01, 2),
                ap.Double("overlapSkipTail", 0, 5),
                ap.Double("entryAngleMaxDeg", 0, 180),
                ap.Double("ambiguousShare", 0, 1),
                ap.Double("ambiguousAngleDeg", 0, 90));
            ap.Done();
        }

        p.Done();
        return direction is null || count is null || retrace is null || approach is null ? null : new ProcessConfig(start, startArc, startOnPath, approach, direction, count, retrace);
    }

    private static FormConfig? ReadForm(ConfigObject? f, List<string> issues)
    {
        if (f is null)
        {
            return null;
        }

        double tau = f.Double("corridorTau", 0.001, 5);
        double loopArea = f.Double("minLoopArea", 0, 5);
        double closureWindow = f.Double("closureWindow", 0.01, 2);
        double touchWindow = f.Double("baselineTouchWindow", 0.01, 2);
        double touchMismatch = f.Double("baselineTouchMaxMismatch", 0, 5);
        DeferredPenaltyConfig? penalty = null;
        if (f.Obj("deferredPenalty") is { } dp)
        {
            penalty = new DeferredPenaltyConfig(dp.Double("missing", 0, 10), dp.Double("misplaced", 0, 10), dp.Double("extra", 0, 10));
            dp.Done();
        }

        var weights = ImmutableArray.CreateBuilder<KeyValuePair<string, double>>();
        if (f.Obj("weights") is { } w)
        {
            foreach (string name in w.Names())
            {
                weights.Add(new KeyValuePair<string, double>(name, w.Double(name, 0, 1000)));
            }

            w.Done();
        }

        ImmutableDictionary<string, ScoreFunction>.Builder scores = ImmutableDictionary.CreateBuilder<string, ScoreFunction>(StringComparer.Ordinal);
        if (f.Obj("scores") is { } s)
        {
            foreach (string name in s.Names())
            {
                if (s.Obj(name) is { } fn)
                {
                    ScoreFunction? function = ReadScoreFunction(fn, $"form.scores.{name}", issues);
                    if (function is not null)
                    {
                        scores[name] = function;
                    }

                    fn.Done();
                }
            }

            s.Done();
        }

        f.Done();
        foreach (KeyValuePair<string, double> weight in weights)
        {
            if (!scores.ContainsKey(weight.Key))
            {
                issues.Add($"form.weights.{weight.Key}: no matching function in form.scores");
            }
        }

        return penalty is null ? null : new FormConfig(tau, loopArea, closureWindow, touchWindow, touchMismatch, penalty, weights.ToImmutable(), scores.ToImmutable());
    }

    private static ScoreFunction? ReadScoreFunction(ConfigObject fn, string path, List<string> issues)
    {
        bool scaled = fn.Bool("toleranceScaled");
        JsonElement? points = fn.Raw("points");
        if (points is not { ValueKind: JsonValueKind.Array } array)
        {
            issues.Add($"{path}.points: expected an array of [x, y] pairs");
            return null;
        }

        var list = ImmutableArray.CreateBuilder<Vec2>();
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != 2
                || item[0].ValueKind != JsonValueKind.Number || item[1].ValueKind != JsonValueKind.Number)
            {
                issues.Add($"{path}.points: every point must be [x, y]");
                return null;
            }

            double x = item[0].GetDouble();
            double y = item[1].GetDouble();
            if (y is < 0.0 or > 1.0 || (list.Count > 0 && x <= list[^1].X))
            {
                issues.Add($"{path}.points: x must increase strictly and y must lie in [0, 1]");
                return null;
            }

            list.Add(new Vec2(x, y));
        }

        if (list.Count < 2)
        {
            issues.Add($"{path}.points: at least two points are required");
            return null;
        }

        return new ScoreFunction(list.ToImmutable(), scaled);
    }

    private static DeferredConfig? ReadDeferred(ConfigObject? d, List<string> issues)
    {
        if (d is null)
        {
            return null;
        }

        string? timing = d.String("acceptTiming");
        DeferredAcceptPolicy policy = DeferredAcceptPolicy.Either;
        if (timing == "afterWord")
        {
            policy = DeferredAcceptPolicy.AfterWord;
        }
        else if (timing != "either")
        {
            issues.Add("deferred.acceptTiming: expected 'either' or 'afterWord'");
        }

        var result = new DeferredConfig(
            policy,
            d.Double("nearU", 0.01, 10),
            d.Double("nearV", 0.01, 10),
            d.Double("nearVUpDot", 0.01, 10),
            d.Bool("barMustCrossStem"),
            d.Double("barCrossTolerance", 0, 5),
            d.Double("assignMaxDistance", 0, 10),
            d.Double("dotMaxLength", 0, 5),
            d.Double("barMinLengthRatio", 0, 1),
            d.Double("barMaxLengthRatio", 1, 100),
            d.Double("extraMarkMaxLength", 0, 10),
            d.Double("extraMarkHorizontalShare", 0, 1),
            d.Double("strictTimingSeverity", 0, 1));
        d.Done();
        return result;
    }

    private static FindingsConfig? ReadFindings(ConfigObject? f, List<string> issues)
    {
        if (f is null)
        {
            return null;
        }

        double f1 = f.Double("f1DtwMean", 0, 10);
        double f2 = f.Double("f2ClosedGap", 0, 10);
        double f4 = f.Double("f4ReachError", 0, 10);
        double f5 = f.Double("f5BaselineError", 0, 10);
        double f6 = f.Double("f6SlantDeviationDeg", 0, 90);
        double sevBase = f.Double("severityBase", 0, 1);
        double sevFull = f.Double("severityFullAtMultiple", 1.01, 100);
        ImmutableDictionary<ErrorCode, double>.Builder fixedSeverity = ImmutableDictionary.CreateBuilder<ErrorCode, double>();
        if (f.Obj("fixedSeverity") is { } fs)
        {
            foreach (string name in fs.Names())
            {
                if (Enum.TryParse(name, out ErrorCode code))
                {
                    fixedSeverity[code] = fs.Double(name, 0, 1);
                }
                else
                {
                    issues.Add($"findings.fixedSeverity.{name}: unknown error code");
                }
            }

            fs.Done();
        }

        f.Done();
        foreach (ErrorCode required in RequiredFixedSeverity)
        {
            if (!fixedSeverity.ContainsKey(required))
            {
                issues.Add($"findings.fixedSeverity.{required}: missing");
            }
        }

        return new FindingsConfig(f1, f2, f4, f5, f6, sevBase, sevFull, fixedSeverity.ToImmutable());
    }

    /// <summary>Чтение объекта JSON с накоплением замечаний и контролем неизвестных ключей.</summary>
    private sealed class ConfigObject
    {
        private readonly JsonElement _element;
        private readonly List<string> _issues;
        private readonly HashSet<string> _used = new(StringComparer.Ordinal);

        private ConfigObject(JsonElement element, string path, List<string> issues)
        {
            _element = element;
            Path = path;
            _issues = issues;
        }

        private string Path { get; }

        public static ConfigObject? Open(JsonElement element, string path, List<string> issues)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                issues.Add($"{(path.Length == 0 ? "(root)" : path)}: expected an object");
                return null;
            }

            return new ConfigObject(element, path, issues);
        }

        public string[] Names() => _element.EnumerateObject().Select(p => p.Name).ToArray();

        public void Ignore(string name) => _used.Add(name);

        public JsonElement? Raw(string name)
        {
            _used.Add(name);
            return _element.TryGetProperty(name, out JsonElement value) ? value : null;
        }

        public ConfigObject? Obj(string name)
        {
            if (Raw(name) is not { } value)
            {
                _issues.Add($"{Full(name)}: missing");
                return null;
            }

            return Open(value, Full(name), _issues);
        }

        public string? String(string name)
        {
            if (Raw(name) is not { } value)
            {
                _issues.Add($"{Full(name)}: missing");
                return null;
            }

            if (value.ValueKind != JsonValueKind.String)
            {
                _issues.Add($"{Full(name)}: expected a string");
                return null;
            }

            return value.GetString();
        }

        public bool Bool(string name)
        {
            if (Raw(name) is not { } value)
            {
                _issues.Add($"{Full(name)}: missing");
                return false;
            }

            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                _issues.Add($"{Full(name)}: expected true or false");
                return false;
            }

            return value.GetBoolean();
        }

        public double Double(string name, double min, double max)
        {
            if (Raw(name) is not { } value)
            {
                _issues.Add($"{Full(name)}: missing");
                return min;
            }

            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) || !double.IsFinite(number))
            {
                _issues.Add($"{Full(name)}: expected a finite number");
                return min;
            }

            if (number < min || number > max)
            {
                _issues.Add(string.Create(CultureInfo.InvariantCulture, $"{Full(name)}: {number} is outside [{min}, {max}]"));
            }

            return number;
        }

        public int Int(string name, int min, int max)
        {
            double value = Double(name, min, max);
            if (value != Math.Floor(value))
            {
                _issues.Add($"{Full(name)}: expected an integer");
            }

            return (int)Math.Clamp(value, min, max);
        }

        public void Done()
        {
            foreach (JsonProperty property in _element.EnumerateObject())
            {
                if (!_used.Contains(property.Name))
                {
                    _issues.Add($"{Full(property.Name)}: unknown key");
                }
            }
        }

        private string Full(string name) => Path.Length == 0 ? name : Path + "." + name;
    }
}
