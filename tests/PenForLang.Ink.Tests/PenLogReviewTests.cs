using System.Globalization;
using System.Text;
using static PenForLang.Ink.Tests.PenLogTests;
using static PenForLang.Ink.Tests.TestData;

namespace PenForLang.Ink.Tests;

/// <summary>Проверки по ревью V0.3: согласие, xHeightMm, пропуск неизвестных записей, перезапуск, культуры.</summary>
public class PenLogReviewTests
{
    [Fact]
    public void Header_xHeightMm_is_checked_against_pixels_with_a_tolerance_and_may_be_absent()
    {
        // 112.5 / 7.5 = 15; допуск покрывает округление при ручной записи.
        PenLog tolerated = ReadFromText(HeaderLine.Replace("\"xHeightMm\":15", "\"xHeightMm\":15.0000001", StringComparison.Ordinal) + "\n");
        Assert.Equal(15, tolerated.Header.Guides.XHeightMm, 9);

        PenLog absent = ReadFromText(HeaderLine.Replace("\"xHeightMm\":15,", string.Empty, StringComparison.Ordinal) + "\n");
        Assert.Equal(112.5, absent.Header.Guides.XHeightPx);

        PenLogFormatException ex = ReadFails(HeaderLine.Replace("\"xHeightMm\":15", "\"xHeightMm\":16", StringComparison.Ordinal) + "\n");
        Assert.Contains("xHeightMm", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Child_recording_without_consent_is_rejected_by_writer_and_reader()
    {
        PenLogHeader child = Header(subject: new PenLogSubject("p-1", 8, PenSubjectKind.Child));
        using var writer = new PenLogWriter(new MemoryStream());
        Assert.Throws<ArgumentException>(() => writer.WriteHeader(child));

        const string SubjectJson = ",\"subject\":{\"anon\":\"p-1\",\"kind\":\"child\"}";
        string withoutConsent = HeaderLine.Replace(",\"qpcFreq\"", SubjectJson + ",\"qpcFreq\"", StringComparison.Ordinal);
        PenLogFormatException ex = ReadFails(withoutConsent + "\n");
        Assert.Contains("consent", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, ex.LineNumber);

        // С согласием — можно; взрослому согласие не нужно.
        string withConsent = withoutConsent.Replace(
            ",\"qpcFreq\"",
            ",\"consent\":{\"id\":1,\"kind\":\"trajectory_recording\",\"version\":\"1.0\"},\"qpcFreq\"",
            StringComparison.Ordinal);
        Assert.Equal(PenSubjectKind.Child, ReadFromText(withConsent + "\n").Header.Subject!.Kind);
        RoundTrip(Log(Header(subject: new PenLogSubject("a-1", null, PenSubjectKind.Adult))));
    }

    [Fact]
    public void Writer_rejects_null_text_fields_in_optional_header_sections()
    {
        using var writer = new PenLogWriter(new MemoryStream());

        Assert.Throws<ArgumentNullException>(() => writer.WriteHeader(Header(task: new PenLogTask(null!, "E1", "t", "L0", "m", "right"))));
        Assert.Throws<ArgumentNullException>(() => writer.WriteHeader(Header(subject: new PenLogSubject(null!, null, PenSubjectKind.Adult))));
        Assert.Throws<ArgumentNullException>(() => writer.WriteHeader(Header(consent: new PenLogConsent(1, null!, "1"))));
    }

    [Fact]
    public void Unknown_record_types_are_skipped_and_counted_and_their_time_is_ignored()
    {
        PenLog log = ReadFromText(
            HeaderLine + "\n"
            + """{"type":"ev","t":10,"name":"a"}""" + "\n"
            + """{"type":"future","t":1,"payload":[1,2,3]}""" + "\n"
            + """{"type":"calib","t":-5}""" + "\n"
            + """{"type":"ev","t":20,"name":"b"}""" + "\n");

        Assert.Equal(2, log.Entries.Length);
        Assert.Equal(2, log.SkippedRecords);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(HeaderLine + "\n" + """{"type":"zzz"}""" + "\n"));
        using var reader = new PenLogReader(stream);
        reader.ReadHeader();
        Assert.Null(reader.ReadEntry());
        Assert.Equal(1, reader.SkippedRecordCount);

        // Но запись без типа — по-прежнему ошибка формата.
        ReadFails(HeaderLine + "\n" + """{"t":1}""" + "\n");
    }

    [Fact]
    public void Nested_event_values_are_kept_as_raw_json_and_nulls_are_skipped()
    {
        PenLog log = ReadFromText(
            HeaderLine + "\n" + """{"type":"ev","t":1,"name":"a","obj":{"k":[1,2],"z":"q"},"arr":[1,"b"],"nil":null,"s":"x"}""" + "\n");

        InkEvent ev = log.Events.Single();
        Assert.Equal("""{"k":[1,2],"z":"q"}""", ev.Data!["obj"]);
        Assert.Equal("""[1,"b"]""", ev.Data["arr"]);
        Assert.Equal("x", ev.Data["s"]);
        Assert.False(ev.Data.ContainsKey("nil"));

        // Повторная запись и чтение дают то же значение (сырой JSON хранится строкой).
        RoundTrip(log);
    }

    [Fact]
    public void ToInkAttempt_keeps_only_strokes_started_after_the_last_restart_by_default()
    {
        PenLog log = Log(
            Header(),
            Sample(100, 0, 0, PenPhase.Down),
            Sample(200, 1, 1, PenPhase.Up),
            Event(300, "restart-button"),
            Sample(400, 5, 5, PenPhase.Down),
            Sample(500, 6, 6, PenPhase.Up),
            Event(600, "restart-button"),
            Sample(700, 9, 9, PenPhase.Down),
            Sample(800, 10, 10, PenPhase.Up),
            Event(900, "completed", ("by", "done-button")));

        InkAttempt attempt = log.ToInkAttempt();

        Assert.Single(attempt.Strokes);
        Assert.Equal(0, attempt.Strokes[0].Index);
        Assert.Equal(9, attempt.Strokes[0].Samples[0].X);
        Assert.Equal(2, attempt.Restarts);
        Assert.Equal(3, attempt.Events.Length); // события сохраняются целиком
        Assert.Equal(InkCompletion.DoneButton, attempt.Completion);

        InkAttempt full = log.ToInkAttempt(includeStrokesBeforeRestart: true);
        Assert.Equal(3, full.Strokes.Length);
        Assert.Equal([0, 1, 2], full.Strokes.Select(s => s.Index));
        Assert.Equal(2, full.Restarts);
    }

    [Fact]
    public void ToInkAttempt_without_restarts_is_unchanged_and_restart_at_the_end_leaves_no_strokes_by_default()
    {
        PenLog plain = Log(Header(), Sample(1, 0, 0, PenPhase.Down), Sample(2, 1, 1, PenPhase.Up));
        Assert.Single(plain.ToInkAttempt().Strokes);

        PenLog restartedAtEnd = Log(Header(), Sample(1, 0, 0, PenPhase.Down), Sample(2, 1, 1, PenPhase.Up), Event(3, "restart-button"));
        Assert.Empty(restartedAtEnd.ToInkAttempt().Strokes);
        Assert.Single(restartedAtEnd.ToInkAttempt(true).Strokes);
    }

    public static TheoryData<string> Cultures => new() { "ru-RU", "de-DE", "fr-FR", "minus-u2212", "ar-SA" };

    private static CultureInfo CultureFor(string name)
    {
        if (name != "minus-u2212")
        {
            return CultureInfo.GetCultureInfo(name);
        }

        // Культура с типографским минусом U+2212 и запятой: ломает наивный ToString/Parse.
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("ru-RU").Clone();
        culture.NumberFormat.NegativeSign = "−";
        culture.NumberFormat.NumberDecimalSeparator = ",";
        culture.NumberFormat.PositiveSign = "➕";
        return culture;
    }

    [Fact]
    public void Test_project_runs_with_real_globalization_so_culture_tests_are_meaningful()
    {
        Assert.Equal("−", CultureFor("minus-u2212").NumberFormat.NegativeSign);
        Assert.Equal(",", CultureInfo.GetCultureInfo("ru-RU").NumberFormat.NumberDecimalSeparator);
        Assert.Equal("1,5", 1.5.ToString(CultureInfo.GetCultureInfo("ru-RU")));
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void Round_trip_is_independent_of_the_current_culture(string cultureName)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        CultureInfo originalUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo culture = CultureFor(cultureName);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;

            PenLog log = Log(
                FullHeader(),
                Event(0, "e", ("n", "-42"), ("f", "-1.5"), ("big", "123456789012")),
                Sample(1, -3.5, 1234.5678, PenPhase.Down, pressure: 0.3125f, tiltX: -12.5f, tiltY: 8.25f),
                Sample(2, -0.001, -1e-7, flags: SampleFlags.Clipped),
                Sample(3, 1e21, 123456789.123456789, PenPhase.Up));

            byte[] bytes = WriteToBytes(log);
            string text = Encoding.UTF8.GetString(bytes);
            Assert.DoesNotContain("−", text, StringComparison.Ordinal);
            Assert.Contains("\"x\":-3.5,", text, StringComparison.Ordinal);

            AssertLogsEqual(log, ReadFromBytes(bytes));
            InkEvent ev = log.Events.Single();
            Assert.True(ev.TryGetInt64("n", out long n));
            Assert.Equal(-42, n);
            Assert.True(ev.TryGetDouble("f", out double f));
            Assert.Equal(-1.5, f);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
            CultureInfo.CurrentUICulture = originalUi;
        }
    }

    [Fact]
    public void InkEvent_typed_accessors_use_invariant_parsing_and_reject_garbage()
    {
        InkEvent ev = Event(1, "e", ("i", "3100"), ("d", "2.5"), ("bad", "1,5"), ("word", "abc"), ("inf", "1e999"));

        Assert.True(ev.TryGetInt64("i", out long i));
        Assert.Equal(3100, i);
        Assert.True(ev.TryGetDouble("d", out double d));
        Assert.Equal(2.5, d);
        Assert.False(ev.TryGetDouble("bad", out _));
        Assert.False(ev.TryGetInt64("word", out _));
        Assert.False(ev.TryGetDouble("inf", out _));
        Assert.False(ev.TryGetInt64("missing", out _));
        Assert.False(new InkEvent(1, "x").TryGetInt64("i", out _));
    }
}
