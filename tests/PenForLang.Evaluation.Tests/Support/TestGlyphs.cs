using System.Collections.Immutable;
using PenForLang.Core.Geometry;
using PenForLang.Glyphs;

namespace PenForLang.Evaluation.Tests.Support;

/// <summary>
/// Глифы-фикстуры в коде для проверок, которые не покрывают a, i, t: буква из двух основных штрихов (P3, P4, P5, P1 на втором
/// штрихе) и буквы с петлёй (F3). Глифы не проходят семантическую проверку стиль-пакета — они нужны только оценщику и SynthInk.
/// </summary>
internal static class TestGlyphs
{
    /// <summary>
    /// Высокое кольцо из двух полудуг без входящего и выходящего штрихов: <c>s1</c> — правая половина (сверху вниз по часовой),
    /// <c>s2</c> — левая (сверху вниз против часовой). Криволинейные штрихи однозначно задают масштаб и положение (прямые
    /// диагонали креста для этого не годятся: вдоль прямой их не различить).
    /// </summary>
    public static Glyph Rings()
    {
        GlyphStroke first = Stroke("s1", 1, GlyphSegment.Cubic(new Vec2(0.5, 2.0), new Vec2(1.3, 2.0), new Vec2(1.3, 0.0), new Vec2(0.5, 0.0)));
        GlyphStroke second = Stroke("s2", 2, GlyphSegment.Cubic(new Vec2(0.5, 2.0), new Vec2(-0.3, 2.0), new Vec2(-0.3, 0.0), new Vec2(0.5, 0.0)));
        return Build("rings", [first, second], GlyphChecks.Empty);
    }

    /// <summary>Штрих с петлёй (как у l): вверх-вправо, над вершиной налево, вниз с пересечением подъёма и вниз-вправо.</summary>
    public static Glyph Loop()
    {
        GlyphStroke stroke = Stroke(
            "s1",
            1,
            GlyphSegment.Cubic(new Vec2(0.1, 0.0), new Vec2(0.3, 0.7), new Vec2(0.6, 1.4), new Vec2(0.7, 1.9)),
            GlyphSegment.Cubic(new Vec2(0.7, 1.9), new Vec2(0.8, 2.2), new Vec2(0.2, 2.3), new Vec2(0.2, 1.8)),
            GlyphSegment.Cubic(new Vec2(0.2, 1.8), new Vec2(0.2, 1.3), new Vec2(0.5, 0.9), new Vec2(0.9, 0.0)));
        var checks = new GlyphChecks([], [], [new StrokeRange("s1", 0.3, 0.9)], []);
        return Build("loop", [stroke], checks);
    }

    /// <summary>Тот же размах, что у <see cref="Loop"/>, но без петли: подъём, дуга вправо и спуск.</summary>
    public static Glyph NoLoop()
    {
        GlyphStroke stroke = Stroke(
            "s1",
            1,
            GlyphSegment.Cubic(new Vec2(0.1, 0.0), new Vec2(0.3, 0.7), new Vec2(0.6, 1.4), new Vec2(0.7, 1.9)),
            GlyphSegment.Cubic(new Vec2(0.7, 1.9), new Vec2(0.95, 2.1), new Vec2(1.1, 1.0), new Vec2(0.9, 0.0)));
        return Build("noloop", [stroke], GlyphChecks.Empty);
    }

    private static GlyphStroke Stroke(string id, int order, params GlyphSegment[] segments) =>
        new(id, order, new GlyphPath(segments));

    private static Glyph Build(string symbol, ImmutableArray<GlyphStroke> strokes, GlyphChecks checks)
    {
        Vec2 start = strokes[0].Path.Start;
        Vec2 end = strokes[^1].Path.End;
        return new Glyph(
            new GlyphId("zaner-bloser", symbol, "default"),
            GlyphCase.Lower,
            1.0,
            new GlyphEntry(EntryClass.Up, start, new Vec2(0.0, 1.0), null),
            new GlyphExit(ExitClass.None, end, new Vec2(0.0, -1.0)),
            null,
            null,
            strokes,
            [],
            [],
            [],
            checks,
            new GlyphAnimationMeta(StartDotMode.Body, [], []),
            new GlyphMeta("test", "illustrative", false, null, null, null));
    }
}
