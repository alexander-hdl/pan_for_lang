using System.Collections.Immutable;
using PenForLang.Core;
using PenForLang.Core.Geometry;
using PenForLang.Evaluation;
using PenForLang.Glyphs;
using PenForLang.Glyphs.Animation;
using PenForLang.Ink;

namespace PenForLang.Testing.SynthInk;

/// <summary>
/// Синтетические попытки письма из глифа (SynthInk-lite, ARCHITECTURE.md §10.3): эталонная траектория по кинематике анимации
/// стиля, дискретизация 200 Гц, «детские» вариации (масштаб, наклон, поворот, дрейф baseline, волна, тремор, скорость,
/// давление) и мутации с разметкой ожидаемой находки. Полностью детерминирован: результат определяется глифом, стилем,
/// параметрами и зерном <see cref="Pcg32"/>.
/// </summary>
/// <remarks>
/// Синтетика проверяет регрессию реализации (находит ли оценщик ровно внесённую ошибку), но не точность на реальных детях;
/// решения go/no-go по ней не принимаются (A08).
/// </remarks>
public sealed class SynthInkGenerator
{
    private const double SampleStepMs = 5.0;
    private const double ApproachStep = 0.03;
    private const double ApproachStepMs = 20.0;
    private const double GapMs = 400.0;

    private readonly StyleManifest _style;
    private readonly SynthOptions _options;

    /// <summary>Создаёт генератор.</summary>
    /// <param name="style">Стиль глифов: из него берётся кинематика анимации.</param>
    /// <param name="options">Пределы вариаций; <c>null</c> — по умолчанию.</param>
    public SynthInkGenerator(StyleManifest style, SynthOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(style);
        _style = style;
        _options = options ?? new SynthOptions();
    }

    /// <summary>Идеальная траектория глифа без вариаций: самооценка эталона должна дать <c>form = 1</c> и ноль находок.</summary>
    /// <param name="glyph">Глиф.</param>
    public RulerInk Ideal(Glyph glyph)
    {
        ArgumentNullException.ThrowIfNull(glyph);
        return Render(BuildIdeal(glyph), Variation.Identity, new Pcg32(0), noisy: false);
    }

    /// <summary>Корректный зашумлённый образец («чистая» попытка ребёнка).</summary>
    /// <param name="glyph">Глиф.</param>
    /// <param name="seed">Зерно: тот же глиф и зерно дают тот же образец.</param>
    public RulerInk Clean(Glyph glyph, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(glyph);
        Pcg32 rng = Rng(glyph, seed);
        Variation variation = Variation.Sample(_options, rng);
        return Render(BuildIdeal(glyph), variation, rng, noisy: true);
    }

    /// <summary>
    /// Образец с одной внесённой мутацией и разметкой. Возвращает <c>null</c>, если мутация к глифу неприменима
    /// (например, нет отложенных штрихов или единственный основной штрих нельзя пропустить).
    /// </summary>
    /// <param name="glyph">Глиф.</param>
    /// <param name="mutation">Мутация.</param>
    /// <param name="seed">Зерно.</param>
    public SynthSample? Mutate(Glyph glyph, SynthMutation mutation, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(glyph);
        Pcg32 rng = Rng(glyph, seed);
        Variation variation = Variation.Sample(_options, rng);
        if (mutation == SynthMutation.Scribble)
        {
            return new SynthSample(Scribble(rng), mutation, [], []);
        }

        List<IdealStroke> strokes = BuildIdeal(glyph);
        List<IdealStroke> main = strokes.Where(s => !s.IsDeferred).ToList();
        List<IdealStroke> deferred = strokes.Where(s => s.IsDeferred).ToList();
        ImmutableArray<ExpectedFinding> expected;
        ImmutableArray<ErrorCode> forbidden = [];
        switch (mutation)
        {
            case SynthMutation.None:
                expected = [];
                break;
            case SynthMutation.WrongStart:
                {
                    IdealStroke target = main[rng.NextInt32(main.Count)];
                    MoveStart(glyph, target, main.IndexOf(target), rng);
                    expected = [new ExpectedFinding(ErrorCode.P1, target.Id)];
                    forbidden = [ErrorCode.P2];
                    break;
                }

            case SynthMutation.ReversedStroke:
                {
                    IdealStroke target = main[rng.NextInt32(main.Count)];
                    Reverse(target);
                    expected = [new ExpectedFinding(ErrorCode.P2, target.Id)];
                    forbidden = [ErrorCode.P1];
                    break;
                }

            case SynthMutation.ExtraStroke:
                AddExtraStroke(strokes, main, rng);
                expected = [new ExpectedFinding(ErrorCode.P4, null)];
                forbidden = [ErrorCode.P5];
                break;
            case SynthMutation.MissingStroke:
                {
                    IdealStroke target = main[^1];
                    if (strokes.Count == 1)
                    {
                        return null;
                    }

                    strokes.Remove(target);
                    expected = [new ExpectedFinding(ErrorCode.P5, target.Id)];
                    forbidden = [ErrorCode.P4];
                    break;
                }

            case SynthMutation.SwappedStrokeOrder:
                if (main.Count < 2)
                {
                    return null;
                }

                SwapOrder(main[0], main[1]);
                expected = [new ExpectedFinding(ErrorCode.P3, main[1].Id)];
                forbidden = [ErrorCode.P4, ErrorCode.P5];
                break;
            case SynthMutation.MissingDeferred:
                if (deferred.Count == 0)
                {
                    return null;
                }

                expected = [.. deferred.Select(d => new ExpectedFinding(ErrorCode.D1, d.Id))];
                strokes.RemoveAll(s => s.IsDeferred);
                forbidden = [ErrorCode.D3, ErrorCode.D4];
                break;
            case SynthMutation.DeferredImmediately:
                if (deferred.Count == 0)
                {
                    return null;
                }

                PlaceDeferredFirst(main, deferred);
                expected = [.. deferred.Select(d => new ExpectedFinding(ErrorCode.D4, d.Id))];
                forbidden = [ErrorCode.D1, ErrorCode.D2, ErrorCode.D3, ErrorCode.P1, ErrorCode.P2, ErrorCode.P3, ErrorCode.P4, ErrorCode.P5];
                break;
            case SynthMutation.MisplacedDeferred:
                if (deferred.Count == 0)
                {
                    return null;
                }

                foreach (IdealStroke d in deferred)
                {
                    double angle = (Math.PI / 6.0) + (rng.NextDouble() * (2.0 * Math.PI / 3.0));
                    double r = 0.9 + (0.2 * rng.NextDouble());
                    Translate(d, new Vec2(Math.Cos(angle) * r, Math.Sin(angle) * r));
                }

                expected = [.. deferred.Select(d => new ExpectedFinding(ErrorCode.D2, d.Id))];
                forbidden = [ErrorCode.D1, ErrorCode.D3];
                break;
            case SynthMutation.ExtraDeferred:
                {
                    if (deferred.Count == 0)
                    {
                        return null;
                    }

                    IdealStroke source = deferred[0];
                    IdealStroke copy = source.Clone();
                    double angle = (-Math.PI / 4.0) + (rng.NextDouble() * (Math.PI / 2.0));
                    double r = 0.8 + (0.2 * rng.NextDouble());
                    Translate(copy, new Vec2(Math.Cos(angle) * r, Math.Sin(angle) * r));
                    ShiftTime(copy, (source.EndMs - source.StartMs) + GapMs);
                    strokes.Add(copy);
                    expected = [new ExpectedFinding(ErrorCode.D3, null)];
                    forbidden = [ErrorCode.D1];
                    break;
                }

            case SynthMutation.ParallelRetrace:
                {
                    (IdealStroke Stroke, RetraceCheck Check)? target = FindRetrace(glyph, main);
                    if (target is null)
                    {
                        return null;
                    }

                    OffsetRange(glyph, target.Value.Stroke, target.Value.Check.Segment, target.Value.Check.StrokeId, 0.28);
                    expected = [new ExpectedFinding(ErrorCode.P6, target.Value.Stroke.Id)];
                    forbidden = [ErrorCode.P1, ErrorCode.P2, ErrorCode.P5];
                    break;
                }

            case SynthMutation.OpenOval:
                {
                    StrokeRange? closed = glyph.Checks.Closed.Length > 0 ? glyph.Checks.Closed[0] : null;
                    IdealStroke? stroke = closed is null ? null : main.FirstOrDefault(s => s.Id == closed.StrokeId);
                    if (closed is null || stroke is null)
                    {
                        return null;
                    }

                    OpenClosure(glyph, stroke, closed, rng, 0.32);
                    expected = [new ExpectedFinding(ErrorCode.F2, stroke.Id)];
                    forbidden = [ErrorCode.P1, ErrorCode.P2, ErrorCode.P5];
                    break;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown mutation.");
        }

        return new SynthSample(Render(strokes, variation, rng, noisy: true), mutation, expected, forbidden);
    }

    // ---------- Построение эталонной траектории ----------

    private List<IdealStroke> BuildIdeal(Glyph glyph)
    {
        AnimationScript script = AnimationScriptBuilder.Build(glyph, _style.Animation, new AnimationOptions(IncludeLeads: true));
        double origin = script.Strokes[0].StartMs;
        var result = new List<IdealStroke>(script.Strokes.Length);
        foreach (AnimatedStroke animated in script.Strokes)
        {
            var stroke = new IdealStroke(animated.StrokeId, animated.IsDeferred, animated.BodyStart, animated.BodyLength);
            double end = animated.EndMs;
            for (double ms = animated.StartMs; ; ms += SampleStepMs)
            {
                double time = Math.Min(ms, end);
                double distance = animated.DistanceAt(time);
                stroke.Points.Add(animated.Path.PointAtDistance(distance));
                stroke.TimesMs.Add(time - origin);
                stroke.Distances.Add(distance);
                if (time >= end)
                {
                    break;
                }
            }

            result.Add(stroke);
        }

        return result;
    }

    // ---------- Мутации ----------

    private static void MoveStart(Glyph glyph, IdealStroke stroke, int mainIndex, Pcg32 rng)
    {
        var allowed = new List<Vec2>();
        GlyphStroke source = glyph.Strokes.OrderBy(s => s.Order).ElementAt(mainIndex);
        allowed.Add(source.Path.Start);
        if (mainIndex == 0 && glyph.LeadIn is not null)
        {
            allowed.Add(glyph.LeadIn.Start);
        }

        Vec2 p0 = stroke.Points[0];
        double radius = 0.75 + (0.25 * rng.NextDouble()); // в единицах эталона: после нормализации масштаба смещение остаётся 0,75–1 x-height
        double angle0 = rng.NextDouble() * 2.0 * Math.PI;
        Vec2 start = p0;
        for (int attempt = 0; attempt < 16; attempt++)
        {
            double angle = angle0 + (attempt * Math.PI / 8.0);
            Vec2 candidate = p0 + new Vec2(Math.Cos(angle) * radius, Math.Sin(angle) * radius);
            if (allowed.All(a => Vec2.Distance(a, candidate) >= 0.6))
            {
                start = candidate;
                break;
            }
        }

        int count = Math.Max(2, (int)Math.Ceiling(radius / ApproachStep));
        double t0 = stroke.TimesMs[0];
        for (int j = 0; j < count; j++)
        {
            double share = (double)j / count;
            stroke.Points.Insert(j, Vec2.Lerp(start, p0, share));
            stroke.TimesMs.Insert(j, t0 - ((count - j) * ApproachStepMs));
            stroke.Distances.Insert(j, -1.0);
        }
    }

    private static void Reverse(IdealStroke stroke)
    {
        double first = stroke.TimesMs[0];
        double last = stroke.TimesMs[^1];
        stroke.Points.Reverse();
        stroke.TimesMs.Reverse();
        for (int i = 0; i < stroke.TimesMs.Count; i++)
        {
            stroke.TimesMs[i] = first + last - stroke.TimesMs[i];
        }

        stroke.Distances.Reverse();
        for (int i = 0; i < stroke.Distances.Count; i++)
        {
            stroke.Distances[i] = -1.0;
        }
    }

    private static void AddExtraStroke(List<IdealStroke> strokes, List<IdealStroke> main, Pcg32 rng)
    {
        double maxU = main.SelectMany(s => s.Points).Max(p => p.X);
        double minV = main.SelectMany(s => s.Points).Min(p => p.Y);
        double angle = (Math.PI / 3.0) + (rng.NextDouble() * (Math.PI / 3.0)); // 60..120 градусов: заведомо не перекладина
        double length = 1.0 + (0.4 * rng.NextDouble());
        Vec2 start = new(maxU + 0.5 + (0.2 * rng.NextDouble()), minV + 0.1);
        Vec2 direction = new(Math.Cos(angle), Math.Sin(angle));
        double end = strokes.Max(s => s.EndMs);
        var stroke = new IdealStroke("extra", false, 0.0, length);
        int count = (int)Math.Ceiling(length / ApproachStep);
        for (int j = 0; j <= count; j++)
        {
            stroke.Points.Add(start + (direction * (length * j / count)));
            stroke.TimesMs.Add(end + GapMs + (j * ApproachStepMs));
            stroke.Distances.Add(-1.0);
        }

        strokes.Add(stroke);
    }

    private static void SwapOrder(IdealStroke first, IdealStroke second)
    {
        double s0 = first.StartMs;
        double d1 = second.EndMs - second.StartMs;
        ShiftTime(second, s0 - second.StartMs);
        ShiftTime(first, s0 + d1 + GapMs - first.StartMs);
    }

    private static void PlaceDeferredFirst(List<IdealStroke> main, List<IdealStroke> deferred)
    {
        double start = main.Min(s => s.StartMs);
        double cursor = start - GapMs;
        for (int i = deferred.Count - 1; i >= 0; i--)
        {
            IdealStroke d = deferred[i];
            double duration = d.EndMs - d.StartMs;
            ShiftTime(d, cursor - duration - d.StartMs);
            cursor = d.StartMs - GapMs;
        }
    }

    private static void Translate(IdealStroke stroke, Vec2 delta)
    {
        for (int i = 0; i < stroke.Points.Count; i++)
        {
            stroke.Points[i] += delta;
        }
    }

    private static void ShiftTime(IdealStroke stroke, double deltaMs)
    {
        for (int i = 0; i < stroke.TimesMs.Count; i++)
        {
            stroke.TimesMs[i] += deltaMs;
        }
    }

    private static (IdealStroke, RetraceCheck)? FindRetrace(Glyph glyph, List<IdealStroke> main)
    {
        foreach (RetraceCheck check in glyph.Checks.Retrace)
        {
            IdealStroke? stroke = main.FirstOrDefault(s => s.Id == check.StrokeId);
            if (stroke is not null)
            {
                return (stroke, check);
            }
        }

        return null;
    }

    /// <summary>
    /// Смещает участок сегмента <paramref name="segment"/> тела перпендикулярно ходу, плавно (синус), на <paramref name="amount"/>
    /// вправо (наружу от буквы: слева лежат другие чернила, и ретрейс было бы трудно отличить от верного).
    /// </summary>
    private static void OffsetRange(Glyph glyph, IdealStroke stroke, int segment, string strokeId, double amount)
    {
        GlyphStroke source = glyph.Strokes.First(s => s.Id == strokeId);
        double from = segment == 0 ? 0.0 : source.Path.FractionAtSegmentEnd(segment - 1);
        double to = source.Path.FractionAtSegmentEnd(segment);
        OffsetByDistance(stroke, stroke.BodyStartDistance + (from * stroke.BodyLength), stroke.BodyStartDistance + (to * stroke.BodyLength), amount);
    }

    private static void OpenClosure(Glyph glyph, IdealStroke stroke, StrokeRange closed, Pcg32 rng, double amount)
    {
        _ = glyph;
        _ = rng;

        // Начало овала смещено, а возврат на прежнее место — нет: перо «не попадает» в точку, с которой началось.
        double centre = stroke.BodyStartDistance + (closed.From * stroke.BodyLength);
        double half = 0.5;
        OffsetByDistance(stroke, centre - half, centre + half, amount);
    }

    private static void OffsetByDistance(IdealStroke stroke, double fromDistance, double toDistance, double amount)
    {
        var shifted = new Vec2[stroke.Points.Count];
        for (int i = 0; i < stroke.Points.Count; i++)
        {
            shifted[i] = stroke.Points[i];
            double d = stroke.Distances[i];
            if (d < fromDistance || d > toDistance || toDistance <= fromDistance)
            {
                continue;
            }

            Vec2 direction = stroke.Points[Math.Min(i + 1, stroke.Points.Count - 1)] - stroke.Points[Math.Max(i - 1, 0)];
            Vec2 normal = direction.Normalized().Perpendicular();
            if (normal.X < 0.0)
            {
                normal = -normal;
            }

            double x = (d - fromDistance) / (toDistance - fromDistance);
            shifted[i] = stroke.Points[i] + (normal * (amount * Math.Sin(Math.PI * x)));
        }

        for (int i = 0; i < shifted.Length; i++)
        {
            stroke.Points[i] = shifted[i];
        }
    }

    private RulerInk Scribble(Pcg32 rng)
    {
        int strokeCount = 3 + rng.NextInt32(3);
        var strokes = new List<RulerStroke>();
        long time = 500_000;
        for (int s = 0; s < strokeCount; s++)
        {
            var points = ImmutableArray.CreateBuilder<RulerPoint>();
            var p = new Vec2(rng.NextDouble() * 2.0, (rng.NextDouble() * 2.5) - 0.5);
            double heading = rng.NextDouble() * 2.0 * Math.PI;
            for (int k = 0; k < 100; k++)
            {
                heading += (rng.NextDouble() - 0.5) * 0.9;
                p += new Vec2(Math.Cos(heading), Math.Sin(heading)) * 0.07;
                time += (long)(1_000_000.0 / _options.SampleRateHz);
                points.Add(new RulerPoint(p.X, p.Y, time, 0.5f));
            }

            time += 400_000;
            strokes.Add(new RulerStroke(s, points.ToImmutable(), false));
        }

        return new RulerInk([.. strokes], null, _options.MmPerUnit, false);
    }

    // ---------- Вариации «детской» руки ----------

    private RulerInk Render(List<IdealStroke> strokes, Variation v, Pcg32 rng, bool noisy)
    {
        List<IdealStroke> ordered = [.. strokes.OrderBy(s => s.StartMs)];
        double timeOrigin = ordered.Min(s => s.StartMs);
        var result = new List<RulerStroke>(ordered.Count);
        double tanSlant = Math.Tan(v.SlantRad);
        double cos = Math.Cos(v.RotationRad);
        double sin = Math.Sin(v.RotationRad);
        long previousTime = 0;
        for (int index = 0; index < ordered.Count; index++)
        {
            IdealStroke stroke = ordered[index];
            double ou = noisy ? Gaussian(rng) * _options.StrokeOffsetSigma : 0.0;
            double ov = noisy ? Gaussian(rng) * _options.StrokeOffsetSigma : 0.0;
            double a1 = noisy ? _options.WobbleAmplitude * rng.NextDouble() : 0.0;
            double l1 = 1.0 + (1.5 * rng.NextDouble());
            double p1 = rng.NextDouble() * 2.0 * Math.PI;
            double a2 = noisy ? 0.5 * _options.WobbleAmplitude * rng.NextDouble() : 0.0;
            double l2 = 0.4 + (0.5 * rng.NextDouble());
            double p2 = rng.NextDouble() * 2.0 * Math.PI;
            double pressurePhase = rng.NextDouble() * 2.0 * Math.PI;
            var points = ImmutableArray.CreateBuilder<RulerPoint>(stroke.Points.Count);
            double arc = 0.0;
            for (int k = 0; k < stroke.Points.Count; k++)
            {
                Vec2 p = stroke.Points[k];
                if (k > 0)
                {
                    arc += Vec2.Distance(stroke.Points[k - 1], p);
                }

                Vec2 tangent = (stroke.Points[Math.Min(k + 1, stroke.Points.Count - 1)] - stroke.Points[Math.Max(k - 1, 0)]).Normalized();
                Vec2 normal = tangent.Perpendicular();
                double wobble = (a1 * Math.Sin((2.0 * Math.PI * arc / l1) + p1)) + (a2 * Math.Sin((2.0 * Math.PI * arc / l2) + p2));
                p += normal * wobble;
                p = new Vec2(p.X + (p.Y * tanSlant), p.Y);
                p = new Vec2((p.X * cos) - (p.Y * sin), (p.X * sin) + (p.Y * cos));
                p = new Vec2((p.X * v.Scale) + v.OffsetU + ou, (p.Y * v.Scale) + v.OffsetV + ov);
                if (noisy)
                {
                    p += new Vec2(Gaussian(rng), Gaussian(rng)) * _options.TremorSigma;
                }

                double timeUs = (((stroke.TimesMs[k] - timeOrigin) / v.Speed) * 1000.0) + v.LatencyUs;
                if (noisy)
                {
                    timeUs += (rng.NextDouble() * 2.0 - 1.0) * _options.TimeJitterUs;
                }

                long t = Math.Max(previousTime, (long)Math.Round(timeUs));
                previousTime = t;
                float pressure = (float)Math.Clamp(0.5 + (0.3 * Math.Sin((k / 40.0) + pressurePhase)) + (noisy ? 0.03 * Gaussian(rng) : 0.0), 0.05, 1.0);
                points.Add(new RulerPoint(p.X, p.Y, t, pressure));
            }

            result.Add(new RulerStroke(index, points.ToImmutable(), false));
        }

        return new RulerInk([.. result], null, _options.MmPerUnit, false);
    }

    private static Pcg32 Rng(Glyph glyph, ulong seed)
    {
        ulong stream = 0;
        foreach (char c in glyph.Id.ToString())
        {
            stream = (stream * 131UL) + c;
        }

        return new Pcg32(seed, stream);
    }

    /// <summary>Нормальное распределение (Бокс–Мюллер) на основе <see cref="Pcg32"/>.</summary>
    private static double Gaussian(Pcg32 rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private readonly record struct Variation(double Scale, double SlantRad, double RotationRad, double OffsetU, double OffsetV, double Speed, double LatencyUs)
    {
        public static Variation Identity { get; } = new(1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0);

        public static Variation Sample(SynthOptions o, Pcg32 rng)
        {
            double Uniform(double lo, double hi) => lo + ((hi - lo) * rng.NextDouble());
            double deg = Math.PI / 180.0;
            return new Variation(
                Uniform(o.ScaleMin, o.ScaleMax),
                Uniform(-o.SlantRangeDeg, o.SlantRangeDeg) * deg,
                Uniform(-o.RotationRangeDeg, o.RotationRangeDeg) * deg,
                1.0 + Uniform(-o.OffsetU, o.OffsetU),
                Uniform(-o.BaselineDrift, o.BaselineDrift),
                Uniform(o.SpeedMin, o.SpeedMax),
                Uniform(500_000.0, 1_500_000.0));
        }
    }

    private sealed class IdealStroke
    {
        public IdealStroke(string id, bool isDeferred, double bodyStartDistance, double bodyLength)
        {
            Id = id;
            IsDeferred = isDeferred;
            BodyStartDistance = bodyStartDistance;
            BodyLength = bodyLength;
        }

        public string Id { get; }

        public bool IsDeferred { get; }

        public double BodyStartDistance { get; }

        public double BodyLength { get; }

        public List<Vec2> Points { get; } = [];

        public List<double> TimesMs { get; } = [];

        /// <summary>Расстояние по пути анимации для каждой точки; −1 — точка добавлена мутацией.</summary>
        public List<double> Distances { get; } = [];

        public double StartMs => TimesMs[0];

        public double EndMs => TimesMs[^1];

        public IdealStroke Clone()
        {
            var copy = new IdealStroke(Id + ".copy", IsDeferred, BodyStartDistance, BodyLength);
            copy.Points.AddRange(Points);
            copy.TimesMs.AddRange(TimesMs);
            copy.Distances.AddRange(Distances);
            return copy;
        }
    }
}
