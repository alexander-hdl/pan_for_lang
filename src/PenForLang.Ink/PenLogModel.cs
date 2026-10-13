using System.Collections.Immutable;

namespace PenForLang.Ink;

/// <summary>Константы формата лога пера <c>pfl.penlog/1</c> (ARCHITECTURE.md §3.4).</summary>
public static class PenLogFormat
{
    /// <summary>Имя схемы без версии.</summary>
    public const string SchemaName = "pfl.penlog";

    /// <summary>Текущая (и единственная поддерживаемая) мажорная версия формата.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Значение поля <c>schema</c> заголовка: <c>pfl.penlog/1</c>.</summary>
    public const string Schema = "pfl.penlog/1";
}

/// <summary>Нарушение формата penlog: некорректный JSON, неподдерживаемая версия, нет обязательных полей, нарушен порядок.</summary>
public sealed class PenLogFormatException : FormatException
{
    /// <summary>Создаёт исключение.</summary>
    /// <param name="message">Описание проблемы.</param>
    /// <param name="lineNumber">Номер строки лога (с 1) или 0, если проблема не привязана к строке.</param>
    public PenLogFormatException(string message, int lineNumber = 0)
        : base(lineNumber > 0 ? $"penlog line {lineNumber}: {message}" : message)
    {
        LineNumber = lineNumber;
    }

    /// <summary>Номер строки лога (с 1) или 0.</summary>
    public int LineNumber { get; }
}

/// <summary>Происхождение значения <c>PxPerMm</c> (поле <c>pxPerMmSource</c>).</summary>
public enum PxPerMmSource
{
    /// <summary>Значение по умолчанию (7,5 px/мм), <c>default</c> в penlog.</summary>
    Default,

    /// <summary>EDID монитора, <c>edid</c> в penlog.</summary>
    Edid,

    /// <summary>Калибровка родителем по банковской карте, <c>calibrated</c> в penlog.</summary>
    Calibrated,
}

/// <summary>Монитор пера и его масштаб — поле <c>screen</c> заголовка penlog.</summary>
/// <param name="WidthPx">Ширина монитора, физические px.</param>
/// <param name="HeightPx">Высота монитора, физические px.</param>
/// <param name="WidthMm">Ширина монитора, мм.</param>
/// <param name="HeightMm">Высота монитора, мм.</param>
/// <param name="DpiScale">DPI-масштаб монитора (1.0 = 100%).</param>
/// <param name="PxPerMm">Физических пикселей в миллиметре.</param>
/// <param name="PxPerMmSource">Происхождение <paramref name="PxPerMm"/>.</param>
public sealed record PenScreenInfo(
    int WidthPx,
    int HeightPx,
    double WidthMm,
    double HeightMm,
    double DpiScale,
    double PxPerMm,
    PxPerMmSource PxPerMmSource);

/// <summary>Задание, во время которого писали, — поле <c>task</c> заголовка penlog.</summary>
/// <param name="TaskId">Идентификатор задания.</param>
/// <param name="Exercise">Код упражнения (например <c>E16</c>).</param>
/// <param name="Target">Цель (например <c>word:cat</c>).</param>
/// <param name="Level">Уровень опоры (например <c>L0</c>).</param>
/// <param name="Mode">Режим (например <c>dictation</c>).</param>
/// <param name="Handedness">Ведущая рука: <c>right</c> или <c>left</c>.</param>
public sealed record PenLogTask(string TaskId, string Exercise, string Target, string Level, string Mode, string Handedness);

/// <summary>Вид субъекта записи.</summary>
public enum PenSubjectKind
{
    /// <summary>Взрослый, <c>adult</c> в penlog.</summary>
    Adult,

    /// <summary>Ребёнок, <c>child</c> в penlog.</summary>
    Child,
}

/// <summary>Субъект записи без имени — поле <c>subject</c> заголовка penlog.</summary>
/// <param name="Anon">Анонимный идентификатор.</param>
/// <param name="AgeYears">Возраст, лет, если известен.</param>
/// <param name="Kind">Взрослый или ребёнок.</param>
public sealed record PenLogSubject(string Anon, int? AgeYears, PenSubjectKind Kind);

/// <summary>Согласие на запись траектории — поле <c>consent</c> заголовка penlog (обязательно для детских записей).</summary>
/// <param name="Id">Номер согласия.</param>
/// <param name="Kind">Вид согласия (например <c>trajectory_recording</c>).</param>
/// <param name="Version">Версия текста согласия.</param>
public sealed record PenLogConsent(int Id, string Kind, string Version);

/// <summary>Заголовок лога пера — первая строка файла (<c>type = header</c>).</summary>
/// <param name="App">Версия приложения.</param>
/// <param name="Os">Версия ОС.</param>
/// <param name="Device">Устройство ввода.</param>
/// <param name="Screen">Монитор пера (размер, DPI, <c>PxPerMm</c>).</param>
/// <param name="Guides">Линейка попытки. <c>PxPerMm</c> линейки при чтении берётся из <paramref name="Screen"/>.</param>
/// <param name="Task">Задание или <c>null</c>.</param>
/// <param name="Subject">Субъект записи или <c>null</c>.</param>
/// <param name="Consent">Согласие или <c>null</c>.</param>
/// <param name="QpcFreq">Частота QPC, Гц (информационно; время точек в логе — уже мкс).</param>
public sealed record PenLogHeader(
    string App,
    string Os,
    PenDeviceInfo Device,
    PenScreenInfo Screen,
    GuideFrame Guides,
    PenLogTask? Task,
    PenLogSubject? Subject,
    PenLogConsent? Consent,
    long QpcFreq);

/// <summary>Запись лога после заголовка: точка пера или событие.</summary>
public abstract record PenLogEntry
{
    /// <summary>Время записи, мкс от начала задания.</summary>
    public abstract long TimeUs { get; }
}

/// <summary>Запись-точка (<c>type = p</c>).</summary>
/// <param name="Sample">Точка.</param>
public sealed record PenLogSampleEntry(PenSample Sample) : PenLogEntry
{
    /// <inheritdoc />
    public override long TimeUs => Sample.TimeUs;
}

/// <summary>Запись-событие (<c>type = ev</c>).</summary>
/// <param name="Event">Событие.</param>
public sealed record PenLogEventEntry(InkEvent Event) : PenLogEntry
{
    /// <inheritdoc />
    public override long TimeUs => Event.TimeUs;
}

/// <summary>Прочитанный (или собранный для записи) лог пера целиком: заголовок и записи в хронологическом порядке.</summary>
/// <param name="Header">Заголовок.</param>
/// <param name="Entries">Точки и события; время не убывает.</param>
public sealed record PenLog(PenLogHeader Header, ImmutableArray<PenLogEntry> Entries)
{
    /// <summary>Только точки, в порядке записи.</summary>
    public IEnumerable<PenSample> Samples => Entries.OfType<PenLogSampleEntry>().Select(e => e.Sample);

    /// <summary>Только события, в порядке записи.</summary>
    public IEnumerable<InkEvent> Events => Entries.OfType<PenLogEventEntry>().Select(e => e.Event);

    /// <summary>
    /// Собирает <see cref="InkAttempt"/>: штрихи — из точек (<see cref="StrokeAssembler"/>), <c>StimulusEndUs</c> — из события
    /// <c>stimulus-end</c>, <c>Restarts</c> — число <c>restart-button</c>, <c>Completion</c> — из последнего <c>completed</c>
    /// (поле <c>by</c>; нет события или неизвестное значение — <see cref="InkCompletion.Cancelled"/>).
    /// Штрихи до «Заново» не удаляются: решение об отбрасывании принимает вызывающий.
    /// </summary>
    public InkAttempt ToInkAttempt()
    {
        var assembler = new StrokeAssembler();
        var events = ImmutableArray.CreateBuilder<InkEvent>();
        long? stimulusEnd = null;
        int restarts = 0;
        InkCompletion completion = InkCompletion.Cancelled;

        foreach (PenLogEntry entry in Entries)
        {
            switch (entry)
            {
                case PenLogSampleEntry sampleEntry:
                    assembler.Add(sampleEntry.Sample);
                    break;
                case PenLogEventEntry eventEntry:
                    InkEvent ev = eventEntry.Event;
                    events.Add(ev);
                    switch (ev.Name)
                    {
                        case InkEvent.Names.StimulusEnd:
                            stimulusEnd = ev.TimeUs;
                            break;
                        case InkEvent.Names.RestartButton:
                            restarts++;
                            break;
                        case InkEvent.Names.Completed:
                            completion = ParseCompletion(ev);
                            break;
                        default:
                            break;
                    }

                    break;
                default:
                    break;
            }
        }

        return new InkAttempt(assembler.ToStrokes(), Header.Guides, Header.Device, stimulusEnd, completion, restarts, events.ToImmutable());
    }

    private static InkCompletion ParseCompletion(InkEvent ev)
    {
        string? by = null;
        ev.Data?.TryGetValue("by", out by);
        return by switch
        {
            "pause" => InkCompletion.Pause,
            "done-button" => InkCompletion.DoneButton,
            "timer" => InkCompletion.Timer,
            "asked-confirmed" => InkCompletion.AskedConfirmed,
            _ => InkCompletion.Cancelled,
        };
    }
}
