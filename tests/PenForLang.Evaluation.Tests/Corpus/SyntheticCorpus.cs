using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using PenForLang.Core;
using PenForLang.Evaluation.Tests.Support;
using PenForLang.Glyphs;
using PenForLang.Ink;
using PenForLang.Testing.SynthInk;

namespace PenForLang.Evaluation.Tests.Corpus;

/// <summary>
/// Маленький корпус во временной папке: синтетические попытки слов, записанные как <c>*.pfl.jsonl.gz</c>, и разметка
/// <c>.ann.&lt;annotator&gt;.json</c> с истинными стыками из SynthInk — строго по <c>schemas/pfl.annotation.v1.schema.json</c>
/// (проверяется схемой в <see cref="Add"/>): <c>penlogSha256</c> несжатого JSONL, <c>indexing</c>, <c>strokes[]</c>, точки с <c>sample</c> и
/// <c>t</c>, <c>confidence</c>. Нужен, чтобы проверить читатель и критерии без реальных записей V0.9.
/// </summary>
internal sealed class SyntheticCorpus : IDisposable
{
    private const double XHeightPx = 112.5;
    private const double OriginXPx = 180;
    private const double BaselineYPx = 620;

    private static readonly MiniJsonSchema Schema = MiniJsonSchema.FromRepo("pfl.annotation.v1.schema.json");

    public SyntheticCorpus()
    {
        Root = Path.Combine(Path.GetTempPath(), "pfl-corpus-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    /// <summary>
    /// Добавляет запись и разметку по истине генератора. <paramref name="annotationLifted"/> позволяет намеренно исказить разметку,
    /// <paramref name="target"/> — цель задания в записи (по умолчанию <c>word:&lt;слово&gt;</c>), <paramref name="subfolder"/> — подпапку.
    /// </summary>
    public string Add(
        string name,
        WordTemplate word,
        SynthWordSample sample,
        string annotator = CorpusFormat.DefaultAnnotator,
        Func<SynthJoinTruth, bool>? annotationLifted = null,
        bool usable = true,
        string? variant = null,
        string? target = null,
        string? subfolder = null,
        bool legacyName = false)
    {
        string directory = subfolder is null ? Root : Path.Combine(Root, subfolder);
        Directory.CreateDirectory(directory);
        string recording = Path.Combine(directory, name + CorpusFormat.RecordingSuffix);
        string sha = WriteRecording(recording, sample.Ink, target ?? "word:" + word.Text);
        File.WriteAllText(legacyName ? CorpusFormat.LegacyAnnotationPath(recording) : CorpusFormat.AnnotationPath(recording, annotator), AnnotationJson(recording, word, sample, annotator, annotationLifted, usable, variant, target ?? "word:" + word.Text, sha));
        return recording;
    }

    /// <summary>Разметка по схеме; нарушение схемы — исключение (тест не должен «писать» неверный формат).</summary>
    public static string AnnotationJson(
        string recording,
        WordTemplate word,
        SynthWordSample sample,
        string annotator,
        Func<SynthJoinTruth, bool>? annotationLifted,
        bool usable,
        string? variant,
        string target,
        string sha)
    {
        RulerInk ink = sample.Ink;
        long TimeOf(SynthInkPoint p) => ink.Strokes.Single(s => s.Index == p.Stroke).Points[p.Point].TimeUs;
        var junctions = new List<object>();
        foreach (SynthJoinTruth truth in sample.Joins)
        {
            bool lifted = annotationLifted?.Invoke(truth) ?? truth.Lifted;
            junctions.Add(new
            {
                after = truth.Join,
                kind = lifted ? "lifted" : "connected",
                end = new { stroke = truth.End.Stroke, sample = truth.End.Point, t = TimeOf(truth.End) },
                start = new { stroke = truth.Start.Stroke, sample = truth.Start.Point, t = TimeOf(truth.Start) },
                confidence = "high",
            });
        }

        var deferred = sample.Deferred
            .Select(d => (object)new { stroke = d.Stroke, letter = d.Letter, kind = d.Kind == DeferredKind.Dot ? "dot" : "crossbar", timing = d.Immediate ? "immediate" : "afterWord" })
            .ToList();
        var annotation = new Dictionary<string, object?>
        {
            ["schema"] = "pfl.annotation/1",
            ["penlog"] = Path.GetFileName(recording),
            ["penlogSha256"] = sha,
            ["indexing"] = CorpusFormat.Indexing,
            ["target"] = target,
            ["annotator"] = annotator,
            ["annotatedAt"] = "2026-10-14T00:00:00Z",
            ["tool"] = "tests/SyntheticCorpus",
            ["text"] = word.Text,
            ["quality"] = new { usable, flags = Array.Empty<string>() },
            ["strokes"] = ink.Strokes.Select(s => new { n = s.Points.Length, t0 = s.Points[0].TimeUs, t1 = s.Points[^1].TimeUs }).ToList(),
            ["junctions"] = junctions,
            ["deferred"] = deferred,
        };
        if (variant is not null)
        {
            annotation["variant"] = variant;
        }

        string json = JsonSerializer.Serialize(annotation);
        List<string> errors = Schema.Validate(json);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("The synthetic annotation violates schemas/pfl.annotation.v1.schema.json: " + string.Join("; ", errors));
        }

        return json;
    }

    private static string WriteRecording(string path, RulerInk ink, string target)
    {
        var header = new PenLogHeader(
            "0.10.0",
            "10.0.26100",
            new PenDeviceInfo("XPPen Artist 12 Pro", PenSourceKind.Win32Pointer, 200, 199.6, 1024, true, PenTimestampKind.Qpc),
            new PenScreenInfo(1920, 1080, 256, 144, 1.5, 7.5, PxPerMmSource.Calibrated),
            new GuideFrame(OriginXPx, BaselineYPx, XHeightPx, 7.5, 3.0, null, new StyleId("zaner-bloser")),
            new PenLogTask("t1", "E16", target, "L2", "dictation", "right"),
            null,
            null,
            10_000_000);
        var entries = ImmutableArray.CreateBuilder<PenLogEntry>();
        foreach (RulerStroke stroke in ink.Strokes)
        {
            for (int i = 0; i < stroke.Points.Length; i++)
            {
                RulerPoint p = stroke.Points[i];
                PenPhase phase = i == 0 ? PenPhase.Down : (i == stroke.Points.Length - 1 ? PenPhase.Up : PenPhase.Move);
                entries.Add(new PenLogSampleEntry(new PenSample(p.TimeUs, OriginXPx + (p.U * XHeightPx), BaselineYPx - (p.V * XHeightPx), p.Pressure, 0, 0, phase, PenButtons.None, SampleFlags.None, 3)));
            }
        }

        using var jsonl = new MemoryStream();
        PenLogWriter.Write(jsonl, new PenLog(header, entries.ToImmutable()));
        byte[] bytes = jsonl.ToArray();
        using (FileStream file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        {
            gzip.Write(bytes);
        }

        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
