using System.Collections.Immutable;
using PenForLang.Core;

namespace PenForLang.Ink.Tests;

/// <summary>Общие фабрики тестовых данных ввода.</summary>
internal static class TestData
{
    public static PenDeviceInfo Device() =>
        new("XPPen Artist 12 Pro", PenSourceKind.Win32Pointer, 200, 199.6, 1024, true, PenTimestampKind.Qpc);

    public static PenScreenInfo Screen(double pxPerMm = 7.5, double dpiScale = 1.5) =>
        new(1920, 1080, 256, 144, dpiScale, pxPerMm, PxPerMmSource.Calibrated);

    public static GuideFrame Guides(double pxPerMm = 7.5) =>
        new(180, 620, 112.5, pxPerMm, 3.0, null, new StyleId("zaner-bloser"));

    public static PenLogHeader Header(PenLogTask? task = null, PenLogSubject? subject = null, PenLogConsent? consent = null) =>
        new("0.3.0", "10.0.26100", Device(), Screen(), Guides(), task, subject, consent, 10_000_000);

    public static PenLogHeader FullHeader() => Header(
        new PenLogTask("s12-t07", "E16", "word:cat", "L0", "dictation", "right"),
        new PenLogSubject("p-3f9a", 9, PenSubjectKind.Child),
        new PenLogConsent(17, "trajectory_recording", "1.0"));

    public static PenSample Sample(
        long t,
        double x,
        double y,
        PenPhase phase = PenPhase.Move,
        uint pid = 3,
        float pressure = 0.5f,
        PenButtons buttons = PenButtons.None,
        SampleFlags flags = SampleFlags.None,
        float tiltX = 0,
        float tiltY = 0) =>
        new(t, x, y, pressure, tiltX, tiltY, phase, buttons, flags, pid);

    public static InkEvent Event(long t, string name, params (string Key, string Value)[] data) =>
        new(t, name, data.Length == 0 ? null : data.ToDictionary(d => d.Key, d => d.Value));

    public static PenLog Log(PenLogHeader header, params object[] entries) =>
        new(header, [.. entries.Select(Entry)]);

    public static PenLogEntry Entry(object item) => item switch
    {
        PenSample s => new PenLogSampleEntry(s),
        InkEvent e => new PenLogEventEntry(e),
        _ => throw new ArgumentException("Unsupported entry."),
    };

    /// <summary>Штрих из <paramref name="count"/> точек с шагом 5 мс (200 Гц), начиная с <paramref name="startUs"/>.</summary>
    public static ImmutableArray<PenSample> Stroke(long startUs, int count, uint pid = 3, double x0 = 200, double y0 = 600)
    {
        var builder = ImmutableArray.CreateBuilder<PenSample>(count);
        for (int i = 0; i < count; i++)
        {
            PenPhase phase = i == 0 ? PenPhase.Down : i == count - 1 ? PenPhase.Up : PenPhase.Move;
            builder.Add(Sample(startUs + (i * 5000L), x0 + (i * 1.5), y0 - (i * 0.5), phase, pid, 0.25f + (i % 4 * 0.125f)));
        }

        return builder.ToImmutable();
    }

    public static void AssertLogsEqual(PenLog expected, PenLog actual)
    {
        Assert.Equal(expected.Header, actual.Header);
        Assert.Equal(expected.Entries.Length, actual.Entries.Length);
        for (int i = 0; i < expected.Entries.Length; i++)
        {
            Assert.True(expected.Entries[i].Equals(actual.Entries[i]), $"Entry {i} differs: {expected.Entries[i]} vs {actual.Entries[i]}");
        }
    }
}
