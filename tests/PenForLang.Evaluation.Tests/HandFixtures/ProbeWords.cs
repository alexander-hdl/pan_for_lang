using System.Collections.Immutable;
using PenForLang.Ink;

namespace PenForLang.Evaluation.Tests.HandFixtures;

/// <summary>
/// Независимые фикстуры слов <c>at</c>/<c>it</c> (портированы из пробы ревьюера V0.10): свои модели букв a, i, t, o, l,
/// русской «т» (крюк-выход, подъём и подход заданы формулами, не глифами пакета и не SynthInk), свои формы соединения
/// (низкое, высокое, с петлёй), свои «неверные слова». Нужны, чтобы оценщик слова проверялся не на собственной модели (A08).
/// </summary>
internal static class ProbeWords
{
    internal delegate PVec PCurve(double t);

    internal readonly record struct PVec(double X, double Y)
    {
        public static PVec operator +(PVec a, PVec b) => new(a.X + b.X, a.Y + b.Y);

        public static PVec operator -(PVec a, PVec b) => new(a.X - b.X, a.Y - b.Y);

        public static PVec operator *(PVec a, double k) => new(a.X * k, a.Y * k);

        public double Len => Math.Sqrt((X * X) + (Y * Y));

        public PVec Unit => this * (1.0 / Len);
    }

    /// <summary>Рука: наклон, масштаб, сдвиги, поворот строки, дрожь и скорость.</summary>
    internal readonly record struct PHand(double Slant = 0, double Scale = 1, double Du = 1.0, double Dv = 0, double Rot = 0, double Tremor = 0.006, double Speed = 1.5)
    {
        public PVec Map(PVec p)
        {
            double k = Math.Tan(Slant * Math.PI / 180);
            double r = Rot * Math.PI / 180;
            p = new PVec(p.X + (p.Y * k), p.Y) * Scale;
            p = new PVec((p.X * Math.Cos(r)) - (p.Y * Math.Sin(r)), (p.X * Math.Sin(r)) + (p.Y * Math.Cos(r)));
            return new PVec(p.X + Du, p.Y + Dv);
        }
    }

    /// <summary>Истинные точки выхода тела левой буквы и входа тела правой (до руки).</summary>
    internal readonly record struct PTruth(PVec Exit, PVec Entry)
    {
        public PTruth Map(PHand h) => new(h.Map(Exit), h.Map(Entry));
    }

    /// <summary>Случай: цель оценки, попытка и (если известна) истина границ.</summary>
    internal sealed record PCase(string Id, string Target, RulerInk Ink, PTruth? Truth);

    internal static class G
    {
        public static PCurve Line(PVec a, PVec b) => t => a + ((b - a) * t);

        public static PCurve Quad(PVec a, PVec c, PVec b) => t => (a * ((1 - t) * (1 - t))) + (c * (2 * (1 - t) * t)) + (b * (t * t));

        public static PCurve Cubic(PVec a, PVec b, PVec c, PVec d) =>
            t => (a * Math.Pow(1 - t, 3)) + (b * (3 * Math.Pow(1 - t, 2) * t)) + (c * (3 * (1 - t) * t * t)) + (d * Math.Pow(t, 3));

        public static PCurve Concat(IReadOnlyList<PCurve> parts)
        {
            double[] len = [.. parts.Select(Length)];
            double total = len.Sum();
            return t =>
            {
                double s = Math.Clamp(t, 0, 1) * total;
                for (int i = 0; i < parts.Count; i++)
                {
                    if (s <= len[i] || i == parts.Count - 1)
                    {
                        return parts[i](len[i] > 0 ? Math.Clamp(s / len[i], 0, 1) : 0);
                    }

                    s -= len[i];
                }

                return parts[^1](1);
            };
        }

        public static double Length(PCurve f)
        {
            double l = 0;
            PVec p = f(0);
            for (int i = 1; i <= 400; i++)
            {
                PVec q = f(i / 400.0);
                l += (q - p).Len;
                p = q;
            }

            return l;
        }
    }

    /// <summary>Штрих руки: кривая, скорость, дрожь, задержка начала.</summary>
    internal sealed class PStroke(PCurve curve, double speed, double tremor, int seed)
    {
        public PCurve Curve { get; } = curve;

        public double Speed { get; } = speed;

        public double Tremor { get; } = tremor;

        public int Seed { get; } = seed;

        public double DelayMs { get; init; }

        public PStroke Cut(double a, double b, double delay)
        {
            PCurve c = Curve;
            return new PStroke(t => c(a + ((b - a) * t)), Speed, Tremor, Seed + (int)(a * 1000)) { DelayMs = delay };
        }

        /// <summary>Смещение начала штроха, затухающее к доле <paramref name="fade"/>: рука ставит перо рядом и сходится к линии.</summary>
        public PStroke Offset(PVec off, double fade)
        {
            PCurve c = Curve;
            return new PStroke(t => c(t) + (off * Math.Max(0, 1 - (t / fade))), Speed, Tremor, Seed) { DelayMs = DelayMs };
        }

        public PStroke Transform(PHand h)
        {
            PCurve c = Curve;
            return new PStroke(t => h.Map(c(t)), Speed, Tremor, Seed) { DelayMs = DelayMs };
        }
    }

    internal static RulerInk Build(params PStroke[] strokes)
    {
        ImmutableArray<RulerStroke>.Builder list = ImmutableArray.CreateBuilder<RulerStroke>();
        long time = 1_000_000;
        int index = 0;
        foreach (PStroke s in strokes)
        {
            time += (long)(s.DelayMs * 1000);
            double length = G.Length(s.Curve);
            double durationS = Math.Max(length / s.Speed, 0.01);
            int n = Math.Max(2, (int)Math.Ceiling(durationS * 200));
            var rng = new Random(s.Seed);
            ImmutableArray<RulerPoint>.Builder pts = ImmutableArray.CreateBuilder<RulerPoint>(n + 1);
            for (int i = 0; i <= n; i++)
            {
                double tau = (double)i / n;

                // Скорость: замедление у концов, но не до нуля (30% → 100%).
                double u = (0.7 * tau) + (0.3 * (tau - (Math.Sin(2 * Math.PI * tau) / (2 * Math.PI))));
                PVec p = s.Curve(u);
                if (s.Tremor > 0)
                {
                    p += new PVec(
                        (s.Tremor * Math.Sin((37 * tau * Math.Max(1, length)) + s.Seed) * 0.6) + (Gauss(rng) * s.Tremor * 0.3),
                        (s.Tremor * Math.Cos(29 * tau * Math.Max(1, length)) * 0.6) + (Gauss(rng) * s.Tremor * 0.3));
                }

                pts.Add(new RulerPoint(p.X, p.Y, time + (long)(i * 5000), 0.5f));
            }

            time += (long)(n * 5000);
            list.Add(new RulerStroke(index++, pts.ToImmutable(), false));
        }

        return new RulerInk(list.ToImmutable(), null, 10.0, false);
    }

    private static double Gauss(Random r) => Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());

    // ============================ модели букв ============================

    public static readonly PHand H0 = new(Slant: 0);

    private static List<PCurve> APart(out PVec exit, double cx = 0.45, double rx = 0.40, double ry = 0.49, bool retrace = true, double hookUp = 0.14)
    {
        var parts = new List<PCurve>();
        double a0 = 35 * Math.PI / 180;
        double a1 = 368 * Math.PI / 180;
        PVec Oval(double ang) => new(cx + (rx * Math.Cos(ang)), 0.5 + (ry * Math.Sin(ang)));
        parts.Add(G.Quad(new PVec(-0.45, 0.02), new PVec(0.3, 0.0), Oval(a0)));
        parts.Add(t => Oval(a0 + ((a1 - a0) * t)));
        PVec top = new(cx + rx + 0.04, 1.0);
        PVec bottom = new(cx + rx - 0.01, 0.15);
        if (retrace)
        {
            parts.Add(G.Line(Oval(a1), top));
            parts.Add(G.Line(top, bottom));
        }
        else
        {
            // Петля вместо ретрейса: вверх левее, вниз правее.
            parts.Add(G.Quad(Oval(a1), new PVec(cx + rx - 0.12, 0.9), top));
            parts.Add(G.Quad(top, new PVec(cx + rx + 0.14, 0.6), bottom));
        }

        exit = new PVec(cx + rx + 0.25, hookUp);
        parts.Add(G.Cubic(bottom, bottom + new PVec(-0.01, -0.12), new PVec(cx + rx + 0.12, -0.04), exit));
        return parts;
    }

    private static List<PCurve> IPart(out PVec exit, out PVec top)
    {
        top = new PVec(0.22, 1.0);
        PVec bottom = new(0.2, 0.17);
        exit = new PVec(0.5, 0.14);
        return
        [
            G.Quad(new PVec(-0.45, 0.02), new PVec(0.02, 0.06), top),
            G.Line(top, bottom),
            G.Cubic(bottom, new PVec(0.2, 0.0), new PVec(0.33, -0.03), exit),
        ];
    }

    private static List<PCurve> TPart(double tl, out PVec top, double h = 1.45, double lean = 0.05)
    {
        top = new PVec(tl + lean, h);
        PVec bottom = new(tl, 0.17);
        return
        [
            G.Line(top, bottom),
            G.Cubic(bottom, new PVec(tl - 0.01, 0.0), new PVec(tl + 0.15, -0.03), new PVec(tl + 0.5, 0.3)),
        ];
    }

    // Соединения от выхода E к верху t (T), подход к T вдоль ствола.
    private static PCurve JoinLow(PVec e, PVec t, double lean = 0.05)
    {
        PVec up = new PVec(lean, 1.28).Unit;
        return G.Cubic(e, e + new PVec(0.32, 0.10), t - (up * 0.60), t);
    }

    private static PCurve JoinHigh(PVec e, PVec t, double lean = 0.05)
    {
        PVec up = new PVec(lean, 1.28).Unit;
        return G.Cubic(e, e + new PVec(0.08, 0.55), t - (up * 0.30), t);
    }

    private static PCurve JoinLoop(PVec e, PVec t, double lean = 0.05)
    {
        PVec up = new PVec(lean, 1.28).Unit;
        PVec m = ((e + t) * 0.5) + new PVec(0.02, -0.15);
        return G.Concat(
        [
            G.Cubic(e, e + new PVec(0.2, 0.1), m - new PVec(0.15, 0.05), m),
            u => m + new PVec(0.07 * Math.Sin(2 * Math.PI * u), 0.07 * (1 - Math.Cos(2 * Math.PI * u))),
            G.Cubic(m, m + new PVec(0.15, 0.05), t - (up * 0.4), t),
        ]);
    }

    private static PCurve JoinTopLoop(PVec e, PVec t) => G.Cubic(e, e + new PVec(0.1, 0.45), t + new PVec(-0.45, -0.05), t);

    /// <summary>Слово: общая кривая, доли конца тела левой буквы и конца соединения, истина границ.</summary>
    internal sealed class PWord
    {
        public required PCurve Curve { get; init; }

        public required PVec Exit { get; init; }

        public required PVec Top { get; init; }

        public double TLeft { get; init; }

        public double BodyEnd { get; init; }

        public double JoinEnd { get; init; }

        public PVec ITop { get; set; }

        public PTruth Truth => new(Exit, Top);

        public static PWord Of(List<PCurve> left, PCurve join, List<PCurve> right, PVec exit, PVec top, double tl)
        {
            double l = left.Sum(G.Length);
            double j = G.Length(join);
            double r = right.Sum(G.Length);
            var all = new List<PCurve>(left) { join };
            all.AddRange(right);
            return new PWord { Curve = G.Concat(all), Exit = exit, Top = top, TLeft = tl, BodyEnd = l / (l + j + r), JoinEnd = (l + j) / (l + j + r) };
        }
    }

    public static PWord MakeAt(Func<PVec, PVec, PCurve> join, double gap = 0.30, double tH = 1.45, double cx = 0.45, double rx = 0.40, bool aRetrace = true, double lean = 0.05, double hookUp = 0.14)
    {
        List<PCurve> a = APart(out PVec exit, cx, rx, retrace: aRetrace, hookUp: hookUp);
        double tl = exit.X + gap;
        List<PCurve> t = TPart(tl, out PVec top, tH, lean);
        return PWord.Of(a, join(exit, top), t, exit, top, tl);
    }

    public static PWord MakeIt(Func<PVec, PVec, PCurve> join, double gap = 0.30)
    {
        List<PCurve> i = IPart(out PVec exit, out PVec itop);
        double tl = exit.X + gap;
        List<PCurve> t = TPart(tl, out PVec top);
        PWord w = PWord.Of(i, join(exit, top), t, exit, top, tl);
        w.ITop = itop;
        return w;
    }

    public static PCurve Low(PVec e, PVec t) => JoinLow(e, t);

    public static PCurve High(PVec e, PVec t) => JoinHigh(e, t);

    public static PStroke S(PCurve curve, PHand h, int seed = 1) => new PStroke(curve, h.Speed, h.Tremor, seed).Transform(h);

    public static PStroke Bar(PHand h, double stemU, double delay = 400, double y = 1.0, bool rtl = false)
    {
        PVec a = new(stemU - 0.28, y);
        PVec b = new(stemU + 0.28, y);
        return new PStroke(rtl ? G.Line(b, a) : G.Line(a, b), 1.5, 0, 31) { DelayMs = delay }.Transform(h with { Tremor = 0 });
    }

    public static PStroke Dot(PHand h, PVec iTop, double delay = 350) =>
        new PStroke(G.Line(iTop + new PVec(0.06, 0.42), iTop + new PVec(0.075, 0.43)), 0.6, 0, 32) { DelayMs = delay }.Transform(h with { Tremor = 0 });

    public static double StemAt(PWord w, double y) => w.TLeft + ((w.Top.X - w.TLeft) * (y - 0.17) / (w.Top.Y - 0.17));

    // ============================ A: at слитно, разные почерки ============================

    public static IEnumerable<PCase> Correct()
    {
        var hands = new (string Id, PHand H)[]
        {
            ("0", H0),
            ("s25", H0 with { Slant = 25 }),
            ("s-10", H0 with { Slant = -10 }),
            ("big", H0 with { Scale = 1.6 }),
            ("small", H0 with { Scale = 0.6 }),
            ("up", H0 with { Dv = 0.5 }),
            ("rot", H0 with { Rot = 5 }),
            ("mix", H0 with { Slant = 15, Scale = 1.2, Dv = 0.2 }),
        };
        foreach ((string hid, PHand h) in hands)
        {
            foreach ((string jid, Func<PVec, PVec, PCurve> join) in new (string, Func<PVec, PVec, PCurve>)[] { ("low", JoinLow0), ("high", JoinHigh0) })
            {
                PWord w = MakeAt(join);
                yield return new PCase($"A-{jid}-{hid}", "at", Build(S(w.Curve, h), Bar(h, StemAt(w, 1.0))), w.Truth.Map(h));
            }
        }

        PWord narrow = MakeAt(JoinHigh0, gap: 0.05);
        yield return new PCase("A-narrow", "at", Build(S(narrow.Curve, H0), Bar(H0, StemAt(narrow, 1.0))), narrow.Truth.Map(H0));
        PWord wideA = MakeAt(JoinLow0, rx: 0.55);
        yield return new PCase("A-wideA", "at", Build(S(wideA.Curve, H0), Bar(H0, StemAt(wideA, 1.0))), wideA.Truth.Map(H0));
        PWord slantT = MakeAt((e, t) => JoinLow(e, t, 0.45), lean: 0.45);
        yield return new PCase("A-slantT", "at", Build(S(slantT.Curve, H0), Bar(H0, StemAt(slantT, 1.0))), slantT.Truth.Map(H0));

        PWord it = MakeIt(JoinLow0);
        foreach ((string hid, PHand h) in new (string, PHand)[] { ("0", H0), ("s20", H0 with { Slant = 20, Scale = 1.3, Dv = 0.15 }), ("small", H0 with { Slant = 8, Scale = 0.7 }) })
        {
            yield return new PCase($"D-conn-{hid}", "it", Build(S(it.Curve, h, 2), Dot(h, it.ITop), Bar(h, StemAt(it, 1.0))), it.Truth.Map(h));
        }
    }

    private static PCurve JoinLow0(PVec e, PVec t) => JoinLow(e, t);

    private static PCurve JoinHigh0(PVec e, PVec t) => JoinHigh(e, t);

    // ============================ W-03: другой интервал и высота t (F8/F4, но не J3 и не process=false) ============================

    public static IEnumerable<PCase> GapAndHeight()
    {
        foreach (double g in new[] { 0.45, 0.6, 0.7, 0.8 })
        {
            PWord w = MakeAt(JoinLow0, gap: g);
            yield return new PCase($"H-gap{g:0.00}", "at", Build(S(w.Curve, H0), Bar(H0, StemAt(w, 1.0))), w.Truth.Map(H0));
        }

        foreach (double th in new[] { 1.35, 1.25, 1.15, 1.05 })
        {
            PWord w = MakeAt(JoinLow0, tH: th);
            yield return new PCase($"H-t{th:0.00}", "at", Build(S(w.Curve, H0), Bar(H0, StemAt(w, Math.Min(1.0, th - 0.25)))), w.Truth.Map(H0));
        }
    }

    // ============================ отрыв / J4 / ретрейс ============================

    public static PCase LoopJoin()
    {
        PWord w = MakeAt((e, t) => JoinLoop(e, t));
        return new PCase("A-loopjoin", "at", Build(S(w.Curve, H0), Bar(H0, StemAt(w, 1.0))), w.Truth.Map(H0));
    }

    public static PCase TopLoop()
    {
        PWord w = MakeAt(JoinTopLoop);
        return new PCase("R-toploop", "at", Build(S(w.Curve, H0), Bar(H0, StemAt(w, 1.0))), w.Truth.Map(H0));
    }

    public static PCase ALoop()
    {
        PWord w = MakeAt(JoinLow0, aRetrace: false);
        return new PCase("R-aloop", "at", Build(S(w.Curve, H0), Bar(H0, StemAt(w, 1.0))), w.Truth.Map(H0));
    }

    /// <summary>Ствол t вниз параллельно соединению: соединение к T, короткий переход вправо, ствол вниз от T+0.25.</summary>
    public static PCase ParallelStem()
    {
        List<PCurve> a = APart(out PVec exit);
        double tl = exit.X + 0.30;
        TPart(tl, out PVec top);
        List<PCurve> t2 = TPart(tl + 0.25, out PVec top2);
        var parts = new List<PCurve>(a) { JoinLow(exit, top), G.Line(top, top2) };
        parts.AddRange(t2);
        return new PCase("R-parallel", "at", Build(S(G.Concat(parts), H0), Bar(H0, tl + 0.25 + 0.03)), new PTruth(exit, top).Map(H0));
    }

    /// <summary>Короткая t (1.15): низкий ствол, ретрейс по соединению короткий (мало точек: «не измерено», а не 0).</summary>
    public static PCase ShortT()
    {
        PWord w = MakeAt(JoinLow0, tH: 1.15);
        return new PCase("A-shortT", "at", Build(S(w.Curve, H0), Bar(H0, StemAt(w, 0.9))), w.Truth.Map(H0));
    }

    public static IEnumerable<PCase> Lifts()
    {
        PWord w = MakeAt(JoinLow0);
        double be = w.BodyEnd;
        double je = w.JoinEnd;
        double bar = StemAt(w, 1.0);
        PStroke Whole(PHand h) => S(w.Curve, h);
        foreach ((string hid, PHand h) in new[] { ("0", H0), ("s20", H0 with { Slant = 20, Scale = 1.2 }) })
        {
            yield return new PCase($"L-aend-{hid}", "at", Build(Whole(h).Cut(0, be, 0), Whole(h).Cut(be, 1, 300), Bar(h, bar)), w.Truth.Map(h));
            yield return new PCase($"L-join20-{hid}", "at", Build(Whole(h).Cut(0, be + (0.2 * (je - be)), 0), Whole(h).Cut(be + (0.2 * (je - be)), 1, 300), Bar(h, bar)), w.Truth.Map(h));
            yield return new PCase($"L-mid-{hid}", "at", Build(Whole(h).Cut(0, (be + je) / 2, 0), Whole(h).Cut((be + je) / 2, 1, 300), Bar(h, bar)), w.Truth.Map(h));
            yield return new PCase($"L-ttop-{hid}", "at", Build(Whole(h).Cut(0, be + (0.9 * (je - be)), 0), Whole(h).Cut(be + (0.9 * (je - be)), 1, 300), Bar(h, bar)), w.Truth.Map(h));
            yield return new PCase($"L-attop-{hid}", "at", Build(Whole(h).Cut(0, je, 0), Whole(h).Cut(je, 1, 300), Bar(h, bar)), w.Truth.Map(h));
            yield return new PCase($"L-skip-{hid}", "at", Build(Whole(h).Cut(0, be, 0), Whole(h).Cut(je, 1, 300), Bar(h, bar)), w.Truth.Map(h));
        }

        yield return new PCase("L-offset", "at", Build(Whole(H0).Cut(0, (be + je) / 2, 0), Whole(H0).Cut((be + je) / 2, 1, 300).Offset(new PVec(0.12, 0.0), 0.2), Bar(H0, bar)), w.Truth.Map(H0));
    }

    /// <summary>Отрывы внутри букв и двойные отрывы.</summary>
    public static IEnumerable<PCase> InsideLifts()
    {
        PWord w = MakeAt(JoinLow0);
        double be = w.BodyEnd;
        double je = w.JoinEnd;
        double bar = StemAt(w, 1.0);
        PStroke Whole() => S(w.Curve, H0);
        double inA = be * 0.45;
        yield return new PCase("L-inA", "at", Build(Whole().Cut(0, inA, 0), Whole().Cut(inA, 1, 300), Bar(H0, bar)), w.Truth.Map(H0));
        double inAstem = be * 0.80;
        yield return new PCase("L-inAstem", "at", Build(Whole().Cut(0, inAstem, 0), Whole().Cut(inAstem, 1, 300), Bar(H0, bar)), w.Truth.Map(H0));
        double inT = je + (0.4 * (1 - je));
        yield return new PCase("L-inT", "at", Build(Whole().Cut(0, inT, 0), Whole().Cut(inT, 1, 300), Bar(H0, bar)), w.Truth.Map(H0));
        yield return new PCase("L-two-a-join", "at", Build(Whole().Cut(0, inA, 0), Whole().Cut(inA, (be + je) / 2, 300), Whole().Cut((be + je) / 2, 1, 300), Bar(H0, bar)), w.Truth.Map(H0));
        yield return new PCase(
            "L-two-join",
            "at",
            Build(Whole().Cut(0, be + (0.3 * (je - be)), 0), Whole().Cut(be + (0.3 * (je - be)), be + (0.7 * (je - be)), 300), Whole().Cut(be + (0.7 * (je - be)), 1, 300), Bar(H0, bar)),
            w.Truth.Map(H0));
        yield return new PCase("L-join-inT", "at", Build(Whole().Cut(0, (be + je) / 2, 0), Whole().Cut((be + je) / 2, inT, 300), Whole().Cut(inT, 1, 300), Bar(H0, bar)), w.Truth.Map(H0));
    }

    /// <summary>W-06: отрыв в стволе t ниже верха на <paramref name="below"/> x-height (0.2 — ещё у верха, 0.5/0.7 — внутри буквы).</summary>
    public static PCase TStemLift(double below)
    {
        PWord w = MakeAt(JoinLow0);
        double total = G.Length(w.Curve);
        double f = w.JoinEnd + (below / total);
        return new PCase($"L2-tstem-{below:0.00}", "at", Build(S(w.Curve, H0).Cut(0, f, 0), S(w.Curve, H0).Cut(f, 1, 300), Bar(H0, StemAt(w, 1.0))), w.Truth.Map(H0));
    }

    // ============================ it: точка, перекладина ============================

    public static IEnumerable<PCase> Dots()
    {
        PWord w = MakeIt(JoinLow0);
        double be = w.BodyEnd;
        double je = w.JoinEnd;
        double bar = StemAt(w, 1.0);
        foreach ((string hid, PHand h) in new[] { ("0", H0), ("s20", H0 with { Slant = 20, Scale = 1.3, Dv = 0.15 }), ("small", H0 with { Slant = 8, Scale = 0.7 }) })
        {
            PStroke Whole() => S(w.Curve, h, 2);
            yield return new PCase($"D-dot-end-{hid}", "it", Build(Whole().Cut(0, be, 0), Dot(h, w.ITop, 250), Whole().Cut(be, 1, 250), Bar(h, bar)), w.Truth.Map(h));
            yield return new PCase($"D-dot-near-{hid}", "it", Build(Whole().Cut(0, be, 0), Dot(h, w.ITop, 250), Whole().Cut(be, 1, 250).Offset(new PVec(0.08, 0.06), 0.15), Bar(h, bar)), w.Truth.Map(h));
            yield return new PCase($"D-dot-gap-{hid}", "it", Build(Whole().Cut(0, be, 0), Dot(h, w.ITop, 250), Whole().Cut(be + (0.3 * (je - be)), 1, 250), Bar(h, bar)), w.Truth.Map(h));
            yield return new PCase($"D-dot-skip-{hid}", "it", Build(Whole().Cut(0, be, 0), Dot(h, w.ITop, 250), Whole().Cut(je, 1, 250), Bar(h, bar)), w.Truth.Map(h));
        }

        PStroke W0() => S(w.Curve, H0, 2);
        double stemBottom = be * 0.78;
        yield return new PCase("D-dot-beforehook", "it", Build(W0().Cut(0, stemBottom, 0), Dot(H0, w.ITop, 250), W0().Cut(stemBottom, 1, 250), Bar(H0, bar)), w.Truth.Map(H0));
        yield return new PCase("D-dot-plus-lift", "it", Build(W0().Cut(0, be, 0), Dot(H0, w.ITop, 250), W0().Cut(be, be + (0.85 * (je - be)), 250), W0().Cut(be + (0.85 * (je - be)), 1, 250), Bar(H0, bar)), w.Truth.Map(H0));
        yield return new PCase("D-nodot-lift", "it", Build(W0().Cut(0, be, 0), W0().Cut(be, 1, 250), Bar(H0, bar)), w.Truth.Map(H0));
        yield return new PCase("D-dot-afterword-lift", "it", Build(W0().Cut(0, be, 0), W0().Cut(be, 1, 250), Dot(H0, w.ITop), Bar(H0, bar)), w.Truth.Map(H0));

        // it: перекладина t сразу, до хвоста t (отрыв в t).
        yield return new PCase("D-bar-immediate-it", "it", Build(W0().Cut(0, je + (0.8 * (1 - je)), 0), Bar(H0, bar, 250), W0().Cut(je + (0.8 * (1 - je)), 1, 250), Dot(H0, w.ITop)), w.Truth.Map(H0));
    }

    /// <summary>at: перекладина до хвоста t (D4 Info, J1 нет) и прочие положения перекладины.</summary>
    public static IEnumerable<PCase> Bars()
    {
        PWord at = MakeAt(JoinLow0);
        double abar = StemAt(at, 1.0);
        PStroke A0() => S(at.Curve, H0);
        double tStemEnd = at.JoinEnd + (0.62 * (1 - at.JoinEnd));
        yield return new PCase("C-bar-beforetail", "at", Build(A0().Cut(0, tStemEnd, 0), Bar(H0, abar, 250), A0().Cut(tStemEnd, 1, 250)), at.Truth.Map(H0));
        yield return new PCase("C-bar-end", "at", Build(A0(), Bar(H0, abar, 150)), at.Truth.Map(H0));
        yield return new PCase("C-bar-attop", "at", Build(A0().Cut(0, at.JoinEnd, 0), Bar(H0, abar, 250), A0().Cut(at.JoinEnd, 1, 250)), at.Truth.Map(H0));
        yield return new PCase("C-bar-rtl", "at", Build(A0(), Bar(H0, abar, 300, rtl: true)), at.Truth.Map(H0));
        yield return new PCase("C-nobar", "at", Build(A0()), at.Truth.Map(H0));
    }

    // ============================ E: концы слова ============================

    public static IEnumerable<PCase> Ends()
    {
        PWord w = MakeAt(JoinLow0);
        double total = G.Length(w.Curve);
        List<PCurve> aParts = APart(out _);
        double leadLen = G.Length(aParts[0]);
        List<PCurve> tParts = TPart(w.TLeft, out _);
        double tailLen = G.Length(tParts[^1]);
        double bar = StemAt(w, 1.0);
        yield return new PCase("E-noleadin", "at", Build(S(w.Curve, H0).Cut(leadLen / total, 1, 0), Bar(H0, bar)), w.Truth.Map(H0));
        yield return new PCase("E-notail", "at", Build(S(w.Curve, H0).Cut(0, 1 - (tailLen / total), 0), Bar(H0, bar)), w.Truth.Map(H0));
        yield return new PCase("E-both", "at", Build(S(w.Curve, H0).Cut(leadLen / total, 1 - (tailLen / total), 0), Bar(H0, bar)), w.Truth.Map(H0));
    }

    // ============================ W: не те буквы ============================

    private static List<PCurve> OPart(out PVec exit)
    {
        PVec Oval(double ang) => new(0.45 + (0.42 * Math.Cos(ang)), 0.5 + (0.48 * Math.Sin(ang)));
        double a0 = 80 * Math.PI / 180;
        double a1 = 440 * Math.PI / 180;
        exit = new PVec(0.95, 0.85);
        return [G.Quad(new PVec(-0.45, 0.02), new PVec(0.2, 0.2), Oval(a0)), t => Oval(a0 + ((a1 - a0) * t)), G.Quad(Oval(a1), new PVec(0.7, 1.02), exit)];
    }

    private static List<PCurve> LPart(double l0, PVec from, out PVec exit)
    {
        PVec top = new(l0 + 0.15, 2.0);
        PVec bottom = new(l0, 0.17);
        exit = new PVec(l0 + 0.45, 0.25);
        return
        [
            G.Cubic(from, from + new PVec(0.35, 0.5), top + new PVec(0.25, -0.5), top),
            G.Cubic(top, top + new PVec(-0.25, 0.0), bottom + new PVec(0.0, 0.6), bottom),
            G.Cubic(bottom, new PVec(l0, 0.0), new PVec(l0 + 0.15, -0.03), exit),
        ];
    }

    private static List<PCurve> RuTePart(double l0, PVec from)
    {
        var parts = new List<PCurve> { G.Quad(from, new PVec(l0 - 0.05, 0.6), new PVec(l0, 0.95)), G.Line(new PVec(l0, 0.95), new PVec(l0, 0.0)) };
        double x = l0;
        for (int k = 0; k < 2; k++)
        {
            parts.Add(G.Cubic(new PVec(x, 0.0), new PVec(x, 0.7), new PVec(x + 0.18, 1.0), new PVec(x + 0.22, 1.0)));
            parts.Add(G.Cubic(new PVec(x + 0.22, 1.0), new PVec(x + 0.3, 1.0), new PVec(x + 0.38, 0.7), new PVec(x + 0.38, 0.0)));
            x += 0.38;
        }

        parts.Add(G.Quad(new PVec(x, 0.0), new PVec(x + 0.05, -0.05), new PVec(x + 0.3, 0.2)));
        return parts;
    }

    /// <summary>Неверные слова при цели <c>at</c> (и <c>it</c> для «at вместо it»).</summary>
    public static IEnumerable<PCase> Wrong()
    {
        {
            List<PCurve> o = OPart(out PVec oe);
            double tl = oe.X + 0.3;
            List<PCurve> t = TPart(tl, out PVec top);
            var parts = new List<PCurve>(o) { G.Cubic(oe, oe + new PVec(0.2, 0.05), top - new PVec(0.05, 0.4), top) };
            parts.AddRange(t);
            yield return new PCase("W-ot", "at", Build(S(G.Concat(parts), H0), Bar(H0, tl + 0.03)), null);
        }

        {
            List<PCurve> a = APart(out PVec ae);
            List<PCurve> l = LPart(ae.X + 0.3, ae, out _);
            var parts = new List<PCurve>(a);
            parts.AddRange(l);
            yield return new PCase("W-al", "at", Build(S(G.Concat(parts), H0)), null);
        }

        {
            List<PCurve> a = APart(out PVec ae);
            List<PCurve> te = RuTePart(ae.X + 0.25, ae);
            var parts = new List<PCurve>(a);
            parts.AddRange(te);
            yield return new PCase("W-ru-at", "at", Build(S(G.Concat(parts), H0)), null);
        }

        {
            List<PCurve> a = APart(out _);
            yield return new PCase("W-a-only", "at", Build(S(G.Concat(a), H0)), null);
        }

        {
            List<PCurve> t = TPart(1.2, out PVec top);
            var parts = new List<PCurve> { G.Quad(new PVec(0.6, 0.02), new PVec(1.1, 0.3), top) };
            parts.AddRange(t);
            yield return new PCase("W-t-only", "at", Build(S(G.Concat(parts), H0), Bar(H0, 1.22)), null);
        }

        {
            PWord w = MakeAt(JoinLow0);
            List<PCurve> t2 = TPart(w.TLeft + 0.85, out PVec top2);
            PVec tailEnd = new(w.TLeft + 0.5, 0.3);
            var parts = new List<PCurve> { w.Curve, G.Cubic(tailEnd, tailEnd + new PVec(0.1, 0.1), top2 - new PVec(0.03, 0.6), top2) };
            parts.AddRange(t2);
            yield return new PCase("W-att", "at", Build(S(G.Concat(parts), H0), Bar(H0, w.TLeft + 0.0), Bar(H0, w.TLeft + 0.86, 200)), null);
        }

        {
            List<PCurve> t = TPart(0.2, out PVec top);
            var parts = new List<PCurve> { G.Quad(new PVec(-0.4, 0.02), new PVec(0.1, 0.3), top) };
            parts.AddRange(t);
            PVec tailEnd = new(0.7, 0.3);
            double cx = 1.35;
            PVec Oval(double ang) => new(cx + (0.4 * Math.Cos(ang)), 0.5 + (0.49 * Math.Sin(ang)));
            double a0 = 35 * Math.PI / 180;
            double a1 = 368 * Math.PI / 180;
            parts.Add(G.Quad(tailEnd, new PVec(1.3, 0.9), Oval(a0)));
            parts.Add(u => Oval(a0 + ((a1 - a0) * u)));
            parts.Add(G.Line(Oval(a1), new PVec(cx + 0.44, 1.0)));
            parts.Add(G.Line(new PVec(cx + 0.44, 1.0), new PVec(cx + 0.39, 0.15)));
            parts.Add(G.Cubic(new PVec(cx + 0.39, 0.15), new PVec(cx + 0.38, 0.03), new PVec(cx + 0.5, -0.04), new PVec(cx + 0.65, 0.14)));
            yield return new PCase("W-ta", "at", Build(S(G.Concat(parts), H0), Bar(H0, 0.21)), null);
        }

        {
            PWord it = MakeIt(JoinLow0);
            yield return new PCase("W-it-for-at", "at", Build(S(it.Curve, H0, 2), Dot(H0, it.ITop), Bar(H0, StemAt(it, 1.0))), null);
            PWord at = MakeAt(JoinLow0);
            yield return new PCase("W-at-for-it", "it", Build(S(at.Curve, H0), Bar(H0, StemAt(at, 1.0))), null);
        }

        yield return new PCase("W-scribble", "at", Build(S(t => new PVec(-0.3 + (2.4 * t), 0.5 + (0.45 * Math.Sin(t * 40))), H0)), null);
    }

    // ============================ S: скорость и дрожь ============================

    public static IEnumerable<PCase> Speeds()
    {
        PWord w = MakeAt(JoinLow0);
        double bar = StemAt(w, 1.0);
        foreach ((string sid, PHand h) in new (string, PHand)[]
        {
            ("slow", H0 with { Speed = 0.3, Tremor = 0.03 }),
            ("vslow", H0 with { Speed = 0.15, Tremor = 0.05 }),
            ("fast", H0 with { Speed = 5 }),
            ("vfast", H0 with { Speed = 12 }),
            ("fast-tilt", H0 with { Speed = 8, Slant = 25, Scale = 1.3 }),
        })
        {
            yield return new PCase($"S-{sid}", "at", Build(S(w.Curve, h), Bar(h, bar)), w.Truth.Map(h));
        }

        PWord it = MakeIt(JoinLow0);
        PHand slow = H0 with { Speed = 0.3, Tremor = 0.03 };
        yield return new PCase("S-it-slow-dot", "it", Build(S(it.Curve, slow, 2).Cut(0, it.BodyEnd, 0), Dot(H0, it.ITop, 300), S(it.Curve, slow, 2).Cut(it.BodyEnd, 1, 300), Bar(H0, StemAt(it, 1.0))), it.Truth.Map(slow));
    }

    /// <summary>Длинное слово <c>atatatat</c>: четыре «at» подряд независимой рукой (~900 исходных точек в слове; для перфа §6.9).</summary>
    public static RulerInk Long(double step = 1.9)
    {
        PWord w = MakeAt(JoinLow0);
        var strokes = new List<PStroke>();
        for (int k = 0; k < 4; k++)
        {
            strokes.Add(S(w.Curve, H0 with { Du = 1.0 + (k * step) }, k + 1).Cut(0, 1, k == 0 ? 0 : 200));
        }

        return Build([.. strokes]);
    }

    /// <summary>Ручная a с крюком-выходом: подготовка для проверки границ с допуском 0.2–0.25 (W10-18).</summary>
    public static PCase HookA() => Correct().First(c => c.Id == "A-low-0");
}
