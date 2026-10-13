using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PenForLang.Ink;

/// <summary>
/// Читает лог пера <c>pfl.penlog/1</c> (ARCHITECTURE.md §3.4) из потока: JSON Lines, UTF-8. Для <c>*.pfl.jsonl.gz</c>
/// передайте <c>GZipStream</c>. Неизвестные поля игнорируются (совместимость вперёд в пределах версии 1); неизвестная версия
/// схемы, пропущенные обязательные поля, неизвестный тип записи и убывающее время — <see cref="PenLogFormatException"/>
/// с номером строки.
/// </summary>
public sealed class PenLogReader : IDisposable
{
    private readonly StreamReader _reader;
    private int _lineNumber;
    private long _lastTimeUs;
    private bool _headerRead;
    private PenLogHeader? _header;

    /// <summary>Создаёт читатель.</summary>
    /// <param name="stream">Поток источника (чтение).</param>
    /// <param name="leaveOpen">Не закрывать поток при <see cref="Dispose"/>.</param>
    /// <exception cref="ArgumentException">Поток не поддерживает чтение.</exception>
    public PenLogReader(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("Stream must be readable.", nameof(stream));
        }

        _reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: leaveOpen);
    }

    /// <summary>Читает лог целиком.</summary>
    /// <exception cref="PenLogFormatException">Формат нарушен.</exception>
    public static PenLog Read(Stream stream, bool leaveOpen = true)
    {
        using var reader = new PenLogReader(stream, leaveOpen);
        PenLogHeader header = reader.ReadHeader();
        var entries = ImmutableArray.CreateBuilder<PenLogEntry>();
        while (reader.ReadEntry() is { } entry)
        {
            entries.Add(entry);
        }

        return new PenLog(header, entries.ToImmutable());
    }

    /// <summary>Читает заголовок — первую непустую строку. Вызывается один раз, до <see cref="ReadEntry"/>.</summary>
    /// <exception cref="PenLogFormatException">Заголовка нет, версия не поддерживается или поля некорректны.</exception>
    /// <exception cref="InvalidOperationException">Заголовок уже прочитан.</exception>
    public PenLogHeader ReadHeader()
    {
        if (_headerRead)
        {
            throw new InvalidOperationException("The penlog header has already been read.");
        }

        string line = NextLine() ?? throw new PenLogFormatException("The log is empty: missing header.");
        _headerRead = true;
        using JsonDocument doc = ParseLine(line);
        JsonElement root = RequireObject(doc.RootElement);
        string type = RequireString(root, "type");
        if (type != "header")
        {
            throw new PenLogFormatException($"The first record must be the header, found type '{type}'.", _lineNumber);
        }

        try
        {
            _header = ParseHeader(root);
        }
        catch (ArgumentException ex)
        {
            throw new PenLogFormatException($"Invalid header value: {ex.Message}", _lineNumber);
        }

        return _header;
    }

    /// <summary>Читает следующую запись или возвращает <c>null</c> в конце лога.</summary>
    /// <exception cref="InvalidOperationException">Заголовок не прочитан.</exception>
    /// <exception cref="PenLogFormatException">Запись некорректна.</exception>
    public PenLogEntry? ReadEntry()
    {
        if (!_headerRead)
        {
            throw new InvalidOperationException("Call ReadHeader first.");
        }

        string? line = NextLine();
        if (line is null)
        {
            return null;
        }

        using JsonDocument doc = ParseLine(line);
        JsonElement root = RequireObject(doc.RootElement);
        string type = RequireString(root, "type");
        PenLogEntry entry = type switch
        {
            "p" => new PenLogSampleEntry(ParseSample(root)),
            "ev" => new PenLogEventEntry(ParseEvent(root)),
            "header" => throw new PenLogFormatException("Duplicate header record.", _lineNumber),
            _ => throw new PenLogFormatException($"Unknown record type '{type}'.", _lineNumber),
        };

        if (entry.TimeUs < 0)
        {
            throw new PenLogFormatException($"Negative time {entry.TimeUs}.", _lineNumber);
        }

        if (entry.TimeUs < _lastTimeUs)
        {
            throw new PenLogFormatException($"Time decreases: {entry.TimeUs} after {_lastTimeUs}.", _lineNumber);
        }

        _lastTimeUs = entry.TimeUs;
        return entry;
    }

    /// <inheritdoc />
    public void Dispose() => _reader.Dispose();

    private static bool TryParseSchema(string schema, out int version)
    {
        version = 0;
        string prefix = PenLogFormat.SchemaName + "/";
        return schema.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(schema.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out version);
    }

    private string? NextLine()
    {
        while (_reader.ReadLine() is { } line)
        {
            _lineNumber++;
            if (!string.IsNullOrWhiteSpace(line))
            {
                return line;
            }
        }

        return null;
    }

    private JsonDocument ParseLine(string line)
    {
        try
        {
            return JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            throw new PenLogFormatException($"Invalid JSON: {ex.Message}", _lineNumber);
        }
    }

    private PenLogHeader ParseHeader(JsonElement root)
    {
        string schema = RequireString(root, "schema");
        if (!TryParseSchema(schema, out int version))
        {
            throw new PenLogFormatException($"Unknown schema '{schema}': not a {PenLogFormat.SchemaName} log.", _lineNumber);
        }

        if (version != PenLogFormat.CurrentVersion)
        {
            throw new PenLogFormatException(
                $"Unsupported penlog format version {version}; this reader supports version {PenLogFormat.CurrentVersion}.",
                _lineNumber);
        }

        JsonElement deviceEl = RequireObject(Require(root, "device"));
        string sourceText = RequireString(deviceEl, "source");
        if (!PenLogNames.TryParse(sourceText, out PenSourceKind source))
        {
            throw new PenLogFormatException($"Unknown device source '{sourceText}'.", _lineNumber);
        }

        PenTimestampKind timestamps = PenTimestampKind.Qpc;
        if (OptionalString(deviceEl, "timestamps") is { } timestampsText && !PenLogNames.TryParse(timestampsText, out timestamps))
        {
            throw new PenLogFormatException($"Unknown timestamps kind '{timestampsText}'.", _lineNumber);
        }

        var device = new PenDeviceInfo(
            RequireString(deviceEl, "product"),
            source,
            OptionalDouble(deviceEl, "rateHzNominal"),
            OptionalDouble(deviceEl, "rateHzMeasured"),
            OptionalInt32(deviceEl, "pressureLevels"),
            OptionalBool(deviceEl, "tilt") ?? false,
            timestamps);

        JsonElement screenEl = RequireObject(Require(root, "screen"));
        PxPerMmSource pxSource = PxPerMmSource.Default;
        if (OptionalString(screenEl, "pxPerMmSource") is { } pxSourceText && !PenLogNames.TryParse(pxSourceText, out pxSource))
        {
            throw new PenLogFormatException($"Unknown pxPerMmSource '{pxSourceText}'.", _lineNumber);
        }

        var screen = new PenScreenInfo(
            RequireInt32(screenEl, "wPx"),
            RequireInt32(screenEl, "hPx"),
            RequireDouble(screenEl, "mmW"),
            RequireDouble(screenEl, "mmH"),
            RequireDouble(screenEl, "dpiScale"),
            RequireDouble(screenEl, "pxPerMm"),
            pxSource);

        JsonElement guidesEl = RequireObject(Require(root, "guides"));
        var guides = new GuideFrame(
            RequireDouble(guidesEl, "x0Px"),
            RequireDouble(guidesEl, "baselinePx"),
            RequireDouble(guidesEl, "xHeightPx"),
            screen.PxPerMm,
            RequireDouble(guidesEl, "lineSpacing"),
            OptionalDouble(guidesEl, "slantDeg"),
            RequireString(guidesEl, "style"));

        PenLogTask? task = null;
        if (OptionalObject(root, "task") is { } taskEl)
        {
            task = new PenLogTask(
                RequireString(taskEl, "taskId"),
                RequireString(taskEl, "exercise"),
                RequireString(taskEl, "target"),
                RequireString(taskEl, "level"),
                RequireString(taskEl, "mode"),
                RequireString(taskEl, "handedness"));
        }

        PenLogSubject? subject = null;
        if (OptionalObject(root, "subject") is { } subjectEl)
        {
            string kindText = RequireString(subjectEl, "kind");
            if (!PenLogNames.TryParse(kindText, out PenSubjectKind kind))
            {
                throw new PenLogFormatException($"Unknown subject kind '{kindText}'.", _lineNumber);
            }

            subject = new PenLogSubject(RequireString(subjectEl, "anon"), OptionalInt32(subjectEl, "ageYears"), kind);
        }

        PenLogConsent? consent = null;
        if (OptionalObject(root, "consent") is { } consentEl)
        {
            consent = new PenLogConsent(
                RequireInt32(consentEl, "id"),
                RequireString(consentEl, "kind"),
                RequireString(consentEl, "version"));
        }

        return new PenLogHeader(
            RequireString(root, "app"),
            RequireString(root, "os"),
            device,
            screen,
            guides,
            task,
            subject,
            consent,
            RequireInt64(root, "qpcFreq"));
    }

    private PenSample ParseSample(JsonElement root)
    {
        string phaseText = RequireString(root, "ph");
        if (!PenLogNames.TryParse(phaseText, out PenPhase phase))
        {
            throw new PenLogFormatException($"Unknown pen phase '{phaseText}'.", _lineNumber);
        }

        int buttons = OptionalInt32(root, "btn") ?? 0;
        if (buttons is < 0 or > byte.MaxValue)
        {
            throw new PenLogFormatException($"Button mask {buttons} is out of range.", _lineNumber);
        }

        long pointerId = RequireInt64(root, "pid");
        if (pointerId is < 0 or > uint.MaxValue)
        {
            throw new PenLogFormatException($"Pointer id {pointerId} is out of range.", _lineNumber);
        }

        SampleFlags flags = SampleFlags.None;
        flags |= OptionalFlag(root, "ti") ? SampleFlags.InterpolatedTime : SampleFlags.None;
        flags |= OptionalFlag(root, "co") ? SampleFlags.Coalesced : SampleFlags.None;
        flags |= OptionalFlag(root, "clip") ? SampleFlags.Clipped : SampleFlags.None;

        return new PenSample(
            RequireInt64(root, "t"),
            RequireDouble(root, "x"),
            RequireDouble(root, "y"),
            RequireSingle(root, "p"),
            OptionalSingle(root, "tx") ?? 0f,
            OptionalSingle(root, "ty") ?? 0f,
            phase,
            (PenButtons)buttons,
            flags,
            (uint)pointerId);
    }

    private InkEvent ParseEvent(JsonElement root)
    {
        long time = RequireInt64(root, "t");
        string name = RequireString(root, "name");
        Dictionary<string, string>? data = null;
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (property.Name is "type" or "t" or "name")
            {
                continue;
            }

            string? value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => throw new PenLogFormatException($"Event field '{property.Name}' must be a string, number or boolean.", _lineNumber),
            };
            (data ??= new Dictionary<string, string>(StringComparer.Ordinal))[property.Name] = value!;
        }

        return new InkEvent(time, name, data);
    }

    private JsonElement RequireObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new PenLogFormatException("Expected a JSON object.", _lineNumber);
        }

        return element;
    }

    private JsonElement Require(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out JsonElement value))
        {
            throw new PenLogFormatException($"Missing required field '{name}'.", _lineNumber);
        }

        return value;
    }

    private static JsonElement? Optional(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null ? value : null;

    private JsonElement? OptionalObject(JsonElement obj, string name) =>
        Optional(obj, name) is { } value ? RequireObject(value) : null;

    private string RequireString(JsonElement obj, string name)
    {
        JsonElement value = Require(obj, name);
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new PenLogFormatException($"Field '{name}' must be a string.", _lineNumber);
    }

    private string? OptionalString(JsonElement obj, string name) =>
        Optional(obj, name) is null ? null : RequireString(obj, name);

    private long RequireInt64(JsonElement obj, string name)
    {
        JsonElement value = Require(obj, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long result))
        {
            throw new PenLogFormatException($"Field '{name}' must be an integer.", _lineNumber);
        }

        return result;
    }

    private int RequireInt32(JsonElement obj, string name)
    {
        long value = RequireInt64(obj, name);
        return value is < int.MinValue or > int.MaxValue
            ? throw new PenLogFormatException($"Field '{name}' is out of range.", _lineNumber)
            : (int)value;
    }

    private int? OptionalInt32(JsonElement obj, string name) =>
        Optional(obj, name) is null ? null : RequireInt32(obj, name);

    private double RequireDouble(JsonElement obj, string name)
    {
        JsonElement value = Require(obj, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double result) || !double.IsFinite(result))
        {
            throw new PenLogFormatException($"Field '{name}' must be a finite number.", _lineNumber);
        }

        return result;
    }

    private double? OptionalDouble(JsonElement obj, string name) =>
        Optional(obj, name) is null ? null : RequireDouble(obj, name);

    private float RequireSingle(JsonElement obj, string name)
    {
        JsonElement value = Require(obj, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out float result) || !float.IsFinite(result))
        {
            throw new PenLogFormatException($"Field '{name}' must be a finite number.", _lineNumber);
        }

        return result;
    }

    private float? OptionalSingle(JsonElement obj, string name) =>
        Optional(obj, name) is null ? null : RequireSingle(obj, name);

    private bool? OptionalBool(JsonElement obj, string name)
    {
        if (Optional(obj, name) is not { } value)
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new PenLogFormatException($"Field '{name}' must be a boolean.", _lineNumber),
        };
    }

    private bool OptionalFlag(JsonElement obj, string name)
    {
        int? value = OptionalInt32(obj, name);
        return value switch
        {
            null or 0 => false,
            1 => true,
            _ => throw new PenLogFormatException($"Flag '{name}' must be 0 or 1.", _lineNumber),
        };
    }
}
