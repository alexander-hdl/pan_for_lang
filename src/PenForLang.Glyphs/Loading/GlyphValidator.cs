using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using PenForLang.Core.Geometry;

namespace PenForLang.Glyphs.Loading;

/// <summary>
/// Допуски семантической проверки глифа (ARCHITECTURE.md §3.2.7). Это допуски формата хранения, а не методики:
/// числа методики и оценщика лежат в <c>content/config</c>.
/// </summary>
internal static class GlyphLimits
{
    /// <summary>Допуск совпадения точек (вход/выход с телом, стыки сегментов), x-height (§3.2.7, п. 4).</summary>
    public const double PointTolerance = 0.01;

    /// <summary>Наибольший излом на стыке сегментов без точки возврата, градусы (§3.2.7, п. 3).</summary>
    public const double G1MaxDegrees = 10.0;

    /// <summary>Допуск расхождения направления входа/выхода с геометрией, градусы.</summary>
    public const double TangentMatchDegrees = 15.0;

    /// <summary>Наименьшая длина сегмента, x-height.</summary>
    public const double MinSegmentLength = 0.005;

    /// <summary>Допуск положения отложенного штриха относительно привязки, x-height.</summary>
    public const double AnchorTolerance = 0.05;

    /// <summary>Допуск выхода экстремумов за линии стиля, x-height (§3.2.7, п. 5).</summary>
    public const double LineTolerance = 0.1;

    /// <summary>Допуск проверки <c>checks.reach</c>, x-height.</summary>
    public const double ReachTolerance = 0.1;

    /// <summary>Допуск касания базовой линии для ключевой точки, x-height.</summary>
    public const double BaselineTouchTolerance = 0.15;

    /// <summary>Точность разбиения кривых на ломаную при проверках, x-height.</summary>
    public const double FlattenTolerance = 0.004;

    /// <summary>Окно соседства для проверки экстремумов и точек возврата, x-height.</summary>
    public const double NeighbourhoodWindow = 0.05;

    /// <summary>Наименьший поворот касательной в точке возврата, градусы.</summary>
    public const double CuspMinDegrees = 45.0;

    /// <summary>Наибольшая длина точки, x-height.</summary>
    public const double DotMaxLength = 0.1;

    /// <summary>Наименьшая длина перекладины, x-height.</summary>
    public const double BarMinLength = 0.2;

    /// <summary>Наименьшее расстояние между стрелками одного штриха по доле длины.</summary>
    public const double ArrowMinSeparation = 0.02;

    /// <summary>Наибольшее расстояние номера от своего штриха, x-height.</summary>
    public const double NumberMaxDistance = 1.0;
}

/// <summary>Семантическая проверка глифа по стилю: инварианты порядка, непрерывности, привязок и ссылок.</summary>
internal static partial class GlyphValidator
{
    [GeneratedRegex(@"^humps:[1-9]$")]
    private static partial Regex HumpsFeature();

    public static void Validate(Glyph glyph, StyleManifest style, IssueSink sink)
    {
        ValidateIdentity(glyph, style, sink);
        ValidateClasses(glyph, style, sink);
        ValidateStrokeIds(glyph, sink);

        ImmutableArray<GlyphStroke> ordered = [.. glyph.Strokes.OrderBy(s => s.Order)];
        var cuspFractions = glyph.KeyPoints
            .Where(k => k.Type == KeyPointType.Cusp)
            .GroupBy(k => k.StrokeId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(k => k.T).ToArray(), StringComparer.Ordinal);

        if (glyph.LeadIn is { } leadIn)
        {
            ValidatePath(leadIn, "leadIn.segments", [], style, sink, checkG1: true);
        }

        for (int i = 0; i < glyph.Strokes.Length; i++)
        {
            GlyphStroke stroke = glyph.Strokes[i];
            cuspFractions.TryGetValue(stroke.Id, out double[]? cusps);
            ValidatePath(stroke.Path, $"strokes[{Str(i)}].segments", cusps ?? [], style, sink, checkG1: true);
        }

        for (int i = 0; i < glyph.Deferred.Length; i++)
        {
            ValidatePath(glyph.Deferred[i].Path, $"deferred[{Str(i)}].segments", [], style, sink, checkG1: false);
        }

        if (glyph.LeadOut is { } leadOut)
        {
            ValidatePath(leadOut, "leadOut.segments", [], style, sink, checkG1: true);
        }

        ValidateEndpoints(glyph, ordered, sink);
        ValidateDeferred(glyph, sink);
        ValidateKeyPoints(glyph, style, sink);
        ValidateFeatures(glyph, sink);
        ValidateChecks(glyph, style, sink);
        ValidateAnimation(glyph, sink);
        ValidateMeta(glyph, sink);
    }

    private static string Str(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Pt(Vec2 p) => $"({Num(p.X)}, {Num(p.Y)})";

    private static double AngleDegrees(Vec2 a, Vec2 b)
    {
        double dot = Math.Clamp(Vec2.Dot(a.Normalized(), b.Normalized()), -1.0, 1.0);
        return Math.Acos(dot) * 180.0 / Math.PI;
    }

    private static void ValidateIdentity(Glyph glyph, StyleManifest style, IssueSink sink)
    {
        if (!string.Equals(glyph.Id.StyleId, style.Id.Value, StringComparison.Ordinal))
        {
            sink.Error("id", "glyph.id.style", $"glyph id '{glyph.Id}' belongs to style '{glyph.Id.StyleId}' but the package style is '{style.Id.Value}'");
        }

        string symbol = glyph.Symbol;
        if (symbol.Length == 1 && char.IsLetter(symbol[0]))
        {
            GlyphCase expected = char.IsUpper(symbol[0]) ? GlyphCase.Upper : GlyphCase.Lower;
            if (glyph.Case != expected)
            {
                sink.Error("case", "glyph.case.mismatch", $"character '{symbol}' is {(expected == GlyphCase.Upper ? "upper" : "lower")}case but 'case' says {(glyph.Case == GlyphCase.Upper ? "upper" : "lower")}");
            }
        }

        if (glyph.Advance <= 0.0)
        {
            sink.Error("advance", "glyph.advance", "advance must be greater than 0");
        }
        else if (Math.Abs(glyph.Exit.Point.X - glyph.Advance) > 0.5)
        {
            sink.Warning("advance", "glyph.advance.exit", $"advance {Num(glyph.Advance)} is far from the exit point u = {Num(glyph.Exit.Point.X)}");
        }
    }

    private static void ValidateClasses(Glyph glyph, StyleManifest style, IssueSink sink)
    {
        bool entryKnown = style.Joins.Rules.Any(r => r.AnyEntry || r.Entry.Contains(glyph.Entry.Class));
        if (!entryKnown)
        {
            sink.Error("entry.class", "glyph.entry.class.undeclared", $"entry class '{glyph.Entry.Class.ToWireName()}' is not used by any join rule of style '{style.Id.Value}' (joins.rules)");
        }

        bool exitKnown = style.Joins.Rules.Any(r => r.Exit == glyph.Exit.Class);
        if (!exitKnown)
        {
            sink.Error("exit.class", "glyph.exit.class.undeclared", $"exit class '{glyph.Exit.Class.ToWireName()}' is not used by any join rule of style '{style.Id.Value}' (joins.rules)");
        }

        if (glyph.Entry.Height is { } height && Math.Abs(height - glyph.Entry.Point.Y) > GlyphLimits.PointTolerance)
        {
            sink.Error("entry.height", "glyph.entry.height", $"entry.height {Num(height)} differs from entry.point v = {Num(glyph.Entry.Point.Y)}");
        }

        if (glyph.Entry.Tangent.Length < 0.1)
        {
            sink.Error("entry.tangent", "glyph.tangent.zero", "entry.tangent must be a non-zero direction");
        }

        if (glyph.Exit.Tangent.Length < 0.1)
        {
            sink.Error("exit.tangent", "glyph.tangent.zero", "exit.tangent must be a non-zero direction");
        }
    }

    private static void ValidateStrokeIds(Glyph glyph, IssueSink sink)
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < glyph.Strokes.Length; i++)
        {
            GlyphStroke stroke = glyph.Strokes[i];
            string path = $"strokes[{Str(i)}]";
            if (!seen.TryAdd(stroke.Id, path))
            {
                sink.Error(path + ".id", "glyph.stroke.duplicate", $"stroke id '{stroke.Id}' is already used by {seen[stroke.Id]}");
            }

            if (stroke.Order != i + 1)
            {
                sink.Error(path + ".order", "glyph.stroke.order", $"strokes must be listed in writing order numbered 1, 2, ...: strokes[{Str(i)}] has order {Str(stroke.Order)}, expected {Str(i + 1)}");
            }
        }

        for (int i = 0; i < glyph.Deferred.Length; i++)
        {
            string path = $"deferred[{Str(i)}]";
            if (!seen.TryAdd(glyph.Deferred[i].Id, path))
            {
                sink.Error(path + ".id", "glyph.stroke.duplicate", $"stroke id '{glyph.Deferred[i].Id}' is already used by {seen[glyph.Deferred[i].Id]}");
            }
        }
    }

    private static void ValidatePath(
        GlyphPath path,
        string prefix,
        double[] cuspFractions,
        StyleManifest style,
        IssueSink sink,
        bool checkG1)
    {
        for (int i = 0; i < path.Segments.Length; i++)
        {
            GlyphSegment segment = path.Segments[i];
            string segmentPath = $"{prefix}[{Str(i)}]";
            if (path.SegmentLength(i) < GlyphLimits.MinSegmentLength)
            {
                sink.Error(segmentPath, "glyph.segment.length", $"segment has zero length ({Num(path.SegmentLength(i))}); the minimum is {Num(GlyphLimits.MinSegmentLength)}");
            }

            if (i == 0)
            {
                continue;
            }

            GlyphSegment previous = path.Segments[i - 1];
            double gap = Vec2.Distance(previous.P3, segment.P0);
            if (gap > GlyphLimits.PointTolerance)
            {
                sink.Error(segmentPath, "glyph.segment.gap", $"segment starts at {Pt(segment.P0)} but the previous segment ends at {Pt(previous.P3)} (gap {Num(gap)}); segments of one stroke must be contiguous");
                continue;
            }

            if (!checkG1 || path.SegmentLength(i) < GlyphLimits.MinSegmentLength || path.SegmentLength(i - 1) < GlyphLimits.MinSegmentLength)
            {
                continue;
            }

            double angle = AngleDegrees(TangentOfSegment(previous, 1.0), TangentOfSegment(segment, 0.0));
            if (angle > GlyphLimits.G1MaxDegrees)
            {
                double joint = path.FractionAtSegmentEnd(i - 1);
                bool hasCusp = cuspFractions.Any(t => Math.Abs(t - joint) <= 0.01);
                if (!hasCusp)
                {
                    sink.Error(segmentPath, "glyph.segment.g1", $"segments {Str(i - 1)} and {Str(i)} meet at a corner of {Num(angle)} degrees (limit {Num(GlyphLimits.G1MaxDegrees)}); smooth the join or add a 'cusp' keypoint at t = {Num(joint)}");
                }
            }
        }

        ValidateExtent(path, prefix, style, sink);
    }

    private static Vec2 TangentOfSegment(GlyphSegment segment, double t)
    {
        CubicBezier curve = segment.Curve;
        Vec2 d = curve.DerivativeAt(t);
        if (d.Length < 1e-12)
        {
            d = t < 0.5 ? curve.P2 - curve.P0 : curve.P3 - curve.P1;
            if (d.Length < 1e-12)
            {
                d = curve.P3 - curve.P0;
            }
        }

        return d.Normalized();
    }

    private static void ValidateExtent(GlyphPath path, string prefix, StyleManifest style, IssueSink sink)
    {
        (Vec2 min, Vec2 max) = path.GetBounds();
        double top = style.Lines.Ascender + GlyphLimits.LineTolerance;
        double bottom = style.Lines.Descender - GlyphLimits.LineTolerance;
        if (max.Y > top)
        {
            sink.Error(prefix, "glyph.extent.lines", $"path reaches v = {Num(max.Y)}, above the ascender line ({Num(style.Lines.Ascender)}) by more than the tolerance {Num(GlyphLimits.LineTolerance)}");
        }

        if (min.Y < bottom)
        {
            sink.Error(prefix, "glyph.extent.lines", $"path reaches v = {Num(min.Y)}, below the descender line ({Num(style.Lines.Descender)}) by more than the tolerance {Num(GlyphLimits.LineTolerance)}");
        }
    }

    private static void ValidateEndpoints(Glyph glyph, ImmutableArray<GlyphStroke> ordered, IssueSink sink)
    {
        GlyphStroke first = ordered[0];
        GlyphStroke last = ordered[^1];

        double entryGap = Vec2.Distance(glyph.Entry.Point, first.Path.Start);
        if (entryGap > GlyphLimits.PointTolerance)
        {
            sink.Error("entry.point", "glyph.entry.point", $"entry.point {Pt(glyph.Entry.Point)} is not the start of the first stroke {Pt(first.Path.Start)} (distance {Num(entryGap)}, tolerance {Num(GlyphLimits.PointTolerance)})");
        }

        double exitGap = Vec2.Distance(glyph.Exit.Point, last.Path.End);
        if (exitGap > GlyphLimits.PointTolerance)
        {
            sink.Error("exit.point", "glyph.exit.point", $"exit.point {Pt(glyph.Exit.Point)} is not the end of the last stroke {Pt(last.Path.End)} (distance {Num(exitGap)}, tolerance {Num(GlyphLimits.PointTolerance)})");
        }

        if (glyph.Exit.Tangent.Length >= 0.1)
        {
            double angle = AngleDegrees(glyph.Exit.Tangent, last.Path.EndTangent);
            if (angle > GlyphLimits.TangentMatchDegrees)
            {
                sink.Error("exit.tangent", "glyph.exit.tangent", $"exit.tangent differs by {Num(angle)} degrees from the direction at the end of the last stroke (limit {Num(GlyphLimits.TangentMatchDegrees)})");
            }
        }

        if (glyph.LeadIn is { } leadIn)
        {
            double gap = Vec2.Distance(leadIn.End, glyph.Entry.Point);
            if (gap > GlyphLimits.PointTolerance)
            {
                sink.Error("leadIn.segments", "glyph.leadIn.end", $"leadIn ends at {Pt(leadIn.End)} but entry.point is {Pt(glyph.Entry.Point)} (distance {Num(gap)})");
            }

            if (glyph.Entry.Tangent.Length >= 0.1)
            {
                double angle = AngleDegrees(glyph.Entry.Tangent, leadIn.EndTangent);
                if (angle > GlyphLimits.TangentMatchDegrees)
                {
                    sink.Error("entry.tangent", "glyph.entry.tangent", $"entry.tangent differs by {Num(angle)} degrees from the direction at the end of leadIn (limit {Num(GlyphLimits.TangentMatchDegrees)})");
                }
            }
        }

        if (glyph.LeadOut is { } leadOut)
        {
            double gap = Vec2.Distance(leadOut.Start, glyph.Exit.Point);
            if (gap > GlyphLimits.PointTolerance)
            {
                sink.Error("leadOut.segments", "glyph.leadOut.start", $"leadOut starts at {Pt(leadOut.Start)} but exit.point is {Pt(glyph.Exit.Point)} (distance {Num(gap)})");
            }
        }
    }

    private static void ValidateDeferred(Glyph glyph, IssueSink sink)
    {
        for (int i = 0; i < glyph.Deferred.Length; i++)
        {
            DeferredStroke deferred = glyph.Deferred[i];
            string path = $"deferred[{Str(i)}]";
            GlyphStroke? anchorStroke = glyph.FindStroke(deferred.Anchor.StrokeId);
            if (anchorStroke is null)
            {
                sink.Error(path + ".anchor.stroke", "glyph.deferred.anchor.stroke", $"deferred stroke '{deferred.Id}' is anchored to unknown stroke '{deferred.Anchor.StrokeId}'; known strokes: {string.Join(", ", glyph.Strokes.Select(s => s.Id))}");
            }
            else
            {
                Vec2 anchorPoint = anchorStroke.Path.PointAtFraction(deferred.Anchor.T);
                Vec2 expectedMid = anchorPoint + deferred.Anchor.Offset;
                Vec2 actualMid = deferred.Path.PointAtFraction(0.5);
                double distance = Vec2.Distance(expectedMid, actualMid);
                if (distance > GlyphLimits.AnchorTolerance)
                {
                    sink.Error(path + ".anchor", "glyph.deferred.anchor.offset", $"anchor (stroke '{deferred.Anchor.StrokeId}' at t = {Num(deferred.Anchor.T)} plus offset) puts the deferred stroke at {Pt(expectedMid)} but its midpoint is at {Pt(actualMid)} (distance {Num(distance)}, tolerance {Num(GlyphLimits.AnchorTolerance)})");
                }
            }

            if (deferred.Direction == DeferredDirection.LeftToRight && deferred.Path.End.X - deferred.Path.Start.X <= GlyphLimits.PointTolerance)
            {
                sink.Error(path + ".direction", "glyph.deferred.direction", $"deferred stroke '{deferred.Id}' has direction 'ltr' but its path does not go left to right (from u = {Num(deferred.Path.Start.X)} to u = {Num(deferred.Path.End.X)})");
            }

            if (deferred.Kind == DeferredKind.Dot && deferred.Path.Length > GlyphLimits.DotMaxLength)
            {
                sink.Error(path + ".kind", "glyph.deferred.kind", $"a 'dot' must be short (length at most {Num(GlyphLimits.DotMaxLength)}), found {Num(deferred.Path.Length)}; use kind 'bar' or 'cross'");
            }

            if (deferred.Kind == DeferredKind.Bar && deferred.Path.Length < GlyphLimits.BarMinLength)
            {
                sink.Error(path + ".kind", "glyph.deferred.kind", $"a 'bar' must be at least {Num(GlyphLimits.BarMinLength)} long, found {Num(deferred.Path.Length)}; use kind 'dot'");
            }
        }
    }

    private static void ValidateKeyPoints(Glyph glyph, StyleManifest style, IssueSink sink)
    {
        var seen = new List<GlyphKeyPoint>();
        for (int i = 0; i < glyph.KeyPoints.Length; i++)
        {
            GlyphKeyPoint keyPoint = glyph.KeyPoints[i];
            string path = $"keypoints[{Str(i)}]";
            GlyphStroke? stroke = glyph.FindStroke(keyPoint.StrokeId);
            if (stroke is null)
            {
                sink.Error(path + ".stroke", "glyph.keypoint.stroke", $"keypoint refers to unknown stroke '{keyPoint.StrokeId}'; known strokes: {string.Join(", ", glyph.Strokes.Select(s => s.Id))}");
                continue;
            }

            if (seen.Any(k => k.Type == keyPoint.Type && k.StrokeId == keyPoint.StrokeId && Math.Abs(k.T - keyPoint.T) < 0.005))
            {
                sink.Error(path, "glyph.keypoint.duplicate", $"duplicate '{WireNames.Name(keyPoint.Type)}' keypoint on stroke '{keyPoint.StrokeId}' at t = {Num(keyPoint.T)}");
                continue;
            }

            seen.Add(keyPoint);
            GlyphPath geometry = stroke.Path;
            double distance = keyPoint.T * geometry.Length;
            Vec2 position = geometry.PointAtDistance(distance);
            switch (keyPoint.Type)
            {
                case KeyPointType.Start when keyPoint.T != 0.0:
                    sink.Error(path + ".t", "glyph.keypoint.t", $"a 'start' keypoint must have t = 0, found {Num(keyPoint.T)}");
                    break;
                case KeyPointType.End when keyPoint.T != 1.0:
                    sink.Error(path + ".t", "glyph.keypoint.t", $"an 'end' keypoint must have t = 1, found {Num(keyPoint.T)}");
                    break;
                case KeyPointType.YMax or KeyPointType.LoopTop or KeyPointType.HumpTop:
                    if (!IsLocalExtreme(geometry, distance, position.Y, maximum: true))
                    {
                        sink.Error(path, "glyph.keypoint.extremum", $"'{WireNames.Name(keyPoint.Type)}' keypoint at t = {Num(keyPoint.T)} (v = {Num(position.Y)}) is not a local maximum of v on stroke '{keyPoint.StrokeId}'");
                    }

                    break;
                case KeyPointType.YMin:
                    if (!IsLocalExtreme(geometry, distance, position.Y, maximum: false))
                    {
                        sink.Error(path, "glyph.keypoint.extremum", $"'ymin' keypoint at t = {Num(keyPoint.T)} (v = {Num(position.Y)}) is not a local minimum of v on stroke '{keyPoint.StrokeId}'");
                    }

                    break;
                case KeyPointType.Cusp:
                    double turn = AngleDegrees(
                        geometry.TangentAtDistance(Math.Max(0.0, distance - GlyphLimits.NeighbourhoodWindow * 0.4)),
                        geometry.TangentAtDistance(Math.Min(geometry.Length, distance + GlyphLimits.NeighbourhoodWindow * 0.4)));
                    if (turn < GlyphLimits.CuspMinDegrees)
                    {
                        sink.Error(path, "glyph.keypoint.cusp", $"'cusp' keypoint at t = {Num(keyPoint.T)} turns the direction by only {Num(turn)} degrees (at least {Num(GlyphLimits.CuspMinDegrees)} expected)");
                    }

                    break;
                case KeyPointType.BaselineTouch:
                    if (Math.Abs(position.Y - style.Lines.Baseline) > GlyphLimits.BaselineTouchTolerance)
                    {
                        sink.Error(path, "glyph.keypoint.baselineTouch", $"'baselineTouch' keypoint at t = {Num(keyPoint.T)} is at v = {Num(position.Y)}, farther than {Num(GlyphLimits.BaselineTouchTolerance)} from the baseline");
                    }

                    break;
                default:
                    break;
            }
        }
    }

    private static bool IsLocalExtreme(GlyphPath path, double distance, double value, bool maximum)
    {
        const double Slack = 0.01;
        double before = path.PointAtDistance(Math.Max(0.0, distance - GlyphLimits.NeighbourhoodWindow)).Y;
        double after = path.PointAtDistance(Math.Min(path.Length, distance + GlyphLimits.NeighbourhoodWindow)).Y;
        return maximum
            ? value >= before - Slack && value >= after - Slack
            : value <= before + Slack && value <= after + Slack;
    }

    private static void ValidateFeatures(Glyph glyph, IssueSink sink)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < glyph.Features.Length; i++)
        {
            string feature = glyph.Features[i];
            string path = $"features[{Str(i)}]";
            if (!WireNames.Features.Contains(feature) && !HumpsFeature().IsMatch(feature))
            {
                sink.Error(path, "glyph.features.unknown", $"unknown feature '{feature}', expected one of: {string.Join(", ", WireNames.Features)}, humps:N");
            }

            if (!seen.Add(feature))
            {
                sink.Error(path, "glyph.features.duplicate", $"feature '{feature}' is listed twice");
            }
        }

        bool hasDeferred = glyph.Deferred.Length > 0;
        if (hasDeferred != seen.Contains("deferred"))
        {
            sink.Error("features", "glyph.features.mismatch", hasDeferred
                ? "the glyph has deferred strokes but 'deferred' is missing from features"
                : "features lists 'deferred' but the glyph has no deferred strokes");
        }

        bool hasRetrace = glyph.Strokes.Any(s => s.Path.Segments.Any(seg => seg.Retrace));
        if (hasRetrace != seen.Contains("retrace"))
        {
            sink.Error("features", "glyph.features.mismatch", hasRetrace
                ? "some segments are marked 'retrace' but 'retrace' is missing from features"
                : "features lists 'retrace' but no segment is marked 'retrace'");
        }
    }

    private static void ValidateChecks(Glyph glyph, StyleManifest style, IssueSink sink)
    {
        GlyphChecks checks = glyph.Checks;
        double min = double.MaxValue;
        double max = double.MinValue;
        foreach (GlyphStroke stroke in glyph.Strokes)
        {
            (Vec2 lo, Vec2 hi) = stroke.Path.GetBounds();
            min = Math.Min(min, lo.Y);
            max = Math.Max(max, hi.Y);
        }

        for (int i = 0; i < checks.Reach.Length; i++)
        {
            ReachCheck reach = checks.Reach[i];
            double expected = reach.Target ?? style.Lines.ValueOf(reach.Line);
            double actual = reach.Extreme == ExtremeKind.Max ? max : min;
            if (Math.Abs(actual - expected) > GlyphLimits.ReachTolerance)
            {
                sink.Error($"checks.reach[{Str(i)}]", "glyph.checks.reach", $"the {(reach.Extreme == ExtremeKind.Max ? "highest" : "lowest")} point of the strokes is v = {Num(actual)} but the check expects {Num(expected)} ({(reach.Target is null ? WireNames.Name(reach.Line) + " line" : "target")}, tolerance {Num(GlyphLimits.ReachTolerance)})");
            }
        }

        ValidateRanges(glyph, checks.Closed, "checks.closed", sink);
        ValidateRanges(glyph, checks.Loops, "checks.loops", sink);

        for (int i = 0; i < checks.Retrace.Length; i++)
        {
            RetraceCheck retrace = checks.Retrace[i];
            string path = $"checks.retrace[{Str(i)}]";
            GlyphStroke? stroke = glyph.FindStroke(retrace.StrokeId);
            if (stroke is null)
            {
                sink.Error(path + ".stroke", "glyph.checks.retrace", $"unknown stroke '{retrace.StrokeId}'");
            }
            else if (retrace.Segment >= stroke.Path.Segments.Length)
            {
                sink.Error(path + ".segment", "glyph.checks.retrace", $"stroke '{retrace.StrokeId}' has {Str(stroke.Path.Segments.Length)} segment(s), no segment {Str(retrace.Segment)}");
            }
            else if (!stroke.Path.Segments[retrace.Segment].Retrace)
            {
                sink.Error(path + ".segment", "glyph.checks.retrace", $"segment {Str(retrace.Segment)} of stroke '{retrace.StrokeId}' is not marked 'retrace': true");
            }
        }
    }

    private static void ValidateRanges(Glyph glyph, ImmutableArray<StrokeRange> ranges, string name, IssueSink sink)
    {
        for (int i = 0; i < ranges.Length; i++)
        {
            StrokeRange range = ranges[i];
            string path = $"{name}[{Str(i)}]";
            if (glyph.FindStroke(range.StrokeId) is null)
            {
                sink.Error(path + ".stroke", "glyph.checks.range", $"unknown stroke '{range.StrokeId}'");
            }
            else if (!(range.From < range.To))
            {
                sink.Error(path, "glyph.checks.range", $"'from' ({Num(range.From)}) must be less than 'to' ({Num(range.To)})");
            }
        }
    }

    private static void ValidateAnimation(Glyph glyph, IssueSink sink)
    {
        GlyphAnimationMeta animation = glyph.Animation;
        if (animation.StartDot == StartDotMode.LeadIn && glyph.LeadIn is null)
        {
            sink.Error("animation.startDot", "glyph.animation.startDot", "startDot is 'leadIn' but the glyph has no leadIn");
        }

        ImmutableArray<PenStroke> penStrokes = glyph.PenStrokes;
        var numbered = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < animation.Numbers.Length; i++)
        {
            AnimationNumber number = animation.Numbers[i];
            string path = $"animation.numbers[{Str(i)}]";
            PenStroke? stroke = penStrokes.FirstOrDefault(s => s.Id == number.StrokeId);
            if (stroke is null)
            {
                sink.Error(path + ".stroke", "glyph.animation.number.stroke", $"number refers to unknown stroke '{number.StrokeId}'; known strokes: {string.Join(", ", penStrokes.Select(s => s.Id))}");
                continue;
            }

            if (!numbered.TryAdd(number.StrokeId, i))
            {
                sink.Error(path + ".stroke", "glyph.animation.number.duplicate", $"stroke '{number.StrokeId}' already has a number at animation.numbers[{Str(numbered[number.StrokeId])}]; each stroke gets exactly one number");
                continue;
            }

            double distance = PolylineMath.DistanceToPolyline(number.At, stroke.Path.Flatten(GlyphLimits.FlattenTolerance));
            if (distance > GlyphLimits.NumberMaxDistance)
            {
                sink.Error(path + ".at", "glyph.animation.number.position", $"number for stroke '{number.StrokeId}' at {Pt(number.At)} is {Num(distance)} away from the stroke (limit {Num(GlyphLimits.NumberMaxDistance)})");
            }
        }

        if (numbered.Count > 0 && numbered.Count < penStrokes.Length)
        {
            string missing = string.Join(", ", penStrokes.Where(s => !numbered.ContainsKey(s.Id)).Select(s => s.Id));
            sink.Error("animation.numbers", "glyph.animation.number.missing", $"numbers are given for some strokes but not for: {missing}; number all strokes or none");
        }

        var arrowsByStroke = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        for (int i = 0; i < animation.Arrows.Length; i++)
        {
            AnimationArrow arrow = animation.Arrows[i];
            string path = $"animation.arrows[{Str(i)}]";
            if (penStrokes.All(s => s.Id != arrow.StrokeId))
            {
                sink.Error(path + ".stroke", "glyph.animation.arrow.stroke", $"arrow refers to unknown stroke '{arrow.StrokeId}'; known strokes: {string.Join(", ", penStrokes.Select(s => s.Id))}");
                continue;
            }

            if (arrow.T <= 0.0 || arrow.T >= 1.0)
            {
                sink.Error(path + ".t", "glyph.animation.arrow.t", $"arrow t = {Num(arrow.T)} must be strictly between 0 and 1");
                continue;
            }

            if (!arrowsByStroke.TryGetValue(arrow.StrokeId, out List<double>? list))
            {
                list = [];
                arrowsByStroke[arrow.StrokeId] = list;
            }

            if (list.Any(t => Math.Abs(t - arrow.T) < GlyphLimits.ArrowMinSeparation))
            {
                sink.Error(path + ".t", "glyph.animation.arrow.duplicate", $"arrow on stroke '{arrow.StrokeId}' at t = {Num(arrow.T)} duplicates another arrow (minimum separation {Num(GlyphLimits.ArrowMinSeparation)})");
                continue;
            }

            list.Add(arrow.T);
        }
    }

    private static void ValidateMeta(Glyph glyph, IssueSink sink)
    {
        if (glyph.Meta.Reviewed && string.IsNullOrWhiteSpace(glyph.Meta.Reviewer))
        {
            sink.Error("meta.reviewer", "glyph.meta.reviewer", "meta.reviewed is true but meta.reviewer is missing; a reviewed glyph names the reviewer");
        }
    }
}
