using System.Collections.Immutable;
using PenForLang.Core.Geometry;

namespace PenForLang.Glyphs.Animation;

/// <summary>Параметры построения сценария анимации.</summary>
/// <param name="IncludeLeads">Рисовать входящий штрих перед первым и выходящий после последнего основного штриха.</param>
public sealed record AnimationOptions(bool IncludeLeads = true);

/// <summary>
/// Строит <see cref="AnimationScript"/> из глифа или слова и настроек анимации стиля.
/// </summary>
/// <remarks>
/// Правила шкалы:
/// <list type="bullet">
/// <item>сначала выдержка <see cref="AnimationSettings.StartHoldMs"/> (видна точка старта), затем основные штрихи по порядку;</item>
/// <item>скорость пера — <see cref="AnimationSettings.SpeedXhPerSec"/> x-height/с по длине дуги, умноженная на <c>tempo</c> сегмента
/// и на <see cref="AnimationSettings.JoinSlowdown"/> на входящем и выходящем штрихах (в слове — и на соединениях);</item>
/// <item>пауза перед сегментом — наибольшая из <c>pauseBeforeMs</c> и <see cref="AnimationSettings.CuspPauseMs"/>,
/// если на стыке есть ключевая точка <c>cusp</c> или излом не меньше 60°;</item>
/// <item>между основными штрихами — <see cref="AnimationSettings.StrokeGapMs"/> (перо в воздухе движется по прямой к началу следующего);</item>
/// <item>отложенные штрихи — после всех основных (в слове — после слова), перед каждым пауза <see cref="AnimationSettings.DeferredGapMs"/>; точка занимает <see cref="AnimationSettings.DotMs"/>;</item>
/// <item>номер штриха появляется, как только закончен предыдущий (у первого — с начала); стрелка — когда перо проходит её положение;
/// точка старта — вместе с номером штриха;</item>
/// <item>в конце выдержка <see cref="AnimationSettings.EndHoldMs"/>.</item>
/// </list>
/// </remarks>
public static class AnimationScriptBuilder
{
    private const double SharpCornerDegrees = 60.0;
    private const double CuspMatchShare = 0.005;
    private const double WordNumberOffsetU = -0.2;
    private const double WordNumberOffsetV = 0.2;

    /// <summary>Строит сценарий глифа.</summary>
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
        var sources = new List<StrokeSource>(penStrokes.Length);
        for (int i = 0; i < penStrokes.Length; i++)
        {
            PenStroke pen = penStrokes[i];
            GlyphPath? lead = i == 0 && options.IncludeLeads ? glyph.LeadIn : null;
            GlyphPath? tail = i == mainCount - 1 && options.IncludeLeads ? glyph.LeadOut : null;
            sources.Add(GlyphSource(glyph, pen, lead, tail));
        }

        ImmutableArray<AnimatedStroke> strokes = Schedule(sources, settings, out double duration);

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

    /// <summary>
    /// Строит сценарий слова: основные штрихи слова (подход, тела букв, соединения; соединения идут медленнее, как входящий и
    /// выходящий штрихи), затем отложенные штрихи букв — после слова, как показывает политика стиля (§3.2.3).
    /// </summary>
    /// <param name="word">Эталон слова.</param>
    /// <param name="settings">Настройки анимации стиля.</param>
    /// <param name="options">Параметры построения; <c>null</c> — по умолчанию (подход и выход показываются).</param>
    public static AnimationScript Build(WordTemplate word, AnimationSettings settings, AnimationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(word);
        ArgumentNullException.ThrowIfNull(settings);
        options ??= new AnimationOptions();

        var sources = new List<StrokeSource>();
        var placements = new List<(int Letter, string StrokeId, int Pen, double From, double Length)>();
        int number = 1;
        foreach (WordPenStroke pen in word.PenStrokes)
        {
            var segments = new List<GlyphSegment>();
            var slow = new List<bool>();
            var cusps = new List<double>();
            double offset = 0.0;
            double bodyStart = -1.0;
            double bodyEnd = 0.0;
            foreach (WordPart part in pen.Parts)
            {
                if (!options.IncludeLeads && part.Kind is WordPartKind.LeadIn or WordPartKind.LeadOut)
                {
                    continue;
                }

                foreach (GlyphSegment segment in part.Path.Segments)
                {
                    segments.Add(segment);
                    slow.Add(part.Kind != WordPartKind.Body);
                }

                if (part.Kind == WordPartKind.Body)
                {
                    bodyStart = bodyStart < 0.0 ? offset : bodyStart;
                    bodyEnd = offset + part.Path.Length;
                    placements.Add((part.Letter, part.StrokeId ?? string.Empty, pen.Index, offset, part.Path.Length));
                    foreach (GlyphKeyPoint key in word.Letters[part.Letter].Glyph.KeyPoints)
                    {
                        if (key.Type == KeyPointType.Cusp && string.Equals(key.StrokeId, part.StrokeId, StringComparison.Ordinal))
                        {
                            cusps.Add(offset + (key.T * part.Path.Length));
                        }
                    }
                }

                offset += part.Path.Length;
            }

            double start = Math.Max(bodyStart, 0.0);
            sources.Add(new StrokeSource($"w{pen.Index + 1}", number++, false, false, segments, [.. slow], start, Math.Max(bodyEnd - start, 0.0), [.. cusps]));
        }

        foreach (WordDeferred deferred in word.Deferred)
        {
            var segments = new List<GlyphSegment>(deferred.Path.Segments);
            sources.Add(new StrokeSource(
                $"{deferred.SlotId}.{deferred.Letter}",
                number++,
                true,
                deferred.Kind == DeferredKind.Dot,
                segments,
                new bool[segments.Count],
                0.0,
                deferred.Path.Length,
                []));
        }

        ImmutableArray<AnimatedStroke> strokes = Schedule(sources, settings, out double duration);
        int mainCount = word.PenStrokes.Length;

        var numbers = ImmutableArray.CreateBuilder<ScriptNumber>();
        var startDots = ImmutableArray.CreateBuilder<ScriptStartDot>();
        for (int i = 0; i < strokes.Length; i++)
        {
            AnimatedStroke stroke = strokes[i];
            Vec2 at = stroke.Path.Start + new Vec2(WordNumberOffsetU, WordNumberOffsetV);
            if (i == 0 && word.Letters[0].Glyph.Animation.Numbers.Length > 0)
            {
                at = word.Letters[0].Glyph.Animation.Numbers[0].At;
            }

            bool dot = false;
            if (i >= mainCount)
            {
                WordDeferred deferred = word.Deferred[i - mainCount];
                WordLetter owner = word.Letters[deferred.Letter];
                AnimationNumber? declared = owner.Glyph.Animation.Numbers.FirstOrDefault(n => string.Equals(n.StrokeId, deferred.SlotId, StringComparison.Ordinal));
                at = declared is null ? deferred.Path.PointAtFraction(0.5) + new Vec2(WordNumberOffsetU, WordNumberOffsetV) : declared.At + new Vec2(owner.OffsetU, 0.0);
                dot = deferred.Kind == DeferredKind.Dot;
            }

            numbers.Add(new ScriptNumber(i, stroke.Number, at, AppearTime(strokes, i)));
            if (!dot)
            {
                startDots.Add(new ScriptStartDot(i, stroke.Path.Start, AppearTime(strokes, i)));
            }
        }

        var arrows = ImmutableArray.CreateBuilder<ScriptArrow>();
        foreach (WordLetter letter in word.Letters)
        {
            foreach (AnimationArrow arrow in letter.Glyph.Animation.Arrows)
            {
                int index = -1;
                double distance = 0.0;
                foreach ((int placedLetter, string strokeId, int pen, double from, double length) in placements)
                {
                    if (placedLetter == letter.Index && string.Equals(strokeId, arrow.StrokeId, StringComparison.Ordinal))
                    {
                        index = pen;
                        distance = from + (arrow.T * length);
                        break;
                    }
                }

                if (index < 0)
                {
                    for (int d = 0; d < word.Deferred.Length; d++)
                    {
                        WordDeferred deferred = word.Deferred[d];
                        if (deferred.Letter == letter.Index && string.Equals(deferred.SlotId, arrow.StrokeId, StringComparison.Ordinal))
                        {
                            index = mainCount + d;
                            distance = arrow.T * deferred.Path.Length;
                            break;
                        }
                    }
                }

                if (index >= 0)
                {
                    AnimatedStroke stroke = strokes[index];
                    arrows.Add(new ScriptArrow(index, stroke.Path.PointAtDistance(distance), stroke.Path.TangentAtDistance(distance), stroke.TimeAtDistance(distance)));
                }
            }
        }

        return new AnimationScript(null, settings, strokes, numbers.ToImmutable(), arrows.ToImmutable(), startDots.ToImmutable(), duration, word);
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

    /// <summary>Исходные данные штриха для расписания: сегменты пути, «ведущие» (медленные) сегменты, положение тела, точки возврата.</summary>
    private sealed record StrokeSource(
        string Id,
        int Number,
        bool IsDeferred,
        bool IsDot,
        List<GlyphSegment> Segments,
        bool[] Slow,
        double BodyStart,
        double BodyLength,
        double[] CuspDistances);

    private static StrokeSource GlyphSource(Glyph glyph, PenStroke pen, GlyphPath? lead, GlyphPath? tail)
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

        var slow = new bool[segments.Count];
        for (int j = 0; j < slow.Length; j++)
        {
            slow[j] = j < leadCount || j >= bodyEnd;
        }

        var path = new GlyphPath(segments);
        double bodyStart = path.SegmentStart(leadCount);
        double bodyLength = pen.Path.Length;
        double[] cusps = glyph.KeyPoints
            .Where(k => k.Type == KeyPointType.Cusp && string.Equals(k.StrokeId, pen.Id, StringComparison.Ordinal))
            .Select(k => bodyStart + (k.T * bodyLength))
            .ToArray();
        return new StrokeSource(pen.Id, pen.Number, pen.IsDeferred, pen.Deferred is { Kind: DeferredKind.Dot }, segments, slow, bodyStart, bodyLength, cusps);
    }

    /// <summary>Расставляет штрихи на общей шкале: выдержка в начале, паузы между штрихами, отложенные — после основных.</summary>
    private static ImmutableArray<AnimatedStroke> Schedule(List<StrokeSource> sources, AnimationSettings settings, out double duration)
    {
        var animated = ImmutableArray.CreateBuilder<AnimatedStroke>(sources.Count);
        double time = settings.StartHoldMs;
        for (int i = 0; i < sources.Count; i++)
        {
            StrokeSource source = sources[i];
            if (i > 0)
            {
                time += source.IsDeferred ? settings.DeferredGapMs : settings.StrokeGapMs;
            }

            animated.Add(BuildStroke(source, settings, time));
            time = animated[^1].EndMs;
        }

        duration = time + settings.EndHoldMs;
        return animated.MoveToImmutable();
    }

    private static AnimatedStroke BuildStroke(StrokeSource source, AnimationSettings settings, double startMs)
    {
        List<GlyphSegment> segments = source.Segments;
        var path = new GlyphPath(segments);

        var knots = ImmutableArray.CreateBuilder<TimeKnot>();
        knots.Add(new TimeKnot(0.0, 0.0));
        double time = 0.0;
        double unitsPerMs = settings.SpeedXhPerSec / 1000.0;

        if (source.IsDot)
        {
            time += settings.DotMs;
            knots.Add(new TimeKnot(time, path.Length));
            return new AnimatedStroke(source.Id, source.Number, source.IsDeferred, path, source.BodyStart, source.BodyLength, startMs, knots.ToImmutable());
        }

        for (int j = 0; j < segments.Count; j++)
        {
            GlyphSegment segment = segments[j];
            double length = path.SegmentLength(j);
            double start = path.SegmentStart(j);

            double pause = segment.PauseBeforeMs;
            if (j > 0 && IsCusp(path, j, source.CuspDistances, source.BodyLength))
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
                double speed = unitsPerMs * segment.Tempo * (source.Slow[j] ? settings.JoinSlowdown : 1.0);
                time += length / speed;
                knots.Add(new TimeKnot(time, start + length));
            }
        }

        return new AnimatedStroke(source.Id, source.Number, source.IsDeferred, path, source.BodyStart, source.BodyLength, startMs, knots.ToImmutable());
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
