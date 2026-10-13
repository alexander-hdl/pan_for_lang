using System.Collections.Immutable;
using PenForLang.Core.Geometry;

namespace PenForLang.Glyphs.Animation;

/// <summary>Параметры построения сценария анимации.</summary>
/// <param name="IncludeLeads">Рисовать входящий штрих перед первым и выходящий после последнего основного штриха.</param>
public sealed record AnimationOptions(bool IncludeLeads = true);

/// <summary>
/// Строит <see cref="AnimationScript"/> из глифа и настроек анимации стиля.
/// </summary>
/// <remarks>
/// Правила шкалы:
/// <list type="bullet">
/// <item>сначала выдержка <see cref="AnimationSettings.StartHoldMs"/> (видна точка старта), затем основные штрихи по порядку;</item>
/// <item>скорость пера — <see cref="AnimationSettings.SpeedXhPerSec"/> x-height/с по длине дуги, умноженная на <c>tempo</c> сегмента
/// и на <see cref="AnimationSettings.JoinSlowdown"/> на входящем и выходящем штрихах;</item>
/// <item>пауза перед сегментом — наибольшая из <c>pauseBeforeMs</c> и <see cref="AnimationSettings.CuspPauseMs"/>,
/// если на стыке есть ключевая точка <c>cusp</c> или излом не меньше 60°;</item>
/// <item>между основными штрихами — <see cref="AnimationSettings.StrokeGapMs"/> (перо в воздухе движется по прямой к началу следующего);</item>
/// <item>отложенные штрихи — после всех основных, перед каждым пауза <see cref="AnimationSettings.DeferredGapMs"/>; точка занимает <see cref="AnimationSettings.DotMs"/>;</item>
/// <item>номер штриха появляется, как только закончен предыдущий (у первого — с начала); стрелка — когда перо проходит её положение;
/// точка старта — вместе с номером штриха;</item>
/// <item>в конце выдержка <see cref="AnimationSettings.EndHoldMs"/>.</item>
/// </list>
/// </remarks>
public static class AnimationScriptBuilder
{
    private const double SharpCornerDegrees = 60.0;
    private const double CuspMatchShare = 0.005;

    /// <summary>Строит сценарий.</summary>
    /// <param name="glyph">Глиф.</param>
    /// <param name="settings">Настройки анимации стиля (<see cref="StyleManifest.Animation"/>).</param>
    /// <param name="options">Параметры построения; <c>null</c> — по умолчанию.</param>
    public static AnimationScript Build(Glyph glyph, AnimationSettings settings, AnimationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(glyph);
        ArgumentNullException.ThrowIfNull(settings);
        options ??= new AnimationOptions();

        ImmutableArray<PenStroke> penStrokes = glyph.PenStrokes;
        int mainCount = glyph.Strokes.Length;
        var animated = ImmutableArray.CreateBuilder<AnimatedStroke>(penStrokes.Length);
        double time = settings.StartHoldMs;

        for (int i = 0; i < penStrokes.Length; i++)
        {
            PenStroke pen = penStrokes[i];
            if (i > 0)
            {
                time += pen.IsDeferred ? settings.DeferredGapMs : settings.StrokeGapMs;
            }

            GlyphPath? lead = i == 0 && options.IncludeLeads ? glyph.LeadIn : null;
            GlyphPath? tail = i == mainCount - 1 && options.IncludeLeads ? glyph.LeadOut : null;
            animated.Add(BuildStroke(glyph, pen, lead, tail, settings, time));
            time = animated[^1].EndMs;
        }

        ImmutableArray<AnimatedStroke> strokes = animated.MoveToImmutable();
        double duration = time + settings.EndHoldMs;

        var numbers = ImmutableArray.CreateBuilder<ScriptNumber>();
        foreach (AnimationNumber number in glyph.Animation.Numbers)
        {
            int index = IndexOf(strokes, number.StrokeId);
            if (index >= 0)
            {
                numbers.Add(new ScriptNumber(index, strokes[index].Number, number.At, AppearTime(strokes, index)));
            }
        }

        var arrows = ImmutableArray.CreateBuilder<ScriptArrow>();
        foreach (AnimationArrow arrow in glyph.Animation.Arrows)
        {
            int index = IndexOf(strokes, arrow.StrokeId);
            if (index < 0)
            {
                continue;
            }

            AnimatedStroke stroke = strokes[index];
            double distance = stroke.BodyStart + (arrow.T * stroke.BodyLength);
            arrows.Add(new ScriptArrow(
                index,
                stroke.Path.PointAtDistance(distance),
                stroke.Path.TangentAtDistance(distance),
                stroke.TimeAtDistance(distance)));
        }

        var startDots = ImmutableArray.CreateBuilder<ScriptStartDot>();
        if (glyph.Animation.StartDot != StartDotMode.None)
        {
            for (int i = 0; i < strokes.Length; i++)
            {
                if (penStrokes[i].Deferred is { Kind: DeferredKind.Dot })
                {
                    // Точка сама является меткой: зелёный кружок закрыл бы её.
                    continue;
                }

                AnimatedStroke stroke = strokes[i];
                bool atLeadStart = i == 0 && glyph.Animation.StartDot == StartDotMode.LeadIn;
                Vec2 position = atLeadStart ? stroke.Path.Start : stroke.Path.PointAtDistance(stroke.BodyStart);
                startDots.Add(new ScriptStartDot(i, position, AppearTime(strokes, i)));
            }
        }

        return new AnimationScript(glyph, settings, strokes, numbers.ToImmutable(), arrows.ToImmutable(), startDots.ToImmutable(), duration);
    }

    private static int IndexOf(ImmutableArray<AnimatedStroke> strokes, string strokeId)
    {
        for (int i = 0; i < strokes.Length; i++)
        {
            if (string.Equals(strokes[i].StrokeId, strokeId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static double AppearTime(ImmutableArray<AnimatedStroke> strokes, int index) =>
        index == 0 ? 0.0 : strokes[index - 1].EndMs;

    private static AnimatedStroke BuildStroke(
        Glyph glyph,
        PenStroke pen,
        GlyphPath? lead,
        GlyphPath? tail,
        AnimationSettings settings,
        double startMs)
    {
        var segments = new List<GlyphSegment>();
        int leadCount = lead?.Segments.Length ?? 0;
        if (lead is not null)
        {
            segments.AddRange(lead.Segments);
        }

        segments.AddRange(pen.Path.Segments);
        int bodyEnd = segments.Count;
        if (tail is not null)
        {
            segments.AddRange(tail.Segments);
        }

        var path = new GlyphPath(segments);
        double bodyStart = path.SegmentStart(leadCount);
        double bodyLength = pen.Path.Length;

        double[] cuspDistances = glyph.KeyPoints
            .Where(k => k.Type == KeyPointType.Cusp && string.Equals(k.StrokeId, pen.Id, StringComparison.Ordinal))
            .Select(k => bodyStart + (k.T * bodyLength))
            .ToArray();

        var knots = ImmutableArray.CreateBuilder<TimeKnot>();
        knots.Add(new TimeKnot(0.0, 0.0));
        double time = 0.0;
        double unitsPerMs = settings.SpeedXhPerSec / 1000.0;

        if (pen.Deferred is { Kind: DeferredKind.Dot })
        {
            time += settings.DotMs;
            knots.Add(new TimeKnot(time, path.Length));
            return new AnimatedStroke(pen.Id, pen.Number, pen.IsDeferred, path, bodyStart, bodyLength, startMs, knots.ToImmutable());
        }

        for (int j = 0; j < segments.Count; j++)
        {
            GlyphSegment segment = segments[j];
            bool isLead = j < leadCount || j >= bodyEnd;
            double length = path.SegmentLength(j);
            double start = path.SegmentStart(j);

            double pause = segment.PauseBeforeMs;
            if (j > 0 && IsCusp(path, j, cuspDistances, bodyLength))
            {
                pause = Math.Max(pause, settings.CuspPauseMs);
            }

            if (pause > 0.0)
            {
                time += pause;
                knots.Add(new TimeKnot(time, start));
            }

            if (length > 0.0)
            {
                double speed = unitsPerMs * segment.Tempo * (isLead ? settings.JoinSlowdown : 1.0);
                time += length / speed;
                knots.Add(new TimeKnot(time, start + length));
            }
        }

        return new AnimatedStroke(pen.Id, pen.Number, pen.IsDeferred, path, bodyStart, bodyLength, startMs, knots.ToImmutable());
    }

    private static bool IsCusp(GlyphPath path, int joint, double[] cuspDistances, double bodyLength)
    {
        double at = path.SegmentStart(joint);
        double tolerance = CuspMatchShare * Math.Max(bodyLength, 1.0);
        if (cuspDistances.Any(d => Math.Abs(d - at) <= tolerance))
        {
            return true;
        }

        Vec2 incoming = path.Segments[joint - 1].Curve.DerivativeAt(1.0);
        Vec2 outgoing = path.Segments[joint].Curve.DerivativeAt(0.0);
        if (incoming.Length < 1e-12 || outgoing.Length < 1e-12)
        {
            return false;
        }

        double dot = Math.Clamp(Vec2.Dot(incoming.Normalized(), outgoing.Normalized()), -1.0, 1.0);
        return Math.Acos(dot) * 180.0 / Math.PI >= SharpCornerDegrees;
    }
}
