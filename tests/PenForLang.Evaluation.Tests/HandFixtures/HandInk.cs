using System.Collections.Immutable;
using PenForLang.Ink;

namespace PenForLang.Evaluation.Tests.HandFixtures;

/// <summary>Точка или вектор в координатах линейки (u, v).</summary>
internal readonly record struct HandVec(double X, double Y)
{
    public double Length => Math.Sqrt((X * X) + (Y * Y));

    public static HandVec operator +(HandVec a, HandVec b) => new(a.X + b.X, a.Y + b.Y);

    public static HandVec operator -(HandVec a, HandVec b) => new(a.X - b.X, a.Y - b.Y);

    public static HandVec operator *(HandVec a, double k) => new(a.X * k, a.Y * k);
}

/// <summary>
/// Параметрические кривые для ручных траекторий: НЕ из глифов и НЕ из SynthInk (независимая модель, как у ревьюера V0.7).
/// Параметр кривой — <c>t ∈ [0, 1]</c>.
/// </summary>
internal static class HandCurves
{
    public static Func<double, HandVec> Line(HandVec a, HandVec b) => t => a + ((b - a) * t);

    public static Func<double, HandVec> Quad(HandVec a, HandVec control, HandVec b) =>
        t => (a * ((1 - t) * (1 - t))) + (control * (2 * (1 - t) * t)) + (b * (t * t));

    /// <summary>Склейка кривых; параметр делится пропорционально длинам частей.</summary>
    public static Func<double, HandVec> Concat(IReadOnlyList<Func<double, HandVec>> parts)
    {
        double[] length = [.. parts.Select(Length)];
        double total = length.Sum();
        return t =>
        {
            double s = Math.Clamp(t, 0, 1) * total;
            for (int i = 0; i < parts.Count; i++)
            {
                if (s <= length[i] || i == parts.Count - 1)
                {
                    return parts[i](length[i] > 0 ? Math.Clamp(s / length[i], 0, 1) : 0);
                }

                s -= length[i];
            }

            return parts[^1](1);
        };
    }

    public static double Length(Func<double, HandVec> curve)
    {
        double sum = 0;
        HandVec previous = curve(0);
        for (int i = 1; i <= 400; i++)
        {
            HandVec next = curve(i / 400.0);
            sum += (next - previous).Length;
            previous = next;
        }

        return sum;
    }
}

/// <summary>Штрих ручной траектории: кривая, скорость (x-height/с), дрожь, задержка начала относительно конца предыдущего.</summary>
internal sealed class HandStroke
{
    public HandStroke(Func<double, HandVec> curve, double speed, double tremor, int seed)
    {
        Curve = curve;
        Speed = speed;
        Tremor = tremor;
        Seed = seed;
    }

    public Func<double, HandVec> Curve { get; }

    public double Speed { get; }

    public double Tremor { get; }

    public int Seed { get; }

    public double DelayMs { get; private init; }

    public HandStroke After(double ms) => new(Curve, Speed, Tremor, Seed) { DelayMs = ms };

    public HandStroke Reverse()
    {
        Func<double, HandVec> c = Curve;
        return new HandStroke(t => c(1 - t), Speed, Tremor, Seed) { DelayMs = DelayMs };
    }

    public HandStroke Cut(double from, double to, double delayMs)
    {
        Func<double, HandVec> c = Curve;
        return new HandStroke(t => c(from + ((to - from) * t)), Speed, Tremor, Seed) { DelayMs = delayMs };
    }

    /// <summary>Наклон (сдвиг вокруг baseline), масштаб, поворот строки, сдвиг по u и v.</summary>
    public HandStroke Transform(double slantDeg, double scale, double du, double dv, double rotationDeg)
    {
        Func<double, HandVec> c = Curve;
        double k = Math.Tan(slantDeg * Math.PI / 180);
        double r = rotationDeg * Math.PI / 180;
        return new HandStroke(
            t =>
            {
                HandVec p = c(t);
                p = new HandVec(p.X + (p.Y * k), p.Y) * scale;
                p = new HandVec((p.X * Math.Cos(r)) - (p.Y * Math.Sin(r)), (p.X * Math.Sin(r)) + (p.Y * Math.Cos(r)));
                return new HandVec(p.X + du, p.Y + dv);
            },
            Speed,
            Tremor,
            Seed)
        {
            DelayMs = DelayMs,
        };
    }
}

/// <summary>Собирает <see cref="RulerInk"/> из ручных штрихов: 200 Гц, колоколообразная скорость, дрожь (медленная волна + шум).</summary>
internal static class HandInkBuilder
{
    public static RulerInk Build(params HandStroke[] strokes)
    {
        var list = ImmutableArray.CreateBuilder<RulerStroke>();
        long time = 1_000_000;
        int index = 0;
        foreach (HandStroke stroke in strokes)
        {
            time += (long)(stroke.DelayMs * 1000);
            double length = HandCurves.Length(stroke.Curve);
            double durationSeconds = Math.Max(length / stroke.Speed, 0.01);
            int n = Math.Max(2, (int)Math.Ceiling(durationSeconds * 200));
            var rng = new Random(stroke.Seed);
            var points = ImmutableArray.CreateBuilder<RulerPoint>(n + 1);
            for (int i = 0; i <= n; i++)
            {
                double tau = (double)i / n;
                double u = tau - (Math.Sin(2 * Math.PI * tau) / (2 * Math.PI));
                HandVec p = stroke.Curve(u);
                if (stroke.Tremor > 0)
                {
                    p += new HandVec(
                        (stroke.Tremor * Math.Sin((37 * tau) + stroke.Seed) * 0.6) + (Gauss(rng) * stroke.Tremor * 0.3),
                        (stroke.Tremor * Math.Cos(29 * tau) * 0.6) + (Gauss(rng) * stroke.Tremor * 0.3));
                }

                points.Add(new RulerPoint(p.X, p.Y, time + (i * 5000L), 0.5f));
            }

            time += n * 5000L;
            list.Add(new RulerStroke(index++, points.ToImmutable(), false));
        }

        return new RulerInk(list.ToImmutable(), null, 10.0, false);
    }

    private static double Gauss(Random random) =>
        Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
}
