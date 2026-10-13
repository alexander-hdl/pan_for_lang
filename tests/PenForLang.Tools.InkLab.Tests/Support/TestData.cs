using System.Collections.Immutable;
using System.IO;
using PenForLang.Core;
using PenForLang.Ink;
using PenForLang.Tools.InkLab.Corpus;
using PenForLang.Tools.InkLab.Recording;

namespace PenForLang.Tools.InkLab.Tests.Support;

/// <summary>Временная папка теста; удаляется при <see cref="Dispose"/>.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pfl-inklab-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Папка во временном каталоге: сбой очистки не должен ронять тест.
        }
    }
}

/// <summary>Синтетические записи для тестов: линейка, штрихи по ломаным, готовые penlog.</summary>
internal static class TestData
{
    /// <summary>x-height в px и положение линейки: origin (200, 600), baseline 600.</summary>
    public const double XHeightPx = 100;

    public static GuideFrame Frame { get; } = new(200, 600, XHeightPx, 7.5, 3.0, null, new StyleId("zaner-bloser"));

    public static PenDeviceInfo Device { get; } = new("Test pen", PenSourceKind.Mouse, null, null, null, false, PenTimestampKind.Qpc);

    public static PenScreenInfo Screen { get; } = new(1920, 1080, 256, 144, 1.0, 7.5, PxPerMmSource.Default);

    public static RecordingContext Context { get; } = new(Device, Screen, Frame, "10.0.19045", "0.1.0", FakeFrequency);

    public const long FakeFrequency = 10_000_000;

    /// <summary>Точка ломаной в координатах линейки (u, v) → пиксели поверхности.</summary>
    public static (double X, double Y) Px(double u, double v) => RulerProjection.ToSurface(Frame, u, v);

    /// <summary>Штрих по ломаной в координатах линейки: down, move…, up; шаг 5 мс (200 Гц).</summary>
    public static PenSample[] Stroke(long startUs, uint pointerId, params (double U, double V)[] points)
    {
        var samples = new PenSample[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            (double x, double y) = Px(points[i].U, points[i].V);
            PenPhase phase = i == 0 ? PenPhase.Down : (i == points.Length - 1 ? PenPhase.Up : PenPhase.Move);
            samples[i] = new PenSample(startUs + (i * 5000L), x, y, 0.5f, 0, 0, phase, PenButtons.None, SampleFlags.None, pointerId);
        }

        return samples;
    }

    /// <summary>Равномерно размеченная прямая от (u0, v0) до (u1, v1) из <paramref name="count"/> точек.</summary>
    public static (double U, double V)[] Line(double u0, double v0, double u1, double v1, int count)
    {
        var points = new (double, double)[count];
        for (int i = 0; i < count; i++)
        {
            double t = count == 1 ? 0 : (double)i / (count - 1);
            points[i] = (u0 + ((u1 - u0) * t), v0 + ((v1 - v0) * t));
        }

        return points;
    }

    public static PenLogHeader Header(string taskId = "inklab-v0.9/at-lift-001", string target = "word:at") =>
        new(
            "0.1.0",
            "10.0.19045",
            Device,
            Screen,
            Frame,
            new PenLogTask(taskId, "INKLAB", target, "L0", "copy", "right"),
            new PenLogSubject("tester", null, PenSubjectKind.Adult),
            null,
            FakeFrequency);

    /// <summary>Penlog из штрихов (каждый — массив точек) и событий-дополнений.</summary>
    public static PenLog Log(string taskId, string target, IEnumerable<PenSample[]> strokes, IEnumerable<InkEvent>? extra = null)
    {
        var entries = new List<PenLogEntry>();
        foreach (PenSample[] stroke in strokes)
        {
            entries.AddRange(stroke.Select(s => (PenLogEntry)new PenLogSampleEntry(s)));
        }

        entries.AddRange((extra ?? []).Select(e => (PenLogEntry)new PenLogEventEntry(e)));
        PenLogEntry[] ordered = [.. entries.OrderBy(e => e.TimeUs)];
        return new PenLog(Header(taskId, target), [.. ordered]);
    }

    /// <summary>
    /// Слово «at» из трёх штрихов: штрих 0 — буква a (u от 0 до 1,0, 21 точка), штрих 1 — ствол t (u 1,2…1,4, 21 точка),
    /// штрих 2 — перекладина t (u 1,0…1,6 на высоте 1,0, 11 точек). Стык a|t — между штрихами 0 и 1 (отрыв).
    /// </summary>
    public static PenLog AtWordLifted(string taskId = "inklab-v0.9/at-lift-001") => Log(
        taskId,
        "word:at",
        [
            Stroke(0, 1, Line(0.0, 0.5, 1.0, 0.5, 21)),
            Stroke(200_000, 1, Line(1.2, 1.5, 1.4, 0.0, 21)),
            Stroke(500_000, 1, Line(1.0, 1.0, 1.6, 1.0, 11)),
        ]);

    /// <summary>Слово «at» слитно: два штриха; штрих 0 — a и ствол t (стык внутри штриха, точка 20 из 41), штрих 1 — перекладина.</summary>
    public static PenLog AtWordJoined(string taskId = "inklab-v0.9/at-crossbar-001")
    {
        (double, double)[] line = [.. Line(0.0, 0.5, 1.0, 0.5, 21), .. Line(1.0, 0.5, 1.4, 0.0, 21).Skip(1)];
        return Log(taskId, "word:at", [Stroke(0, 1, line), Stroke(400_000, 1, Line(1.0, 1.0, 1.6, 1.0, 11))]);
    }

    /// <summary>Одна буква «i»: штрих-ствол и точка.</summary>
    public static PenLog LetterI(string taskId = "inklab-v0.9/i-001") => Log(
        taskId,
        "glyph:i",
        [
            Stroke(0, 1, Line(0.0, 1.0, 0.0, 0.0, 15)),
            Stroke(300_000, 1, Line(0.0, 1.5, 0.0, 1.55, 3)),
        ]);

    public static string WriteLog(string folder, string name, PenLog log)
    {
        Directory.CreateDirectory(folder);
        string path = System.IO.Path.Combine(folder, name + CorpusStore.PenLogExtension);
        CorpusStore.WritePenLogFile(path, log);
        return path;
    }

    public static ImmutableArray<InkStroke> Strokes(PenLog log) => log.ToInkAttempt().Strokes;
}
