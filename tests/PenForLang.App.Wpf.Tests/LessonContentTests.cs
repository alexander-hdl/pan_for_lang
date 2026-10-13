using System.IO;
using System.Text.RegularExpressions;
using PenForLang.App.Wpf.Lesson;
using PenForLang.Core;
using PenForLang.Core.Geometry;
using PenForLang.Evaluation;
using PenForLang.Glyphs;
using static PenForLang.App.Wpf.Tests.LessonTestSupport;

namespace PenForLang.App.Wpf.Tests;

public class LessonConfigTests
{
    [Fact]
    public void The_shipped_config_describes_E01_then_three_traces_then_two_copies()
    {
        LessonConfig config = Content.Config;

        Assert.Equal(["E01", "E03", "E04"], config.Steps.Select(s => s.Code));
        Assert.Equal([StepKind.Demo, StepKind.Trace, StepKind.Copy], config.Steps.Select(s => s.Kind));
        Assert.Equal([0, 3, 2], config.Steps.Select(s => s.Attempts));
        Assert.Equal([SupportLevel.L4, SupportLevel.L4, SupportLevel.L3], config.Steps.Select(s => s.Level));
        Assert.Equal([false, true, false], config.Steps.Select(s => s.LiveCue)); // мягкий сигнал — только при эталоне под пером
        Assert.Equal(3, config.Feedback.RepeatLimit);
        Assert.Equal(12, config.Feedback.MaxWords);
        Assert.Equal(1, config.Feedback.UnrecognizedRetries);
        Assert.True(config.LiveCue.Enabled);
        Assert.Equal(1, config.LiveCue.MaxPerAttempt);
        Assert.Contains(ErrorCode.P1, config.Feedback.StopCodes);
        Assert.DoesNotContain(ErrorCode.D1, config.Feedback.ProcessCodes);
    }

    [Theory]
    [InlineData("{}", "schema")]
    [InlineData("""{ "schema": "pfl.config.lesson/1" }""", "scenario")]
    [InlineData("not json", "JSON")]
    public void Bad_config_is_rejected_with_a_clear_message(string json, string expected)
    {
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => LessonConfig.Parse(json));

        Assert.Contains(expected, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_demo_step_with_attempts_or_a_writing_step_without_them_is_rejected()
    {
        string text = File.ReadAllText(Path.Combine(ContentRoot, "config", "lesson.json"));

        Assert.Throws<InvalidDataException>(() => LessonConfig.Parse(text.Replace("\"kind\": \"demo\", \"attempts\": 0", "\"kind\": \"demo\", \"attempts\": 1", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => LessonConfig.Parse(text.Replace("\"attempts\": 3", "\"attempts\": 0", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => LessonConfig.Parse(text.Replace("\"P1\", \"P2\", \"P3\"]", "\"P1\", \"ZZ\"]", StringComparison.Ordinal)));
    }

    [Fact]
    public void Missing_files_are_reported_not_swallowed()
    {
        var empty = new InMemoryContentSource();

        Assert.Throws<InvalidDataException>(() => LessonConfig.Load(empty));
        Assert.Throws<InvalidDataException>(() => LocaleCatalog.Load(empty, "ru"));
    }
}

public class LocaleCatalogTests
{
    private static readonly string[] Languages = ["ru", "en"];

    private static IEnumerable<string> AllKeys =>
        [.. FeedbackKeys.Hints, .. FeedbackKeys.Praises, "ui.show", "ui.showAgain", "ui.restart", "ui.done", "ui.next", "ui.restartLesson", "ui.evaluating", "ui.accepted", "ui.writeFirst", "ui.step.demo", "ui.step.trace", "ui.step.copy", "ui.step.copyAided", "ui.finished", "ui.letter", "ui.lang.switch", "part.dot", "part.bar", "part.cross"];

    [Fact]
    public void Every_key_the_feedback_can_show_exists_in_both_languages()
    {
        foreach (string language in Languages)
        {
            LocaleCatalog catalog = Content.Locale(language);
            foreach (string key in AllKeys)
            {
                Assert.True(catalog.Contains(key), $"{language}: missing '{key}'");
                Assert.NotEqual(key, catalog.Get(key));
            }
        }
    }

    [Fact]
    public void Both_languages_have_the_same_keys_and_the_same_placeholders()
    {
        LocaleCatalog ru = Content.Locale("ru");
        LocaleCatalog en = Content.Locale("en");

        Assert.Equal(ru.Strings.Keys.Order(StringComparer.Ordinal), en.Strings.Keys.Order(StringComparer.Ordinal));
        foreach (string key in ru.Strings.Keys)
        {
            Assert.Equal(
                LocaleCatalog.Placeholders(ru.Strings[key].Text).Order(StringComparer.Ordinal),
                LocaleCatalog.Placeholders(en.Strings[key].Text).Order(StringComparer.Ordinal));
            Assert.Equal(ru.Strings[key].Child, en.Strings[key].Child);
        }
    }

    [Fact]
    public void Child_phrases_have_at_most_twelve_words_in_both_languages()
    {
        int limit = Content.Config.Feedback.MaxWords;
        foreach (string language in Languages)
        {
            foreach ((string key, LocaleString entry) in Content.Locale(language).Strings.Where(p => p.Value.Child))
            {
                int words = Regex.Count(entry.Text, @"[\p{L}\p{N}']+");
                Assert.True(words <= limit, $"{language}/{key}: {words} words");
            }
        }

        // Все подсказки и похвала — фразы для ребёнка.
        foreach (string key in FeedbackKeys.Hints.Concat(FeedbackKeys.Praises))
        {
            Assert.True(Content.Locale("ru").Strings[key].Child, key);
        }
    }

    [Fact]
    public void Praise_names_the_process_not_the_person_and_hints_never_scold()
    {
        string[] forbidden = ["молодец", "умница", "талант", "лучш", "неправильно", "ошибк", "плохо", "great job", "genius", "good boy", "good girl", "wrong", "bad", "mistake", "smart"];
        foreach (string language in Languages)
        {
            foreach (string key in FeedbackKeys.Hints.Concat(FeedbackKeys.Praises))
            {
                string text = Content.Locale(language).Get(key).ToLowerInvariant();
                foreach (string word in forbidden)
                {
                    Assert.DoesNotContain(word, text, StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public void Texts_are_substituted_by_placeholder_and_unknown_keys_stay_visible()
    {
        LocaleCatalog ru = Content.Locale("ru");
        var parameters = new Dictionary<string, string> { ["part"] = ru.Get("part.bar") };

        Assert.Contains("чёрточку", ru.Get(FeedbackKeys.DeferredMissing, parameters), StringComparison.Ordinal);
        Assert.DoesNotContain('{', ru.Get(FeedbackKeys.DeferredMissing, parameters));
        Assert.Equal("no.such.key", ru.Get("no.such.key"));
        Assert.Equal("{part}", LocaleCatalog.Placeholders(ru.Strings[FeedbackKeys.DeferredMissing].Text).Select(p => "{" + p + "}").Single());
    }

    [Fact]
    public void Bad_locale_files_are_rejected()
    {
        Assert.Throws<InvalidDataException>(() => LocaleCatalog.Parse("""{ "schema": "pfl.locale/2", "lang": "ru", "strings": {} }"""));
        Assert.Throws<InvalidDataException>(() => LocaleCatalog.Parse("""{ "schema": "pfl.locale/1", "strings": {} }"""));
        Assert.Throws<InvalidDataException>(() => LocaleCatalog.Parse("""{ "schema": "pfl.locale/1", "lang": "ru", "strings": { "a": {} } }"""));
        Assert.Throws<InvalidDataException>(() => LocaleCatalog.Parse("["));
    }
}

public class TemplateGeometryTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("i")]
    [InlineData("t")]
    public void The_start_point_is_the_beginning_of_the_lead_in_and_lies_on_the_allowed_start_paths(string letter)
    {
        Glyph glyph = Content.GetGlyph(letter);

        Vec2 start = TemplateGeometry.StartPoint(glyph);

        Assert.Equal(glyph.LeadIn!.Start, start);
        Assert.True(LiveStartCheck.Clearance(glyph, start) < 1e-6);
        Assert.True(LiveStartCheck.Clearance(glyph, glyph.PenStrokes[0].Path.Start) < 1e-6);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("t")]
    public void A_start_far_from_the_lead_in_is_a_wrong_start_and_a_nearby_one_is_not(string letter)
    {
        Glyph glyph = Content.GetGlyph(letter);
        double threshold = Content.Config.LiveCue.StartClearance;
        Vec2 start = TemplateGeometry.StartPoint(glyph);

        Assert.False(LiveStartCheck.IsWrongStart(glyph, start + new Vec2(0.2, 0.1), threshold));
        Assert.True(LiveStartCheck.IsWrongStart(glyph, start + new Vec2(2.0, 1.8), threshold));
        Assert.True(LiveStartCheck.Clearance(glyph, new Vec2(10, 10)) > 5);
    }

    [Fact]
    public void A_span_resolves_to_the_requested_part_of_the_stroke()
    {
        Glyph glyph = Content.GetGlyph("a");
        GlyphPath body = glyph.FindStroke("s1")!.Path;

        ResolvedHighlight whole = TemplateGeometry.Resolve(glyph, new HighlightSpec(HighlightKind.Span, new TemplateSpan("s1", TemplatePart.Body, 0.0, 1.0)), FeedbackTone.Corrective);
        ResolvedHighlight half = TemplateGeometry.Resolve(glyph, new HighlightSpec(HighlightKind.Span, new TemplateSpan("s1", TemplatePart.Body, 0.0, 0.5)), FeedbackTone.Corrective);

        Vec2[] wholeLine = Assert.Single(whole.Polylines);
        Vec2[] halfLine = Assert.Single(half.Polylines);
        Assert.Equal(body.Start.X, wholeLine[0].X, 6);
        Assert.Equal(body.End.X, wholeLine[^1].X, 6);
        Assert.True(halfLine.Length < wholeLine.Length);
        Assert.Equal(body.PointAtFraction(0.5).Y, halfLine[^1].Y, 6);
        Assert.Null(half.StartPoint);
    }

    [Fact]
    public void A_deferred_slot_resolves_and_unknown_strokes_fall_back_to_the_whole_letter()
    {
        Glyph glyph = Content.GetGlyph("t");

        ResolvedHighlight bar = TemplateGeometry.Resolve(glyph, new HighlightSpec(HighlightKind.Span, new TemplateSpan("bar", TemplatePart.Body, 0.0, 1.0)), FeedbackTone.Corrective);
        ResolvedHighlight unknown = TemplateGeometry.Resolve(glyph, new HighlightSpec(HighlightKind.Span, new TemplateSpan("zz", TemplatePart.Body, 0.0, 1.0)), FeedbackTone.Corrective);
        ResolvedHighlight whole = TemplateGeometry.Resolve(glyph, new HighlightSpec(HighlightKind.Whole, TemplateSpan.Whole), FeedbackTone.Corrective);

        Vec2[] line = Assert.Single(bar.Polylines);
        Assert.All(line, p => Assert.Equal(1.0, p.Y, 6)); // перекладина на высоте x-height
        Assert.Equal(whole.Polylines.Count, unknown.Polylines.Count);
        Assert.True(whole.Polylines.Count >= 3); // входящий штрих, ствол, перекладина, выходящий штрих
    }

    [Fact]
    public void The_lead_in_span_and_the_start_highlight_cover_the_beginning_of_the_template()
    {
        Glyph glyph = Content.GetGlyph("a");

        ResolvedHighlight start = TemplateGeometry.Resolve(glyph, new HighlightSpec(HighlightKind.Start, new TemplateSpan("s1", TemplatePart.LeadIn, 0.0, 0.15)), FeedbackTone.Corrective);
        ResolvedHighlight leadIn = TemplateGeometry.Resolve(glyph, new HighlightSpec(HighlightKind.Span, new TemplateSpan("s1", TemplatePart.LeadIn, 0.0, 1.0)), FeedbackTone.Corrective);

        Assert.Equal(glyph.LeadIn!.Start, start.StartPoint);
        Assert.Equal(glyph.LeadIn.Start.X, start.Polylines[0][0].X, 6);
        Assert.Equal(glyph.LeadIn.End.X, leadIn.Polylines[0][^1].X, 6);
    }
}
