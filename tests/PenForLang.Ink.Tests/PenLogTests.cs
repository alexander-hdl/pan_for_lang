using System.IO.Compression;
using System.Text;
using PenForLang.Core;
using static PenForLang.Ink.Tests.TestData;

namespace PenForLang.Ink.Tests;

public class PenLogTests
{
    private static byte[] WriteToBytes(PenLog log)
    {
        using var stream = new MemoryStream();
        PenLogWriter.Write(stream, log);
        return stream.ToArray();
    }

    private static PenLog ReadFromBytes(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return PenLogReader.Read(stream);
    }

    private static PenLog ReadFromText(string text) => ReadFromBytes(new UTF8Encoding(false).GetBytes(text));

    private static PenLog RoundTrip(PenLog log)
    {
        PenLog result = ReadFromBytes(WriteToBytes(log));
        AssertLogsEqual(log, result);
        return result;
    }

    private static PenLogFormatException ReadFails(string text) =>
        Assert.Throws<PenLogFormatException>(() => ReadFromText(text));

    private const string HeaderLine =
        """{"type":"header","schema":"pfl.penlog/1","app":"0.3.0","os":"10.0.26100","device":{"product":"P","source":"wm_pointer","tilt":true,"timestamps":"qpc"},"screen":{"wPx":1920,"hPx":1080,"mmW":256,"mmH":144,"dpiScale":1.5,"pxPerMm":7.5,"pxPerMmSource":"calibrated"},"guides":{"x0Px":180,"baselinePx":620,"xHeightPx":112.5,"xHeightMm":15,"lineSpacing":3.0,"style":"zaner-bloser","slantDeg":null},"qpcFreq":10000000}""";

    [Fact]
    public void Full_log_round_trips_with_every_header_section_and_all_record_kinds()
    {
        PenLog log = Log(
            FullHeader(),
            Event(0, "stimulus-start", ("key", "word/cat#v2")),
            Event(640_000, "stimulus-end"),
            Sample(1_810_000, 231.4, 598.2, PenPhase.Down, pressure: 0.31f, tiltX: 12, tiltY: -8),
            Sample(1_815_012, 232.0, 597.1, pressure: 0.35f, tiltX: 12, tiltY: -8),
            Sample(1_920_000, -3.5, 601.0, pressure: 0.30f, flags: SampleFlags.Clipped),
            Sample(1_930_000, 300, 601.0, PenPhase.Hover, flags: SampleFlags.InterpolatedTime | SampleFlags.Coalesced, buttons: PenButtons.Barrel | PenButtons.Eraser),
            Sample(2_903_300, 410.7, 612.9, PenPhase.Up, pressure: 0f),
            Event(3_000_000, "restart-button"),
            Event(4_500_000, "completed", ("by", "pause"), ("tEndMs", "3100")));

        PenLog result = RoundTrip(log);

        Assert.Equal(9, result.Entries.Length);
        Assert.Equal(5, result.Samples.Count());
        Assert.Equal(4, result.Events.Count());
    }

    [Fact]
    public void Header_optional_sections_stay_null_and_optional_numbers_stay_absent()
    {
        var header = new PenLogHeader(
            string.Empty,
            string.Empty,
            new PenDeviceInfo(string.Empty, PenSourceKind.Mouse, null, null, null, false, PenTimestampKind.Interpolated),
            new PenScreenInfo(1, 1, 0.5, 0.5, 1.0, 7.5, PxPerMmSource.Default),
            new GuideFrame(0, 0, 1, 7.5, 0, 12.5, string.Empty),
            null,
            null,
            null,
            0);

        PenLog result = RoundTrip(new PenLog(header, []));

        Assert.Null(result.Header.Task);
        Assert.Null(result.Header.Subject);
        Assert.Null(result.Header.Consent);
        Assert.Null(result.Header.Device.RateHzNominal);
        Assert.Equal(12.5, result.Header.Guides.SlantDeg);
    }

    [Theory]
    [InlineData(PenSourceKind.Win32Pointer)]
    [InlineData(PenSourceKind.WpfStylus)]
    [InlineData(PenSourceKind.Replay)]
    [InlineData(PenSourceKind.Mouse)]
    public void Every_source_kind_timestamp_kind_and_pxpermm_source_round_trips(PenSourceKind kind)
    {
        foreach (PenTimestampKind ts in Enum.GetValues<PenTimestampKind>())
        {
            foreach (PxPerMmSource px in Enum.GetValues<PxPerMmSource>())
            {
                foreach (PenSubjectKind subject in Enum.GetValues<PenSubjectKind>())
                {
                    PenLogHeader header = FullHeader() with
                    {
                        Device = Device() with { Source = kind, Timestamps = ts },
                        Screen = Screen() with { PxPerMmSource = px },
                        Subject = new PenLogSubject("a", null, subject),
                    };
                    RoundTrip(new PenLog(header, []));
                }
            }
        }
    }

    [Fact]
    public void Extreme_values_survive_a_round_trip_exactly()
    {
        PenLog log = Log(
            Header(),
            Sample(0, double.MaxValue, double.MinValue, PenPhase.Down, pid: uint.MaxValue, pressure: float.MaxValue, buttons: (PenButtons)255, tiltX: float.MinValue, tiltY: float.Epsilon),
            Sample(1, double.Epsilon, -double.Epsilon, pressure: float.Epsilon, tiltX: 90, tiltY: -90),
            Sample(2, 0.1 + 0.2, 1.0 / 3.0, pressure: 1f, flags: (SampleFlags)7),
            Sample(3, 123456789.123456789, -1e-300, pressure: 0.1f),
            Sample(long.MaxValue - 1, 1e300, -1e300, PenPhase.Up, pressure: 0f),
            Event(long.MaxValue, "end"));

        PenLog result = RoundTrip(log);

        PenSample first = result.Samples.First();
        Assert.Equal(double.MaxValue, first.X);
        Assert.Equal(float.Epsilon, first.TiltYDeg);
        Assert.Equal(uint.MaxValue, first.PointerId);
        Assert.Equal((PenButtons)255, first.Buttons);
        Assert.Equal(0.1 + 0.2, result.Samples.ElementAt(2).X);
    }

    [Fact]
    public void Negative_zero_is_preserved_for_coordinates()
    {
        PenLog result = RoundTrip(Log(Header(), Sample(0, -0.0, -0.0, PenPhase.Down)));

        PenSample sample = result.Samples.Single();
        Assert.True(double.IsNegative(sample.X));
        Assert.True(double.IsNegative(sample.Y));
    }

    [Fact]
    public void Randomised_logs_round_trip_bit_exactly()
    {
        var rng = new Pcg32(20260101);
        for (int round = 0; round < 40; round++)
        {
            var entries = new List<object>();
            long t = 0;
            for (int i = 0; i < 150; i++)
            {
                t += rng.NextInt32(20_000);
                if (rng.NextInt32(10) == 0)
                {
                    entries.Add(Event(t, "ev" + rng.NextInt32(5), ("n", rng.NextInt32(1000).ToString(System.Globalization.CultureInfo.InvariantCulture))));
                    continue;
                }

                entries.Add(Sample(
                    t,
                    (rng.NextDouble() - 0.5) * 4000,
                    (rng.NextDouble() - 0.5) * 4000,
                    (PenPhase)rng.NextInt32(4),
                    (uint)rng.NextInt32(8),
                    (float)rng.NextDouble(),
                    (PenButtons)rng.NextInt32(4),
                    (SampleFlags)rng.NextInt32(8),
                    (float)((rng.NextDouble() - 0.5) * 180),
                    (float)((rng.NextDouble() - 0.5) * 180)));
            }

            RoundTrip(Log(Header(), [.. entries]));
        }
    }

    [Fact]
    public void Empty_logs_and_empty_strokes_are_valid()
    {
        PenLog headerOnly = RoundTrip(new PenLog(FullHeader(), []));
        Assert.Empty(headerOnly.Entries);
        Assert.Empty(headerOnly.ToInkAttempt().Strokes);
        Assert.Equal(InkCompletion.Cancelled, headerOnly.ToInkAttempt().Completion);

        PenLog eventsOnly = RoundTrip(Log(Header(), Event(5, "stimulus-start"), Event(9, "completed", ("by", "timer"))));
        InkAttempt attempt = eventsOnly.ToInkAttempt();
        Assert.Empty(attempt.Strokes);
        Assert.Equal(InkCompletion.Timer, attempt.Completion);
        Assert.Equal(2, attempt.Events.Length);

        // Штрих без точек сериализуется как «ничего»: в логе штрихов нет, только точки.
        var emptyStroke = new InkStroke(0, []);
        Assert.Empty(emptyStroke.Samples);
    }

    [Fact]
    public void Single_touch_down_up_and_equal_timestamps_round_trip()
    {
        PenLog result = RoundTrip(Log(Header(), Sample(7, 1, 1, PenPhase.Down), Sample(7, 1, 1, PenPhase.Up), Event(7, "x")));

        Assert.Single(result.ToInkAttempt().Strokes);
        Assert.Equal(2, result.ToInkAttempt().Strokes[0].Samples.Length);
    }

    [Fact]
    public void Tilt_is_omitted_when_zero_and_written_when_any_axis_is_non_zero()
    {
        string noTilt = Encoding.UTF8.GetString(WriteToBytes(Log(Header(), Sample(1, 1, 1))));
        string tilt = Encoding.UTF8.GetString(WriteToBytes(Log(Header(), Sample(1, 1, 1, tiltY: 3))));

        Assert.DoesNotContain("\"tx\"", noTilt, StringComparison.Ordinal);
        Assert.Contains("\"tx\":0,\"ty\":3", tilt, StringComparison.Ordinal);
        RoundTrip(Log(Header(), Sample(1, 1, 1, tiltY: 3)));
    }

    [Fact]
    public void Output_is_deterministic_utf8_without_bom_with_lf_lines_and_sorted_event_keys()
    {
        PenLog log = Log(Header(), Event(1, "e", ("zeta", "1"), ("alpha", "x"), ("mid", "007")), Sample(2, 1.5, 2.5, PenPhase.Down));

        byte[] first = WriteToBytes(log);
        byte[] second = WriteToBytes(log);
        string text = Encoding.UTF8.GetString(first);

        Assert.Equal(first, second);
        Assert.NotEqual(0xEF, first[0]);
        Assert.DoesNotContain('\r', text);
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
        Assert.Equal(3, text.Count(c => c == '\n'));
        Assert.Contains("\"name\":\"e\",\"alpha\":\"x\",\"mid\":\"007\",\"zeta\":1}", text, StringComparison.Ordinal);
        Assert.StartsWith("{\"type\":\"header\",\"schema\":\"pfl.penlog/1\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Event_data_with_special_text_round_trips()
    {
        PenLog log = Log(
            Header(),
            Event(1, "e", ("quote", "a\"b\\c"), ("nl", "line1\nline2\t!"), ("ru", "Привет"), ("empty", string.Empty), ("emoji", "\U0001F58A")),
            Event(2, "n", ("int", "3100"), ("neg", "-5"), ("zero", "0"), ("lead", "007"), ("minus0", "-0"), ("dec", "1.50"), ("big", "12345678901234567890"), ("bool", "true"), ("plus", "+1")));

        RoundTrip(log);
    }

    [Fact]
    public void Event_numbers_and_booleans_from_foreign_files_are_read_as_text()
    {
        PenLog log = ReadFromText(HeaderLine + "\n" + """{"type":"ev","t":5,"name":"x","a":3100,"b":1.50,"c":true,"d":"s"}""" + "\n");

        InkEvent ev = log.Events.Single();
        Assert.Equal("3100", ev.Data!["a"]);
        Assert.Equal("1.50", ev.Data["b"]);
        Assert.Equal("true", ev.Data["c"]);
        Assert.Equal("s", ev.Data["d"]);
    }

    [Fact]
    public void Gzip_stream_round_trip_works_for_pfl_jsonl_gz()
    {
        PenLog log = Log(FullHeader(), Sample(1, 1, 1, PenPhase.Down), Sample(2, 2, 2, PenPhase.Up));
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            PenLogWriter.Write(gzip, log);
        }

        compressed.Position = 0;
        using var gunzip = new GZipStream(compressed, CompressionMode.Decompress);
        PenLog result = PenLogReader.Read(gunzip);

        AssertLogsEqual(log, result);
    }

    [Fact]
    public void Document_example_from_section_3_4_is_parsed()
    {
        // Строки примера ARCHITECTURE.md §3.4 (комментарии убраны).
        const string text = """
            {"type":"header","schema":"pfl.penlog/1","app":"0.3.0","os":"10.0.26100","device":{"product":"XPPen Artist 12 Pro","source":"wm_pointer","rateHzNominal":200,"rateHzMeasured":199.6,"pressureLevels":1024,"tilt":true,"timestamps":"qpc"},"screen":{"wPx":1920,"hPx":1080,"mmW":256,"mmH":144,"dpiScale":1.5,"pxPerMm":7.5,"pxPerMmSource":"calibrated"},"guides":{"x0Px":180,"baselinePx":620,"xHeightPx":112.5,"xHeightMm":15,"lineSpacing":3.0,"style":"zaner-bloser","slantDeg":null},"task":{"taskId":"s12-t07","exercise":"E16","target":"word:cat","level":"L0","mode":"dictation","handedness":"right"},"subject":{"anon":"p-3f9a","ageYears":9,"kind":"child"},"consent":{"id":17,"kind":"trajectory_recording","version":"1.0"},"qpcFreq":10000000}
            {"type":"ev","t":0,"name":"stimulus-start","key":"word/cat#v2"}
            {"type":"ev","t":640000,"name":"stimulus-end"}
            {"type":"p","t":1810000,"x":231.4,"y":598.2,"p":0.31,"tx":12,"ty":-8,"btn":0,"ph":"down","pid":3}
            {"type":"p","t":1815012,"x":232.0,"y":597.1,"p":0.35,"tx":12,"ty":-8,"btn":0,"ph":"move","pid":3}
            {"type":"p","t":1920000,"x":-3.5,"y":601.0,"p":0.30,"btn":0,"ph":"move","pid":3,"clip":1}
            {"type":"p","t":2903300,"x":410.7,"y":612.9,"p":0.0,"btn":0,"ph":"up","pid":3}
            {"type":"ev","t":3000000,"name":"restart-button"}
            {"type":"ev","t":4500000,"name":"completed","by":"pause","tEndMs":3100}
            """;

        PenLog log = ReadFromText(text);

        Assert.Equal("XPPen Artist 12 Pro", log.Header.Device.Product);
        Assert.Equal(PenSourceKind.Win32Pointer, log.Header.Device.Source);
        Assert.Equal(199.6, log.Header.Device.RateHzMeasured);
        Assert.Equal(1.5, log.Header.Screen.DpiScale);
        Assert.Equal(PxPerMmSource.Calibrated, log.Header.Screen.PxPerMmSource);
        Assert.Equal(7.5, log.Header.Guides.PxPerMm);
        Assert.Equal(112.5, log.Header.Guides.XHeightPx);
        Assert.Equal(15, log.Header.Guides.XHeightMm);
        Assert.Null(log.Header.Guides.SlantDeg);
        Assert.Equal(new PenLogSubject("p-3f9a", 9, PenSubjectKind.Child), log.Header.Subject);
        Assert.Equal(new PenLogConsent(17, "trajectory_recording", "1.0"), log.Header.Consent);
        Assert.Equal(10_000_000, log.Header.QpcFreq);
        Assert.Equal(8, log.Entries.Length);

        PenSample clipped = log.Samples.ElementAt(2);
        Assert.Equal(-3.5, clipped.X);
        Assert.True(clipped.IsClipped);
        Assert.Equal(0f, clipped.TiltXDeg);

        InkAttempt attempt = log.ToInkAttempt();
        Assert.Single(attempt.Strokes);
        Assert.Equal(4, attempt.Strokes[0].Samples.Length);
        Assert.Equal(640_000, attempt.StimulusEndUs);
        Assert.Equal(1, attempt.Restarts);
        Assert.Equal(InkCompletion.Pause, attempt.Completion);
        Assert.Equal("3100", attempt.Events[^1].Data!["tEndMs"]);

        // И обратно: запись → чтение даёт тот же лог.
        RoundTrip(log);
    }

    [Fact]
    public void Unknown_fields_are_ignored_for_forward_compatibility()
    {
        PenLog log = ReadFromText(
            HeaderLine.Replace("\"qpcFreq\"", "\"futureField\":{\"x\":1},\"qpcFreq\"", StringComparison.Ordinal) + "\n"
            + """{"type":"p","t":1,"x":1,"y":2,"p":0.5,"btn":0,"ph":"move","pid":1,"newField":[1,2]}""" + "\n");

        Assert.Single(log.Entries);
    }

    [Fact]
    public void Blank_lines_and_crlf_line_endings_are_tolerated()
    {
        PenLog log = ReadFromText("\n" + HeaderLine + "\r\n\r\n" + """{"type":"ev","t":1,"name":"a"}""" + "\r\n\r\n");

        Assert.Single(log.Entries);
    }

    [Fact]
    public void Utf8_bom_is_tolerated_on_read()
    {
        byte[] body = new UTF8Encoding(false).GetBytes(HeaderLine + "\n");
        byte[] withBom = [0xEF, 0xBB, 0xBF, .. body];

        Assert.Empty(ReadFromBytes(withBom).Entries);
    }

    [Theory]
    [InlineData("pfl.penlog/2", "version 2")]
    [InlineData("pfl.penlog/0", "version 0")]
    [InlineData("pfl.penlog/99", "version 99")]
    [InlineData("pfl.penlog/1.1", "not a pfl.penlog")]
    [InlineData("pfl.penlog", "not a pfl.penlog")]
    [InlineData("pfl.glyph/1", "not a pfl.penlog")]
    [InlineData("", "not a pfl.penlog")]
    public void Unsupported_or_foreign_schema_versions_are_rejected_with_a_clear_message(string schema, string expectedFragment)
    {
        PenLogFormatException ex = ReadFails(HeaderLine.Replace("pfl.penlog/1", schema, StringComparison.Ordinal) + "\n");

        Assert.Contains(expectedFragment, ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, ex.LineNumber);
    }

    [Fact]
    public void Structural_problems_are_reported_with_line_numbers()
    {
        Assert.Contains("empty", ReadFails(string.Empty).Message, StringComparison.Ordinal);
        Assert.Contains("empty", ReadFails("\n  \n").Message, StringComparison.Ordinal);
        Assert.Contains("must be the header", ReadFails("""{"type":"ev","t":1,"name":"a"}""").Message, StringComparison.Ordinal);
        Assert.Contains("Invalid JSON", ReadFails("not json").Message, StringComparison.Ordinal);
        Assert.Contains("Expected a JSON object", ReadFails("[1,2]").Message, StringComparison.Ordinal);
        Assert.Contains("Missing required field 'type'", ReadFails("{}").Message, StringComparison.Ordinal);

        PenLogFormatException duplicate = ReadFails(HeaderLine + "\n" + HeaderLine + "\n");
        Assert.Equal(2, duplicate.LineNumber);
        Assert.Contains("Duplicate header", duplicate.Message, StringComparison.Ordinal);

        PenLogFormatException unknown = ReadFails(HeaderLine + "\n" + """{"type":"zzz","t":1}""" + "\n");
        Assert.Equal(2, unknown.LineNumber);
        Assert.Contains("Unknown record type 'zzz'", unknown.Message, StringComparison.Ordinal);

        PenLogFormatException truncated = ReadFails(HeaderLine + "\n" + """{"type":"p","t":1,"x":1,"y""");
        Assert.Equal(2, truncated.LineNumber);
    }

    [Theory]
    [InlineData("""{"type":"p","t":1,"y":2,"p":0.5,"btn":0,"ph":"move","pid":1}""", "Missing required field 'x'")]
    [InlineData("""{"type":"p","t":1,"x":1,"y":2,"p":0.5,"btn":0,"ph":"swipe","pid":1}""", "Unknown pen phase 'swipe'")]
    [InlineData("""{"type":"p","t":1,"x":"1","y":2,"p":0.5,"btn":0,"ph":"move","pid":1}""", "'x' must be a finite number")]
    [InlineData("""{"type":"p","t":1.5,"x":1,"y":2,"p":0.5,"btn":0,"ph":"move","pid":1}""", "'t' must be an integer")]
    [InlineData("""{"type":"p","t":1,"x":1,"y":2,"p":0.5,"btn":256,"ph":"move","pid":1}""", "Button mask")]
    [InlineData("""{"type":"p","t":1,"x":1,"y":2,"p":0.5,"btn":0,"ph":"move","pid":-1}""", "Pointer id")]
    [InlineData("""{"type":"p","t":1,"x":1,"y":2,"p":0.5,"btn":0,"ph":"move","pid":4294967296}""", "Pointer id")]
    [InlineData("""{"type":"p","t":1,"x":1,"y":2,"p":0.5,"btn":0,"ph":"move","pid":1,"clip":2}""", "Flag 'clip'")]
    [InlineData("""{"type":"p","t":-1,"x":1,"y":2,"p":0.5,"btn":0,"ph":"move","pid":1}""", "Negative time")]
    [InlineData("""{"type":"p","t":1,"x":1e999,"y":2,"p":0.5,"btn":0,"ph":"move","pid":1}""", "'x' must be a finite number")]
    [InlineData("""{"type":"ev","t":1}""", "Missing required field 'name'")]
    [InlineData("""{"type":"ev","t":1,"name":"a","x":{"y":1}}""", "must be a string, number or boolean")]
    [InlineData("""{"type":"ev","t":1,"name":"a","x":null}""", "must be a string, number or boolean")]
    public void Invalid_records_are_rejected(string record, string expectedFragment)
    {
        PenLogFormatException ex = ReadFails(HeaderLine + "\n" + record + "\n");

        Assert.Contains(expectedFragment, ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, ex.LineNumber);
    }

    [Fact]
    public void Decreasing_time_is_rejected_by_reader_and_writer()
    {
        PenLogFormatException ex = ReadFails(
            HeaderLine + "\n" + """{"type":"ev","t":10,"name":"a"}""" + "\n" + """{"type":"ev","t":9,"name":"b"}""" + "\n");
        Assert.Equal(3, ex.LineNumber);
        Assert.Contains("decreases", ex.Message, StringComparison.Ordinal);

        using var stream = new MemoryStream();
        using var writer = new PenLogWriter(stream, leaveOpen: true);
        writer.WriteHeader(Header());
        writer.WriteSample(Sample(10, 0, 0));
        Assert.Throws<ArgumentException>(() => writer.WriteEvent(Event(9, "x")));
        Assert.Throws<ArgumentException>(() => writer.WriteSample(Sample(-1, 0, 0)));
    }

    [Theory]
    [InlineData("device", "Missing required field 'device'")]
    [InlineData("screen", "Missing required field 'screen'")]
    [InlineData("guides", "Missing required field 'guides'")]
    [InlineData("app", "Missing required field 'app'")]
    [InlineData("qpcFreq", "Missing required field 'qpcFreq'")]
    public void Header_missing_required_sections_are_rejected(string field, string expectedFragment)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(HeaderLine);
        var props = doc.RootElement.EnumerateObject().Where(p => p.Name != field).Select(p => $"\"{p.Name}\":{p.Value.GetRawText()}");
        string header = "{" + string.Join(",", props) + "}";

        PenLogFormatException ex = ReadFails(header + "\n");

        Assert.Contains(expectedFragment, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"xHeightPx\":112.5", "\"xHeightPx\":0")]
    [InlineData("\"pxPerMm\":7.5", "\"pxPerMm\":-7.5")]
    [InlineData("\"source\":\"wm_pointer\"", "\"source\":\"bluetooth\"")]
    [InlineData("\"pxPerMmSource\":\"calibrated\"", "\"pxPerMmSource\":\"guess\"")]
    [InlineData("\"timestamps\":\"qpc\"", "\"timestamps\":\"ntp\"")]
    [InlineData("\"wPx\":1920", "\"wPx\":1920.5")]
    public void Header_with_invalid_values_is_rejected(string original, string replacement)
    {
        Assert.Contains(original, HeaderLine, StringComparison.Ordinal);

        ReadFails(HeaderLine.Replace(original, replacement, StringComparison.Ordinal) + "\n");
    }

    [Fact]
    public void Stream_reading_api_yields_header_then_entries_then_null()
    {
        PenLog log = Log(Header(), Sample(1, 1, 1, PenPhase.Down), Event(2, "a"));
        using var stream = new MemoryStream(WriteToBytes(log));
        using var reader = new PenLogReader(stream);

        Assert.Throws<InvalidOperationException>(() => reader.ReadEntry());
        Assert.Equal(log.Header, reader.ReadHeader());
        Assert.Throws<InvalidOperationException>(() => reader.ReadHeader());
        Assert.IsType<PenLogSampleEntry>(reader.ReadEntry());
        Assert.IsType<PenLogEventEntry>(reader.ReadEntry());
        Assert.Null(reader.ReadEntry());
        Assert.Null(reader.ReadEntry());
    }

    [Fact]
    public void Writer_enforces_call_order_and_rejects_unrepresentable_values()
    {
        using var stream = new MemoryStream();
        using var writer = new PenLogWriter(stream, leaveOpen: true);

        Assert.Throws<InvalidOperationException>(() => writer.WriteSample(Sample(1, 0, 0)));
        Assert.Throws<InvalidOperationException>(() => writer.WriteEvent(Event(1, "x")));
        writer.WriteHeader(Header());
        Assert.Throws<InvalidOperationException>(() => writer.WriteHeader(Header()));
        Assert.Throws<ArgumentException>(() => writer.WriteSample(Sample(1, double.NaN, 0)));
        Assert.Throws<ArgumentException>(() => writer.WriteSample(Sample(1, 0, double.PositiveInfinity)));
        Assert.Throws<ArgumentException>(() => writer.WriteSample(Sample(1, 0, 0, pressure: float.NaN)));
        Assert.Throws<ArgumentException>(() => writer.WriteSample(Sample(1, 0, 0, tiltX: float.PositiveInfinity)));
        Assert.Throws<ArgumentException>(() => writer.WriteEvent(Event(1, "x", ("name", "clash"))));
        Assert.Throws<ArgumentException>(() => writer.WriteEvent(Event(1, "x", ("t", "1"))));

        // После отклонённых записей лог остаётся корректным.
        writer.WriteSample(Sample(2, 1, 1));
        writer.Dispose();
        Assert.Single(ReadFromBytes(stream.ToArray()).Entries);
    }

    [Fact]
    public void Writer_rejects_guides_that_contradict_the_screen_pxpermm()
    {
        PenLogHeader header = Header() with { Guides = Guides(pxPerMm: 5.0) };
        using var writer = new PenLogWriter(new MemoryStream());

        Assert.Throws<ArgumentException>(() => writer.WriteHeader(header));
    }

    [Fact]
    public void Writer_and_reader_respect_leaveOpen_and_stream_capabilities()
    {
        var stream = new MemoryStream();
        new PenLogWriter(stream, leaveOpen: true).Dispose();
        Assert.True(stream.CanWrite);

        var owned = new MemoryStream();
        new PenLogWriter(owned).Dispose();
        Assert.False(owned.CanWrite);

        using var readOnly = new MemoryStream([], writable: false);
        Assert.Throws<ArgumentException>(() => new PenLogWriter(readOnly));
        using var writeOnly = new WriteOnlyStream();
        Assert.Throws<ArgumentException>(() => new PenLogReader(writeOnly));

        var source = new MemoryStream(Encoding.UTF8.GetBytes(HeaderLine + "\n"));
        PenLogReader.Read(source, leaveOpen: true);
        Assert.True(source.CanRead);
    }

    [Fact]
    public void Writer_after_dispose_throws()
    {
        var writer = new PenLogWriter(new MemoryStream());
        writer.Dispose();
        writer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => writer.WriteHeader(Header()));
    }

    [Fact]
    public void Streaming_writer_matches_whole_log_writer()
    {
        PenLog log = Log(Header(), Event(0, "a"), Sample(1, 1, 1, PenPhase.Down), Sample(2, 2, 2, PenPhase.Up));
        using var stream = new MemoryStream();
        using (var writer = new PenLogWriter(stream, leaveOpen: true))
        {
            writer.WriteHeader(log.Header);
            writer.WriteEvent(((PenLogEventEntry)log.Entries[0]).Event);
            writer.WriteSamples(log.Samples.ToArray());
            writer.Flush();
        }

        Assert.Equal(WriteToBytes(log), stream.ToArray());
    }

    [Fact]
    public void InkAttempt_completion_reasons_are_mapped()
    {
        foreach ((string by, InkCompletion expected) in new[]
        {
            ("pause", InkCompletion.Pause),
            ("done-button", InkCompletion.DoneButton),
            ("timer", InkCompletion.Timer),
            ("asked-confirmed", InkCompletion.AskedConfirmed),
            ("other", InkCompletion.Cancelled),
        })
        {
            PenLog log = Log(Header(), Event(1, "completed", ("by", by)));
            Assert.Equal(expected, log.ToInkAttempt().Completion);
        }

        Assert.Equal(InkCompletion.Cancelled, Log(Header(), Event(1, "completed")).ToInkAttempt().Completion);
    }

    private sealed class WriteOnlyStream : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
        }
    }
}
