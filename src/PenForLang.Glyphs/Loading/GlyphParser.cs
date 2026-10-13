using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using PenForLang.Core.Geometry;

namespace PenForLang.Glyphs.Loading;

/// <summary>Структурный разбор глифа (<c>pfl.glyph/1</c>) в модель. Семантику проверяет <see cref="GlyphValidator"/>.</summary>
internal static partial class GlyphParser
{
    public const string SchemaName = "pfl.glyph/1";

    private const double MinDirectionLength = 0.1;

    [GeneratedRegex(@"^[a-z][a-z0-9-]{0,31}$")]
    private static partial Regex VariantName();

    [GeneratedRegex(@"^[a-z][A-Za-z0-9_-]{0,15}$")]
    private static partial Regex StrokeIdName();

    [GeneratedRegex(@"^[A-Za-z0-9]+(\.[A-Za-z0-9]+)+$")]
    private static partial Regex LabelKey();

    public static Glyph? Parse(JsonElement root, IssueSink sink)
    {
        if (ObjectReader.Open(root, string.Empty, sink) is not { } o)
        {
            return null;
        }

        int errorsBefore = sink.ErrorCount;

        string? schema = o.String("schema");
        if (schema is not null && schema != SchemaName)
        {
            sink.Error("schema", "glyph.schema", $"unsupported schema '{schema}', expected '{SchemaName}'");
        }

        string? idText = o.String("id");
        GlyphId id = default;
        if (idText is not null && !GlyphId.TryParse(idText, out id))
        {
            sink.Error("id", "glyph.id", $"id '{idText}' does not match '<styleId>/<char>.<variant>'");
            idText = null;
        }

        string? symbol = o.String("char");
        if (symbol is not null && !(symbol.Length == 1 && !char.IsWhiteSpace(symbol[0]) && !char.IsControl(symbol[0]) && symbol[0] is not ('/' or '.')))
        {
            sink.Error("char", "glyph.char", $"'{symbol}' must be a single character (not whitespace, '/' or '.')");
            symbol = null;
        }

        GlyphCase? glyphCase = o.EnumValue("case", WireNames.Cases);
        string? variant = o.String("variant");
        if (variant is not null && !VariantName().IsMatch(variant))
        {
            sink.Error("variant", "glyph.variant", $"variant '{variant}' must match [a-z][a-z0-9-]*");
            variant = null;
        }

        if (idText is not null && symbol is not null && !string.Equals(symbol, id.Symbol, StringComparison.Ordinal))
        {
            sink.Error("char", "glyph.id.char", $"char '{symbol}' does not match the character '{id.Symbol}' in id '{idText}'");
            symbol = null;
        }

        if (idText is not null && variant is not null && !string.Equals(variant, id.Variant, StringComparison.Ordinal))
        {
            sink.Error("variant", "glyph.id.variant", $"variant '{variant}' does not match the variant '{id.Variant}' in id '{idText}'");
            variant = null;
        }

        double? advance = o.Number("advance", 0.0, 4.0);
        GlyphEntry? entry = ParseEntry(o, sink);
        GlyphExit? exit = ParseExit(o, sink);
        GlyphPath? leadIn = ParseLead(o, "leadIn", sink);
        GlyphPath? leadOut = ParseLead(o, "leadOut", sink);
        ImmutableArray<GlyphStroke>? strokes = ParseStrokes(o, sink);
        ImmutableArray<DeferredStroke>? deferred = ParseDeferred(o, sink);
        ImmutableArray<GlyphKeyPoint>? keyPoints = ParseKeyPoints(o, sink);
        ImmutableArray<string>? features = ParseFeatures(o, sink);
        GlyphChecks? checks = ParseChecks(o, sink);
        GlyphAnimationMeta? animation = ParseAnimation(o, sink);
        GlyphMeta? meta = ParseMeta(o, sink);
        o.RejectUnknown();

        if (sink.ErrorCount > errorsBefore
            || idText is null
            || symbol is null
            || variant is null
            || glyphCase is not { } caseValue
            || advance is not { } advanceValue
            || entry is null
            || exit is null
            || strokes is not { } strokeList
            || deferred is not { } deferredList
            || keyPoints is not { } keyPointList
            || features is not { } featureList
            || checks is null
            || animation is null
            || meta is null)
        {
            return null;
        }

        return new Glyph(id, caseValue, advanceValue, entry, exit, leadIn, leadOut, strokeList, deferredList, keyPointList, featureList, checks, animation, meta);
    }

    private static GlyphEntry? ParseEntry(ObjectReader parent, IssueSink sink)
    {
        if (parent.Object("entry") is not { } o)
        {
            return null;
        }

        string? classText = o.String("class");
        EntryClass entryClass = default;
        if (classText is not null && !JoinClasses.TryParseEntry(classText, out entryClass))
        {
            sink.Error(o.ChildPath("class"), "glyph.class", $"unknown entry class '{classText}', expected one of: E-UP, E-UP-TALL, E-C, E-HUMP");
            classText = null;
        }

        double? height = o.Number("height", 0.0, CoordinateLimits.MaxV, required: false);
        Vec2? point = o.Point("point");
        Vec2? tangent = ReadDirection(o, "tangent");
        o.RejectUnknown();
        return classText is null || point is not { } p || tangent is not { } t ? null : new GlyphEntry(entryClass, p, t, height);
    }

    private static GlyphExit? ParseExit(ObjectReader parent, IssueSink sink)
    {
        if (parent.Object("exit") is not { } o)
        {
            return null;
        }

        string? classText = o.String("class");
        ExitClass exitClass = default;
        if (classText is not null && !JoinClasses.TryParseExit(classText, out exitClass))
        {
            sink.Error(o.ChildPath("class"), "glyph.class", $"unknown exit class '{classText}', expected one of: X-BASE, X-HIGH, X-DESC, X-NONE");
            classText = null;
        }

        Vec2? point = o.Point("point");
        Vec2? tangent = ReadDirection(o, "tangent");
        o.RejectUnknown();
        return classText is null || point is not { } p || tangent is not { } t ? null : new GlyphExit(exitClass, p, t);
    }

    /// <summary>Направление: точка <c>[dx, dv]</c> ненулевой длины; в модель попадает единичный вектор.</summary>
    private static Vec2? ReadDirection(ObjectReader o, string name)
    {
        if (o.Point(name) is not { } direction)
        {
            return null;
        }

        if (direction.Length < MinDirectionLength)
        {
            o.Sink.Error(o.ChildPath(name), "glyph.tangent.zero", $"{name} must be a non-zero direction (length at least 0.1)");
            return null;
        }

        return direction.Normalized();
    }

    private static GlyphPath? ParseLead(ObjectReader parent, string name, IssueSink sink)
    {
        JsonElement? element = parent.Get(name);
        if (element is null || element.Value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (ObjectReader.Open(element.Value, name, sink) is not { } o)
        {
            return null;
        }

        GlyphPath? path = ParseSegments(o, "segments", sink);
        o.RejectUnknown();
        return path;
    }

    private static ImmutableArray<GlyphStroke>? ParseStrokes(ObjectReader parent, IssueSink sink)
    {
        if (parent.Array("strokes", 1, 16) is not { } array)
        {
            return null;
        }

        var result = ImmutableArray.CreateBuilder<GlyphStroke>();
        bool ok = true;
        int i = 0;
        foreach (JsonElement item in array.EnumerateArray())
        {
            string path = ObjectReader.Index("strokes", i++);
            if (ObjectReader.Open(item, path, sink) is not { } o)
            {
                ok = false;
                continue;
            }

            string? id = ParseStrokeId(o, sink);
            int? order = o.Integer("order", 1, 16);
            GlyphPath? geometry = ParseSegments(o, "segments", sink);
            o.RejectUnknown();
            if (id is null || order is not { } orderValue || geometry is null)
            {
                ok = false;
                continue;
            }

            result.Add(new GlyphStroke(id, orderValue, geometry));
        }

        return ok ? result.ToImmutable() : null;
    }

    private static string? ParseStrokeId(ObjectReader o, IssueSink sink)
    {
        string? id = o.String("id");
        if (id is not null && !StrokeIdName().IsMatch(id))
        {
            sink.Error(o.ChildPath("id"), "glyph.stroke.id", $"stroke id '{id}' must match [a-z][A-Za-z0-9_-]* (at most 16 characters)");
            return null;
        }

        return id;
    }

    private static ImmutableArray<DeferredStroke>? ParseDeferred(ObjectReader parent, IssueSink sink)
    {
        JsonElement? element = parent.Get("deferred", required: false);
        if (element is null)
        {
            return ImmutableArray<DeferredStroke>.Empty;
        }

        if (ObjectReader.CheckArray(element.Value, "deferred", sink, 0, 8) is not { } array)
        {
            return null;
        }

        var result = ImmutableArray.CreateBuilder<DeferredStroke>();
        bool ok = true;
        int i = 0;
        foreach (JsonElement item in array.EnumerateArray())
        {
            string path = ObjectReader.Index("deferred", i++);
            if (ObjectReader.Open(item, path, sink) is not { } o)
            {
                ok = false;
                continue;
            }

            string? id = ParseStrokeId(o, sink);
            DeferredKind? kind = o.EnumValue("kind", WireNames.DeferredKinds);
            DeferredDirection? direction = o.EnumValue("direction", WireNames.DeferredDirections);
            GlyphPath? geometry = ParseSegments(o, "segments", sink);
            StrokeAnchor? anchor = ParseAnchor(o, sink);
            o.RejectUnknown();
            if (id is null || kind is not { } kindValue || direction is not { } directionValue || geometry is null || anchor is null)
            {
                ok = false;
                continue;
            }

            result.Add(new DeferredStroke(id, kindValue, directionValue, geometry, anchor));
        }

        return ok ? result.ToImmutable() : null;
    }

    private static StrokeAnchor? ParseAnchor(ObjectReader parent, IssueSink sink)
    {
        if (parent.Object("anchor") is not { } o)
        {
            return null;
        }

        string? stroke = o.String("stroke");
        double? t = o.Number("t", 0.0, 1.0);
        Vec2? offset = o.Point("offset");
        o.RejectUnknown();
        return stroke is null || t is not { } tValue || offset is not { } offsetValue ? null : new StrokeAnchor(stroke, tValue, offsetValue);
    }

    private static GlyphPath? ParseSegments(ObjectReader parent, string name, IssueSink sink)
    {
        if (parent.Array(name, 1, 32) is not { } array)
        {
            return null;
        }

        string arrayPath = parent.ChildPath(name);
        var segments = new List<GlyphSegment>();
        bool ok = true;
        int i = 0;
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (ParseSegment(item, ObjectReader.Index(arrayPath, i++), sink) is { } segment)
            {
                segments.Add(segment);
            }
            else
            {
                ok = false;
            }
        }

        return ok ? new GlyphPath(segments) : null;
    }

    private static GlyphSegment? ParseSegment(JsonElement element, string path, IssueSink sink)
    {
        if (ObjectReader.Open(element, path, sink) is not { } o)
        {
            return null;
        }

        JsonElement? cubic = o.Get("c", required: false);
        JsonElement? line = o.Get("l", required: false);
        Vec2[]? points = null;
        SegmentKind kind = SegmentKind.Cubic;
        if (cubic is not null && line is not null)
        {
            sink.Error(path, "glyph.segment.kind", "a segment must have exactly one of 'c' (cubic) or 'l' (line), found both");
        }
        else if (cubic is null && line is null)
        {
            sink.Error(path, "glyph.segment.kind", "a segment must have exactly one of 'c' (cubic: 4 points) or 'l' (line: 2 points)");
        }
        else if (cubic is { } cubicValue)
        {
            points = ReadPoints(cubicValue, o.ChildPath("c"), 4, sink);
        }
        else
        {
            kind = SegmentKind.Line;
            points = ReadPoints(line!.Value, o.ChildPath("l"), 2, sink);
        }

        bool retrace = o.Bool("retrace", required: false) ?? false;
        string? label = null;
        if (o.Get("label", required: false) is { } labelElement)
        {
            label = ObjectReader.ReadString(labelElement, o.ChildPath("label"), sink);
            if (label is not null && !LabelKey().IsMatch(label))
            {
                sink.Error(o.ChildPath("label"), "glyph.segment.label", $"label key '{label}' must look like '<char>.<name>' (letters and digits)");
                label = null;
            }
        }

        double tempo = o.Number("tempo", 0.1, 5.0, required: false) ?? 1.0;
        int pause = o.Integer("pauseBeforeMs", 0, 5000, required: false) ?? 0;
        o.RejectUnknown();
        if (points is null)
        {
            return null;
        }

        return kind == SegmentKind.Line
            ? GlyphSegment.Line(points[0], points[1], retrace, label, tempo, pause)
            : GlyphSegment.Cubic(points[0], points[1], points[2], points[3], retrace, label, tempo, pause);
    }

    private static Vec2[]? ReadPoints(JsonElement element, string path, int count, IssueSink sink)
    {
        if (ObjectReader.CheckArray(element, path, sink, count, count) is not { } array)
        {
            return null;
        }

        var points = new Vec2[count];
        bool ok = true;
        int i = 0;
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (ObjectReader.ReadPoint(item, ObjectReader.Index(path, i), sink) is { } point)
            {
                points[i] = point;
            }
            else
            {
                ok = false;
            }

            i++;
        }

        return ok ? points : null;
    }

    private static ImmutableArray<GlyphKeyPoint>? ParseKeyPoints(ObjectReader parent, IssueSink sink)
    {
        JsonElement? element = parent.Get("keypoints", required: false);
        if (element is null)
        {
            return ImmutableArray<GlyphKeyPoint>.Empty;
        }

        if (ObjectReader.CheckArray(element.Value, "keypoints", sink, 0, 64) is not { } array)
        {
            return null;
        }

        var result = ImmutableArray.CreateBuilder<GlyphKeyPoint>();
        bool ok = true;
        int i = 0;
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (ObjectReader.Open(item, ObjectReader.Index("keypoints", i++), sink) is not { } o)
            {
                ok = false;
                continue;
            }

            KeyPointType? type = o.EnumValue("type", WireNames.KeyPointTypes);
            string? stroke = o.String("stroke");
            double? t = o.Number("t", 0.0, 1.0);
            o.RejectUnknown();
            if (type is not { } typeValue || stroke is null || t is not { } tValue)
            {
                ok = false;
                continue;
            }

            result.Add(new GlyphKeyPoint(typeValue, stroke, tValue));
        }

        return ok ? result.ToImmutable() : null;
    }

    private static ImmutableArray<string>? ParseFeatures(ObjectReader parent, IssueSink sink)
    {
        JsonElement? element = parent.Get("features", required: false);
        return element is null
            ? ImmutableArray<string>.Empty
            : ObjectReader.ReadStringArray(element.Value, "features", sink, 0, 16);
    }

    private static GlyphChecks? ParseChecks(ObjectReader parent, IssueSink sink)
    {
        if (parent.Object("checks", required: false) is not { } o)
        {
            return GlyphChecks.Empty;
        }

        int errorsBefore = sink.ErrorCount;
        var reach = ImmutableArray.CreateBuilder<ReachCheck>();
        var closed = ImmutableArray.CreateBuilder<StrokeRange>();
        var loops = ImmutableArray.CreateBuilder<StrokeRange>();
        var retrace = ImmutableArray.CreateBuilder<RetraceCheck>();

        if (o.Array("reach", 0, 8, required: false) is { } reachArray)
        {
            int i = 0;
            foreach (JsonElement item in reachArray.EnumerateArray())
            {
                if (ObjectReader.Open(item, ObjectReader.Index("checks.reach", i++), sink) is not { } r)
                {
                    continue;
                }

                double? target = r.Number("target", CoordinateLimits.MinV, CoordinateLimits.MaxV, required: false);
                LineName? line = r.EnumValue("line", WireNames.Lines, required: false);
                ExtremeKind? extreme = r.EnumValue("extreme", WireNames.Extremes);
                if (line is null && target is null && r.Get("line", required: false) is null)
                {
                    sink.Error(ObjectReader.Index("checks.reach", i - 1), "glyph.checks.reach", "a reach check needs a 'line' or a 'target'");
                }

                r.RejectUnknown();
                if (extreme is { } extremeValue && (line is not null || target is not null))
                {
                    reach.Add(new ReachCheck(line, extremeValue, target));
                }
            }
        }

        ParseRanges(o, "closed", closed, sink);
        ParseRanges(o, "loops", loops, sink);

        if (o.Array("retrace", 0, 16, required: false) is { } retraceArray)
        {
            int i = 0;
            foreach (JsonElement item in retraceArray.EnumerateArray())
            {
                if (ObjectReader.Open(item, ObjectReader.Index("checks.retrace", i++), sink) is not { } r)
                {
                    continue;
                }

                string? stroke = r.String("stroke");
                int? segment = r.Integer("segment", 0, 31);
                r.RejectUnknown();
                if (stroke is not null && segment is { } segmentValue)
                {
                    retrace.Add(new RetraceCheck(stroke, segmentValue));
                }
            }
        }

        o.RejectUnknown();
        return sink.ErrorCount > errorsBefore
            ? null
            : new GlyphChecks(reach.ToImmutable(), closed.ToImmutable(), loops.ToImmutable(), retrace.ToImmutable());
    }

    private static void ParseRanges(ObjectReader parent, string name, ImmutableArray<StrokeRange>.Builder target, IssueSink sink)
    {
        if (parent.Array(name, 0, 16, required: false) is not { } array)
        {
            return;
        }

        string arrayPath = parent.ChildPath(name);
        int i = 0;
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (ObjectReader.Open(item, ObjectReader.Index(arrayPath, i++), sink) is not { } r)
            {
                continue;
            }

            string? stroke = r.String("stroke");
            double? from = r.Number("from", 0.0, 1.0);
            double? to = r.Number("to", 0.0, 1.0);
            r.RejectUnknown();
            if (stroke is not null && from is { } fromValue && to is { } toValue)
            {
                target.Add(new StrokeRange(stroke, fromValue, toValue));
            }
        }
    }

    private static GlyphAnimationMeta? ParseAnimation(ObjectReader parent, IssueSink sink)
    {
        if (parent.Object("animation") is not { } o)
        {
            return null;
        }

        int errorsBefore = sink.ErrorCount;
        StartDotMode? startDot = o.EnumValue("startDot", WireNames.StartDots);
        var numbers = ImmutableArray.CreateBuilder<AnimationNumber>();
        var arrows = ImmutableArray.CreateBuilder<AnimationArrow>();

        if (o.Array("numbers", 0, 16) is { } numbersArray)
        {
            int i = 0;
            foreach (JsonElement item in numbersArray.EnumerateArray())
            {
                if (ObjectReader.Open(item, ObjectReader.Index("animation.numbers", i++), sink) is not { } n)
                {
                    continue;
                }

                string? stroke = n.String("stroke");
                Vec2? at = n.Point("at");
                n.RejectUnknown();
                if (stroke is not null && at is { } atValue)
                {
                    numbers.Add(new AnimationNumber(stroke, atValue));
                }
            }
        }

        if (o.Array("arrows", 0, 64) is { } arrowsArray)
        {
            int i = 0;
            foreach (JsonElement item in arrowsArray.EnumerateArray())
            {
                if (ObjectReader.Open(item, ObjectReader.Index("animation.arrows", i++), sink) is not { } a)
                {
                    continue;
                }

                string? stroke = a.String("stroke");
                double? t = a.Number("t", 0.0, 1.0);
                a.RejectUnknown();
                if (stroke is not null && t is { } tValue)
                {
                    arrows.Add(new AnimationArrow(stroke, tValue));
                }
            }
        }

        o.RejectUnknown();
        return sink.ErrorCount > errorsBefore || startDot is not { } startDotValue
            ? null
            : new GlyphAnimationMeta(startDotValue, numbers.ToImmutable(), arrows.ToImmutable());
    }

    private static GlyphMeta? ParseMeta(ObjectReader parent, IssueSink sink)
    {
        if (parent.Object("meta") is not { } o)
        {
            return null;
        }

        string? author = o.String("author");
        string? source = o.String("source");
        if (source is not null && !WireNames.GlyphSources.Contains(source))
        {
            sink.Error(o.ChildPath("source"), "json.enum", $"unknown value '{source}', expected one of: {string.Join(", ", WireNames.GlyphSources)}");
            source = null;
        }

        bool? reviewed = o.Bool("reviewed");
        string? reviewer = o.String("reviewer", required: false);
        string? license = o.String("license", required: false);
        string? notes = o.String("notes", required: false, allowEmpty: true);
        o.RejectUnknown();
        return author is null || source is null || reviewed is not { } reviewedValue
            ? null
            : new GlyphMeta(author, source, reviewedValue, reviewer, license, notes);
    }
}
