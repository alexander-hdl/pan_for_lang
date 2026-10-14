using System.Globalization;
using System.Text;
using PenForLang.Core.Geometry;

namespace PenForLang.Glyphs.Rendering;

/// <summary>
/// Детерминированный SVG-рендер эталона слова для ревью (ARCHITECTURE.md §3.2.7, п. 9): линейки, подход и выход пунктиром,
/// тела букв, соединения (другой цвет), отложенные штрихи в конце, границы тел (конец тела левой и начало тела правой
/// буквы), точки старта, номера штрихов и подписи букв. Одинаковые входные данные дают одинаковую строку.
/// </summary>
public static class WordSvgRenderer
{
    private const double MarginU = 0.35;
    private const double MarginAbove = 0.30;
    private const double MarginBelow = 0.30;
    private const double CaptionHeight = 0.30;
    private const double CaptionCharWidth = 6.4;
    private const double CaptionPadding = 8.0;
    private const double BoundaryRadius = 0.035;
    private const string JoinColor = "#e66100";
    private const string BodyEndColor = "#1c71d8";
    private const string BodyStartColor = "#813d9c";

    /// <summary>Рисует слово в SVG-строку.</summary>
    /// <param name="word">Эталон слова.</param>
    /// <param name="options">Параметры (те же, что у глифа); <c>null</c> — по умолчанию.</param>
    public static string Render(WordTemplate word, SvgRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(word);
        options ??= new SvgRenderOptions();
        StyleManifest style = word.Style;
        const double scale = GlyphSvgRenderer.Scale;

        (double minU, double maxU) = HorizontalExtent(word, options);
        minU -= MarginU;
        maxU += MarginU;
        string caption = Caption(word);
        if (options.ShowCaption)
        {
            maxU = Math.Max(maxU, minU + (((caption.Length * CaptionCharWidth) + (2.0 * CaptionPadding)) / scale));
        }

        double topV = style.Lines.Ascender + MarginAbove;
        double bottomV = style.Lines.Descender - MarginBelow - (options.ShowCaption ? CaptionHeight : 0.0);
        double viewX = minU * scale;
        double viewY = -topV * scale;
        double viewW = (maxU - minU) * scale;
        double viewH = (topV - bottomV) * scale;

        var sb = new StringBuilder(16384);
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"")
            .Append(F(viewX)).Append(' ').Append(F(viewY)).Append(' ').Append(F(viewW)).Append(' ').Append(F(viewH))
            .Append("\" width=\"").Append(F(viewW / scale * options.PixelsPerXHeight))
            .Append("\" height=\"").Append(F(viewH / scale * options.PixelsPerXHeight)).Append("\">\n");
        sb.Append("<title>").Append(GlyphSvgRenderer.Escape(word.Text)).Append("</title>\n");
        sb.Append("<rect x=\"").Append(F(viewX)).Append("\" y=\"").Append(F(viewY)).Append("\" width=\"").Append(F(viewW))
            .Append("\" height=\"").Append(F(viewH)).Append("\" fill=\"#ffffff\"/>\n");

        if (options.ShowGuides)
        {
            GlyphSvgRenderer.AppendGuides(sb, style, minU, maxU, options.ShowSlantGuides);
        }

        string strokeAttributes = " fill=\"none\" stroke-width=\"" + F(GlyphSvgRenderer.StrokeWidth) + "\" stroke-linecap=\"round\" stroke-linejoin=\"round\"";
        if (options.ShowLeads)
        {
            sb.Append("<g id=\"leads\">\n");
            foreach (WordPenStroke pen in word.PenStrokes)
            {
                foreach (WordPart part in pen.Parts.Where(p => p.Kind is WordPartKind.LeadIn or WordPartKind.LeadOut))
                {
                    GlyphSvgRenderer.AppendLead(sb, part.Path, part.Kind == WordPartKind.LeadIn ? "leadIn" : "leadOut");
                }
            }

            sb.Append("</g>\n");
        }

        sb.Append("<g id=\"bodies\" stroke=\"").Append(GlyphSvgRenderer.InkColor).Append('"').Append(strokeAttributes).Append(">\n");
        foreach (WordPenStroke pen in word.PenStrokes)
        {
            foreach (WordPart part in pen.Parts.Where(p => p.Kind == WordPartKind.Body))
            {
                sb.Append("<path id=\"body-").Append(part.Letter.ToString(CultureInfo.InvariantCulture)).Append('.').Append(GlyphSvgRenderer.Escape(part.StrokeId ?? string.Empty))
                    .Append("\" d=\"").Append(GlyphSvgRenderer.PathData(part.Path)).Append("\"/>\n");
            }
        }

        sb.Append("</g>\n");

        sb.Append("<g id=\"joins\" stroke=\"").Append(JoinColor).Append('"').Append(strokeAttributes).Append(">\n");
        foreach (WordPenStroke pen in word.PenStrokes)
        {
            foreach (WordPart part in pen.Parts.Where(p => p.Kind == WordPartKind.Join))
            {
                WordJoin join = word.Joins[part.Letter];
                sb.Append("<path id=\"join-").Append(part.Letter.ToString(CultureInfo.InvariantCulture)).Append("\" data-family=\"")
                    .Append(GlyphSvgRenderer.Escape(join.Family ?? "none")).Append("\" d=\"").Append(GlyphSvgRenderer.PathData(part.Path)).Append("\"/>\n");
            }
        }

        sb.Append("</g>\n");

        if (word.Deferred.Length > 0)
        {
            sb.Append("<g id=\"deferred\" stroke=\"").Append(GlyphSvgRenderer.DeferredColor).Append('"').Append(strokeAttributes).Append(">\n");
            foreach (WordDeferred deferred in word.Deferred)
            {
                sb.Append("<path id=\"").Append(GlyphSvgRenderer.Escape(deferred.SlotId)).Append('-').Append(deferred.Letter.ToString(CultureInfo.InvariantCulture))
                    .Append("\" d=\"").Append(GlyphSvgRenderer.PathData(deferred.Path)).Append("\"/>\n");
            }

            sb.Append("</g>\n");
        }

        AppendBoundaries(sb, word);

        if (options.ShowStartDots)
        {
            sb.Append("<g id=\"start-dots\" fill=\"").Append(GlyphSvgRenderer.StartColor).Append("\">\n");
            foreach (WordPenStroke pen in word.PenStrokes)
            {
                AppendCircle(sb, pen.Path.Start, GlyphSvgRenderer.StartDotRadius);
            }

            sb.Append("</g>\n");
        }

        if (options.ShowNumbers)
        {
            AppendNumbers(sb, word);
        }

        AppendLetterLabels(sb, word);

        if (options.ShowCaption)
        {
            sb.Append("<text x=\"").Append(F((minU * scale) + 8.0)).Append("\" y=\"").Append(F(-(bottomV * scale) - 8.0))
                .Append("\" font-family=\"sans-serif\" font-size=\"11\" fill=\"#5e5c64\">").Append(GlyphSvgRenderer.Escape(caption)).Append("</text>\n");
        }

        sb.Append("</svg>\n");
        return sb.ToString();
    }

    private static string Caption(WordTemplate word)
    {
        string joins = word.Joins.Length == 0
            ? "no joins"
            : string.Join(", ", word.Joins.Select(j => j.Family ?? "break"));
        return $"{word.Text} | {word.Style.Id} | joins: {joins} | not reviewed";
    }

    private static (double Min, double Max) HorizontalExtent(WordTemplate word, SvgRenderOptions options)
    {
        double min = 0.0;
        double max = word.Advance;
        void Include(GlyphPath path)
        {
            (Vec2 lo, Vec2 hi) = path.GetBounds();
            min = Math.Min(min, lo.X);
            max = Math.Max(max, hi.X);
        }

        foreach (WordPenStroke pen in word.PenStrokes)
        {
            foreach (WordPart part in pen.Parts)
            {
                if (options.ShowLeads || part.Kind is not (WordPartKind.LeadIn or WordPartKind.LeadOut))
                {
                    Include(part.Path);
                }
            }
        }

        foreach (WordDeferred deferred in word.Deferred)
        {
            Include(deferred.Path);
        }

        return (min, max);
    }

    private static void AppendBoundaries(StringBuilder sb, WordTemplate word)
    {
        sb.Append("<g id=\"boundaries\" stroke=\"#ffffff\" stroke-width=\"1\">\n");
        foreach (WordLetter letter in word.Letters)
        {
            if (letter.Index > 0)
            {
                sb.Append("<circle class=\"body-start\" fill=\"").Append(BodyStartColor).Append("\" cx=\"").Append(F(letter.EntryPoint.X * GlyphSvgRenderer.Scale))
                    .Append("\" cy=\"").Append(F(-letter.EntryPoint.Y * GlyphSvgRenderer.Scale)).Append("\" r=\"").Append(F(BoundaryRadius * GlyphSvgRenderer.Scale)).Append("\"/>\n");
            }

            if (letter.Index + 1 < word.Letters.Length)
            {
                sb.Append("<circle class=\"body-end\" fill=\"").Append(BodyEndColor).Append("\" cx=\"").Append(F(letter.ExitPoint.X * GlyphSvgRenderer.Scale))
                    .Append("\" cy=\"").Append(F(-letter.ExitPoint.Y * GlyphSvgRenderer.Scale)).Append("\" r=\"").Append(F(BoundaryRadius * GlyphSvgRenderer.Scale)).Append("\"/>\n");
            }
        }

        sb.Append("</g>\n");
    }

    private static void AppendCircle(StringBuilder sb, Vec2 center, double radius) =>
        sb.Append("<circle cx=\"").Append(F(center.X * GlyphSvgRenderer.Scale)).Append("\" cy=\"").Append(F(-center.Y * GlyphSvgRenderer.Scale))
            .Append("\" r=\"").Append(F(radius * GlyphSvgRenderer.Scale)).Append("\"/>\n");

    private static void AppendNumbers(StringBuilder sb, WordTemplate word)
    {
        sb.Append("<g id=\"numbers\" font-family=\"sans-serif\" font-size=\"11\" text-anchor=\"middle\">\n");
        int number = 1;
        foreach (WordPenStroke pen in word.PenStrokes)
        {
            Vec2 at = pen.Path.Start + new Vec2(-0.2, 0.2);
            AppendBadge(sb, at, number++);
        }

        foreach (WordDeferred deferred in word.Deferred)
        {
            AppendBadge(sb, deferred.Path.PointAtFraction(0.5) + new Vec2(-0.25, 0.2), number++);
        }

        sb.Append("</g>\n");
    }

    private static void AppendBadge(StringBuilder sb, Vec2 at, int number)
    {
        const double scale = GlyphSvgRenderer.Scale;
        sb.Append("<circle cx=\"").Append(F(at.X * scale)).Append("\" cy=\"").Append(F(-at.Y * scale)).Append("\" r=\"").Append(F(GlyphSvgRenderer.BadgeRadius * scale))
            .Append("\" fill=\"#ffffff\" stroke=\"").Append(GlyphSvgRenderer.BadgeColor).Append("\" stroke-width=\"1.5\"/>\n");
        sb.Append("<text x=\"").Append(F(at.X * scale)).Append("\" y=\"").Append(F((-at.Y * scale) + 4.0)).Append("\" fill=\"").Append(GlyphSvgRenderer.BadgeColor).Append("\">")
            .Append(number.ToString(CultureInfo.InvariantCulture)).Append("</text>\n");
    }

    private static void AppendLetterLabels(StringBuilder sb, WordTemplate word)
    {
        sb.Append("<g id=\"letters\" font-family=\"sans-serif\" font-size=\"14\" text-anchor=\"middle\" fill=\"#5e5c64\">\n");
        foreach (WordLetter letter in word.Letters)
        {
            double centre = letter.OffsetU + (letter.Glyph.Advance / 2.0);
            sb.Append("<text x=\"").Append(F(centre * GlyphSvgRenderer.Scale)).Append("\" y=\"").Append(F(0.3 * GlyphSvgRenderer.Scale)).Append("\">")
                .Append(GlyphSvgRenderer.Escape(letter.Glyph.Symbol)).Append("</text>\n");
        }

        sb.Append("</g>\n");
    }

    private static string F(double value) => GlyphSvgRenderer.F(value);
}
