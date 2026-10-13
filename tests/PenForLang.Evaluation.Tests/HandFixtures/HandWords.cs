using PenForLang.Ink;

namespace PenForLang.Evaluation.Tests.HandFixtures;

/// <summary>
/// Ручные фикстуры слов <c>at</c> и <c>it</c>: траектории — параметрические кривые (овал, прямые, квадратичные дуги), а не
/// глифы пакета и не SynthInk. Соединение — своя форма (дуга с заданной «вытянутостью»), а не кубика Эрмита генератора
/// эталона, поэтому тесты не проверяют оценщик на его собственной модели слова.
/// </summary>
internal static class HandWords
{
    private const double StemDx = 0.42;
    private const double StemDy = 1.31;

    /// <summary>Положение граней слова: доли длины общей кривой, где кончается тело левой буквы и начинается тело правой.</summary>
    internal readonly record struct Cuts(double BodyEnd, double JoinEnd, HandVec ExitPoint, HandVec EntryPoint);

    // ----------------------------------- части букв -----------------------------------

    /// <summary>a: подход, овал против часовой с 40°, ствол вверх и вниз по той же линии; конец — внизу ствола.</summary>
    private static List<Func<double, HandVec>> APart(double rx = 0.45, double cx = 0.5)
    {
        double a0 = 40 * Math.PI / 180;
        double a1 = 380 * Math.PI / 180;
        HandVec Oval(double angle) => new(cx + (rx * Math.Cos(angle)), 0.5 + (0.5 * Math.Sin(angle)));
        HandVec top = new(cx + rx + 0.05, 1.0);
        HandVec bottom = new(cx + rx, 0.12);
        return
        [
            HandCurves.Quad(new HandVec(-0.5, 0.0), new HandVec(0.35, 0.05), Oval(a0)),
            t => Oval(a0 + ((a1 - a0) * t)),
            HandCurves.Line(Oval(a1), top),
            HandCurves.Line(top, bottom),
        ];
    }

    /// <summary>t: стержень сверху вниз и подхват вправо; <paramref name="left"/> — u нижней точки стержня.</summary>
    private static List<Func<double, HandVec>> TPart(double left, double height, out HandVec top)
    {
        top = new HandVec(left + (StemDx * (height - 0.14) / StemDy), height);
        HandVec bottom = new(left, 0.14);
        return
        [
            HandCurves.Line(top, bottom),
            HandCurves.Quad(bottom, new HandVec(left + 0.02, -0.06), new HandVec(left + 0.6, 0.42)),
        ];
    }

    /// <summary>i: подход, ствол вниз, подхват вправо; конец — выход тела внизу.</summary>
    private static List<Func<double, HandVec>> IPart()
    {
        HandVec top = new(0.28, 1.0);
        HandVec bottom = new(0.06, 0.14);
        return
        [
            HandCurves.Quad(new HandVec(-0.55, 0.0), new HandVec(0.0, 0.08), top),
            HandCurves.Line(top, bottom),
            HandCurves.Quad(bottom, new HandVec(0.05, -0.06), new HandVec(0.30, 0.16)),
        ];
    }

    /// <summary>
    /// Соединение: квадратичная дуга от точки выхода к верху t. Контрольная точка лежит на продолжении стержня t вверх на
    /// <paramref name="reach"/> от верха: меньше — дуга круче у верха и положе внизу, больше — вытянутее вниз. Подход к верху идёт вдоль
    /// стержня, как при реальном ретрейсе.
    /// </summary>
    private static Func<double, HandVec> Join(HandVec exit, HandVec top, double reach)
    {
        HandVec up = new HandVec(StemDx, StemDy) * (1.0 / Math.Sqrt((StemDx * StemDx) + (StemDy * StemDy)));
        return HandCurves.Quad(exit, top - (up * reach), top);
    }

    // ----------------------------------- слова -----------------------------------

    /// <summary>
    /// Слово <c>at</c> одним штрихом (подход, a, соединение, t с подхватом) и границы тел; <paramref name="reach"/> — форма соединения.
    /// </summary>
    public static (Func<double, HandVec> Curve, Cuts Cuts) At(double reach = 0.6, double tLeft = 1.3)
    {
        List<Func<double, HandVec>> a = APart();
        HandVec exit = a[^1](1.0);
        List<Func<double, HandVec>> t = TPart(tLeft, 1.45, out HandVec top);
        return Assemble(a, Join(exit, top, reach), t, exit, top);
    }

    /// <summary>Слово <c>it</c> одним штрихом.</summary>
    public static (Func<double, HandVec> Curve, Cuts Cuts) It(double reach = 0.6, double tLeft = 0.5)
    {
        List<Func<double, HandVec>> i = IPart();
        HandVec exit = i[^1](1.0);
        List<Func<double, HandVec>> t = TPart(tLeft, 1.45, out HandVec top);
        return Assemble(i, Join(exit, top, reach), t, exit, top);
    }

    private static (Func<double, HandVec>, Cuts) Assemble(
        List<Func<double, HandVec>> left,
        Func<double, HandVec> join,
        List<Func<double, HandVec>> right,
        HandVec exit,
        HandVec top)
    {
        double lengthLeft = left.Sum(HandCurves.Length);
        double lengthJoin = HandCurves.Length(join);
        double lengthRight = right.Sum(HandCurves.Length);
        double total = lengthLeft + lengthJoin + lengthRight;
        var all = new List<Func<double, HandVec>>(left) { join };
        all.AddRange(right);
        return (HandCurves.Concat(all), new Cuts(lengthLeft / total, (lengthLeft + lengthJoin) / total, exit, top));
    }

    // ----------------------------------- попытки -----------------------------------

    /// <summary>Оттенки руки: наклон, масштаб, сдвиг, дрожь.</summary>
    internal readonly record struct HandStyle(double Slant = 0, double Scale = 1, double Dv = 0, double Rotation = 0, double Tremor = 0.008, double Speed = 1.4);

    private static HandStroke Stroke(Func<double, HandVec> curve, HandStyle h, int seed) =>
        new HandStroke(curve, h.Speed, h.Tremor, seed).Transform(h.Slant, h.Scale, 1.0, h.Dv, h.Rotation);

    /// <summary>Перекладина t слова: через стержень на высоте 1.0.</summary>
    public static HandStroke Bar(HandStyle h, double stemAtBar, double delayMs = 400) =>
        Stroke(HandCurves.Line(new HandVec(stemAtBar - 0.3, 1.0), new HandVec(stemAtBar + 0.3, 1.0)), h with { Speed = 1.5, Tremor = 0 }, 21).After(delayMs);

    /// <summary>Точка над i.</summary>
    public static HandStroke Dot(HandStyle h, double delayMs = 400) =>
        Stroke(HandCurves.Line(new HandVec(0.45, 1.45), new HandVec(0.48, 1.46)), h with { Speed = 0.8, Tremor = 0 }, 22).After(delayMs);

    /// <summary>Слово <c>at</c> слитно, перекладина после слова.</summary>
    public static RulerInk AtConnected(HandStyle h, double reach = 0.6)
    {
        (Func<double, HandVec> curve, Cuts _) = At(reach);
        return HandInkBuilder.Build(Stroke(curve, h, 1), Bar(h, 1.3 + (StemDx * 0.86 / StemDy)));
    }

    /// <summary>
    /// Слово <c>at</c> с отрывом пера на доле <paramref name="at"/> общей кривой (после a и соединения — свои места): перо
    /// оторвано и поставлено на то же место; перекладина после слова.
    /// </summary>
    public static RulerInk AtLiftedAt(HandStyle h, double at, double reach = 0.6)
    {
        (Func<double, HandVec> curve, Cuts _) = At(reach);
        HandStroke whole = Stroke(curve, h, 1);
        return HandInkBuilder.Build(whole.Cut(0.0, at, 0), whole.Cut(at, 1.0, 350), Bar(h, 1.3 + (StemDx * 0.86 / StemDy)));
    }

    /// <summary>Слово <c>at</c> с пропущенным соединением: a, отрыв, t начато сверху.</summary>
    public static RulerInk AtSkippedJoin(HandStyle h, double reach = 0.6)
    {
        (Func<double, HandVec> curve, Cuts cuts) = At(reach);
        HandStroke whole = Stroke(curve, h, 1);
        return HandInkBuilder.Build(whole.Cut(0.0, cuts.BodyEnd, 0), whole.Cut(cuts.JoinEnd, 1.0, 350), Bar(h, 1.3 + (StemDx * 0.86 / StemDy)));
    }

    /// <summary>Слово <c>it</c> слитно: перекладина после слова, точка после слова.</summary>
    public static RulerInk ItConnected(HandStyle h, double reach = 0.6)
    {
        (Func<double, HandVec> curve, Cuts _) = It(reach);
        return HandInkBuilder.Build(Stroke(curve, h, 2), Dot(h), Bar(h, 0.5 + (StemDx * 0.86 / StemDy)));
    }

    /// <summary>Слово <c>it</c> с точкой сразу: i, точка, затем (с того же места) соединение и t, перекладина после слова.</summary>
    public static RulerInk ItDotImmediately(HandStyle h, double reach = 0.6)
    {
        (Func<double, HandVec> curve, Cuts cuts) = It(reach);
        HandStroke whole = Stroke(curve, h, 2);
        return HandInkBuilder.Build(whole.Cut(0.0, cuts.BodyEnd, 0), Dot(h, 300), whole.Cut(cuts.BodyEnd, 1.0, 300), Bar(h, 0.5 + (StemDx * 0.86 / StemDy)));
    }

    /// <summary>Слово <c>it</c> с отрывом на соединении (точка и перекладина после слова): i, отрыв, t сверху (соединения нет).</summary>
    public static RulerInk ItSkippedJoin(HandStyle h, double reach = 0.6)
    {
        (Func<double, HandVec> curve, Cuts cuts) = It(reach);
        HandStroke whole = Stroke(curve, h, 2);
        return HandInkBuilder.Build(whole.Cut(0.0, cuts.BodyEnd, 0), whole.Cut(cuts.JoinEnd, 1.0, 350), Dot(h), Bar(h, 0.5 + (StemDx * 0.86 / StemDy)));
    }

    /// <summary>Истинные границы тел в линейке для рукописи с рукой <paramref name="h"/> (точка выхода и точка входа).</summary>
    public static (HandVec Exit, HandVec Entry) Truth(Cuts cuts, HandStyle h)
    {
        HandVec Place(HandVec p)
        {
            double k = Math.Tan(h.Slant * Math.PI / 180);
            var q = new HandVec(p.X + (p.Y * k), p.Y) * h.Scale;
            double r = h.Rotation * Math.PI / 180;
            q = new HandVec((q.X * Math.Cos(r)) - (q.Y * Math.Sin(r)), (q.X * Math.Sin(r)) + (q.Y * Math.Cos(r)));
            return new HandVec(q.X + 1.0, q.Y + h.Dv);
        }

        return (Place(cuts.ExitPoint), Place(cuts.EntryPoint));
    }
}
