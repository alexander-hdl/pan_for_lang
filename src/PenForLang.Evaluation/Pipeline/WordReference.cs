using System.Collections.Immutable;
using PenForLang.Core.Geometry;
using PenForLang.Evaluation.Config;
using PenForLang.Glyphs;

namespace PenForLang.Evaluation.Pipeline;

/// <summary>Вид участка эталона слова, к которому относится его точка.</summary>
internal enum ZoneKind : byte
{
    /// <summary>Подход первой буквы.</summary>
    LeadIn,

    /// <summary>Тело буквы (включая точки входа и выхода).</summary>
    Body,

    /// <summary>Внутренние точки соединения (геометрия).</summary>
    Join,

    /// <summary>Внутренние точки прямого «мостика» между штрихами эталона (разрыв стиля, штрих буквы): отрыв ожидаем.</summary>
    Connector,

    /// <summary>Выход последней буквы.</summary>
    LeadOut,
}

/// <summary>
/// Диапазон эталона между телами: <c>[Lo, Hi]</c> = конец тела левой — начало тела правой (соединение) либо конец и начало
/// соседних штрихов одной буквы. Отрыв пера в диапазоне — альтернатива геометрии (A03).
/// </summary>
/// <param name="Lo">Индекс конца предыдущего тела.</param>
/// <param name="Hi">Индекс начала следующего тела.</param>
/// <param name="Join">Индекс соединения между буквами или −1 (штрихи одной буквы).</param>
/// <param name="Letter">Левая буква.</param>
/// <param name="Expected">Отрыв здесь ожидаем по эталону (разрыв стиля, штрихи одной буквы).</param>
internal sealed record JoinInterval(int Lo, int Hi, int Join, int Letter, bool Expected);

/// <summary>Ретрейс эталона слова: сегмент идёт по уже написанному (включая соединение).</summary>
/// <param name="Letter">Буква.</param>
/// <param name="FromIndex">Начало участка в <see cref="WordReference.Pos"/>.</param>
/// <param name="ToIndex">Конец участка.</param>
/// <param name="RefShare">Доля участка эталона, лежащая по ранее написанному (или <c>null</c>, если писать нечего).</param>
/// <param name="StartsBody">Участок начинается в начале тела буквы (повторяет соединение, а не штрих самой буквы).</param>
internal sealed record WordRetrace(int Letter, int FromIndex, int ToIndex, double? RefShare, bool StartsBody);

/// <summary>Слот отложенного штриха слова.</summary>
/// <param name="Letter">Буква.</param>
/// <param name="Slot">Слот в виде, принятом назначением отложенных штрихов.</param>
/// <param name="AnchorIndex">Индекс точки привязки (штрих буквы в доле <c>anchor.t</c>) в <see cref="WordReference.Pos"/>.</param>
internal sealed record WordSlot(int Letter, RefDeferred Slot, int AnchorIndex);

/// <summary>
/// Эталон слова, подготовленный для выравнивания (кеш по экземпляру <see cref="WordTemplate"/>; A33): основной след
/// одной ломаной (подход, тела, соединения, «мостики» между штрихами, выход), зоны точек, границы тел, диапазоны отрыва,
/// ретрейсы, слоты отложенных штрихов и копии глифов букв для локальной оценки в слове.
/// </summary>
internal sealed class WordReference : INormalizationReference
{
    private WordReference()
    {
    }

    public required WordTemplate Template { get; init; }

    /// <summary>Сглаженная ломаная основного следа в порядке написания (с мостиками между штрихами).</summary>
    public required Vec2[] Pos { get; init; }

    public required Vec2[] Dir { get; init; }

    public required ZoneKind[] Zone { get; init; }

    /// <summary>Индекс начала тела каждой буквы (первый штрих) в <see cref="Pos"/>.</summary>
    public required int[] BodyStart { get; init; }

    /// <summary>Индекс конца тела каждой буквы (последний штрих).</summary>
    public required int[] BodyEnd { get; init; }

    public required ImmutableArray<JoinInterval> Intervals { get; init; }

    /// <summary>Префиксные суммы весов пропуска: <c>SkipPrefix[j]</c> — число точек тела среди <c>[0, j)</c>; длина <c>N + 1</c>.</summary>
    public required double[] SkipPrefix { get; init; }

    /// <summary>Наибольший индекс допустимого старта (конец подхода — начало тела первой буквы).</summary>
    public required int OpenBeginMax { get; init; }

    /// <summary>Наименьший индекс допустимого конца (конец тела последней буквы).</summary>
    public required int OpenEndMin { get; init; }

    public required ImmutableArray<WordRetrace> Retrace { get; init; }

    public required ImmutableArray<WordSlot> Slots { get; init; }

    /// <summary>Глифы букв для локальной оценки: подход только у первой, выход только у последней, без отложенных штрихов.</summary>
    public required ImmutableArray<Glyph> LetterGlyphs { get; init; }

    /// <inheritdoc />
    public required double? SlantDeg { get; init; }

    /// <inheritdoc />
    public required double CentroidU { get; init; }

    /// <inheritdoc />
    public required double LowV { get; init; }

    /// <inheritdoc />
    public required double HighV { get; init; }

    /// <summary>Середина горизонтального размаха эталона (подход, тела, соединения, выход): начальный сдвиг рукописи по u.</summary>
    public required double SpanCenterU { get; init; }

    public int Count => Pos.Length;

    /// <summary>Диапазон соединения <paramref name="join"/> между буквами (диапазон всегда есть: геометрия или мостик).</summary>
    public JoinInterval IntervalOf(int join) => Intervals.First(i => i.Join == join);

    public static WordReference Build(WordTemplate template, ScoringConfig config, SavitzkyGolayFilter filter)
    {
        const double FlattenTolerance = 0.002;
        double step = config.ResampleStep;
        int letters = template.Letters.Length;
        var pos = new List<Vec2>();
        var dir = new List<Vec2>();
        var zone = new List<ZoneKind>();
        var penArrays = new List<Vec2[]>();
        var bodyStart = new int[letters];
        var bodyEnd = new int[letters];
        Array.Fill(bodyStart, -1);
        var bodyRanges = new Dictionary<(int Letter, string StrokeId), (int From, int To)>();
        var intervals = ImmutableArray.CreateBuilder<JoinInterval>();
        var retraceSources = new List<(int Letter, int From, int To)>();
        int previousLetter = -1;
        int previousEnd = -1;

        foreach (WordPenStroke pen in template.PenStrokes)
        {
            var points = new List<Vec2>();
            var zones = new List<ZoneKind>();
            var ranges = new List<(WordPart Part, int From, int To)>();
            foreach (WordPart part in pen.Parts)
            {
                Vec2[] flat = part.Path.Flatten(FlattenTolerance);
                var resampled = new Vec2[Resampler.CountForStep(part.Path.Length, step)];
                Resampler.Resample(flat, resampled);
                ZoneKind kind = part.Kind switch
                {
                    WordPartKind.LeadIn => ZoneKind.LeadIn,
                    WordPartKind.Body => ZoneKind.Body,
                    WordPartKind.Join => ZoneKind.Join,
                    _ => ZoneKind.LeadOut,
                };
                int from = points.Count == 0 ? 0 : points.Count - 1;
                for (int k = points.Count == 0 ? 0 : 1; k < resampled.Length; k++)
                {
                    points.Add(resampled[k]);
                    zones.Add(kind);
                }

                ranges.Add((part, from, points.Count - 1));
            }

            Vec2[] smoothed = [.. points];
            if (points.Count >= 3)
            {
                filter.Apply(smoothed.ToArray(), smoothed);
            }

            penArrays.Add(smoothed);

            // Мостик от конца предыдущего штриха к началу этого: ожидаемый отрыв (разрыв стиля или второй штрих буквы).
            if (pos.Count > 0)
            {
                Vec2 last = pos[^1];
                Vec2 first = smoothed[0];
                int bridge = Math.Max(0, Resampler.CountForStep(Vec2.Distance(last, first), step) - 2);
                for (int k = 1; k <= bridge; k++)
                {
                    pos.Add(Vec2.Lerp(last, first, (double)k / (bridge + 1)));
                    dir.Add((first - last).Normalized());
                    zone.Add(ZoneKind.Connector);
                }
            }

            int offset = pos.Count;
            pos.AddRange(smoothed);
            dir.AddRange(PolylineTools.Directions(smoothed));
            zone.AddRange(zones);

            (WordPart Part, int From, int To) firstBody = default;
            (WordPart Part, int From, int To) lastBody = default;
            foreach ((WordPart part, int from, int to) in ranges.Where(r => r.Part.Kind == WordPartKind.Body))
            {
                int a = offset + from;
                int b = offset + to;
                if (firstBody.Part is null)
                {
                    firstBody = (part, a, b);
                }

                lastBody = (part, a, b);
                if (bodyStart[part.Letter] < 0)
                {
                    bodyStart[part.Letter] = a;
                }

                bodyEnd[part.Letter] = b;
                bodyRanges[(part.Letter, part.StrokeId ?? string.Empty)] = (a, b);
                double length = part.Path.Length;
                for (int s = 0; s < part.Path.Segments.Length && length > 0.0; s++)
                {
                    if (!part.Path.Segments[s].Retrace)
                    {
                        continue;
                    }

                    double f0 = part.Path.SegmentStart(s) / length;
                    double f1 = (part.Path.SegmentStart(s) + part.Path.SegmentLength(s)) / length;
                    retraceSources.Add((
                        part.Letter,
                        a + (int)Math.Round(f0 * (b - a), MidpointRounding.AwayFromZero),
                        a + (int)Math.Round(f1 * (b - a), MidpointRounding.AwayFromZero)));
                }
            }

            // Отрыв между предыдущим штрихом и этим ожидаем по эталону (разрыв стиля, второй штрих буквы).
            if (previousEnd >= 0 && firstBody.Part is not null)
            {
                int join = firstBody.Part.Letter != previousLetter ? previousLetter : -1;
                intervals.Add(new JoinInterval(previousEnd, firstBody.From, join, previousLetter, true));
            }

            previousLetter = lastBody.Part!.Letter;
            previousEnd = lastBody.To;
        }

        // Соединения с геометрией: внутри одного штриха от конца тела буквы k до начала тела k + 1; отрыв — альтернатива.
        foreach (WordJoin join in template.Joins.Where(j => j.Path is not null))
        {
            intervals.Add(new JoinInterval(bodyEnd[join.Left], bodyStart[join.Right], join.Index, join.Left, false));
        }

        Vec2[] posArray = [.. pos];
        ZoneKind[] zoneArray = [.. zone];
        for (int k = 0; k < letters; k++)
        {
            zoneArray[bodyStart[k]] = ZoneKind.Body;
            zoneArray[bodyEnd[k]] = ZoneKind.Body;
        }

        var skip = new double[posArray.Length + 1];
        for (int j = 0; j < posArray.Length; j++)
        {
            skip[j + 1] = skip[j] + (zoneArray[j] == ZoneKind.Body ? 1.0 : 0.0);
        }

        var retrace = ImmutableArray.CreateBuilder<WordRetrace>();
        foreach ((int letter, int from, int to) in retraceSources)
        {
            double? share = from >= config.Process.Retrace.MinPriorPoints
                ? PolylineTools.ShareOnPriorInk(posArray, from, to, config.Process.Retrace.Tolerance)
                : null;
            retrace.Add(new WordRetrace(letter, from, to, share, from == bodyStart[letter]));
        }

        var slots = ImmutableArray.CreateBuilder<WordSlot>(template.Deferred.Length);
        foreach (WordDeferred deferred in template.Deferred)
        {
            Vec2[] slotPos = PolylineTools.ResampleSmooth(deferred.Path.Flatten(FlattenTolerance), step, filter, int.MaxValue);
            var refSlot = new RefDeferred
            {
                Id = deferred.SlotId,
                Kind = deferred.Kind,
                Direction = deferred.Direction,
                Pos = slotPos,
                Length = deferred.Path.Length,
                AnchorStrokeId = deferred.AnchorStrokeId,
                AnchorT = deferred.AnchorT,
                AnchorOffset = deferred.AnchorOffset,
                ExpectedCenter = deferred.AnchorPoint + deferred.AnchorOffset,
                AnchorPoint = deferred.AnchorPoint,
            };
            (int from, int to) = bodyRanges.TryGetValue((deferred.Letter, deferred.AnchorStrokeId), out var range) ? range : (bodyStart[deferred.Letter], bodyEnd[deferred.Letter]);
            int anchorIndex = from + (int)Math.Round(Math.Clamp(deferred.AnchorT, 0.0, 1.0) * (to - from), MidpointRounding.AwayFromZero);
            slots.Add(new WordSlot(deferred.Letter, refSlot, anchorIndex));
        }

        var angles = new List<double>();
        foreach (Vec2[] stroke in penArrays)
        {
            PolylineTools.CollectSlantAngles(stroke, config.Normalization.Deslant.MinRunLength, config.Normalization.Deslant.MaxDeviationDeg, angles);
        }

        SlantEstimate slant = SlantEstimator.Summarize(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(angles));
        ScaleConfig scale = config.Normalization.Scale;
        return new WordReference
        {
            Template = template,
            Pos = posArray,
            Dir = [.. dir],
            Zone = zoneArray,
            BodyStart = bodyStart,
            BodyEnd = bodyEnd,
            Intervals = [.. intervals.OrderBy(i => i.Lo).ThenBy(i => i.Hi)],
            SkipPrefix = skip,
            OpenBeginMax = bodyStart[0],
            OpenEndMin = bodyEnd[letters - 1],
            Retrace = retrace.ToImmutable(),
            Slots = slots.ToImmutable(),
            LetterGlyphs = LetterGlyphsOf(template),
            SlantDeg = slant.Count > 0 && slant.StdDevDegrees <= config.Normalization.Deslant.MaxSpreadDeg ? slant.MedianDegrees : null,
            CentroidU = PolylineTools.MeanU(penArrays),
            LowV = PolylineTools.PercentileV(penArrays, scale.LowPercentile),
            HighV = PolylineTools.PercentileV(penArrays, scale.HighPercentile),
            SpanCenterU = (penArrays.Min(a => a.Min(p => p.X)) + penArrays.Max(a => a.Max(p => p.X))) / 2.0,
        };
    }

    private static ImmutableArray<Glyph> LetterGlyphsOf(WordTemplate template)
    {
        var result = ImmutableArray.CreateBuilder<Glyph>(template.Letters.Length);
        int last = template.Letters.Length - 1;
        foreach (WordLetter letter in template.Letters)
        {
            Glyph g = letter.Glyph;
            result.Add(g with
            {
                LeadIn = letter.Index == 0 ? g.LeadIn : null,
                LeadOut = letter.Index == last ? g.LeadOut : null,
                Deferred = [],
            });
        }

        return result.MoveToImmutable();
    }
}
