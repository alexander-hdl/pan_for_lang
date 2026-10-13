using System.Globalization;
using System.Text;
using PenForLang.Wpf.Controls;

namespace PenForLang.App.Wpf;

/// <summary>Данные dev-оверлея V0.6 (ARCHITECTURE.md §7.5): всё, что показывается разработчику поверх холста.</summary>
public sealed record DevOverlayInput
{
    /// <summary>Вид источника ввода.</summary>
    public required string SourceKind { get; init; }

    /// <summary>Медианная частота точек контакта за последние интервалы, Гц (0 — мало данных).</summary>
    public double RateHz { get; init; }

    /// <summary>5-й процентиль мгновенной частоты, Гц.</summary>
    public double RateP5Hz { get; init; }

    /// <summary>Задержка «метка точки → Rendering».</summary>
    public RenderLatency Latency { get; init; }

    /// <summary>Штрихов в живых чернилах.</summary>
    public int Strokes { get; init; }

    /// <summary>Отрезков в живых чернилах.</summary>
    public int Segments { get; init; }

    /// <summary>Перо касается поверхности.</summary>
    public bool PenDown { get; init; }

    /// <summary>Толщина линии зависит от давления.</summary>
    public bool PressureWidth { get; init; } = true;

    /// <summary>DPI-масштаб окна.</summary>
    public double DpiScale { get; init; } = 1.0;

    /// <summary>Номер монитора пера (с 1), 0 — неизвестен.</summary>
    public int MonitorNumber { get; init; }

    /// <summary>Имя монитора.</summary>
    public string? MonitorName { get; init; }

    /// <summary>Размер монитора, px.</summary>
    public (int Width, int Height)? MonitorSizePx { get; init; }

    /// <summary>Размер монитора, мм.</summary>
    public (double Width, double Height)? MonitorSizeMm { get; init; }

    /// <summary>Физических пикселей в миллиметре и его происхождение.</summary>
    public double PxPerMm { get; init; }

    /// <summary>Происхождение <see cref="PxPerMm"/>.</summary>
    public string PxPerMmSource { get; init; } = "default";

    /// <summary>Режим GC на время попытки включён.</summary>
    public bool GcLowLatencyActive { get; init; }

    /// <summary>Сборок GC поколений 0, 1, 2.</summary>
    public (int Gen0, int Gen1, int Gen2) GcCollections { get; init; }

    /// <summary>Суммарное время пауз GC.</summary>
    public TimeSpan GcPause { get; init; }

    /// <summary>Буква песочницы.</summary>
    public string Letter { get; init; } = string.Empty;

    /// <summary>Номер попытки.</summary>
    public int Attempt { get; init; }

    /// <summary>Нажатий «Заново» в попытке.</summary>
    public int Restarts { get; init; }

    /// <summary>Записывается ли penlog.</summary>
    public bool Recording { get; init; }

    /// <summary>Файл penlog текущей попытки.</summary>
    public string? PenLogPath { get; init; }

    /// <summary>Точек в текущей записи.</summary>
    public int PenLogSamples { get; init; }

    /// <summary>Завершённых записанных попыток.</summary>
    public int Recorded { get; init; }

    /// <summary>Ошибка записи penlog.</summary>
    public string? PenLogError { get; init; }
}

/// <summary>Текст dev-оверлея V0.6: частота, задержка, штрихи, DPI, монитор, GC, попытка. Чистая функция — тестируется без окна.</summary>
public static class DevOverlayText
{
    private static string F(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>Строит текст оверлея.</summary>
    public static string Build(DevOverlayInput i)
    {
        ArgumentNullException.ThrowIfNull(i);
        CultureInfo inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();

        sb.AppendLine("PenForLang V0.6 - live ink (dev overlay; F12 - hide, F11 - details)");
        string rate = i.RateHz > 0 ? F(i.RateHz) + " Hz (p5 " + F(i.RateP5Hz) + ")" : "-";
        sb.AppendLine(inv, $"Source: {i.SourceKind}   rate: {rate}");

        if (i.Latency.IsEmpty)
        {
            sb.AppendLine("Latency (point -> Rendering): no data yet");
        }
        else
        {
            RenderLatency l = i.Latency;
            sb.AppendLine(inv, $"Latency (point -> Rendering): mean {l.MeanPointToRenderMs:0.0} ms, max {l.MaxPointToRenderMs:0.0}, last {l.LastPointToRenderMs:0.0} ({l.Frames} frames)");
            sb.AppendLine(inv, $"  app only (arrival -> Rendering): mean {l.MeanArrivalToRenderMs:0.0} ms, max {l.MaxArrivalToRenderMs:0.0}");
        }

        sb.AppendLine(inv, $"Ink: {i.Strokes} strokes, {i.Segments} segments, pen {(i.PenDown ? "DOWN" : "up")}, pressure width {(i.PressureWidth ? "on" : "off")}");
        sb.AppendLine(inv, $"GC: SustainedLowLatency {(i.GcLowLatencyActive ? "ON" : "off")}; collections {i.GcCollections.Gen0}/{i.GcCollections.Gen1}/{i.GcCollections.Gen2}; pauses {i.GcPause.TotalMilliseconds:0.0} ms");
        sb.AppendLine(inv, $"DPI: {i.DpiScale * 100:0}% ({i.DpiScale * 96:0} dpi)   {i.PxPerMm:0.00} px/mm ({i.PxPerMmSource})");

        string monitor = i.MonitorName is null
            ? "-"
            : (i.MonitorNumber > 0 ? "[" + i.MonitorNumber.ToString(inv) + "] " : string.Empty) + i.MonitorName
                + (i.MonitorSizePx is { } px ? " " + px.Width.ToString(inv) + "x" + px.Height.ToString(inv) + " px" : string.Empty)
                + (i.MonitorSizeMm is { } mm ? " " + mm.Width.ToString("0", inv) + "x" + mm.Height.ToString("0", inv) + " mm" : string.Empty);
        sb.AppendLine(inv, $"Monitor: {monitor}");

        sb.AppendLine(inv, $"Attempt #{i.Attempt}: letter '{i.Letter}', restarts {i.Restarts}");
        if (!i.Recording)
        {
            sb.AppendLine("Penlog: off (needs --dev)");
        }
        else
        {
            string file = i.PenLogPath is null ? "(opened with the attempt; removed if there are no touches)" : i.PenLogPath + " (" + i.PenLogSamples.ToString(inv) + " samples)";
            sb.AppendLine(inv, $"Penlog: {file}; saved attempts {i.Recorded}");
        }

        if (i.PenLogError is not null)
        {
            sb.AppendLine("Penlog ERROR: " + i.PenLogError);
        }

        return sb.ToString().TrimEnd();
    }
}
