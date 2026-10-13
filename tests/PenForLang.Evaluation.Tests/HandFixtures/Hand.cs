using PenForLang.Ink;

namespace PenForLang.Evaluation.Tests.HandFixtures;

/// <summary>
/// Ручные фикстуры букв a, i, t: траектории заданы формулами (овал, подход, ствол, хвост), а не глифом и не SynthInk.
/// Перенесены из независимой пробы ревью V0.7 (F-17), чтобы регрессии, которых SynthInk не видит, ловились тестами.
/// </summary>
internal static class Hand
{
    // ----------------------------------- a -----------------------------------

    /// <summary>
    /// Ручная модель a: подход от baseline, овал (по умолчанию против часовой с ~1 ч), подъём к x-height, ретрейс вниз, хвост.
    /// </summary>
    public static HandStroke A(
        double rx = 0.45,
        double slant = 0,
        double cx = 0.5,
        double scale = 1,
        double dv = 0,
        bool leadIn = true,
        double startDeg = 40,
        double endDeg = 380,
        double speed = 1.5,
        double tremor = 0,
        int seed = 1,
        double leadInFromU = -0.5,
        bool angular = false,
        double stemTop = 1.0)
    {
        var parts = new List<Func<double, HandVec>>();
        double a0 = startDeg * Math.PI / 180;
        double a1 = endDeg * Math.PI / 180;
        HandVec Oval(double angle) => new(cx + (rx * Math.Cos(angle)), 0.5 + (0.5 * Math.Sin(angle)));
        HandVec ovalStart = Oval(a0);
        if (leadIn)
        {
            parts.Add(HandCurves.Quad(new HandVec(leadInFromU, 0.0), new HandVec(0.35, 0.05), ovalStart));
        }

        if (angular)
        {
            HandVec tl = new(cx - rx, 0.95);
            HandVec bl = new(cx - rx, 0.05);
            HandVec br = new(cx + rx, 0.05);
            parts.Add(HandCurves.Line(ovalStart, tl));
            parts.Add(HandCurves.Line(tl, bl));
            parts.Add(HandCurves.Line(bl, br));
            parts.Add(HandCurves.Line(br, new HandVec(cx + rx + 0.03, stemTop)));
        }
        else
        {
            parts.Add(t => Oval(a0 + ((a1 - a0) * t)));
            parts.Add(HandCurves.Line(Oval(a1), new HandVec(cx + rx + 0.05, stemTop)));
        }

        HandVec top = new(cx + rx + (angular ? 0.03 : 0.05), stemTop);
        HandVec bottom = new(cx + rx, 0.12);
        parts.Add(HandCurves.Line(top, bottom));
        parts.Add(HandCurves.Quad(bottom, new HandVec(cx + rx + 0.02, -0.05), new HandVec(cx + rx + 0.5, 0.5)));
        return new HandStroke(HandCurves.Concat(parts), speed, tremor, seed).Transform(slant, scale, 1.0, dv, 0);
    }

    /// <summary>a, овал по часовой (ошибка направления; главная P2-ошибка методики). С подходом и без.</summary>
    public static HandStroke AClockwise(bool withApproach = true, double startDeg = 40) =>
        A(leadIn: withApproach, startDeg: startDeg, endDeg: startDeg - 340);

    /// <summary>a, овал по часовой, старт в 9 часов (как «о» у ребёнка).</summary>
    public static HandStroke AClockwiseFromNineOClock() => A(leadIn: false, startDeg: 180, endDeg: -160);

    /// <summary>a с подходом, начатым с <paramref name="fromU"/> на baseline.</summary>
    public static HandStroke AApproach(double fromU) => A(leadInFromU: fromU);

    /// <summary>a без подхода: старт с овала на <paramref name="startDeg"/> градусах (40 — «1 час»).</summary>
    public static HandStroke AWithoutApproach(double startDeg, double sweepDeg = 340) => A(leadIn: false, startDeg: startDeg, endDeg: startDeg + sweepDeg);

    /// <summary>a двумя кусками: овал с подходом, затем отдельным штрихом ствол с хвостом.</summary>
    public static HandStroke[] ATwoPieces() =>
    [
        A(startDeg: 40, endDeg: 400).Cut(0.0, 0.75, 0),
        A(startDeg: 40, endDeg: 400).Cut(0.75, 1.0, 400),
    ];

    /// <summary>«o» вместо a: овал и хвост сверху, ствола с ретрейсом нет.</summary>
    public static HandStroke OInsteadOfA() => new HandStroke(
        HandCurves.Concat(
        [
            HandCurves.Quad(new HandVec(-0.5, 0), new HandVec(0.35, 0.05), new HandVec(0.845, 0.82)),
            t => new HandVec(0.5 + (0.45 * Math.Cos((40 + (360 * t)) * Math.PI / 180)), 0.5 + (0.5 * Math.Sin((40 + (360 * t)) * Math.PI / 180))),
            HandCurves.Quad(new HandVec(0.845, 0.82), new HandVec(1.1, 0.95), new HandVec(1.4, 0.8)),
        ]),
        1.5,
        0,
        1).Transform(0, 1, 1.0, 0, 0);

    /// <summary>a с незамкнутым овалом (зазор 0,3).</summary>
    public static HandStroke AOpenOval() => new HandStroke(
        HandCurves.Concat(
        [
            HandCurves.Quad(new HandVec(-0.5, 0), new HandVec(0.35, 0.05), new HandVec(0.845, 0.82)),
            t => new HandVec(0.5 + (0.45 * Math.Cos((40 + (300 * t)) * Math.PI / 180)), 0.5 + (0.5 * Math.Sin((40 + (300 * t)) * Math.PI / 180))),
            HandCurves.Line(new HandVec(0.84, 0.2), new HandVec(1.2, 1.0)),
            HandCurves.Line(new HandVec(1.2, 1.0), new HandVec(1.15, 0.12)),
            HandCurves.Quad(new HandVec(1.15, 0.12), new HandVec(1.17, -0.05), new HandVec(1.6, 0.5)),
        ]),
        1.5,
        0,
        1).Transform(0, 1, 1.0, 0, 0);

    // ----------------------------------- i -----------------------------------

    public static HandStroke I(double slant = 0, double top = 1.0, double scale = 1, double dv = 0, bool leadIn = true, double speed = 1.5, double tremor = 0, bool reversed = false)
    {
        var parts = new List<Func<double, HandVec>>();
        HandVec t0 = new(0.28, top);
        HandVec b = new(0.06, 0.14);
        HandVec e = new(0.55, 0.45);
        if (leadIn)
        {
            parts.Add(HandCurves.Quad(new HandVec(-0.55, 0), new HandVec(0.0, 0.08), t0));
        }

        parts.Add(HandCurves.Line(t0, b));
        parts.Add(HandCurves.Quad(b, new HandVec(0.05, -0.06), e));
        var stroke = new HandStroke(HandCurves.Concat(parts), speed, tremor, 3);
        if (reversed)
        {
            stroke = stroke.Reverse();
        }

        return stroke.Transform(slant, scale, 1.0, dv, 0);
    }

    /// <summary>Точка: короткая черта длительностью <paramref name="durationMs"/> мс.</summary>
    public static HandStroke Dot(double u, double v, double radius = 0.015, double durationMs = 60) =>
        new HandStroke(HandCurves.Line(new HandVec(u - radius, v), new HandVec(u + radius, v + (radius / 2))), 2 * radius / (durationMs / 1000.0), 0, 5).Transform(0, 1, 1.0, 0, 0);

    // ----------------------------------- t -----------------------------------

    public static HandStroke T(double height = 1.5, double slant = 0, double scale = 1, double dv = 0, bool leadIn = true, double speed = 1.5, double tremor = 0, bool reversed = false)
    {
        var parts = new List<Func<double, HandVec>>();
        HandVec top = new(0.42 + (0.1 * (height - 1.5)), height);
        HandVec b = new(0.06, 0.14);
        HandVec e = new(0.6, 0.42);
        if (leadIn)
        {
            parts.Add(HandCurves.Quad(new HandVec(-0.55, 0), new HandVec(0.05, 0.15), top));
        }

        parts.Add(HandCurves.Line(top, b));
        parts.Add(HandCurves.Quad(b, new HandVec(0.05, -0.06), e));
        var stroke = new HandStroke(HandCurves.Concat(parts), speed, tremor, 11);
        if (reversed)
        {
            stroke = stroke.Reverse();
        }

        return stroke.Transform(slant, scale, 1.0, dv, 0);
    }

    /// <summary>Перекладина на высоте <paramref name="v"/> от <paramref name="u0"/> до <paramref name="u1"/> (справа налево при <paramref name="rtl"/>).</summary>
    public static HandStroke Bar(double v = 1.0, double u0 = 0.02, double u1 = 0.58, bool rtl = false) =>
        new HandStroke(rtl ? HandCurves.Line(new HandVec(u1, v), new HandVec(u0, v)) : HandCurves.Line(new HandVec(u0, v), new HandVec(u1, v)), 1.5, 0, 13).Transform(0, 1, 1.0, 0, 0);

    /// <summary>Русская «т» (≈ m) вместо t.</summary>
    public static HandStroke RussianT() => new HandStroke(
        HandCurves.Concat(
        [
            HandCurves.Quad(new HandVec(-0.3, 0), new HandVec(-0.1, 0.4), new HandVec(0.0, 1.0)),
            HandCurves.Line(new HandVec(0.0, 1.0), new HandVec(0.02, 0.0)),
            HandCurves.Quad(new HandVec(0.02, 0.0), new HandVec(0.1, 1.15), new HandVec(0.45, 0.95)),
            HandCurves.Line(new HandVec(0.45, 0.95), new HandVec(0.48, 0.0)),
            HandCurves.Quad(new HandVec(0.48, 0.0), new HandVec(0.6, 1.15), new HandVec(0.95, 0.95)),
            HandCurves.Line(new HandVec(0.95, 0.95), new HandVec(0.98, 0.12)),
            HandCurves.Quad(new HandVec(0.98, 0.12), new HandVec(1.0, -0.03), new HandVec(1.3, 0.35)),
        ]),
        1.5,
        0,
        17);

    // ----------------------------------- готовые попытки -----------------------------------

    public static RulerInk TypicalA() => HandInkBuilder.Build(A());

    public static RulerInk TypicalI() => HandInkBuilder.Build(I(), Dot(0.45, 1.45).After(400));

    public static RulerInk TypicalT() => HandInkBuilder.Build(T(), Bar().After(400));

    /// <summary>Медленное письмо с дрожью: скорость <paramref name="speed"/> x-height/с, амплитуда дрожи <paramref name="tremor"/>.</summary>
    public static RulerInk SlowTrembling(string letter, double speed = 0.3, double tremor = 0.03) => letter switch
    {
        "a" => HandInkBuilder.Build(A(speed: speed, tremor: tremor)),
        "i" => HandInkBuilder.Build(I(speed: speed, tremor: tremor), Dot(0.45, 1.45).After(400)),
        "t" => HandInkBuilder.Build(T(speed: speed, tremor: tremor), Bar().After(400)),
        _ => throw new ArgumentOutOfRangeException(nameof(letter), letter, "Only a, i, t."),
    };

    /// <summary>Буква с ручной траекторией, поднятая над строкой (или опущенная) на <paramref name="dv"/>.</summary>
    public static RulerInk Lifted(string letter, double dv) => letter switch
    {
        "a" => HandInkBuilder.Build(A(dv: dv)),
        "i" => HandInkBuilder.Build(I(dv: dv), Dot(0.45, 1.45 + dv).After(400)),
        "t" => HandInkBuilder.Build(T(dv: dv), Bar(1.0 + dv).After(400)),
        _ => throw new ArgumentOutOfRangeException(nameof(letter), letter, "Only a, i, t."),
    };
}
