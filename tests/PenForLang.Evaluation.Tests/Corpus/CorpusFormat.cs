using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using PenForLang.Ink;

namespace PenForLang.Evaluation.Tests.Corpus;

/// <summary>Точка записи по разметке: штрих и номер точки в нём (нумерация как в <see cref="RulerInk"/>).</summary>
/// <param name="Stroke"><see cref="RulerStroke.Index"/>.</param>
/// <param name="Sample">Индекс в <see cref="RulerStroke.Points"/>.</param>
internal readonly record struct CorpusPoint(int Stroke, int Sample);

/// <summary>Стык двух соседних букв по разметке: конец тела левой и начало тела правой.</summary>
/// <param name="After">Между буквами <c>After</c> и <c>After + 1</c>.</param>
/// <param name="End">Выход тела левой буквы.</param>
/// <param name="Start">Вход тела правой буквы.</param>
/// <param name="Lifted">Перо оторвано: <c>End.Stroke &lt; Start.Stroke</c> (в разметке — <c>kind = lifted</c>).</param>
internal sealed record CorpusJunction(int After, CorpusPoint End, CorpusPoint Start, bool Lifted);

/// <summary>Отложенный штрих по разметке.</summary>
/// <param name="Stroke">Штрих записи.</param>
/// <param name="Letter">Буква слова.</param>
/// <param name="Immediate">Поставлен сразу (после него пишется ещё штрих основного следа), а не после слова.</param>
internal sealed record CorpusMark(int Stroke, int Letter, bool Immediate);

/// <summary>Запись корпуса: проекция в линейку, цель задания и хеш несжатого JSONL.</summary>
/// <param name="Path">Файл записи.</param>
/// <param name="Ink">Попытка в линейке (штрихи после последнего «Заново»).</param>
/// <param name="Target">Цель задания (<c>word:at</c>) или <c>null</c>.</param>
/// <param name="Sha256">SHA-256 несжатого JSONL, строчные hex.</param>
internal sealed record CorpusRecording(string Path, RulerInk Ink, string? Target, string Sha256);

/// <summary>Разметка одной записи одним разметчиком (то, что нужно корпусному тесту слов).</summary>
/// <param name="Annotator">Кто размечал.</param>
/// <param name="Text">Что написано.</param>
/// <param name="Variant">Вариант задания (<c>lift</c>, <c>joined</c>, <c>dot</c>) или <c>null</c>.</param>
/// <param name="Usable">Запись годится для корпуса.</param>
/// <param name="Junctions">Стыки по порядку букв.</param>
/// <param name="Deferred">Отложенные штрихи.</param>
internal sealed record CorpusAnnotation(string Annotator, string Text, string? Variant, bool Usable, ImmutableArray<CorpusJunction> Junctions, ImmutableArray<CorpusMark> Deferred);

/// <summary>Итог чтения разметки: разметка или причина, по которой она не годится.</summary>
/// <param name="Annotation">Разметка (годная и сверенная с записью) или <c>null</c>.</param>
/// <param name="Problem">Причина отказа или <c>null</c>.</param>
internal sealed record AnnotationLoad(CorpusAnnotation? Annotation, string? Problem);

/// <summary>
/// Единственное место, знающее форматы корпуса: запись пера <c>*.pfl.jsonl.gz</c> и разметка InkLab
/// <c>&lt;запись&gt;.pfl.jsonl.gz.ann.&lt;annotatorId&gt;.json</c> (<c>pfl.annotation/1</c>, схема <c>schemas/pfl.annotation.v1.schema.json</c>;
/// старый <c>&lt;запись&gt;.pfl.jsonl.gz.ann.json</c> читается как файл автора, названного внутри). Разметка сверяется с записью:
/// <c>penlogSha256</c> (хеш несжатого JSONL), <c>indexing</c> (<c>inkattempt-after-last-restart</c>) и <c>strokes[]</c> (число точек,
/// <c>t0</c>, <c>t1</c> каждого штриха); устаревшая разметка пропускается. Точка находится по времени <c>t</c> (приоритетнее <c>sample</c>:
/// время переживает пересборку записи). Папки <c>superseded</c> пропускаются.
/// </summary>
internal static class CorpusFormat
{
    public const string RecordingSuffix = ".pfl.jsonl.gz";
    public const string DefaultAnnotator = "A";
    public const string Indexing = "inkattempt-after-last-restart";
    public const string SupersededFolder = "superseded";

    private const byte GzipFirst = 0x1f;
    private const byte GzipSecond = 0x8b;

    /// <summary>Путь разметки записи для разметчика <paramref name="annotator"/>.</summary>
    public static string AnnotationPath(string recording, string annotator) => recording + ".ann." + annotator + ".json";

    /// <summary>Путь старой разметки (до введения идентификатора разметчика).</summary>
    public static string LegacyAnnotationPath(string recording) => recording + ".ann.json";

    /// <summary>Записи корпуса (рекурсивно, без <c>superseded</c>), по порядку имён.</summary>
    public static IEnumerable<string> FindRecordings(string root)
    {
        string marker = Path.DirectorySeparatorChar + SupersededFolder + Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(root, "*" + RecordingSuffix, SearchOption.AllDirectories)
            .Where(f => !(Path.DirectorySeparatorChar + Path.GetRelativePath(root, f)).Contains(marker, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);
    }

    /// <summary>Читает запись пера (gzip определяется по сигнатуре <c>1f 8b</c>), считает хеш несжатого JSONL и проецирует в линейку.</summary>
    public static CorpusRecording ReadRecording(string path)
    {
        byte[] raw = File.ReadAllBytes(path);
        byte[] jsonl = raw.Length >= 2 && raw[0] == GzipFirst && raw[1] == GzipSecond ? Decompress(raw) : raw;
        PenLog log = PenLogReader.Read(new MemoryStream(jsonl));
        return new CorpusRecording(path, RulerProjection.Project(log.ToInkAttempt()), log.Header.Task?.Target, Convert.ToHexStringLower(SHA256.HashData(jsonl)));
    }

    /// <summary>Цель-слово записи (<c>word:at</c> → <c>at</c>) или <c>null</c>.</summary>
    public static string? WordTarget(CorpusRecording recording) =>
        recording.Target is { } t && t.StartsWith("word:", StringComparison.Ordinal) ? t["word:".Length..] : null;

    /// <summary>Читает и сверяет разметку; файла нет — <c>Annotation = null, Problem = null</c>.</summary>
    public static AnnotationLoad ReadAnnotation(CorpusRecording recording, string annotator)
    {
        string path = AnnotationPath(recording.Path, annotator);
        if (!File.Exists(path))
        {
            path = LegacyAnnotationPath(recording.Path);
            if (!File.Exists(path))
            {
                return new AnnotationLoad(null, null);
            }
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement root = document.RootElement;
        string author = root.TryGetProperty("annotator", out JsonElement a) ? a.GetString() ?? string.Empty : string.Empty;
        if (author != annotator)
        {
            return new AnnotationLoad(null, null); // чужая разметка (или старый файл другого автора)
        }

        string? problem = Verify(root, recording);
        if (problem is not null)
        {
            return new AnnotationLoad(null, problem);
        }

        string text = root.TryGetProperty("text", out JsonElement t) ? t.GetString() ?? string.Empty : string.Empty;
        string? variant = root.TryGetProperty("variant", out JsonElement v) ? v.GetString() : null;
        bool usable = !root.TryGetProperty("quality", out JsonElement quality) || !quality.TryGetProperty("usable", out JsonElement u) || u.GetBoolean();

        var junctions = ImmutableArray.CreateBuilder<CorpusJunction>();
        if (root.TryGetProperty("junctions", out JsonElement js))
        {
            foreach (JsonElement j in js.EnumerateArray())
            {
                CorpusPoint end = ReadPoint(j.GetProperty("end"), recording.Ink);
                CorpusPoint start = ReadPoint(j.GetProperty("start"), recording.Ink);
                bool lifted = j.TryGetProperty("kind", out JsonElement kind) ? kind.GetString() == "lifted" : end.Stroke < start.Stroke;
                junctions.Add(new CorpusJunction(j.GetProperty("after").GetInt32(), end, start, lifted));
            }
        }

        var marks = ImmutableArray.CreateBuilder<CorpusMark>();
        if (root.TryGetProperty("deferred", out JsonElement ds))
        {
            foreach (JsonElement d in ds.EnumerateArray())
            {
                bool immediate = d.TryGetProperty("timing", out JsonElement timing) && timing.GetString() == "immediate";
                marks.Add(new CorpusMark(d.GetProperty("stroke").GetInt32(), d.GetProperty("letter").GetInt32(), immediate));
            }
        }

        return new AnnotationLoad(new CorpusAnnotation(annotator, text, variant, usable, junctions.ToImmutable(), marks.ToImmutable()), null);
    }

    /// <summary>Положение точки в линейке или <c>null</c>, если штриха или точки нет.</summary>
    public static (double U, double V)? Locate(RulerInk ink, CorpusPoint point)
    {
        RulerStroke? stroke = ink.Strokes.FirstOrDefault(s => s.Index == point.Stroke);
        if (stroke is null || point.Sample < 0 || point.Sample >= stroke.Points.Length)
        {
            return null;
        }

        RulerPoint p = stroke.Points[point.Sample];
        return (p.U, p.V);
    }

    /// <summary>Сверка разметки с записью: хеш, нумерация и штрихи. Возвращает причину отказа или <c>null</c>.</summary>
    private static string? Verify(JsonElement root, CorpusRecording recording)
    {
        if (root.TryGetProperty("penlogSha256", out JsonElement sha) && sha.GetString() is { } hash && hash != recording.Sha256)
        {
            return "penlogSha256 differs: the recording changed after the annotation";
        }

        if (root.TryGetProperty("indexing", out JsonElement indexing) && indexing.GetString() != Indexing)
        {
            return $"unknown indexing '{indexing.GetString()}'";
        }

        if (root.TryGetProperty("strokes", out JsonElement strokes))
        {
            JsonElement[] listed = [.. strokes.EnumerateArray()];
            if (listed.Length != recording.Ink.Strokes.Length)
            {
                return $"the annotation lists {listed.Length} strokes, the recording has {recording.Ink.Strokes.Length}";
            }

            for (int i = 0; i < listed.Length; i++)
            {
                RulerStroke s = recording.Ink.Strokes[i];
                if (listed[i].GetProperty("n").GetInt32() != s.Points.Length
                    || listed[i].GetProperty("t0").GetInt64() != s.Points[0].TimeUs
                    || listed[i].GetProperty("t1").GetInt64() != s.Points[^1].TimeUs)
                {
                    return $"stroke {i} differs from the annotation (n, t0, t1)";
                }
            }
        }

        return null;
    }

    private static CorpusPoint ReadPoint(JsonElement element, RulerInk ink)
    {
        int stroke = element.GetProperty("stroke").GetInt32();
        RulerStroke? s = ink.Strokes.FirstOrDefault(x => x.Index == stroke);
        if (element.TryGetProperty("t", out JsonElement time) && s is not null && s.Points.Length > 0)
        {
            // Время точки (мкс) приоритетнее номера: ближайшая по времени точка штриха.
            long t = time.GetInt64();
            int best = 0;
            for (int i = 1; i < s.Points.Length; i++)
            {
                if (Math.Abs(s.Points[i].TimeUs - t) < Math.Abs(s.Points[best].TimeUs - t))
                {
                    best = i;
                }
            }

            return new CorpusPoint(stroke, best);
        }

        return new CorpusPoint(stroke, element.TryGetProperty("sample", out JsonElement sample) ? sample.GetInt32() : 0);
    }

    private static byte[] Decompress(byte[] gz)
    {
        using var input = new MemoryStream(gz);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}
