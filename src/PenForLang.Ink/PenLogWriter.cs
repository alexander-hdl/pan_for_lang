using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PenForLang.Ink;

/// <summary>
/// Пишет лог пера <c>pfl.penlog/1</c> (ARCHITECTURE.md §3.4) в поток: JSON Lines, UTF-8 без BOM, строки разделены <c>\n</c>.
/// Сжатие (<c>*.pfl.jsonl.gz</c>) — забота вызывающего: передайте <c>GZipStream</c>. Файловый ввод-вывод домену запрещён (R2).
/// Вывод детерминирован: одинаковые данные дают одинаковые байты.
/// </summary>
/// <remarks>
/// Порядок вызовов: <see cref="WriteHeader"/> один раз первым, затем <see cref="WriteSample"/> и <see cref="WriteEvent"/> с неубывающим
/// временем. Не потокобезопасен.
/// </remarks>
public sealed class PenLogWriter : IDisposable
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly ArrayBufferWriter<byte> _buffer = new(256);
    private readonly Utf8JsonWriter _json;
    private bool _headerWritten;
    private long _lastTimeUs;
    private bool _disposed;

    /// <summary>Создаёт писатель.</summary>
    /// <param name="stream">Поток назначения (запись).</param>
    /// <param name="leaveOpen">Не закрывать поток при <see cref="Dispose"/>.</param>
    /// <exception cref="ArgumentException">Поток не поддерживает запись.</exception>
    public PenLogWriter(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new ArgumentException("Stream must be writable.", nameof(stream));
        }

        _stream = stream;
        _leaveOpen = leaveOpen;
        _json = new Utf8JsonWriter(_buffer, WriterOptions);
    }

    /// <summary>Записывает лог целиком: заголовок и все записи.</summary>
    public static void Write(Stream stream, PenLog log, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(log);
        using var writer = new PenLogWriter(stream, leaveOpen);
        writer.WriteHeader(log.Header);
        foreach (PenLogEntry entry in log.Entries)
        {
            switch (entry)
            {
                case PenLogSampleEntry sample:
                    writer.WriteSample(sample.Sample);
                    break;
                case PenLogEventEntry ev:
                    writer.WriteEvent(ev.Event);
                    break;
                default:
                    throw new ArgumentException($"Unknown entry type {entry.GetType().Name}.", nameof(log));
            }
        }
    }

    /// <summary>Записывает заголовок. Должен вызываться ровно один раз и первым.</summary>
    /// <exception cref="InvalidOperationException">Заголовок уже записан.</exception>
    /// <exception cref="ArgumentException">Значения не представимы в JSON или линейка и экран противоречат друг другу.</exception>
    public void WriteHeader(PenLogHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        ThrowIfDisposed();
        if (_headerWritten)
        {
            throw new InvalidOperationException("The penlog header has already been written.");
        }

        if (header.Guides.PxPerMm != header.Screen.PxPerMm)
        {
            throw new ArgumentException("Guides.PxPerMm must equal Screen.PxPerMm: the format stores pxPerMm once, in 'screen'.", nameof(header));
        }

        ArgumentNullException.ThrowIfNull(header.App);
        ArgumentNullException.ThrowIfNull(header.Os);
        ArgumentNullException.ThrowIfNull(header.Device);
        ArgumentNullException.ThrowIfNull(header.Device.Product);
        ArgumentNullException.ThrowIfNull(header.Screen);
        if (!AllFinite(
            header.Device.RateHzNominal ?? 0,
            header.Device.RateHzMeasured ?? 0,
            header.Screen.WidthMm,
            header.Screen.HeightMm,
            header.Screen.DpiScale,
            header.Screen.PxPerMm))
        {
            throw new ArgumentException("Header numbers must be finite.", nameof(header));
        }

        Utf8JsonWriter w = _json;
        w.WriteStartObject();
        w.WriteString("type", "header");
        w.WriteString("schema", PenLogFormat.Schema);
        w.WriteString("app", header.App);
        w.WriteString("os", header.Os);

        PenDeviceInfo device = header.Device;
        w.WriteStartObject("device");
        w.WriteString("product", device.Product);
        w.WriteString("source", PenLogNames.ToText(device.Source));
        WriteOptionalNumber(w, "rateHzNominal", device.RateHzNominal);
        WriteOptionalNumber(w, "rateHzMeasured", device.RateHzMeasured);
        if (device.PressureLevels is { } levels)
        {
            w.WriteNumber("pressureLevels", levels);
        }

        w.WriteBoolean("tilt", device.Tilt);
        w.WriteString("timestamps", PenLogNames.ToText(device.Timestamps));
        w.WriteEndObject();

        PenScreenInfo screen = header.Screen;
        w.WriteStartObject("screen");
        w.WriteNumber("wPx", screen.WidthPx);
        w.WriteNumber("hPx", screen.HeightPx);
        WriteNumber(w, "mmW", screen.WidthMm);
        WriteNumber(w, "mmH", screen.HeightMm);
        WriteNumber(w, "dpiScale", screen.DpiScale);
        WriteNumber(w, "pxPerMm", screen.PxPerMm);
        w.WriteString("pxPerMmSource", PenLogNames.ToText(screen.PxPerMmSource));
        w.WriteEndObject();

        GuideFrame guides = header.Guides;
        w.WriteStartObject("guides");
        WriteNumber(w, "x0Px", guides.OriginXPx);
        WriteNumber(w, "baselinePx", guides.BaselineYPx);
        WriteNumber(w, "xHeightPx", guides.XHeightPx);
        WriteNumber(w, "xHeightMm", guides.XHeightMm);
        WriteNumber(w, "lineSpacing", guides.LineSpacing);
        w.WriteString("style", guides.Style);
        if (guides.SlantDeg is { } slant)
        {
            WriteNumber(w, "slantDeg", slant);
        }
        else
        {
            w.WriteNull("slantDeg");
        }

        w.WriteEndObject();

        if (header.Task is { } task)
        {
            w.WriteStartObject("task");
            w.WriteString("taskId", task.TaskId);
            w.WriteString("exercise", task.Exercise);
            w.WriteString("target", task.Target);
            w.WriteString("level", task.Level);
            w.WriteString("mode", task.Mode);
            w.WriteString("handedness", task.Handedness);
            w.WriteEndObject();
        }

        if (header.Subject is { } subject)
        {
            w.WriteStartObject("subject");
            w.WriteString("anon", subject.Anon);
            if (subject.AgeYears is { } age)
            {
                w.WriteNumber("ageYears", age);
            }

            w.WriteString("kind", PenLogNames.ToText(subject.Kind));
            w.WriteEndObject();
        }

        if (header.Consent is { } consent)
        {
            w.WriteStartObject("consent");
            w.WriteNumber("id", consent.Id);
            w.WriteString("kind", consent.Kind);
            w.WriteString("version", consent.Version);
            w.WriteEndObject();
        }

        w.WriteNumber("qpcFreq", header.QpcFreq);
        w.WriteEndObject();
        EndLine();
        _headerWritten = true;
    }

    /// <summary>Записывает точку.</summary>
    /// <exception cref="InvalidOperationException">Заголовок ещё не записан.</exception>
    /// <exception cref="ArgumentException">Время отрицательно или меньше предыдущей записи; число не конечно.</exception>
    public void WriteSample(in PenSample sample)
    {
        ThrowIfDisposed();
        RequireHeader();
        CheckTime(sample.TimeUs);
        if (!double.IsFinite(sample.X) || !double.IsFinite(sample.Y) || !float.IsFinite(sample.Pressure)
            || !float.IsFinite(sample.TiltXDeg) || !float.IsFinite(sample.TiltYDeg))
        {
            throw new ArgumentException("Sample numbers must be finite.", nameof(sample));
        }

        Utf8JsonWriter w = _json;
        w.WriteStartObject();
        w.WriteString("type", "p");
        w.WriteNumber("t", sample.TimeUs);
        w.WriteNumber("x", sample.X);
        w.WriteNumber("y", sample.Y);
        w.WriteNumber("p", sample.Pressure);
        if (sample.TiltXDeg != 0 || sample.TiltYDeg != 0)
        {
            w.WriteNumber("tx", sample.TiltXDeg);
            w.WriteNumber("ty", sample.TiltYDeg);
        }

        w.WriteNumber("btn", (byte)sample.Buttons);
        w.WriteString("ph", PenLogNames.ToText(sample.Phase));
        w.WriteNumber("pid", sample.PointerId);
        if ((sample.Flags & SampleFlags.InterpolatedTime) != 0)
        {
            w.WriteNumber("ti", 1);
        }

        if ((sample.Flags & SampleFlags.Coalesced) != 0)
        {
            w.WriteNumber("co", 1);
        }

        if ((sample.Flags & SampleFlags.Clipped) != 0)
        {
            w.WriteNumber("clip", 1);
        }

        w.WriteEndObject();
        EndLine();
        _lastTimeUs = sample.TimeUs;
    }

    /// <summary>Записывает пачку точек подряд.</summary>
    public void WriteSamples(ReadOnlySpan<PenSample> samples)
    {
        foreach (PenSample sample in samples)
        {
            WriteSample(sample);
        }
    }

    /// <summary>Записывает событие. Ключи <see cref="InkEvent.Data"/> не должны совпадать с <c>type</c>, <c>t</c>, <c>name</c>.</summary>
    /// <exception cref="InvalidOperationException">Заголовок ещё не записан.</exception>
    /// <exception cref="ArgumentException">Время отрицательно или меньше предыдущей записи; зарезервированный ключ данных.</exception>
    public void WriteEvent(InkEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);
        ThrowIfDisposed();
        RequireHeader();
        CheckTime(ev.TimeUs);
        ArgumentNullException.ThrowIfNull(ev.Name);
        if (ev.Data is { Count: > 0 } keys && keys.Keys.FirstOrDefault(k => k is "type" or "t" or "name") is { } reserved)
        {
            throw new ArgumentException($"Event data key '{reserved}' is reserved.", nameof(ev));
        }

        Utf8JsonWriter w = _json;
        w.WriteStartObject();
        w.WriteString("type", "ev");
        w.WriteNumber("t", ev.TimeUs);
        w.WriteString("name", ev.Name);
        if (ev.Data is { Count: > 0 } data)
        {
            foreach (KeyValuePair<string, string> pair in data.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (IsCanonicalInteger(pair.Value))
                {
                    w.WritePropertyName(pair.Key);
                    w.WriteRawValue(pair.Value);
                }
                else
                {
                    w.WriteString(pair.Key, pair.Value);
                }
            }
        }

        w.WriteEndObject();
        EndLine();
        _lastTimeUs = ev.TimeUs;
    }

    /// <summary>Сбрасывает буферы в поток.</summary>
    public void Flush()
    {
        ThrowIfDisposed();
        _stream.Flush();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _json.Dispose();
        _stream.Flush();
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }

    private static bool AllFinite(params double[] values) => values.All(double.IsFinite);

    private static bool IsCanonicalInteger(string value)
    {
        // Целое без плюса, ведущих нулей и «-0», не длиннее 15 цифр: при чтении как число даёт тот же текст.
        if (value.Length is 0 or > 16)
        {
            return false;
        }

        int start = value[0] == '-' ? 1 : 0;
        if (start == value.Length || (value[start] == '0' && value.Length > start + 1) || (start == 1 && value[1] == '0'))
        {
            return false;
        }

        for (int i = start; i < value.Length; i++)
        {
            if (value[i] is < '0' or > '9')
            {
                return false;
            }
        }

        return value.Length - start <= 15;
    }

    private static void WriteNumber(Utf8JsonWriter w, string name, double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentException($"Header value '{name}' must be finite.");
        }

        w.WriteNumber(name, value);
    }

    private static void WriteOptionalNumber(Utf8JsonWriter w, string name, double? value)
    {
        if (value is { } v)
        {
            WriteNumber(w, name, v);
        }
    }

    private void EndLine()
    {
        _json.Flush();
        _json.Reset(_buffer);
        _buffer.GetSpan(1)[0] = (byte)'\n';
        _buffer.Advance(1);
        _stream.Write(_buffer.WrittenSpan);
        _buffer.Clear();
    }

    private void CheckTime(long timeUs)
    {
        if (timeUs < 0)
        {
            throw new ArgumentException("Time must not be negative.");
        }

        if (timeUs < _lastTimeUs)
        {
            throw new ArgumentException($"Time must not decrease: {timeUs} after {_lastTimeUs}.");
        }
    }

    private void RequireHeader()
    {
        if (!_headerWritten)
        {
            throw new InvalidOperationException("WriteHeader must be called first.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
