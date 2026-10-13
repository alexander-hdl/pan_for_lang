using System.Collections.Immutable;
using System.IO.Compression;
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
/// <param name="Lifted">Перо оторвано: <c>End.Stroke &lt; Start.Stroke</c>.</param>
internal sealed record CorpusJunction(int After, CorpusPoint End, CorpusPoint Start, bool Lifted);

/// <summary>Отложенный штрих по разметке.</summary>
/// <param name="Stroke">Штрих записи.</param>
/// <param name="Letter">Буква слова.</param>
/// <param name="Immediate">Поставлен сразу (до продолжения слова), а не после слова.</param>
internal sealed record CorpusMark(int Stroke, int Letter, bool Immediate);

/// <summary>Разметка одной записи (то, что нужно корпусному тесту слов).</summary>
/// <param name="Annotator">Кто размечал.</param>
/// <param name="Text">Что написано.</param>
/// <param name="Usable">Запись годится для корпуса.</param>
/// <param name="Junctions">Стыки по порядку букв.</param>
/// <param name="Deferred">Отложенные штрихи.</param>
internal sealed record CorpusAnnotation(string Annotator, string Text, bool Usable, ImmutableArray<CorpusJunction> Junctions, ImmutableArray<CorpusMark> Deferred);

/// <summary>
/// Единственное место, знающее форматы корпуса: запись пера <c>*.pfl.jsonl.gz</c> и разметка InkLab
/// <c>&lt;запись&gt;.pfl.jsonl.gz.ann.&lt;annotatorId&gt;.json</c> (<c>pfl.annotation/1</c>). Формат разметки в ветке InkLab
/// переделывается (стык = две точки End и Start, отрыв ⇔ <c>End.stroke &lt; Start.stroke</c>, привязка точек по <c>t</c>); этот класс читает
/// и номер точки (<c>sample</c>), и время (<c>t</c>, мкс, как <see cref="RulerPoint.TimeUs"/>) и подгоняется под итоговый формат здесь.
/// </summary>
internal static class CorpusFormat
{
    public const string RecordingSuffix = ".pfl.jsonl.gz";
    public const string DefaultAnnotator = "A";

    /// <summary>Путь разметки записи для разметчика <paramref name="annotator"/>.</summary>
    public static string AnnotationPath(string recording, string annotator) => recording + ".ann." + annotator + ".json";

    /// <summary>Записи корпуса (рекурсивно), по порядку имён.</summary>
    public static IEnumerable<string> FindRecordings(string root) =>
        Directory.EnumerateFiles(root, "*" + RecordingSuffix, SearchOption.AllDirectories).Order(StringComparer.Ordinal);

    /// <summary>Читает запись пера (с распаковкой) и проецирует в линейку.</summary>
    public static RulerInk ReadInk(string recording)
    {
        using FileStream file = File.OpenRead(recording);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        PenLog log = PenLogReader.Read(gzip);
        return RulerProjection.Project(log.ToInkAttempt());
    }

    /// <summary>Цель задания из заголовка записи (<c>word:at</c> → <c>at</c>) или <c>null</c>.</summary>
    public static string? ReadTargetWord(string recording)
    {
        using FileStream file = File.OpenRead(recording);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        string? target = PenLogReader.Read(gzip).Header.Task?.Target;
        return target is not null && target.StartsWith("word:", StringComparison.Ordinal) ? target["word:".Length..] : null;
    }

    /// <summary>Читает разметку или возвращает <c>null</c>, если файла нет.</summary>
    public static CorpusAnnotation? ReadAnnotation(string recording, string annotator, RulerInk ink)
    {
        string path = AnnotationPath(recording, annotator);
        if (!File.Exists(path))
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement root = document.RootElement;
        string text = root.TryGetProperty("text", out JsonElement t) ? t.GetString() ?? string.Empty : string.Empty;
        bool usable = !root.TryGetProperty("quality", out JsonElement quality) || !quality.TryGetProperty("usable", out JsonElement u) || u.GetBoolean();

        var junctions = ImmutableArray.CreateBuilder<CorpusJunction>();
        if (root.TryGetProperty("junctions", out JsonElement js))
        {
            foreach (JsonElement j in js.EnumerateArray())
            {
                CorpusPoint end = ReadPoint(j.GetProperty("end"), ink);
                CorpusPoint start = ReadPoint(j.GetProperty("start"), ink);
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

        return new CorpusAnnotation(annotator, text, usable, junctions.ToImmutable(), marks.ToImmutable());
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

    private static CorpusPoint ReadPoint(JsonElement element, RulerInk ink)
    {
        int stroke = element.GetProperty("stroke").GetInt32();
        if (element.TryGetProperty("sample", out JsonElement sample))
        {
            return new CorpusPoint(stroke, sample.GetInt32());
        }

        // Привязка по времени точки (мкс): ближайшая точка штриха.
        long time = element.GetProperty("t").GetInt64();
        RulerStroke? s = ink.Strokes.FirstOrDefault(x => x.Index == stroke);
        if (s is null || s.Points.Length == 0)
        {
            return new CorpusPoint(stroke, 0);
        }

        int best = 0;
        for (int i = 1; i < s.Points.Length; i++)
        {
            if (Math.Abs(s.Points[i].TimeUs - time) < Math.Abs(s.Points[best].TimeUs - time))
            {
                best = i;
            }
        }

        return new CorpusPoint(stroke, best);
    }
}
