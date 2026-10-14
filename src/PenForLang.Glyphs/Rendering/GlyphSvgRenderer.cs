using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using PenForLang.Core.Geometry;

namespace PenForLang.Glyphs.Rendering;

/// <summary>Параметры SVG-рендера глифа. Все флаги включены по умолчанию, кроме ключевых точек.</summary>
public sealed record SvgRenderOptions
{
    /// <summary>Размер x-height в пикселях итогового изображения (атрибуты <c>width</c>/<c>height</c>).</summary>
    public double PixelsPerXHeight { get; init; } = 160.0;

    /// <summary>Рисовать линии линейки (асцендеры, x-height, базовая, десцендеры).</summary>
    public bool ShowGuides { get; init; } = true;

    /// <summary>Рисовать наклонные линии линейки (если у стиля задан наклон).</summary>
    public bool ShowSlantGuides { get; init; } = true;

    /// <summary>Рисовать входящий и выходящий штрихи (пунктиром).</summary>
    public bool ShowLeads { get; init; } = true;

    /// <summary>Рисовать стрелки направления.</summary>
    public bool ShowArrows { get; init; } = true;

    /// <summary>Рисовать номера штрихов.</summary>
    public bool ShowNumbers { get; init; } = true;

    /// <summary>Рисовать точки старта.</summary>
    public bool ShowStartDots { get; init; } = true;

    /// <summary>Рисовать ключевые точки с подписями типов.</summary>
    public bool ShowKeyPoints { get; init; }

    /// <summary>Подпись внизу: идентификатор глифа, источник форм и статус ревью.</summary>
    public bool ShowCaption { get; init; } = true;
}

/// <summary>
/// Детерминированный SVG-рендер глифа для ревью форм: линейки, штрихи (кривые Безье как есть), отложенные штрихи,
/// стрелки, номера, точки старта, ключевые точки. Одинаковые входные данные дают одинаковую строку (LF, без меток времени).
/// </summary>
public static class GlyphSvgRenderer
{
    internal const double Scale = 100.0;
    private const double MarginU = 0.35;
    private const double MarginAbove = 0.30;
    private const double MarginBelow = 0.30;
    private const double CaptionHeight = 0.30;
    private const double CaptionCharWidth = 6.4;
    private const double CaptionPadding = 8.0;
    internal const double StrokeWidth = 5.0;
    private const double ArrowSize = 0.075;
    internal const double BadgeRadius = 0.085;
    internal const double StartDotRadius = 0.05;
    private const double KeyPointRadius = 0.025;

    internal const string InkColor = "#1a5fb4";
    internal const string DeferredColor = "#c01c28";
    internal const string LeadColor = "#9a9996";
    private const string GuideColor = "#c0bfbc";
    private const string BaselineColor = "#5e5c64";
    internal const string StartColor = "#26a269";
    internal const string BadgeColor = "#613583";

    /// <summary>Рисует глиф в SVG-строку.</summary>
    /// <param name="glyph">Глиф.</param>
    /// <param name="style">Стиль глифа (линии линейки, наклон).</param>
    /// <param name="options">Параметры; <c>null</c> — по умолчанию.</param>
    public static string Render(Glyph glyph, StyleManifest style, SvgRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(glyph);
        ArgumentNullException.ThrowIfNull(style);
        options ??= new SvgRenderOptions();

        ImmutableArray<PenStroke> strokes = glyph.PenStrokes;
        (double minU, double maxU) = HorizontalExtent(glyph, options);
        minU -= MarginU;
        maxU += MarginU;
        if (options.ShowCaption)
        {
            // Подпись должна поместиться в ширину изображения (у узких букв вроде i она иначе обрезается).
            string captionText = Caption(glyph);
            maxU = Math.Max(maxU, minU + ((captionText.Length * CaptionCharWidth) + (2.0 * CaptionPadding)) / Scale);
        }
        double topV = style.Lines.Ascender + MarginAbove;
        double bottomV = style.Lines.Descender - MarginBelow - (options.ShowCaption ? CaptionHeight : 0.0);

        double viewX = minU * Scale;
        double viewY = -topV * Scale;
        double viewW = (maxU - minU) * Scale;
        double viewH = (topV - bottomV) * Scale;

        var sb = new StringBuilder(8192);
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"")
            .Append(F(viewX)).Append(' ').Append(F(viewY)).Append(' ').Append(F(viewW)).Append(' ').Append(F(viewH))
            .Append("\" width=\"").Append(F(viewW / Scale * options.PixelsPerXHeight))
            .Append("\" height=\"").Append(F(viewH / Scale * options.PixelsPerXHeight)).Append("\">\n");
        sb.Append("<title>").Append(Escape(glyph.Id.ToString())).Append("</title>\n");
        sb.Append("<rect x=\"").Append(F(viewX)).Append("\" y=\"").Append(F(viewY)).Append("\" width=\"").Append(F(viewW))
            .Append("\" height=\"").Append(F(viewH)).Append("\" fill=\"#ffffff\"/>\n");

        if (options.ShowGuides)
        {
            AppendGuides(sb, style, minU, maxU, options.ShowSlantGuides);
        }

        if (options.ShowLeads)
        {
            AppendLead(sb, glyph.LeadIn, "leadIn");
            AppendLead(sb, glyph.LeadOut, "leadOut");
        }

        sb.Append("<g id=\"strokes\" fill=\"none\" stroke=\"").Append(InkColor).Append("\" stroke-width=\"").Append(F(StrokeWidth))
            .Append("\" stroke-linecap=\"round\" stroke-linejoin=\"round\">\n");
        foreach (PenStroke stroke in strokes.Where(s => !s.IsDeferred))
        {
            sb.Append("<path id=\"").Append(Escape(stroke.Id)).Append("\" d=\"").Append(PathData(stroke.Path)).Append("\"/>\n");
        }

        sb.Append("</g>\n");

        if (strokes.Any(s => s.IsDeferred))
        {
            sb.Append("<g id=\"deferred\" fill=\"none\" stroke=\"").Append(DeferredColor).Append("\" stroke-width=\"").Append(F(StrokeWidth))
                .Append("\" stroke-linecap=\"round\" stroke-linejoin=\"round\">\n");
            foreach (PenStroke stroke in strokes.Where(s => s.IsDeferred))
            {
                sb.Append("<path id=\"").Append(Escape(stroke.Id)).Append("\" d=\"").Append(PathData(stroke.Path)).Append("\"/>\n");
            }

            sb.Append("</g>\n");
        }

        if (options.ShowArrows && glyph.Animation.Arrows.Length > 0)
        {
            AppendArrows(sb, glyph, strokes);
        }

        if (options.ShowStartDots && glyph.Animation.StartDot != StartDotMode.None)
        {
            AppendStartDots(sb, glyph, strokes);
        }

        if (options.ShowNumbers && glyph.Animation.Numbers.Length > 0)
        {
            AppendNumbers(sb, glyph, strokes);
        }

        if (options.ShowKeyPoints && glyph.KeyPoints.Length > 0)
        {
            AppendKeyPoints(sb, glyph);
        }

        if (options.ShowCaption)
        {
            string caption = Caption(glyph);
            sb.Append("<text x=\"").Append(F(minU * Scale + 8.0)).Append("\" y=\"").Append(F(-(bottomV * Scale) - 8.0))
                .Append("\" font-family=\"sans-serif\" font-size=\"11\" fill=\"#5e5c64\">").Append(Escape(caption)).Append("</text>\n");
        }

        sb.Append("</svg>\n");
        return sb.ToString();
    }

    private static string Caption(Glyph glyph) =>
        $"{glyph.Id} | {glyph.Meta.Source} | {(glyph.Meta.Reviewed ? "reviewed" : "not reviewed")}";

    private static (double Min, double Max) HorizontalExtent(Glyph glyph, SvgRenderOptions options)
    {
        double min = 0.0;
        double max = glyph.Advance;
        void Include(GlyphPath? path)
        {
            if (path is null)
            {
                return;
            }

            (Vec2 lo, Vec2 hi) = path.GetBounds();
            min = Math.Min(min, lo.X);
            max = Math.Max(max, hi.X);
        }

        foreach (PenStroke stroke in glyph.PenStrokes)
        {
            Include(stroke.Path);
        }

        if (options.ShowLeads)
        {
            Include(glyph.LeadIn);
            Include(glyph.LeadOut);
        }

        if (options.ShowNumbers)
        {
            foreach (AnimationNumber number in glyph.Animation.Numbers)
            {
                min = Math.Min(min, number.At.X - BadgeRadius);
                max = Math.Max(max, number.At.X + BadgeRadius);
            }
        }

        return (min, max);
    }

    internal static void AppendGuides(StringBuilder sb, StyleManifest style, double minU, double maxU, bool slantGuides)
    {
        sb.Append("<g id=\"guides\" stroke-width=\"1\" fill=\"none\">\n");
        if (slantGuides && style.SlantDeg is { } slant)
        {
            double k = Math.Tan(slant * Math.PI / 180.0);
            double v0 = style.Lines.Descender;
            double v1 = style.Lines.Ascender;
            sb.Append("<g stroke=\"#e5e5e5\">\n");
            for (double u = Math.Floor(minU - (k * v1)); u <= maxU - (k * v0); u += 0.5)
            {
                sb.Append("<line x1=\"").Append(F((u + (k * v0)) * Scale)).Append("\" y1=\"").Append(F(-v0 * Scale))
                    .Append("\" x2=\"").Append(F((u + (k * v1)) * Scale)).Append("\" y2=\"").Append(F(-v1 * Scale)).Append("\"/>\n");
            }

            sb.Append("</g>\n");
        }

        void HorizontalLine(string id, double v, string color, string? dash)
        {
            sb.Append("<line id=\"").Append(id).Append("\" x1=\"").Append(F(minU * Scale)).Append("\" y1=\"").Append(F(-v * Scale))
                .Append("\" x2=\"").Append(F(maxU * Scale)).Append("\" y2=\"").Append(F(-v * Scale))
                .Append("\" stroke=\"").Append(color).Append('"');
            if (dash is not null)
            {
                sb.Append(" stroke-dasharray=\"").Append(dash).Append('"');
            }

            sb.Append("/>\n");
        }

        HorizontalLine("line-ascender", style.Lines.Ascender, GuideColor, "6 4");
        HorizontalLine("line-xheight", style.Lines.XHeight, GuideColor, "6 4");
        HorizontalLine("line-baseline", style.Lines.Baseline, BaselineColor, null);
        HorizontalLine("line-descender", style.Lines.Descender, GuideColor, "6 4");
        sb.Append("</g>\n");
    }

    internal static void AppendLead(StringBuilder sb, GlyphPath? path, string id)
    {
        if (path is null)
        {
            return;
        }

        sb.Append("<path id=\"").Append(id).Append("\" d=\"").Append(PathData(path)).Append("\" fill=\"none\" stroke=\"").Append(LeadColor)
            .Append("\" stroke-width=\"").Append(F(StrokeWidth * 0.6)).Append("\" stroke-dasharray=\"5 5\" stroke-linecap=\"round\"/>\n");
    }

    private static void AppendArrows(StringBuilder sb, Glyph glyph, ImmutableArray<PenStroke> strokes)
    {
        sb.Append("<g id=\"arrows\" fill=\"").Append(InkColor).Append("\" stroke=\"#ffffff\" stroke-width=\"1\" stroke-linejoin=\"round\">\n");
        foreach (AnimationArrow arrow in glyph.Animation.Arrows)
        {
            PenStroke? stroke = strokes.FirstOrDefault(s => s.Id == arrow.StrokeId);
            if (stroke is null)
            {
                continue;
            }

            Vec2 p = stroke.Path.PointAtFraction(arrow.T);
            Vec2 d = stroke.Path.TangentAtFraction(arrow.T);
            Vec2 n = d.Perpendicular();
            Vec2 tip = p + (d * ArrowSize);
            Vec2 left = p - (d * (ArrowSize * 0.7)) + (n * (ArrowSize * 0.75));
            Vec2 right = p - (d * (ArrowSize * 0.7)) - (n * (ArrowSize * 0.75));
            sb.Append("<polygon points=\"").Append(Pt(tip)).Append(' ').Append(Pt(left)).Append(' ').Append(Pt(right)).Append("\"/>\n");
        }

        sb.Append("</g>\n");
    }

    private static void AppendStartDots(StringBuilder sb, Glyph glyph, ImmutableArray<PenStroke> strokes)
    {
        sb.Append("<g id=\"start-dots\" fill=\"").Append(StartColor).Append("\">\n");
        for (int i = 0; i < strokes.Length; i++)
        {
            if (strokes[i].Deferred is { Kind: DeferredKind.Dot })
            {
                // Точка сама является меткой: кружок старта закрыл бы её.
                continue;
            }

            Vec2 position = strokes[i].Path.Start;
            if (i == 0 && glyph.Animation.StartDot == StartDotMode.LeadIn && glyph.LeadIn is { } leadIn)
            {
                position = leadIn.Start;
            }

            sb.Append("<circle cx=\"").Append(F(position.X * Scale)).Append("\" cy=\"").Append(F(-position.Y * Scale))
                .Append("\" r=\"").Append(F(StartDotRadius * Scale)).Append("\"/>\n");
        }

        sb.Append("</g>\n");
    }

    private static void AppendNumbers(StringBuilder sb, Glyph glyph, ImmutableArray<PenStroke> strokes)
    {
        sb.Append("<g id=\"numbers\" font-family=\"sans-serif\" font-size=\"11\" text-anchor=\"middle\">\n");
        foreach (AnimationNumber number in glyph.Animation.Numbers)
        {
            PenStroke? stroke = strokes.FirstOrDefault(s => s.Id == number.StrokeId);
            if (stroke is null)
            {
                continue;
            }

            sb.Append("<circle cx=\"").Append(F(number.At.X * Scale)).Append("\" cy=\"").Append(F(-number.At.Y * Scale))
                .Append("\" r=\"").Append(F(BadgeRadius * Scale)).Append("\" fill=\"#ffffff\" stroke=\"").Append(BadgeColor).Append("\" stroke-width=\"1.5\"/>\n");
            sb.Append("<text x=\"").Append(F(number.At.X * Scale)).Append("\" y=\"").Append(F((-number.At.Y * Scale) + 4.0))
                .Append("\" fill=\"").Append(BadgeColor).Append("\">").Append(stroke.Number.ToString(CultureInfo.InvariantCulture)).Append("</text>\n");
        }

        sb.Append("</g>\n");
    }

    private static void AppendKeyPoints(StringBuilder sb, Glyph glyph)
    {
        sb.Append("<g id=\"keypoints\" font-family=\"sans-serif\" font-size=\"8\" fill=\"#e66100\">\n");
        foreach (GlyphKeyPoint keyPoint in glyph.KeyPoints)
        {
            GlyphStroke? stroke = glyph.FindStroke(keyPoint.StrokeId);
            if (stroke is null)
            {
                continue;
            }

            Vec2 p = stroke.Path.PointAtFraction(keyPoint.T);
            sb.Append("<circle cx=\"").Append(F(p.X * Scale)).Append("\" cy=\"").Append(F(-p.Y * Scale))
                .Append("\" r=\"").Append(F(KeyPointRadius * Scale)).Append("\"/>\n");
            sb.Append("<text x=\"").Append(F((p.X * Scale) + 5.0)).Append("\" y=\"").Append(F((-p.Y * Scale) - 4.0)).Append("\">")
                .Append(WireNames.Name(keyPoint.Type)).Append("</text>\n");
        }

        sb.Append("</g>\n");
    }

    internal static string PathData(GlyphPath path)
    {
        var sb = new StringBuilder();
        sb.Append('M').Append(F(path.Start.X * Scale)).Append(' ').Append(F(-path.Start.Y * Scale));
        foreach (GlyphSegment segment in path.Segments)
        {
            if (segment.Kind == SegmentKind.Line)
            {
                sb.Append(" L").Append(Pt(segment.P3));
            }
            else
            {
                sb.Append(" C").Append(Pt(segment.P1)).Append(' ').Append(Pt(segment.P2)).Append(' ').Append(Pt(segment.P3));
            }
        }

        return sb.ToString();
    }

    internal static string Pt(Vec2 p) => F(p.X * Scale) + " " + F(-p.Y * Scale);

    internal static string F(double value)
    {
        double rounded = Math.Round(value, 2, MidpointRounding.AwayFromZero);
        if (rounded == 0.0)
        {
            rounded = 0.0;
        }

        return rounded.ToString("0.##", CultureInfo.InvariantCulture);
    }

    internal static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
}
