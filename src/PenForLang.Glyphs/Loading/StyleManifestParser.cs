using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using PenForLang.Core;

namespace PenForLang.Glyphs.Loading;

/// <summary>Разбор и проверка <c>style.json</c> (схема <c>pfl.style/1</c>).</summary>
internal static partial class StyleManifestParser
{
    public const string SchemaName = "pfl.style/1";

    private static readonly string[] TrademarkWords = ["zaner", "bloser"];

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex SemVer();

    [GeneratedRegex(@"^JF[1-9]$")]
    private static partial Regex Family();

    [GeneratedRegex(@"^[a-z]{2}$")]
    private static partial Regex LanguageKey();

    [GeneratedRegex(@"^[a-z][a-z0-9-]{0,31}$")]
    private static partial Regex VariantName();

    public static StyleManifest? Parse(JsonElement root, IssueSink sink)
    {
        if (ObjectReader.Open(root, string.Empty, sink) is not { } o)
        {
            return null;
        }

        int errorsBefore = sink.ErrorCount;

        string? schema = o.String("schema");
        if (schema is not null && schema != SchemaName)
        {
            sink.Error("schema", "style.schema", $"unsupported schema '{schema}', expected '{SchemaName}'");
        }

        StyleId id = default;
        string? idText = o.String("id");
        if (idText is not null && !StyleId.TryParse(idText, out id))
        {
            sink.Error("id", "style.id", $"invalid style id '{idText}': expected 1..{StyleId.MaxLength} chars of [a-z0-9_-], starting with a letter or digit");
        }

        string? version = o.String("version");
        if (version is not null && !SemVer().IsMatch(version))
        {
            sink.Error("version", "style.version", $"version '{version}' is not a semantic version (major.minor.patch)");
        }

        StyleRole? role = o.EnumValue("role", WireNames.Roles);
        string? script = o.String("script");
        if (script is not null && script is not ("cursive" or "print"))
        {
            sink.Error("script", "json.enum", $"unknown value '{script}', expected one of: cursive, print");
        }

        string? language = o.String("language");
        if (language is not null && !LanguageKey().IsMatch(language))
        {
            sink.Error("language", "style.language", $"language '{language}' is not a two-letter lowercase code");
        }

        IReadOnlyDictionary<string, string>? displayName = ParseDisplayName(o, sink);
        GuideLines? lines = ParseLines(o, sink);
        double? slant = ParseSlant(o, sink);
        double? letterSpacing = o.Number("letterSpacing", 0.0, 2.0);
        double? wordSpacing = o.Number("wordSpacing", 0.0, 5.0);
        JoinPolicy? joins = ParseJoins(o, sink);
        ImmutableArray<VariantRule>? variantRules = ParseVariantRules(o, sink);
        ImmutableArray<ImmutableArray<string>>? motorFamilies = ParseMotorFamilies(o, sink);
        CapitalJoinMode? capitals = ParseCapitals(o, sink);
        DeferredTeachTiming? teachTiming = ParseDeferred(o, sink);
        AnimationSettings? animation = ParseAnimation(o, sink);
        o.RejectUnknown();

        if (sink.ErrorCount > errorsBefore
            || id.IsDefault
            || version is null
            || role is not { } roleValue
            || script is null
            || language is null
            || displayName is null
            || lines is null
            || letterSpacing is not { } letter
            || wordSpacing is not { } word
            || joins is null
            || variantRules is not { } variants
            || motorFamilies is not { } motor
            || capitals is not { } capital
            || teachTiming is not { } timing
            || animation is null)
        {
            return null;
        }

        return new StyleManifest(id, version, roleValue, script, language, displayName, lines, slant, letter, word, joins, variants, motor, capital, timing, animation);
    }

    private static SortedDictionary<string, string>? ParseDisplayName(ObjectReader parent, IssueSink sink)
    {
        JsonElement? element = parent.Get("displayName");
        if (element is null || ObjectReader.Open(element.Value, "displayName", sink) is not { })
        {
            return null;
        }

        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        bool ok = true;
        foreach (JsonProperty property in element.Value.EnumerateObject())
        {
            string path = ObjectReader.Child("displayName", property.Name);
            if (!LanguageKey().IsMatch(property.Name))
            {
                sink.Error(path, "style.displayName.language", $"'{property.Name}' is not a two-letter lowercase language code");
                ok = false;
                continue;
            }

            string? text = ObjectReader.ReadString(property.Value, path, sink);
            if (text is null)
            {
                ok = false;
                continue;
            }

            foreach (string word in TrademarkWords)
            {
                if (text.Contains(word, StringComparison.OrdinalIgnoreCase))
                {
                    sink.Error(path, "style.displayName.trademark", $"display name '{text}' contains the trademark word '{word}', which must not appear in the UI; use a generic name");
                    ok = false;
                    break;
                }
            }

            result[property.Name] = text;
        }

        foreach (string required in new[] { "ru", "en" })
        {
            if (!result.ContainsKey(required) && ok)
            {
                sink.Error(ObjectReader.Child("displayName", required), "json.missing", $"display name for language '{required}' is required");
                ok = false;
            }
        }

        return ok ? result : null;
    }

    private static GuideLines? ParseLines(ObjectReader parent, IssueSink sink)
    {
        if (parent.Object("lines") is not { } o)
        {
            return null;
        }

        double? ascender = o.Number("ascender", CoordinateLimits.MinV, CoordinateLimits.MaxV);
        double? cap = o.Number("cap", CoordinateLimits.MinV, CoordinateLimits.MaxV);
        double? xHeight = o.Number("xHeight", CoordinateLimits.MinV, CoordinateLimits.MaxV);
        double? baseline = o.Number("baseline", CoordinateLimits.MinV, CoordinateLimits.MaxV);
        double? descender = o.Number("descender", CoordinateLimits.MinV, CoordinateLimits.MaxV);
        o.RejectUnknown();
        if (ascender is not { } asc || cap is not { } capHeight || xHeight is not { } xh || baseline is not { } bl || descender is not { } desc)
        {
            return null;
        }

        bool ok = true;
        if (xh != 1.0)
        {
            sink.Error("lines.xHeight", "style.lines", $"xHeight must be 1 (the ruler unit), found {ObjectReader.Format(xh)}");
            ok = false;
        }

        if (bl != 0.0)
        {
            sink.Error("lines.baseline", "style.lines", $"baseline must be 0 (the ruler origin), found {ObjectReader.Format(bl)}");
            ok = false;
        }

        if (!(desc < bl))
        {
            sink.Error("lines.descender", "style.lines", $"descender ({ObjectReader.Format(desc)}) must be below the baseline");
            ok = false;
        }

        if (!(capHeight > xh))
        {
            sink.Error("lines.cap", "style.lines", $"cap ({ObjectReader.Format(capHeight)}) must be above the x-height line");
            ok = false;
        }

        if (!(asc >= capHeight))
        {
            sink.Error("lines.ascender", "style.lines", $"ascender ({ObjectReader.Format(asc)}) must not be below cap ({ObjectReader.Format(capHeight)})");
            ok = false;
        }

        return ok ? new GuideLines(asc, capHeight, xh, bl, desc) : null;
    }

    private static double? ParseSlant(ObjectReader parent, IssueSink sink)
    {
        JsonElement? element = parent.Get("slantDeg");
        if (element is null || element.Value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return ObjectReader.ReadNumber(element.Value, "slantDeg", sink, -45.0, 45.0);
    }

    private static JoinPolicy? ParseJoins(ObjectReader parent, IssueSink sink)
    {
        if (parent.Object("joins") is not { } o)
        {
            return null;
        }

        int errorsBefore = sink.ErrorCount;
        var rules = ImmutableArray.CreateBuilder<JoinRule>();
        if (o.Array("rules", 1, int.MaxValue) is { } rulesArray)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int i = 0;
            foreach (JsonElement item in rulesArray.EnumerateArray())
            {
                if (ParseJoinRule(item, ObjectReader.Index("joins.rules", i), sink) is { } rule)
                {
                    foreach (string key in RuleKeys(rule))
                    {
                        if (!seen.Add(key))
                        {
                            sink.Error(ObjectReader.Index("joins.rules", i), "style.joins.duplicate", $"duplicate join rule for exit/entry pair '{key}'");
                        }
                    }

                    rules.Add(rule);
                }

                i++;
            }
        }

        EntryClass? tall = null;
        string? tallText = o.String("tallEntryClass");
        if (tallText is not null)
        {
            if (JoinClasses.TryParseEntry(tallText, out EntryClass parsed))
            {
                tall = parsed;
            }
            else
            {
                sink.Error("joins.tallEntryClass", "style.joins.class", $"unknown entry class '{tallText}', expected one of: E-UP, E-UP-TALL, E-C, E-HUMP");
            }
        }

        if (o.Array("pairOverrides", 0, int.MaxValue, required: false) is { } overrides && overrides.GetArrayLength() > 0)
        {
            sink.Error("joins.pairOverrides", "style.joins.pairOverrides", "pair overrides are not supported by this version of the loader: the array must be empty");
        }

        o.RejectUnknown();
        return sink.ErrorCount > errorsBefore || tall is not { } tallClass ? null : new JoinPolicy(rules.ToImmutable(), tallClass);
    }

    private static IEnumerable<string> RuleKeys(JoinRule rule)
    {
        string exit = rule.Exit.ToWireName();
        if (rule.AnyEntry)
        {
            yield return exit + "/*";
        }

        foreach (EntryClass entry in rule.Entry)
        {
            yield return exit + "/" + entry.ToWireName();
        }
    }

    private static JoinRule? ParseJoinRule(JsonElement element, string path, IssueSink sink)
    {
        if (ObjectReader.Open(element, path, sink) is not { } o)
        {
            return null;
        }

        int errorsBefore = sink.ErrorCount;

        string? family = null;
        JsonElement? familyElement = o.Get("family");
        if (familyElement is { } familyValue && familyValue.ValueKind != JsonValueKind.Null)
        {
            family = ObjectReader.ReadString(familyValue, o.ChildPath("family"), sink);
            if (family is not null && !Family().IsMatch(family))
            {
                sink.Error(o.ChildPath("family"), "style.joins.family", $"family '{family}' is not one of JF1..JF9");
                family = null;
            }
        }

        string? exitText = o.String("exit");
        ExitClass exit = default;
        if (exitText is not null && !JoinClasses.TryParseExit(exitText, out exit))
        {
            sink.Error(o.ChildPath("exit"), "style.joins.class", $"unknown exit class '{exitText}', expected one of: X-BASE, X-HIGH, X-DESC, X-NONE");
            exitText = null;
        }

        var entries = ImmutableArray.CreateBuilder<EntryClass>();
        bool anyEntry = false;
        if (o.Array("entry", 1, 4) is { } entryArray)
        {
            int i = 0;
            foreach (JsonElement item in entryArray.EnumerateArray())
            {
                string entryPath = ObjectReader.Index(o.ChildPath("entry"), i);
                string? text = ObjectReader.ReadString(item, entryPath, sink);
                if (text == "*")
                {
                    anyEntry = true;
                }
                else if (text is not null)
                {
                    if (JoinClasses.TryParseEntry(text, out EntryClass entry))
                    {
                        entries.Add(entry);
                    }
                    else
                    {
                        sink.Error(entryPath, "style.joins.class", $"unknown entry class '{text}', expected one of: E-UP, E-UP-TALL, E-C, E-HUMP, or '*'");
                    }
                }

                i++;
            }
        }

        JoinShape? shape = o.EnumValue("shape", WireNames.JoinShapes);
        bool high = o.Bool("high", required: false) ?? false;

        var parameters = new SortedDictionary<string, double>(StringComparer.Ordinal);
        if (o.Get("params", required: false) is { } paramsElement && ObjectReader.Open(paramsElement, o.ChildPath("params"), sink) is { })
        {
            foreach (JsonProperty property in paramsElement.EnumerateObject())
            {
                if (ObjectReader.ReadNumber(property.Value, ObjectReader.Child(o.ChildPath("params"), property.Name), sink, -10.0, 10.0) is { } number)
                {
                    parameters[property.Name] = number;
                }
            }
        }

        o.RejectUnknown();

        if (sink.ErrorCount > errorsBefore || exitText is null || shape is not { } shapeValue)
        {
            return null;
        }

        if (family is null && !(shapeValue == JoinShape.Break && exit == ExitClass.None))
        {
            sink.Error(path, "style.joins.family", "a rule without a family (null) must have exit 'X-NONE' and shape 'break'");
            return null;
        }

        if (anyEntry && family is not null)
        {
            sink.Error(o.ChildPath("entry"), "style.joins.class", "the wildcard entry '*' is allowed only in the break rule (family null)");
            return null;
        }

        if (shapeValue == JoinShape.Break && family is not null)
        {
            sink.Error(o.ChildPath("shape"), "style.joins.shape", "shape 'break' is allowed only for the rule without a family");
            return null;
        }

        return new JoinRule(family, exit, entries.ToImmutable(), anyEntry, shapeValue, high, parameters);
    }

    private static ImmutableArray<VariantRule>? ParseVariantRules(ObjectReader parent, IssueSink sink)
    {
        if (parent.Array("variantRules", 0, 64) is not { } array)
        {
            return null;
        }

        var result = ImmutableArray.CreateBuilder<VariantRule>();
        bool ok = true;
        int i = 0;
        foreach (JsonElement item in array.EnumerateArray())
        {
            string path = ObjectReader.Index("variantRules", i++);
            if (ObjectReader.Open(item, path, sink) is not { } o)
            {
                ok = false;
                continue;
            }

            string? symbol = o.String("char");
            if (symbol is not null && !IsSingleSymbol(symbol))
            {
                sink.Error(o.ChildPath("char"), "style.variantRules.char", $"'{symbol}' must be a single character");
                symbol = null;
            }

            ExitClass? prevExit = null;
            if (o.Object("when") is { } when)
            {
                string? exitText = when.String("prevExit");
                if (exitText is not null)
                {
                    if (JoinClasses.TryParseExit(exitText, out ExitClass parsed))
                    {
                        prevExit = parsed;
                    }
                    else
                    {
                        sink.Error(when.ChildPath("prevExit"), "style.joins.class", $"unknown exit class '{exitText}', expected one of: X-BASE, X-HIGH, X-DESC, X-NONE");
                    }
                }

                when.RejectUnknown();
            }

            string? variant = o.String("variant");
            if (variant is not null && !VariantName().IsMatch(variant))
            {
                sink.Error(o.ChildPath("variant"), "style.variantRules.variant", $"variant '{variant}' must match [a-z][a-z0-9-]*");
                variant = null;
            }

            o.RejectUnknown();
            if (symbol is null || prevExit is not { } exit || variant is null)
            {
                ok = false;
                continue;
            }

            result.Add(new VariantRule(symbol, exit, variant));
        }

        return ok ? result.ToImmutable() : null;
    }

    private static ImmutableArray<ImmutableArray<string>>? ParseMotorFamilies(ObjectReader parent, IssueSink sink)
    {
        if (parent.Array("motorFamilies", 0, 32) is not { } array)
        {
            return null;
        }

        var result = ImmutableArray.CreateBuilder<ImmutableArray<string>>();
        bool ok = true;
        int i = 0;
        foreach (JsonElement family in array.EnumerateArray())
        {
            string path = ObjectReader.Index("motorFamilies", i++);
            if (ObjectReader.ReadStringArray(family, path, sink, 2, 8) is not { } symbols)
            {
                ok = false;
                continue;
            }

            if (symbols.Any(s => !IsSingleSymbol(s)))
            {
                sink.Error(path, "style.motorFamilies", "every member of a motor family must be a single character");
                ok = false;
            }
            else if (symbols.Distinct(StringComparer.Ordinal).Count() != symbols.Length)
            {
                sink.Error(path, "style.motorFamilies", "a motor family must not list a character twice");
                ok = false;
            }
            else
            {
                result.Add(symbols);
            }
        }

        return ok ? result.ToImmutable() : null;
    }

    private static CapitalJoinMode? ParseCapitals(ObjectReader parent, IssueSink sink)
    {
        if (parent.Object("capitals") is not { } o)
        {
            return null;
        }

        CapitalJoinMode? mode = o.EnumValue("joinMode", WireNames.CapitalJoinModes);
        o.RejectUnknown();
        return mode;
    }

    private static DeferredTeachTiming? ParseDeferred(ObjectReader parent, IssueSink sink)
    {
        if (parent.Object("deferred") is not { } o)
        {
            return null;
        }

        DeferredTeachTiming? timing = o.EnumValue("teachTiming", WireNames.TeachTimings);
        o.RejectUnknown();
        return timing;
    }

    private static AnimationSettings? ParseAnimation(ObjectReader parent, IssueSink sink)
    {
        if (parent.Object("animation", required: false) is not { } o)
        {
            return new AnimationSettings();
        }

        var defaults = new AnimationSettings();
        int errorsBefore = sink.ErrorCount;
        double speed = o.Number("speedXhPerSec", 0.05, 10.0, required: false) ?? defaults.SpeedXhPerSec;
        int cusp = o.Integer("cuspPauseMs", 0, 5000, required: false) ?? defaults.CuspPauseMs;
        double slowdown = o.Number("joinSlowdown", 0.05, 1.0, required: false) ?? defaults.JoinSlowdown;
        int strokeGap = o.Integer("strokeGapMs", 0, 5000, required: false) ?? defaults.StrokeGapMs;
        int deferredGap = o.Integer("deferredGapMs", 0, 5000, required: false) ?? defaults.DeferredGapMs;
        int startHold = o.Integer("startHoldMs", 0, 5000, required: false) ?? defaults.StartHoldMs;
        int endHold = o.Integer("endHoldMs", 0, 5000, required: false) ?? defaults.EndHoldMs;
        int dot = o.Integer("dotMs", 1, 5000, required: false) ?? defaults.DotMs;
        o.RejectUnknown();
        return sink.ErrorCount > errorsBefore ? null : new AnimationSettings(speed, cusp, slowdown, strokeGap, deferredGap, startHold, endHold, dot);
    }

    private static bool IsSingleSymbol(string text) =>
        text.Length == 1 && !char.IsWhiteSpace(text[0]) && !char.IsControl(text[0]) && text[0] is not ('/' or '.');
}
