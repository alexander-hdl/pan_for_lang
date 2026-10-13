using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace PenForLang.Tools.InkLab.Annotation;

/// <summary>
/// Точка записи: штрих и номер точки внутри него. Нумерация — как в <c>PenLog.ToInkAttempt()</c>: штрихи считаются
/// после последнего «Заново» с нуля, внутри штриха — точки контакта по порядку.
/// </summary>
/// <param name="Stroke">Номер штриха, с 0.</param>
/// <param name="Sample">Номер точки в штрихе, с 0.</param>
public readonly record struct StrokePoint(int Stroke, int Sample) : IComparable<StrokePoint>
{
    /// <inheritdoc />
    public int CompareTo(StrokePoint other) => Stroke != other.Stroke ? Stroke.CompareTo(other.Stroke) : Sample.CompareTo(other.Sample);

    /// <summary>Левый операнд строго раньше правого.</summary>
    public static bool operator <(StrokePoint left, StrokePoint right) => left.CompareTo(right) < 0;

    /// <summary>Левый операнд строго позже правого.</summary>
    public static bool operator >(StrokePoint left, StrokePoint right) => left.CompareTo(right) > 0;

    /// <summary>Левый операнд не позже правого.</summary>
    public static bool operator <=(StrokePoint left, StrokePoint right) => left.CompareTo(right) <= 0;

    /// <summary>Левый операнд не раньше правого.</summary>
    public static bool operator >=(StrokePoint left, StrokePoint right) => left.CompareTo(right) >= 0;
}

/// <summary>Как буквы соединены на стыке.</summary>
public enum JunctionKind
{
    /// <summary>Перо не отрывалось: стык лежит внутри одного штриха (<c>connected</c>).</summary>
    Connected,

    /// <summary>Перо оторвано на стыке: предыдущая буква кончается в конце штриха, следующая начинается с начала другого (<c>lifted</c>).</summary>
    Lifted,
}

/// <summary>
/// Стык двух соседних букв. Тело буквы <c>k</c> заканчивается в <see cref="End"/>, тело буквы <c>k+1</c> начинается в
/// <see cref="Start"/>. Участок <c>End..Start</c> — соединительный штрих (если он есть); при отсутствии отдельного соединения
/// <see cref="End"/> = <see cref="Start"/> (одна граница). При <see cref="JunctionKind.Lifted"/> это последняя точка одного
/// штриха и первая точка следующего основного штриха.
/// </summary>
/// <param name="After">Между буквами <c>After</c> и <c>After + 1</c> (с 0).</param>
/// <param name="Kind">Соединены или есть отрыв.</param>
/// <param name="End">Где кончается тело левой буквы.</param>
/// <param name="Start">Где начинается тело правой буквы.</param>
public sealed record Junction(int After, JunctionKind Kind, StrokePoint End, StrokePoint Start);

/// <summary>Что за отложенный штрих.</summary>
public enum DeferredPartKind
{
    /// <summary>Точка (<c>i</c>, <c>j</c>).</summary>
    Dot,

    /// <summary>Перекладина (<c>t</c>, <c>f</c>).</summary>
    Crossbar,

    /// <summary>Другое.</summary>
    Other,
}

/// <summary>Когда поставлен отложенный штрих (ARCHITECTURE.md §3.2.3, §6.4.8).</summary>
public enum DeferredTiming
{
    /// <summary>Сразу после ствола буквы, до следующих букв слова (<c>immediate</c>).</summary>
    Immediate,

    /// <summary>После всего слова (<c>afterWord</c>).</summary>
    AfterWord,
}

/// <summary>Штрих записи, принадлежащий букве как отложенный (точка, перекладина). Из основного следа он исключён.</summary>
/// <param name="Stroke">Номер штриха, с 0.</param>
/// <param name="Letter">К какой букве слова относится (с 0).</param>
/// <param name="Kind">Точка, перекладина или другое.</param>
/// <param name="Timing">Сразу или после слова — по утверждению разметчика.</param>
public sealed record DeferredMark(int Stroke, int Letter, DeferredPartKind Kind, DeferredTiming Timing);

/// <summary>Качество записи.</summary>
/// <param name="Usable">Годится для корпуса (<c>true</c>) или брак (<c>false</c>).</param>
/// <param name="Flags">Флаги из <see cref="QualityFlags"/> (необязательно).</param>
/// <param name="Note">Свободная заметка разметчика.</param>
public sealed record QualityMark(bool Usable, ImmutableArray<string> Flags, string? Note = null)
{
    /// <summary>Годная запись без флагов.</summary>
    public static QualityMark Good { get; } = new(true, []);
}

/// <summary>Известные флаги качества.</summary>
public static class QualityFlags
{
    /// <summary>Написано не то, что просили.</summary>
    public const string WrongContent = "wrongContent";

    /// <summary>Заметные исправления и дописывания поверх.</summary>
    public const string Corrected = "corrected";

    /// <summary>Заметная пауза или колебание посреди штриха.</summary>
    public const string Hesitation = "hesitation";

    /// <summary>Значительно вышло за линейки.</summary>
    public const string OutOfGuides = "outOfGuides";

    /// <summary>Сбой устройства: пропуски точек, скачки.</summary>
    public const string DeviceGlitch = "deviceGlitch";

    /// <summary>Другое (пояснение — в заметке).</summary>
    public const string Other = "other";

    /// <summary>Все известные флаги.</summary>
    public static IReadOnlyList<string> All { get; } = [WrongContent, Corrected, Hesitation, OutOfGuides, DeviceGlitch, Other];
}

/// <summary>
/// Разметка одной записи пера — формат <c>pfl.annotation/1</c> (файл <c>&lt;запись&gt;.pfl.jsonl.gz.ann.json</c> рядом с записью).
/// Разметчик задаёт: что фактически написано, границы букв, соединения, отложенные штрихи, годность записи.
/// </summary>
/// <remarks>
/// <para><b>Основной след</b> — все штрихи, кроме отложенных (<see cref="Deferred"/>), в порядке написания.
/// Граница буквы — точка основного следа; стыки заданы по порядку букв.</para>
/// <para><b>Полнота.</b> Для годной записи (<see cref="QualityMark.Usable"/>) стыков должно быть ровно <c>букв − 1</c>
/// (<see cref="AnnotationValidator"/>). Для брака стыки не нужны.</para>
/// <para><b>Привязка к записи.</b> <see cref="StrokeSamples"/> хранит число точек в каждом штрихе на момент разметки; если запись
/// пересобрали иначе, проверка это заметит.</para>
/// </remarks>
/// <param name="Schema">Всегда <see cref="AnnotationFormat.Schema"/>.</param>
/// <param name="Penlog">Имя файла записи (без пути).</param>
/// <param name="Target">Цель из <c>task.target</c> записи (<c>word:at</c>) — для удобства чтения.</param>
/// <param name="Annotator">Кто размечал (идентификатор, без имён).</param>
/// <param name="AnnotatedAt">Когда сохранено (UTC, ISO 8601).</param>
/// <param name="Text">Что фактически написано (буквы по порядку, без пробелов).</param>
/// <param name="Quality">Годится или брак.</param>
/// <param name="StrokeSamples">Сколько точек в каждом штрихе записи.</param>
/// <param name="Junctions">Стыки между буквами.</param>
/// <param name="Deferred">Отложенные штрихи.</param>
public sealed record AnnotationFile(
    string Schema,
    string Penlog,
    string? Target,
    string Annotator,
    string? AnnotatedAt,
    string Text,
    QualityMark Quality,
    ImmutableArray<int> StrokeSamples,
    ImmutableArray<Junction> Junctions,
    ImmutableArray<DeferredMark> Deferred)
{
    /// <summary>Число букв: длина <see cref="Text"/>.</summary>
    [JsonIgnore]
    public int LetterCount => Text.Length;

    /// <summary>Номера отложенных штрихов.</summary>
    [JsonIgnore]
    public IReadOnlySet<int> DeferredStrokes => Deferred.Select(d => d.Stroke).ToHashSet();
}

/// <summary>Константы формата <c>pfl.annotation/1</c>.</summary>
public static class AnnotationFormat
{
    /// <summary>Значение поля <c>schema</c>.</summary>
    public const string Schema = "pfl.annotation/1";
}
