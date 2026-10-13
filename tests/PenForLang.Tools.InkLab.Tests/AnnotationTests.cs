using System.Collections.Immutable;
using PenForLang.Tools.InkLab.Annotation;

namespace PenForLang.Tools.InkLab.Tests;

/// <summary>Формат <c>pfl.annotation/1</c>: round-trip JSON и проверка. Записи не нужны — проверке достаточно числа точек в штрихах.</summary>
public class AnnotationTests
{
    // Слово «at»: штрих 0 — 21 точка (буква a), штрих 1 — 21 точка (ствол t), штрих 2 — 11 точек (перекладина).
    private static readonly int[] Sizes = [21, 21, 11];

    private static AnnotationFile Lifted() => new(
        AnnotationFormat.Schema,
        "x.pfl.jsonl.gz",
        "word:at",
        "A",
        "2026-10-14T12:00:00Z",
        "at",
        QualityMark.Good,
        [.. Sizes],
        [new Junction(0, JunctionKind.Lifted, new StrokePoint(0, 20), new StrokePoint(1, 0))],
        [new DeferredMark(2, 1, DeferredPartKind.Crossbar, DeferredTiming.AfterWord)]);

    private static IReadOnlyList<AnnotationIssue> Errors(AnnotationFile a, int[]? sizes = null) =>
        [.. AnnotationValidator.Validate(a, sizes ?? Sizes).Where(i => i.Severity == IssueSeverity.Error)];

    private static void AssertHasError(IReadOnlyList<AnnotationIssue> issues, string code) =>
        Assert.Contains(issues, i => i.Code == code && i.Severity == IssueSeverity.Error);

    [Fact]
    public void Json_round_trip_keeps_every_field()
    {
        AnnotationFile original = Lifted() with
        {
            Quality = new QualityMark(false, [QualityFlags.Hesitation, QualityFlags.Other], "рука дрогнула"),
        };

        string json = AnnotationJson.Serialize(original);
        AnnotationFile copy = AnnotationJson.Deserialize(json);

        Assert.Equal(original.Schema, copy.Schema);
        Assert.Equal(original.Penlog, copy.Penlog);
        Assert.Equal(original.Target, copy.Target);
        Assert.Equal(original.Annotator, copy.Annotator);
        Assert.Equal(original.AnnotatedAt, copy.AnnotatedAt);
        Assert.Equal(original.Text, copy.Text);
        Assert.False(copy.Quality.Usable);
        Assert.Equal(original.Quality.Flags, copy.Quality.Flags);
        Assert.Equal("рука дрогнула", copy.Quality.Note);
        Assert.Equal(original.StrokeSamples, copy.StrokeSamples);
        Assert.Equal(original.Junctions, copy.Junctions);
        Assert.Equal(original.Deferred, copy.Deferred);
        Assert.Equal(json, AnnotationJson.Serialize(copy)); // повторная запись даёт те же байты
    }

    [Fact]
    public void Json_uses_camel_case_names_and_enum_strings()
    {
        string json = AnnotationJson.Serialize(Lifted());

        Assert.Contains("\"schema\": \"pfl.annotation/1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"lifted\"", json, StringComparison.Ordinal);
        Assert.Contains("\"timing\": \"afterWord\"", json, StringComparison.Ordinal);
        Assert.Contains("\"strokeSamples\"", json, StringComparison.Ordinal);
        Assert.Contains("\"stroke\": 0", json, StringComparison.Ordinal);
        Assert.Contains("\"sample\": 20", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_rejects_foreign_or_broken_files()
    {
        Assert.Throws<AnnotationFormatException>(() => AnnotationJson.Deserialize("not json"));
        Assert.Throws<AnnotationFormatException>(() => AnnotationJson.Deserialize("{\"schema\":\"other/1\",\"text\":\"a\"}"));
        Assert.Throws<AnnotationFormatException>(() => AnnotationJson.Deserialize("{\"schema\":\"pfl.annotation/1\"}"));
        Assert.Throws<AnnotationFormatException>(() => AnnotationJson.Deserialize("null"));
    }

    [Fact]
    public void A_complete_lifted_annotation_is_valid()
    {
        Assert.Empty(Errors(Lifted()));
    }

    [Fact]
    public void A_connected_junction_inside_one_stroke_is_valid()
    {
        var joined = new AnnotationFile(
            AnnotationFormat.Schema, "x", "word:at", "A", null, "at", QualityMark.Good,
            [41, 11],
            [new Junction(0, JunctionKind.Connected, new StrokePoint(0, 20), new StrokePoint(0, 20))],
            [new DeferredMark(1, 1, DeferredPartKind.Crossbar, DeferredTiming.AfterWord)]);

        Assert.Empty(Errors(joined, [41, 11]));
    }

    [Fact]
    public void A_connection_stroke_between_end_and_start_is_valid()
    {
        var joined = new AnnotationFile(
            AnnotationFormat.Schema, "x", "word:at", "A", null, "at", QualityMark.Good,
            [41, 11],
            [new Junction(0, JunctionKind.Connected, new StrokePoint(0, 15), new StrokePoint(0, 25))],
            [new DeferredMark(1, 1, DeferredPartKind.Crossbar, DeferredTiming.AfterWord)]);

        Assert.Empty(Errors(joined, [41, 11]));
    }

    [Fact]
    public void The_number_of_junctions_must_equal_letters_minus_one()
    {
        AssertHasError(Errors(Lifted() with { Junctions = [] }), IssueCodes.JunctionCount);
        AssertHasError(Errors(Lifted() with { Text = "ati" }), IssueCodes.JunctionCount);
    }

    [Fact]
    public void A_single_letter_needs_no_junctions()
    {
        var i = new AnnotationFile(
            AnnotationFormat.Schema, "x", "glyph:i", "A", null, "i", QualityMark.Good, [15, 3], [],
            [new DeferredMark(1, 0, DeferredPartKind.Dot, DeferredTiming.AfterWord)]);

        Assert.Empty(Errors(i, [15, 3]));
    }

    [Fact]
    public void Empty_text_is_an_error_for_a_usable_recording_but_not_for_a_reject()
    {
        AnnotationFile empty = Lifted() with { Text = string.Empty, Junctions = [] };

        AssertHasError(Errors(empty), IssueCodes.TextEmpty);
        Assert.Empty(Errors(empty with { Quality = new QualityMark(false, [QualityFlags.WrongContent]) }));
    }

    [Fact]
    public void Whitespace_in_the_text_is_an_error()
    {
        AssertHasError(Errors(Lifted() with { Text = "a t" }), IssueCodes.TextChars);
    }

    [Fact]
    public void Junction_points_must_exist_in_the_recording()
    {
        AssertHasError(
            Errors(Lifted() with { Junctions = [new Junction(0, JunctionKind.Lifted, new StrokePoint(0, 99), new StrokePoint(1, 0))] }),
            IssueCodes.PointRange);
        AssertHasError(
            Errors(Lifted() with { Junctions = [new Junction(0, JunctionKind.Lifted, new StrokePoint(5, 0), new StrokePoint(1, 0))] }),
            IssueCodes.PointRange);
        AssertHasError(
            Errors(Lifted() with { Junctions = [new Junction(0, JunctionKind.Connected, new StrokePoint(0, -1), new StrokePoint(0, 3))] }),
            IssueCodes.PointRange);
    }

    [Fact]
    public void Boundaries_must_be_monotone_along_the_trace()
    {
        // Три буквы: стык 0|1 стоит позже стыка 1|2.
        var three = new AnnotationFile(
            AnnotationFormat.Schema, "x", null, "A", null, "ati", QualityMark.Good, [60],
            [
                new Junction(0, JunctionKind.Connected, new StrokePoint(0, 40), new StrokePoint(0, 40)),
                new Junction(1, JunctionKind.Connected, new StrokePoint(0, 20), new StrokePoint(0, 20)),
            ],
            []);

        AssertHasError(Errors(three, [60]), IssueCodes.NotMonotone);
        Assert.Empty(Errors(
            three with
            {
                Junctions =
                [
                    new Junction(0, JunctionKind.Connected, new StrokePoint(0, 20), new StrokePoint(0, 20)),
                    new Junction(1, JunctionKind.Connected, new StrokePoint(0, 40), new StrokePoint(0, 40)),
                ],
            },
            [60]));
    }

    [Fact]
    public void The_end_of_the_left_letter_must_not_be_after_the_start_of_the_right_letter()
    {
        var joined = new AnnotationFile(
            AnnotationFormat.Schema, "x", null, "A", null, "at", QualityMark.Good, [41],
            [new Junction(0, JunctionKind.Connected, new StrokePoint(0, 25), new StrokePoint(0, 15))],
            []);

        AssertHasError(Errors(joined, [41]), IssueCodes.NotMonotone);
    }

    [Fact]
    public void A_letter_must_not_be_empty()
    {
        var atStart = new AnnotationFile(
            AnnotationFormat.Schema, "x", null, "A", null, "at", QualityMark.Good, [41],
            [new Junction(0, JunctionKind.Connected, new StrokePoint(0, 0), new StrokePoint(0, 0))],
            []);
        var atEnd = atStart with { Junctions = [new Junction(0, JunctionKind.Connected, new StrokePoint(0, 40), new StrokePoint(0, 40))] };

        AssertHasError(Errors(atStart, [41]), IssueCodes.EmptyLetter);
        AssertHasError(Errors(atEnd, [41]), IssueCodes.EmptyLetter);
    }

    [Fact]
    public void A_connected_junction_across_two_strokes_is_an_error()
    {
        AssertHasError(
            Errors(Lifted() with { Junctions = [new Junction(0, JunctionKind.Connected, new StrokePoint(0, 20), new StrokePoint(1, 0))] }),
            IssueCodes.ConnectedAcrossStrokes);
    }

    [Fact]
    public void A_lifted_junction_must_sit_at_the_end_of_a_stroke_and_the_start_of_the_next_main_stroke()
    {
        // Конец не в последней точке штриха.
        AssertHasError(
            Errors(Lifted() with { Junctions = [new Junction(0, JunctionKind.Lifted, new StrokePoint(0, 10), new StrokePoint(1, 0))] }),
            IssueCodes.LiftGeometry);

        // Начало не в первой точке.
        AssertHasError(
            Errors(Lifted() with { Junctions = [new Junction(0, JunctionKind.Lifted, new StrokePoint(0, 20), new StrokePoint(1, 3))] }),
            IssueCodes.LiftGeometry);

        // Начало не в следующем основном штрихе (штрих 1 пропущен).
        AssertHasError(
            Errors(Lifted() with
            {
                Deferred = [],
                Junctions = [new Junction(0, JunctionKind.Lifted, new StrokePoint(0, 20), new StrokePoint(2, 0))],
            }),
            IssueCodes.LiftGeometry);
    }

    [Fact]
    public void A_lifted_junction_skips_deferred_strokes_when_looking_for_the_next_main_stroke()
    {
        // Штрих 1 — отложенная точка (например, i), буква справа начинается со штриха 2.
        var a = new AnnotationFile(
            AnnotationFormat.Schema, "x", null, "A", null, "ia", QualityMark.Good, [15, 3, 20],
            [new Junction(0, JunctionKind.Lifted, new StrokePoint(0, 14), new StrokePoint(2, 0))],
            [new DeferredMark(1, 0, DeferredPartKind.Dot, DeferredTiming.Immediate)]);

        Assert.Empty(Errors(a, [15, 3, 20]));
    }

    [Fact]
    public void A_junction_on_a_deferred_stroke_is_an_error()
    {
        var a = Lifted() with
        {
            Junctions = [new Junction(0, JunctionKind.Connected, new StrokePoint(2, 5), new StrokePoint(2, 5))],
        };

        AssertHasError(Errors(a), IssueCodes.JunctionOnDeferred);
    }

    [Fact]
    public void Deferred_marks_must_point_at_existing_strokes_and_letters_without_repeats()
    {
        AssertHasError(
            Errors(Lifted() with { Deferred = [new DeferredMark(7, 1, DeferredPartKind.Crossbar, DeferredTiming.AfterWord)] }),
            IssueCodes.DeferredRange);
        AssertHasError(
            Errors(Lifted() with { Deferred = [new DeferredMark(2, 5, DeferredPartKind.Crossbar, DeferredTiming.AfterWord)] }),
            IssueCodes.DeferredLetter);
        AssertHasError(
            Errors(Lifted() with
            {
                Deferred =
                [
                    new DeferredMark(2, 1, DeferredPartKind.Crossbar, DeferredTiming.AfterWord),
                    new DeferredMark(2, 1, DeferredPartKind.Dot, DeferredTiming.Immediate),
                ],
            }),
            IssueCodes.DeferredDuplicate);
    }

    [Fact]
    public void Marking_every_stroke_as_deferred_leaves_no_main_trace()
    {
        var all = Lifted() with
        {
            Deferred =
            [
                new DeferredMark(0, 0, DeferredPartKind.Other, DeferredTiming.AfterWord),
                new DeferredMark(1, 1, DeferredPartKind.Other, DeferredTiming.AfterWord),
                new DeferredMark(2, 1, DeferredPartKind.Other, DeferredTiming.AfterWord),
            ],
        };

        AssertHasError(Errors(all), IssueCodes.NoMainTrace);
    }

    [Fact]
    public void A_recording_that_changed_after_annotation_is_reported()
    {
        AssertHasError(Errors(Lifted(), [21, 22, 11]), IssueCodes.StrokeMismatch);
        AssertHasError(Errors(Lifted(), [21, 21]), IssueCodes.StrokeMismatch);
    }

    [Fact]
    public void Junction_indexes_must_be_consecutive_from_zero()
    {
        var three = new AnnotationFile(
            AnnotationFormat.Schema, "x", null, "A", null, "ati", QualityMark.Good, [60],
            [
                new Junction(0, JunctionKind.Connected, new StrokePoint(0, 20), new StrokePoint(0, 20)),
                new Junction(0, JunctionKind.Connected, new StrokePoint(0, 40), new StrokePoint(0, 40)),
            ],
            []);

        AssertHasError(Errors(three, [60]), IssueCodes.JunctionIndex);
    }

    [Fact]
    public void Unknown_quality_flags_are_a_warning_not_an_error()
    {
        AnnotationFile a = Lifted() with { Quality = new QualityMark(true, ["strange"]) };

        IReadOnlyList<AnnotationIssue> issues = AnnotationValidator.Validate(a, Sizes);

        Assert.Contains(issues, i => i.Code == IssueCodes.UnknownFlag && i.Severity == IssueSeverity.Warning);
        Assert.True(AnnotationValidator.IsValid(issues));
    }

    [Fact]
    public void Foreign_schema_is_an_error()
    {
        AssertHasError(Errors(Lifted() with { Schema = "pfl.annotation/2" }), IssueCodes.Schema);
    }

    [Fact]
    public void Stroke_points_compare_by_stroke_then_sample()
    {
        Assert.True(new StrokePoint(0, 40) < new StrokePoint(1, 0));
        Assert.True(new StrokePoint(1, 3) > new StrokePoint(1, 2));
        Assert.True(new StrokePoint(2, 2) <= new StrokePoint(2, 2));
        ImmutableArray<StrokePoint> sorted = [.. new[] { new StrokePoint(1, 0), new StrokePoint(0, 9), new StrokePoint(0, 1) }.Order()];
        Assert.Equal(new StrokePoint(0, 1), sorted[0]);
        Assert.Equal(new StrokePoint(1, 0), sorted[2]);
    }
}
