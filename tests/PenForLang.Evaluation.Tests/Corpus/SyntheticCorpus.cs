using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.Json;
using PenForLang.Core;
using PenForLang.Glyphs;
using PenForLang.Ink;
using PenForLang.Testing.SynthInk;

namespace PenForLang.Evaluation.Tests.Corpus;

/// <summary>
/// Маленький корпус во временной папке: синтетические попытки слов, записанные как <c>*.pfl.jsonl.gz</c>, и разметка
/// <c>.ann.&lt;annotator&gt;.json</c> с истинными стыками из SynthInk. Нужен, чтобы проверить сам корпусный тест (читатель и
/// критерии) без реальных записей V0.9.
/// </summary>
internal sealed class SyntheticCorpus : IDisposable
{
    private const double XHeightPx = 112.5;
    private const double OriginXPx = 180;
    private const double BaselineYPx = 620;

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

    /// <summary>Добавляет запись и разметку по истине генератора; <paramref name="annotationLifted"/> позволяет намеренно исказить разметку.</summary>
    public string Add(string name, WordTemplate word, SynthWordSample sample, string annotator = CorpusFormat.DefaultAnnotator, Func<SynthJoinTruth, bool>? annotationLifted = null, bool usable = true)
    {
        string recording = Path.Combine(Root, name + CorpusFormat.RecordingSuffix);
        WriteRecording(recording, sample.Ink, "word:" + word.Text);

        var junctions = new List<object>();
        foreach (SynthJoinTruth truth in sample.Joins)
        {
            bool lifted = annotationLifted?.Invoke(truth) ?? truth.Lifted;
            junctions.Add(new
            {
                after = truth.Join,
                kind = lifted ? "lifted" : "connected",
                end = new { stroke = truth.End.Stroke, sample = truth.End.Point },
                start = new { stroke = truth.Start.Stroke, sample = truth.Start.Point },
            });
        }

        var deferred = new List<object>();
        foreach (ExpectedFinding immediate in sample.Expected.Where(e => e.Code == ErrorCode.D4))
        {
            WordDeferred slot = word.Deferred.First(d => d.SlotId == immediate.StrokeId);
            deferred.Add(new { stroke = 0, letter = slot.Letter, kind = slot.Kind == DeferredKind.Dot ? "dot" : "crossbar", timing = "immediate" });
        }

        var annotation = new
        {
            schema = "pfl.annotation/1",
            penlog = Path.GetFileName(recording),
            target = "word:" + word.Text,
            annotator,
            text = word.Text,
            quality = new { usable, flags = Array.Empty<string>() },
            junctions,
            deferred,
        };
        File.WriteAllText(CorpusFormat.AnnotationPath(recording, annotator), JsonSerializer.Serialize(annotation));
        return recording;
    }

    private static void WriteRecording(string path, RulerInk ink, string target)
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

        using FileStream file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        PenLogWriter.Write(gzip, new PenLog(header, entries.ToImmutable()));
    }
}
