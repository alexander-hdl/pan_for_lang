using System.Collections.Immutable;
using PenForLang.Ink;
using PenForLang.Tools.InkLab.Annotation;
using PenForLang.Tools.InkLab.Tests.Support;

namespace PenForLang.Tools.InkLab.Tests;

/// <summary>Формат <c>pfl.annotation/1</c>: round-trip JSON и проверка (привязка к записи, соглашение о стыках, предупреждения).</summary>
public class AnnotationTests
{
    // Слово «at»: штрих 0 — 21 точка (буква a), штрих 1 — 21 точка (ствол t), штрих 2 — 11 точек (перекладина).
    private static readonly int[] Sizes = [21, 21, 11];

    private static PointRef P(int stroke, int sample) => new(stroke, sample, TestData.Time(stroke, sample));

    private static AnnotationFile Make(
        int[] sizes,
        string text,
        IEnumerable<Junction> junctions,
        IEnumerable<DeferredMark>? deferred = null,
        QualityMark? quality = null) => new(
            AnnotationFormat.Schema,
            "x.pfl.jsonl.gz",
            TestData.Sha,
            AnnotationFormat.Indexing,
            "word:at",
            "lift",
            "A",
            "2026-10-14T12:00:00Z",
            "InkLab 0.1.0",
            text,
            quality ?? QualityMark.Good,
            StrokeInfo.Of(TestData.StrokesOf(sizes)),
            [.. junctions],
            [.. deferred ?? []]);

    private static DeferredMark Crossbar(int stroke = 2, int letter = 1) =>
        new(stroke, letter, DeferredPartKind.Crossbar, DeferredTiming.AfterWord);

    /// <summary>Отрыв пера между a и t: выход a — конец штриха 0, вход t — начало штриха 1; перекладина — отложенный штрих 2.</summary>
    private static AnnotationFile Lifted() =>
        Make(Sizes, "at", [new Junction(0, JunctionKind.Lifted, P(0, 20), P(1, 0))], [Crossbar()]);

    private static IReadOnlyList<AnnotationIssue> Issues(AnnotationFile a, int[]? sizes = null) =>
        AnnotationValidator.Validate(a, TestData.StrokesOf(sizes ?? Sizes), TestData.Sha);

    private static IReadOnlyList<AnnotationIssue> Errors(AnnotationFile a, int[]? sizes = null) =>
        [.. Issues(a, sizes).Where(i => i.Severity == IssueSeverity.Error)];

    private static void AssertHasError(IReadOnlyList<AnnotationIssue> issues, string code) =>
        Assert.Contains(issues, i => i.Code == code && i.Severity == IssueSeverity.Error);

    private static void AssertHasWarning(IReadOnlyList<AnnotationIssue> issues, string code) =>
        Assert.Contains(issues, i => i.Code == code && i.Severity == IssueSeverity.Warning);

    [Fact]
    public void Json_round_trip_keeps_every_field()
    {
        AnnotationFile original = Lifted() with
        {
            Quality = new QualityMark(false, [QualityFlags.Hesitation, QualityFlags.Other], "рука дрогнула"),
            Junctions = [new Junction(0, JunctionKind.Lifted, P(0, 20), P(1, 0), Confidence.Low)],
            Deferred = [new DeferredMark(2, 1, DeferredPartKind.Crossbar, DeferredTiming.AfterWord, DeferredTiming.Immediate)],
        };

        string json = AnnotationJson.Serialize(original);
        AnnotationFile copy = AnnotationJson.Deserialize(json);

        Assert.Equal(original.Schema, copy.Schema);
        Assert.Equal(original.Penlog, copy.Penlog);
        Assert.Equal(original.PenlogSha256, copy.PenlogSha256);
        Assert.Equal(original.Indexing, copy.Indexing);
        Assert.Equal(original.Target, copy.Target);
        Assert.Equal(original.Variant, copy.Variant);
        Assert.Equal(original.Annotator, copy.Annotator);
        Assert.Equal(original.AnnotatedAt, copy.AnnotatedAt);
        Assert.Equal(original.Tool, copy.Tool);
        Assert.Equal(original.Text, copy.Text);
        Assert.False(copy.Quality.Usable);
        Assert.Equal(original.Quality.Flags, copy.Quality.Flags);
        Assert.Equal("рука дрогнула", copy.Quality.Note);
        Assert.Equal(original.Strokes, copy.Strokes);
        Assert.Equal(original.Junctions, copy.Junctions);
        Assert.Equal(original.Deferred, copy.Deferred);
        Assert.Equal(json, AnnotationJson.Serialize(copy)); // повторная запись даёт те же байты
    }

    [Fact]
    public void Json_uses_camel_case_names_and_enum_strings_and_points_carry_time()
    {
        string json = AnnotationJson.Serialize(Lifted());

        Assert.Contains("\"schema\": \"pfl.annotation/1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"indexing\": \"inkattempt-after-last-restart\"", json, StringComparison.Ordinal);
        Assert.Contains("\"penlogSha256\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"lifted\"", json, StringComparison.Ordinal);
        Assert.Contains("\"confidence\": \"high\"", json, StringComparison.Ordinal);
        Assert.Contains("\"timing\": \"afterWord\"", json, StringComparison.Ordinal);
        Assert.Contains("\"t\": 20000", json, StringComparison.Ordinal);
        Assert.Contains("\"n\": 21", json, StringComparison.Ordinal);
        Assert.DoesNotContain("timingOpinion", json, StringComparison.Ordinal);
        Assert.DoesNotContain("strokeSamples", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_reading_is_versioned_and_strict()
    {
        string valid = AnnotationJson.Serialize(Lifted());

        Assert.Throws<AnnotationFormatException>(() => AnnotationJson.Deserialize("not json"));
        Assert.Throws<AnnotationFormatException>(() => AnnotationJson.Deserialize("null"));
        Assert.Throws<AnnotationFormatException>(() => AnnotationJson.Deserialize("[]"));
        Assert.Throws<AnnotationFormatException>(() => AnnotationJson.Deserialize("{\"text\":\"a\"}")); // нет schema

        AnnotationFormatException version = Assert.Throws<AnnotationFormatException>(
            () => AnnotationJson.Deserialize(valid.Replace("pfl.annotation/1", "pfl.annotation/2", StringComparison.Ordinal)));
        Assert.Contains("pfl.annotation/2", version.Message, StringComparison.Ordinal);
        Assert.Contains("pfl.annotation/1", version.Message, StringComparison.Ordinal);

        // Неизвестное поле внутри версии 1 — ошибка, а не молчаливая потеря данных.
        Assert.Throws<AnnotationFormatException>(
            () => AnnotationJson.Deserialize(valid.Replace("\"tool\":", "\"extra\": 1, \"tool\":", StringComparison.Ordinal)));
        Assert.Throws<AnnotationFormatException>(() => AnnotationJson.Deserialize("{\"schema\":\"pfl.annotation/1\"}"));
    }

    [Fact]
    public void A_complete_lifted_annotation_is_valid()
    {
        Assert.Empty(Errors(Lifted()));
    }

    [Fact]
    public void A_lifted_junction_may_have_its_points_anywhere_inside_their_strokes()
    {
        // F2: отрыв — это отрыв пера между выходом и входом; выход не обязан быть в последней точке штриха, вход — в первой.
        AnnotationFile a = Lifted() with { Junctions = [new Junction(0, JunctionKind.Lifted, P(0, 15), P(1, 5))] };

        Assert.Empty(Errors(a));
    }

    [Fact]
    public void A_connected_junction_inside_one_stroke_is_valid_with_or_without_a_connecting_stroke()
    {
        int[] sizes = [41, 11];
        DeferredMark bar = Crossbar(1);

        Assert.Empty(Errors(Make(sizes, "at", [new Junction(0, JunctionKind.Connected, P(0, 20), P(0, 20))], [bar]), sizes));
        Assert.Empty(Errors(Make(sizes, "at", [new Junction(0, JunctionKind.Connected, P(0, 15), P(0, 25))], [bar]), sizes));
    }

    [Fact]
    public void Kind_must_follow_the_strokes_connected_means_one_stroke_lifted_means_a_pen_lift()
    {
        AssertHasError(
            Errors(Lifted() with { Junctions = [new Junction(0, JunctionKind.Connected, P(0, 20), P(1, 0))] }),
            IssueCodes.ConnectedAcrossStrokes);
        AssertHasError(
            Errors(Lifted() with { Junctions = [new Junction(0, JunctionKind.Lifted, P(0, 10), P(0, 15))] }),
            IssueCodes.LiftGeometry);
        Assert.Equal(JunctionKind.Connected, Junction.KindOf(new StrokePoint(0, 1), new StrokePoint(0, 9)));
        Assert.Equal(JunctionKind.Lifted, Junction.KindOf(new StrokePoint(0, 1), new StrokePoint(2, 0)));
    }

    [Fact]
    public void A_lift_must_not_hide_a_main_stroke_between_exit_and_entry_but_deferred_strokes_may_lie_there()
    {
        int[] sizes = [15, 3, 20];

        // Штрих 1 — точка i (отложенный): между выходом i и входом a лежит только он.
        AnnotationFile withDot = Make(
            sizes, "ia", [new Junction(0, JunctionKind.Lifted, P(0, 14), P(2, 0))], [new DeferredMark(1, 0, DeferredPartKind.Dot, DeferredTiming.Immediate)]);
        Assert.Empty(Errors(withDot, sizes));

        // Тот же стык, но штрих 1 основной: он принадлежал бы какой-то букве.
        AssertHasError(Errors(withDot with { Deferred = [] }, sizes), IssueCodes.LiftGeometry);
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
        int[] sizes = [15, 3];
        AnnotationFile i = Make(sizes, "i", [], [new DeferredMark(1, 0, DeferredPartKind.Dot, DeferredTiming.AfterWord)]);

        Assert.Empty(Errors(i, sizes));
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
    public void Junction_points_must_exist_in_the_recording_with_the_recording_time()
    {
        AssertHasError(
            Errors(Lifted() with { Junctions = [new Junction(0, JunctionKind.Lifted, new PointRef(0, 99, 0), P(1, 0))] }),
            IssueCodes.PointRange);
        AssertHasError(
            Errors(Lifted() with { Junctions = [new Junction(0, JunctionKind.Lifted, new PointRef(5, 0, 0), P(1, 0))] }),
            IssueCodes.PointRange);

        // Номер верный, а время точки не то: запись другая или перенумерована.
        AssertHasError(
            Errors(Lifted() with { Junctions = [new Junction(0, JunctionKind.Lifted, new PointRef(0, 20, 123), P(1, 0))] }),
            IssueCodes.PointTime);
    }

    [Fact]
    public void The_recording_hash_must_be_well_formed_and_match_the_recording()
    {
        AssertHasError(Errors(Lifted() with { PenlogSha256 = "abc" }), IssueCodes.HashFormat);
        AssertHasError(Errors(Lifted() with { PenlogSha256 = new string('f', 64) }), IssueCodes.HashMismatch);

        // Без хеша записи (проверка без файла) совпадение не проверяется.
        Assert.DoesNotContain(
            AnnotationValidator.Validate(Lifted() with { PenlogSha256 = new string('f', 64) }, TestData.StrokesOf(Sizes)),
            i => i.Code == IssueCodes.HashMismatch);
    }

    [Fact]
    public void Indexing_and_schema_must_be_the_supported_ones()
    {
        AssertHasError(Errors(Lifted() with { Indexing = "all-strokes" }), IssueCodes.Indexing);
        AssertHasError(Errors(Lifted() with { Schema = "pfl.annotation/2" }), IssueCodes.Schema);
    }

    [Fact]
    public void Boundaries_must_be_monotone_along_the_trace()
    {
        int[] sizes = [60];
        AnnotationFile three = Make(
            sizes, "ati",
            [
                new Junction(0, JunctionKind.Connected, P(0, 40), P(0, 40)),
                new Junction(1, JunctionKind.Connected, P(0, 20), P(0, 20)),
            ]);

        AssertHasError(Errors(three, sizes), IssueCodes.NotMonotone);
        Assert.Empty(Errors(
            three with
            {
                Junctions =
                [
                    new Junction(0, JunctionKind.Connected, P(0, 20), P(0, 20)),
                    new Junction(1, JunctionKind.Connected, P(0, 40), P(0, 40)),
                ],
            },
            sizes));
    }

    [Fact]
    public void The_exit_of_the_left_letter_must_not_be_after_the_entry_of_the_right_letter()
    {
        int[] sizes = [41];
        AnnotationFile joined = Make(sizes, "at", [new Junction(0, JunctionKind.Connected, P(0, 25), P(0, 15))]);

        AssertHasError(Errors(joined, sizes), IssueCodes.NotMonotone);
    }

    [Fact]
    public void A_letter_must_not_be_empty()
    {
        int[] sizes = [41];
        AnnotationFile atStart = Make(sizes, "at", [new Junction(0, JunctionKind.Connected, P(0, 0), P(0, 0))]);
        AnnotationFile atEnd = Make(sizes, "at", [new Junction(0, JunctionKind.Connected, P(0, 40), P(0, 40))]);

        AssertHasError(Errors(atStart, sizes), IssueCodes.EmptyLetter);
        AssertHasError(Errors(atEnd, sizes), IssueCodes.EmptyLetter);
    }

    [Fact]
    public void A_junction_on_a_deferred_stroke_is_an_error()
    {
        AnnotationFile a = Lifted() with
        {
            Junctions = [new Junction(0, JunctionKind.Connected, P(2, 5), P(2, 5))],
        };

        AssertHasError(Errors(a), IssueCodes.JunctionOnDeferred);
    }

    [Fact]
    public void Deferred_marks_must_point_at_existing_strokes_and_letters_without_repeats()
    {
        AssertHasError(Errors(Lifted() with { Deferred = [Crossbar(stroke: 7)] }), IssueCodes.DeferredRange);
        AssertHasError(Errors(Lifted() with { Deferred = [Crossbar(letter: 5)] }), IssueCodes.DeferredLetter);
        AssertHasError(
            Errors(Lifted() with { Deferred = [Crossbar(), new DeferredMark(2, 1, DeferredPartKind.Dot, DeferredTiming.AfterWord)] }),
            IssueCodes.DeferredDuplicate);
    }

    [Fact]
    public void Marking_every_stroke_as_deferred_leaves_no_main_trace()
    {
        AnnotationFile all = Lifted() with
        {
            Deferred = [Crossbar(0, 0), Crossbar(1, 1), Crossbar(2, 1)],
        };

        AssertHasError(Errors(all), IssueCodes.NoMainTrace);
    }

    [Fact]
    public void The_strokes_of_the_recording_must_match_the_ones_stored_in_the_annotation()
    {
        AssertHasError(Errors(Lifted(), [21, 22, 11]), IssueCodes.StrokeMismatch);
        AssertHasError(Errors(Lifted(), [21, 21]), IssueCodes.StrokeMismatch);

        // Число точек то же, а времена другие (запись перезаписана).
        AssertHasError(
            AnnotationValidator.Validate(Lifted(), TestData.Strokes(TestData.AtWordLifted()), TestData.Sha),
            IssueCodes.StrokeMismatch);
    }

    [Fact]
    public void Junction_indexes_must_be_consecutive_from_zero()
    {
        int[] sizes = [60];
        AnnotationFile three = Make(
            sizes, "ati",
            [
                new Junction(0, JunctionKind.Connected, P(0, 20), P(0, 20)),
                new Junction(0, JunctionKind.Connected, P(0, 40), P(0, 40)),
            ]);

        AssertHasError(Errors(three, sizes), IssueCodes.JunctionIndex);
    }

    [Fact]
    public void Unknown_quality_flags_and_a_wrong_content_flag_on_a_usable_recording_are_warnings()
    {
        AnnotationFile a = Lifted() with { Quality = new QualityMark(true, ["strange", QualityFlags.WrongContent]) };

        IReadOnlyList<AnnotationIssue> issues = Issues(a);

        AssertHasWarning(issues, IssueCodes.UnknownFlag);
        AssertHasWarning(issues, IssueCodes.QualityConsistency);
        Assert.True(AnnotationValidator.IsValid(issues));
    }

    [Fact]
    public void A_letter_that_needs_a_dot_or_a_crossbar_without_one_gets_a_warning()
    {
        // F3: t без перекладины, i без точки.
        IReadOnlyList<AnnotationIssue> noBar = Issues(Lifted() with { Deferred = [] });
        AssertHasWarning(noBar, IssueCodes.DeferredMissing);
        Assert.True(AnnotationValidator.IsValid(noBar));

        int[] sizes = [15];
        AnnotationFile i = Make(sizes, "i", []);
        AssertHasWarning(AnnotationValidator.Validate(i, TestData.StrokesOf(sizes), TestData.Sha), IssueCodes.DeferredMissing);

        // Буква a точки не требует.
        AnnotationFile a = Make([21], "a", []);
        Assert.DoesNotContain(AnnotationValidator.Validate(a, TestData.StrokesOf(21), TestData.Sha), x => x.Code == IssueCodes.DeferredMissing);
    }

    [Fact]
    public void A_dot_on_a_t_or_a_crossbar_on_an_i_gets_a_kind_warning()
    {
        AnnotationFile wrong = Lifted() with { Deferred = [new DeferredMark(2, 1, DeferredPartKind.Dot, DeferredTiming.AfterWord)] };

        AssertHasWarning(Issues(wrong), IssueCodes.DeferredKind);
        Assert.DoesNotContain(Issues(Lifted()), x => x.Code == IssueCodes.DeferredKind);
    }

    [Fact]
    public void Deferred_timing_is_checked_against_the_stroke_order_and_the_opinion_is_a_warning()
    {
        // Перекладина (штрих 2) написана последней: «после слова». Записано «сразу» — устарело.
        AnnotationFile stale = Lifted() with { Deferred = [new DeferredMark(2, 1, DeferredPartKind.Crossbar, DeferredTiming.Immediate)] };
        AssertHasWarning(Issues(stale), IssueCodes.TimingStale);

        AnnotationFile opinion = Lifted() with
        {
            Deferred = [new DeferredMark(2, 1, DeferredPartKind.Crossbar, DeferredTiming.AfterWord, DeferredTiming.Immediate)],
        };
        AssertHasWarning(Issues(opinion), IssueCodes.TimingOpinion);
        Assert.DoesNotContain(Issues(Lifted()), x => x.Code is IssueCodes.TimingStale or IssueCodes.TimingOpinion);
    }

    [Fact]
    public void Derived_timing_is_immediate_when_a_main_stroke_follows()
    {
        Assert.True(AnnotationFile.IsImmediate(0, Sizes, new HashSet<int> { 0 }));
        Assert.False(AnnotationFile.IsImmediate(2, Sizes, new HashSet<int> { 2 }));
        Assert.False(AnnotationFile.IsImmediate(1, Sizes, new HashSet<int> { 1, 2 }));
    }

    [Fact]
    public void Expected_deferred_kind_follows_the_letter()
    {
        Assert.Equal(DeferredPartKind.Dot, AnnotationValidator.ExpectedDeferredKind('i'));
        Assert.Equal(DeferredPartKind.Dot, AnnotationValidator.ExpectedDeferredKind('J'));
        Assert.Equal(DeferredPartKind.Crossbar, AnnotationValidator.ExpectedDeferredKind('t'));
        Assert.Null(AnnotationValidator.ExpectedDeferredKind('a'));
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

    [Fact]
    public void Point_refs_are_built_from_the_strokes_with_their_time()
    {
        ImmutableArray<InkStroke> strokes = TestData.StrokesOf(5, 5);

        Assert.Equal(new PointRef(1, 3, 1_003_000), PointRef.Of(strokes, new StrokePoint(1, 3)));
        Assert.Null(PointRef.Of(strokes, new StrokePoint(1, 9)));
        Assert.Null(PointRef.Of(strokes, new StrokePoint(-1, 0)));
    }
}
