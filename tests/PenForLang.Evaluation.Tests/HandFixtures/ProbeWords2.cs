using PenForLang.Ink;
using static PenForLang.Evaluation.Tests.HandFixtures.ProbeWords;

namespace PenForLang.Evaluation.Tests.HandFixtures;

/// <summary>
/// Вторая, независимая модель букв (перенос из пробы ревьюера V0.10, R2-01): <c>a</c> — эллипс, наклонённый на 15°, с круглым
/// крюком; ствол <c>t</c> слегка изогнут; соединение — квадратичная дуга с подходом вдоль ствола. Нужна, чтобы пороги слова
/// не подгонялись под одну модель (<see cref="ProbeWords"/>): в ней широкий зазор a–t давал немонотонный ложный U0.
/// </summary>
internal static class ProbeWords2
{
    private static List<PCurve> APart(out PVec exit, double cx = 0.48, double rx = 0.36, double ry = 0.47, double tall = 1.0)
    {
        double tilt = 15 * Math.PI / 180;
        PVec Ov(double ang)
        {
            double x = rx * Math.Cos(ang);
            double y = ry * Math.Sin(ang) * tall;
            return new PVec(cx + (x * Math.Cos(tilt)) - (y * Math.Sin(tilt)), (0.5 * tall) + (x * Math.Sin(tilt)) + (y * Math.Cos(tilt)));
        }

        double a0 = 30 * Math.PI / 180;
        double a1 = 372 * Math.PI / 180;
        PVec top = new(cx + rx + 0.10, 1.0 * tall);
        PVec bottom = new(cx + rx + 0.02, 0.12);
        exit = new PVec(cx + rx + 0.30, 0.18);
        return
        [
            G.Line(new PVec(-0.3, 0.08), Ov(a0)),
            t => Ov(a0 + ((a1 - a0) * t)),
            G.Quad(Ov(a1), new PVec(cx + rx + 0.06, 0.8 * tall), top),
            G.Quad(top, new PVec(cx + rx + 0.09, 0.5 * tall), bottom),
            G.Quad(bottom, new PVec(cx + rx + 0.1, -0.06), exit),
        ];
    }

    private static List<PCurve> TPart(double tl, out PVec top, double h = 1.5, double lean = 0.12)
    {
        top = new PVec(tl + lean, h);
        PVec bottom = new(tl - 0.02, 0.15);
        return [G.Quad(top, new PVec(tl + (lean * 0.3) + 0.04, h * 0.5), bottom), G.Cubic(bottom, new PVec(tl - 0.04, -0.02), new PVec(tl + 0.3, -0.02), new PVec(tl + 0.7, 0.22))];
    }

    private static PCurve Arc(PVec e, PVec t, double lean = 0.12)
    {
        PVec up = new PVec((lean * 0.7) - 0.04, 1.35).Unit;
        PVec k = t - (up * 0.45);
        return G.Concat([G.Quad(e, new PVec(((e.X + k.X) / 2) + 0.12, ((e.Y + k.Y) / 2) - 0.12), k), G.Line(k, t)]);
    }

    /// <summary>Слово «at» второй модели с зазором между выходом a и стволом t (в x-height).</summary>
    public static PWord MakeAt(double gap = 0.28, double tH = 1.5, double lean = 0.12, double aTall = 1.0)
    {
        List<PCurve> a = APart(out PVec exit, tall: aTall);
        double tl = exit.X + gap;
        List<PCurve> t = TPart(tl, out PVec top, tH, lean);
        return PWord.Of(a, Arc(exit, top, lean), t, exit, top, tl);
    }

    /// <summary>x ствола t на высоте <paramref name="y"/> (для перекладины).</summary>
    public static double StemAt(PWord w, double y) => w.TLeft - 0.02 + ((w.Top.X - w.TLeft + 0.02) * (y - 0.15) / (w.Top.Y - 0.15));

    public static PCase At(string id, PHand hand, double gap = 0.28, double tH = 1.5, double lean = 0.12, double aTall = 1.0)
    {
        PWord w = MakeAt(gap, tH, lean, aTall);
        return new PCase(id, "at", Build(S(w.Curve, hand, 5), Bar(hand, StemAt(w, Math.Min(1.0, w.Top.Y - 0.3)))), w.Truth.Map(hand));
    }

    /// <summary>Зазоры a–t второй модели 0,6…1,4 (шаг 0,1) для проверки монотонности: верное слово при любом зазоре — цель, не U0.</summary>
    public static IEnumerable<PCase> Gaps()
    {
        foreach (double g in new[] { 0.6, 0.7, 0.8, 0.9, 1.0, 1.1, 1.2, 1.3, 1.4 })
        {
            yield return At($"G2-gap{g:0.0}", H0, g);
        }
    }

    /// <summary>Верные почерки второй модели (разные наклон, размер, скорость, зазор).</summary>
    public static IEnumerable<PCase> Correct()
    {
        yield return At("N2-base", H0);
        yield return At("N2-s35", H0 with { Slant = 35 });
        yield return At("N2-s-20", H0 with { Slant = -20 });
        yield return At("N2-wide1.35", H0 with { XS = 1.35 });
        yield return At("N2-narrow0.75", H0 with { XS = 0.75 });
        yield return At("N2-rot-6", H0 with { Rot = -6 });
        yield return At("N2-tremor", H0 with { Tremor = 0.04 });
        yield return At("N2-tallT", H0, tH: 1.85, aTall: 1.15);
        yield return At("N2-leanT", H0, lean: 0.75);
        yield return At("N2-shortT", H0, tH: 1.1);
    }
}
