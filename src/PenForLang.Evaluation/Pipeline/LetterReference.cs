using System.Collections.Immutable;
using PenForLang.Core.Geometry;
using PenForLang.Evaluation.Config;
using PenForLang.Glyphs;

namespace PenForLang.Evaluation.Pipeline;

/// <summary>Допустимая точка старта (или конца для обращённого штриха) эталона.</summary>
internal readonly record struct EndpointCandidate(Vec2 Position, Vec2 Direction, TemplatePart Part);

/// <summary>Проверка замкнутости: перо в <c>ToIndex</c> возвращается к <c>FromIndex</c>.</summary>
internal sealed record ClosedRef(int FromIndex, int ToIndex, double FromT, double ToT, double RefGap);

/// <summary>Проверка петли на участке.</summary>
internal sealed record LoopRef(int FromIndex, int ToIndex, double FromT, double ToT);

/// <summary>Проверка ретрейса: сегмент возвращается по написанному. <c>RefShare</c> — доля у самого эталона (или <c>null</c>).</summary>
internal sealed record RetraceRef(int FromIndex, int ToIndex, double FromT, double ToT, double? RefShare);

/// <summary>Касание baseline (keypoint <c>baselineTouch</c>): индекс точки тела штриха и наименьшее v эталона в её окрестности.</summary>
internal sealed record TouchRef(string StrokeId, int Index, double RefMin);

/// <summary>Проверка достижения линии: экстремум v основных штрихов равен <c>Target</c>.</summary>
internal sealed record ReachRef(ExtremeKind Extreme, double Target, bool IsBaseline);

/// <summary>Основной штрих эталона в виде, готовом для сравнения: ресэмплирован, сглажен, с признаками направления.</summary>
internal sealed class RefStroke
{
    public required string Id { get; init; }

    public required int Order { get; init; }

    /// <summary>Входящий + тело + выходящий (где есть), сглаженная ломаная.</summary>
    public required Vec2[] Pos { get; init; }

    public required Vec2[] Dir { get; init; }

    public required Vec2[] PosRev { get; init; }

    public required Vec2[] DirRev { get; init; }

    /// <summary>Индексы начала и конца тела в <see cref="Pos"/>.</summary>
    public required int BodyStart { get; init; }

    public required int BodyEnd { get; init; }

    public required bool HasLeadIn { get; init; }

    /// <summary>Направление прихода в начало тела (<c>entry.tangent</c> глифа) для первого штриха с подходом, иначе нулевой вектор.</summary>
    public Vec2 EntryTangent { get; init; }

    public required bool HasLeadOut { get; init; }

    public required double Length { get; init; }

    public required double BodyLength { get; init; }

    public required ImmutableArray<EndpointCandidate> Starts { get; init; }

    /// <summary>Допустимые концы (старты обращённого штриха); направление — вдоль обратного обхода.</summary>
    public required ImmutableArray<EndpointCandidate> Ends { get; init; }

    public required ImmutableArray<ClosedRef> Closed { get; init; }

    public required ImmutableArray<LoopRef> Loops { get; init; }

    public required ImmutableArray<RetraceRef> Retrace { get; init; }

    /// <summary>Участки с проверяемым направлением обхода (овалы и петли): индексы в <see cref="Pos"/> и доли длины дуги тела.</summary>
    public IEnumerable<(int FromIndex, int ToIndex, double FromT, double ToT)> OrientedRanges =>
        Closed.Select(c => (c.FromIndex, c.ToIndex, c.FromT, c.ToT)).Concat(Loops.Select(l => (l.FromIndex, l.ToIndex, l.FromT, l.ToT)));

    public ReadOnlySpan<Vec2> Body => Pos.AsSpan(BodyStart, BodyEnd - BodyStart + 1);

    public int IndexOfBodyFraction(double t) => BodyStart + (int)Math.Round(Math.Clamp(t, 0.0, 1.0) * (BodyEnd - BodyStart), MidpointRounding.AwayFromZero);
}

/// <summary>Слот отложенного штриха эталона.</summary>
internal sealed class RefDeferred
{
    public required string Id { get; init; }

    public required DeferredKind Kind { get; init; }

    public required DeferredDirection Direction { get; init; }

    public required Vec2[] Pos { get; init; }

    public required double Length { get; init; }

    public required string AnchorStrokeId { get; init; }

    public required double AnchorT { get; init; }

    public required Vec2 AnchorOffset { get; init; }

    /// <summary>Ожидаемая середина штриха в системе эталона.</summary>
    public required Vec2 ExpectedCenter { get; init; }

    /// <summary>Ожидаемая середина слота в долях длины дуги основного штриха (для привязки к стволу рукописи).</summary>
    public required Vec2 AnchorPoint { get; init; }
}

/// <summary>
/// Эталон изолированной буквы, подготовленный один раз (кеш по экземпляру глифа): штрихи с входящим/выходящим штрихом,
/// отложенные слоты, проверки и сводки для нормализации (ARCHITECTURE.md §6.4.1–6.4.2, §6.6, A33).
/// </summary>
internal sealed class LetterReference : INormalizationReference
{
    private LetterReference()
    {
    }

    public required Glyph Glyph { get; init; }

    public required ImmutableArray<RefStroke> Strokes { get; init; }

    public required ImmutableArray<RefDeferred> Deferred { get; init; }

    public required ImmutableArray<ReachRef> Reach { get; init; }

    /// <summary>Касания baseline по keypoints глифа: нижние точки, по которым судится посадка на строку (F5) у любой буквы.</summary>
    public required ImmutableArray<TouchRef> Touches { get; init; }

    /// <summary>Медиана наклона квазивертикалей эталона или <c>null</c>: участков нет или их углы расходятся (x).</summary>
    public required double? SlantDeg { get; init; }

    public required double CentroidU { get; init; }

    public required double LowV { get; init; }

    public required double HighV { get; init; }

    public static LetterReference Build(Glyph glyph, GuideLines lines, ScoringConfig config, SavitzkyGolayFilter filter)
    {
        double step = config.ResampleStep;
        ImmutableArray<GlyphStroke> ordered = [.. glyph.Strokes.OrderBy(s => s.Order)];
        bool useLeadIn = glyph.Animation.StartDot == StartDotMode.LeadIn;
        var strokes = ImmutableArray.CreateBuilder<RefStroke>(ordered.Length);
        var allPositions = new List<Vec2[]>();
        var angles = new List<double>();
        for (int k = 0; k < ordered.Length; k++)
        {
            GlyphStroke source = ordered[k];
            GlyphPath? leadIn = k == 0 ? glyph.LeadIn : null;
            GlyphPath? leadOut = k == ordered.Length - 1 ? glyph.LeadOut : null;
            RefStroke stroke = BuildStroke(glyph, source, leadIn, leadOut, useLeadIn, config, filter);
            strokes.Add(stroke);
            allPositions.Add(stroke.Pos);
            PolylineTools.CollectSlantAngles(stroke.Pos, config.Normalization.Deslant.MinRunLength, config.Normalization.Deslant.MaxDeviationDeg, angles);
        }

        ImmutableArray<RefDeferred> deferred = BuildDeferred(glyph, ordered, step, filter);
        ImmutableArray<ReachRef> reach = BuildReach(glyph, lines);
        ImmutableArray<TouchRef> touches = BuildTouches(glyph, strokes, config);
        SlantEstimate slant = SlantEstimator.Summarize(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(angles));
        ScaleConfig scale = config.Normalization.Scale;
        return new LetterReference
        {
            Glyph = glyph,
            Strokes = strokes.MoveToImmutable(),
            Deferred = deferred,
            Reach = reach,
            Touches = touches,
            SlantDeg = slant.Count > 0 && slant.StdDevDegrees <= config.Normalization.Deslant.MaxSpreadDeg ? slant.MedianDegrees : null,
            CentroidU = PolylineTools.MeanU(allPositions),
            LowV = PolylineTools.PercentileV(allPositions, scale.LowPercentile),
            HighV = PolylineTools.PercentileV(allPositions, scale.HighPercentile),
        };
    }

    private static RefStroke BuildStroke(
        Glyph glyph,
        GlyphStroke source,
        GlyphPath? leadIn,
        GlyphPath? leadOut,
        bool useLeadInStart,
        ScoringConfig config,
        SavitzkyGolayFilter filter)
    {
        const double FlattenTolerance = 0.002;
        var combined = new List<Vec2>();
        void Append(GlyphPath path)
        {
            foreach (Vec2 point in path.Flatten(FlattenTolerance))
            {
                if (combined.Count == 0 || Vec2.Distance(combined[^1], point) > 1e-9)
                {
                    combined.Add(point);
                }
            }
        }

        double leadInLength = 0.0;
        double leadOutLength = 0.0;
        if (leadIn is not null)
        {
            Append(leadIn);
            leadInLength = leadIn.Length;
        }

        Append(source.Path);
        if (leadOut is not null)
        {
            Append(leadOut);
            leadOutLength = leadOut.Length;
        }

        Vec2[] points = [.. combined];
        double totalLength = PolylineMath.Length(points);
        Vec2[] pos = PolylineTools.ResampleSmooth(points, config.ResampleStep, filter, int.MaxValue);
        int last = pos.Length - 1;
        int bodyStart = leadInLength > 0.0 ? (int)Math.Round(leadInLength / totalLength * last, MidpointRounding.AwayFromZero) : 0;
        int bodyEnd = leadOutLength > 0.0 ? (int)Math.Round((totalLength - leadOutLength) / totalLength * last, MidpointRounding.AwayFromZero) : last;
        bodyStart = Math.Clamp(bodyStart, 0, last);
        bodyEnd = Math.Clamp(bodyEnd, bodyStart, last);

        Vec2[] dir = PolylineTools.Directions(pos);
        Vec2[] posRev = PolylineTools.Reversed(pos);
        Vec2[] dirRev = PolylineTools.ReversedDirections(dir);
        double firstSegment = config.Process.Direction.FirstSegmentLength;

        var starts = ImmutableArray.CreateBuilder<EndpointCandidate>();
        if (leadIn is not null && useLeadInStart)
        {
            starts.Add(Candidate(pos, 0, +1, firstSegment, TemplatePart.LeadIn));
        }

        starts.Add(Candidate(pos, bodyStart, +1, firstSegment, TemplatePart.Body));
        var ends = ImmutableArray.CreateBuilder<EndpointCandidate>();
        if (leadOut is not null)
        {
            ends.Add(Candidate(pos, last, -1, firstSegment, TemplatePart.LeadOut));
        }

        ends.Add(Candidate(pos, bodyEnd, -1, firstSegment, TemplatePart.Body));

        var stroke = new RefStroke
        {
            Id = source.Id,
            Order = source.Order,
            Pos = pos,
            Dir = dir,
            PosRev = posRev,
            DirRev = dirRev,
            BodyStart = bodyStart,
            BodyEnd = bodyEnd,
            HasLeadIn = leadIn is not null,
            EntryTangent = leadIn is not null ? glyph.Entry.Tangent : Vec2.Zero,
            HasLeadOut = leadOut is not null,
            Length = totalLength,
            BodyLength = totalLength - leadInLength - leadOutLength,
            Starts = starts.ToImmutable(),
            Ends = ends.ToImmutable(),
            Closed = [],
            Loops = [],
            Retrace = [],
        };

        return WithChecks(glyph, source, stroke, config);
    }

    private static EndpointCandidate Candidate(Vec2[] pos, int index, int direction, double length, TemplatePart part)
    {
        int target = PolylineTools.IndexAtArc(pos, index, direction, length);
        return new EndpointCandidate(pos[index], pos[target] - pos[index], part);
    }

    private static RefStroke WithChecks(Glyph glyph, GlyphStroke source, RefStroke stroke, ScoringConfig config)
    {
        var closed = ImmutableArray.CreateBuilder<ClosedRef>();
        foreach (StrokeRange range in glyph.Checks.Closed.Where(c => c.StrokeId == source.Id))
        {
            int from = stroke.IndexOfBodyFraction(range.From);
            int to = stroke.IndexOfBodyFraction(range.To);
            closed.Add(new ClosedRef(from, to, range.From, range.To, PolylineTools.ClosureGap(stroke.Pos, to, from, PolylineTools.PointsFor(config.Form.ClosureWindow, config.ResampleStep))));
        }

        var loops = ImmutableArray.CreateBuilder<LoopRef>();
        foreach (StrokeRange range in glyph.Checks.Loops.Where(c => c.StrokeId == source.Id))
        {
            loops.Add(new LoopRef(stroke.IndexOfBodyFraction(range.From), stroke.IndexOfBodyFraction(range.To), range.From, range.To));
        }

        var retrace = ImmutableArray.CreateBuilder<RetraceRef>();
        foreach (RetraceCheck check in glyph.Checks.Retrace.Where(c => c.StrokeId == source.Id))
        {
            double from = check.Segment == 0 ? 0.0 : source.Path.FractionAtSegmentEnd(check.Segment - 1);
            double to = source.Path.FractionAtSegmentEnd(check.Segment);
            int fromIndex = stroke.IndexOfBodyFraction(from);
            int toIndex = stroke.IndexOfBodyFraction(to);
            double? refShare = fromIndex >= config.Process.Retrace.MinPriorPoints
                ? PolylineTools.ShareOnPriorInk(stroke.Pos, fromIndex, toIndex, config.Process.Retrace.Tolerance)
                : null;
            retrace.Add(new RetraceRef(fromIndex, toIndex, from, to, refShare));
        }

        return new RefStroke
        {
            Id = stroke.Id,
            Order = stroke.Order,
            Pos = stroke.Pos,
            Dir = stroke.Dir,
            PosRev = stroke.PosRev,
            DirRev = stroke.DirRev,
            BodyStart = stroke.BodyStart,
            BodyEnd = stroke.BodyEnd,
            HasLeadIn = stroke.HasLeadIn,
            EntryTangent = stroke.EntryTangent,
            HasLeadOut = stroke.HasLeadOut,
            Length = stroke.Length,
            BodyLength = stroke.BodyLength,
            Starts = stroke.Starts,
            Ends = stroke.Ends,
            Closed = closed.ToImmutable(),
            Loops = loops.ToImmutable(),
            Retrace = retrace.ToImmutable(),
        };
    }

    private static ImmutableArray<RefDeferred> BuildDeferred(
        Glyph glyph,
        ImmutableArray<GlyphStroke> ordered,
        double step,
        SavitzkyGolayFilter filter)
    {
        var result = ImmutableArray.CreateBuilder<RefDeferred>(glyph.Deferred.Length);
        foreach (DeferredStroke slot in glyph.Deferred)
        {
            Vec2[] pos = PolylineTools.ResampleSmooth(slot.Path.Flatten(0.002), step, filter, int.MaxValue);
            GlyphStroke? anchorStroke = ordered.FirstOrDefault(s => s.Id == slot.Anchor.StrokeId);
            Vec2 anchorPoint = anchorStroke is null ? slot.Path.Start : anchorStroke.Path.PointAtFraction(slot.Anchor.T);
            result.Add(new RefDeferred
            {
                Id = slot.Id,
                Kind = slot.Kind,
                Direction = slot.Direction,
                Pos = pos,
                Length = slot.Path.Length,
                AnchorStrokeId = slot.Anchor.StrokeId,
                AnchorT = slot.Anchor.T,
                AnchorOffset = slot.Anchor.Offset,
                ExpectedCenter = anchorPoint + slot.Anchor.Offset,
                AnchorPoint = anchorPoint,
            });
        }

        return result.MoveToImmutable();
    }

    private static ImmutableArray<TouchRef> BuildTouches(Glyph glyph, ImmutableArray<RefStroke>.Builder strokes, ScoringConfig config)
    {
        int half = PolylineTools.PointsFor(config.Form.BaselineTouchWindow, config.ResampleStep);
        var list = ImmutableArray.CreateBuilder<TouchRef>();
        foreach (GlyphKeyPoint key in glyph.KeyPoints.Where(k => k.Type == KeyPointType.BaselineTouch))
        {
            RefStroke? stroke = strokes.FirstOrDefault(s => s.Id == key.StrokeId);
            if (stroke is null)
            {
                continue;
            }

            int index = stroke.IndexOfBodyFraction(key.T);
            double min = double.PositiveInfinity;
            for (int i = Math.Max(0, index - half); i <= Math.Min(stroke.Pos.Length - 1, index + half); i++)
            {
                min = Math.Min(min, stroke.Pos[i].Y);
            }

            list.Add(new TouchRef(stroke.Id, index, min));
        }

        return list.ToImmutable();
    }

    private static ImmutableArray<ReachRef> BuildReach(Glyph glyph, GuideLines lines)
    {
        var list = ImmutableArray.CreateBuilder<ReachRef>(glyph.Checks.Reach.Length);
        foreach (ReachCheck check in glyph.Checks.Reach)
        {
            // Валидатор гарантирует: задан line или target.
            double target = check.Target ?? lines.ValueOf(check.Line ?? LineName.XHeight);
            bool baseline = check.Line == LineName.Baseline;
            list.Add(new ReachRef(check.Extreme, target, baseline));
        }

        return list.MoveToImmutable();
    }
}
