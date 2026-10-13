using PenForLang.Core.Geometry;
using PenForLang.Evaluation;
using PenForLang.Glyphs;

namespace PenForLang.App.Wpf.Lesson;

/// <summary>Подсветка эталона в координатах линейки (u, v): готова к рисованию слоем.</summary>
/// <param name="Polylines">Ломаные участков эталона.</param>
/// <param name="StartPoint">Точка старта, которая пульсирует, или <c>null</c>.</param>
/// <param name="Kind">Вид подсветки.</param>
/// <param name="Tone">Тон.</param>
public sealed record ResolvedHighlight(IReadOnlyList<Vec2[]> Polylines, Vec2? StartPoint, HighlightKind Kind, FeedbackTone Tone);

/// <summary>
/// Перевод участков эталона из находок оценщика (<see cref="TemplateSpan"/>) в геометрию линейки: подсветка без чернил ребёнка
/// (ARCHITECTURE.md §6.4.14, A01). Чистые функции от глифа.
/// </summary>
public static class TemplateGeometry
{
    private const double SampleStep = 0.03;
    private const double StartBodyShare = 0.25;

    /// <summary>
    /// Каноническая точка старта буквы: начало входящего штриха, если стиль показывает точку старта там, иначе начало первого
    /// основного штриха.
    /// </summary>
    public static Vec2 StartPoint(Glyph glyph)
    {
        ArgumentNullException.ThrowIfNull(glyph);
        return glyph.Animation.StartDot == StartDotMode.LeadIn && glyph.LeadIn is { } leadIn
            ? leadIn.Start
            : glyph.PenStrokes[0].Path.Start;
    }

    /// <summary>Допустимые места старта: входящий штрих и начало тела (для мягкого сигнала по ходу письма).</summary>
    public static IReadOnlyList<Vec2[]> AllowedStartPaths(Glyph glyph)
    {
        ArgumentNullException.ThrowIfNull(glyph);
        var list = new List<Vec2[]>();
        if (glyph.LeadIn is { } leadIn)
        {
            list.Add(leadIn.Flatten(SampleStep / 2));
        }

        list.Add([glyph.PenStrokes[0].Path.Start]);
        return list;
    }

    /// <summary>Находит ломаные и точку старта для описания подсветки.</summary>
    /// <param name="glyph">Глиф цели.</param>
    /// <param name="spec">Что подсветить.</param>
    /// <param name="tone">Тон наложения.</param>
    public static ResolvedHighlight Resolve(Glyph glyph, HighlightSpec spec, FeedbackTone tone)
    {
        ArgumentNullException.ThrowIfNull(glyph);
        ArgumentNullException.ThrowIfNull(spec);
        switch (spec.Kind)
        {
            case HighlightKind.Start:
                {
                    // Начало эталона: входящий штрих целиком (откуда войти в букву) и точка старта, которая пульсирует.
                    Vec2[] path = glyph.LeadIn is { } leadIn
                        ? leadIn.Flatten(SampleStep / 2)
                        : SubPath(glyph.PenStrokes[0].Path, 0.0, StartBodyShare);
                    return new ResolvedHighlight([path], StartPoint(glyph), HighlightKind.Start, tone);
                }

            case HighlightKind.Span:
                return new ResolvedHighlight(SpanPolylines(glyph, spec.Span), null, HighlightKind.Span, tone);

            default:
                return new ResolvedHighlight(Whole(glyph), null, HighlightKind.Whole, tone);
        }
    }

    /// <summary>Вся буква: входящий штрих, основные и отложенные штрихи, выходящий штрих.</summary>
    public static IReadOnlyList<Vec2[]> Whole(Glyph glyph)
    {
        ArgumentNullException.ThrowIfNull(glyph);
        var list = new List<Vec2[]>();
        if (glyph.LeadIn is { } leadIn)
        {
            list.Add(leadIn.Flatten(SampleStep / 2));
        }

        foreach (PenStroke stroke in glyph.PenStrokes)
        {
            list.Add(stroke.Path.Flatten(SampleStep / 2));
        }

        if (glyph.LeadOut is { } leadOut)
        {
            list.Add(leadOut.Flatten(SampleStep / 2));
        }

        return list;
    }

    /// <summary>Участок пути по долям длины дуги <paramref name="from"/>…<paramref name="to"/>.</summary>
    public static Vec2[] SubPath(GlyphPath path, double from, double to)
    {
        ArgumentNullException.ThrowIfNull(path);
        double a = Math.Clamp(Math.Min(from, to), 0.0, 1.0);
        double b = Math.Clamp(Math.Max(from, to), 0.0, 1.0);
        int count = Math.Max(2, (int)Math.Ceiling((b - a) * path.Length / SampleStep) + 1);
        var points = new Vec2[count];
        for (int i = 0; i < count; i++)
        {
            points[i] = path.PointAtFraction(a + ((b - a) * i / (count - 1)));
        }

        return points;
    }

    private static IReadOnlyList<Vec2[]> SpanPolylines(Glyph glyph, TemplateSpan span)
    {
        GlyphPath? path = span.Part switch
        {
            TemplatePart.LeadIn => glyph.LeadIn,
            TemplatePart.LeadOut => glyph.LeadOut,
            _ => span.StrokeId is null ? null : glyph.FindPenStroke(span.StrokeId)?.Path,
        };
        return path is null ? Whole(glyph) : [SubPath(path, span.FromT, span.ToT)];
    }
}
