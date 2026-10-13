using System.Collections.Immutable;
using System.Text.Json.Serialization;
using PenForLang.Ink;

namespace PenForLang.Tools.InkLab.Annotation;

/// <summary>
/// Точка записи: штрих и номер точки внутри него. Нумерация — <see cref="AnnotationFormat.Indexing"/>: штрихи после последнего
/// «Заново» с нуля, внутри штриха — точки контакта по порядку.
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

/// <summary>Точка в файле разметки: штрих, точка и её время (проверка привязки к записи).</summary>
/// <param name="Stroke">Номер штриха, с 0.</param>
/// <param name="Sample">Номер точки в штрихе, с 0.</param>
/// <param name="T">Время точки, мкс от начала задания (<c>PenSample.TimeUs</c>).</param>
public sealed record PointRef(int Stroke, int Sample, long T)
{
    /// <summary>Штрих и точка без времени.</summary>
    [JsonIgnore]
    public StrokePoint Point => new(Stroke, Sample);

    /// <summary>Ссылка на точку штриха записи (с её временем); <c>null</c>, если точки нет.</summary>
    public static PointRef? Of(IReadOnlyList<InkStroke> strokes, StrokePoint point)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        if (point.Stroke < 0 || point.Stroke >= strokes.Count || point.Sample < 0 || point.Sample >= strokes[point.Stroke].Samples.Length)
        {
            return null;
        }

        return new PointRef(point.Stroke, point.Sample, strokes[point.Stroke].Samples[point.Sample].TimeUs);
    }
}

/// <summary>Как соединены соседние буквы: определяется штрихами, а не выбором разметчика.</summary>
public enum JunctionKind
{
    /// <summary>Перо не отрывалось: конец левой буквы и начало правой — в одном штрихе (<c>connected</c>).</summary>
    Connected,

    /// <summary>Между концом левой буквы и началом правой — отрыв пера основного следа: они в разных штрихах (<c>lifted</c>).</summary>
    Lifted,
}

/// <summary>Уверенность разметчика в стыке.</summary>
public enum Confidence
{
    /// <summary>Граница очевидна.</summary>
    High,

    /// <summary>Граница с разбросом в несколько точек.</summary>
    Medium,

    /// <summary>Граница условна (слитный росчерк, нет явного места).</summary>
    Low,
}

/// <summary>
/// Стык двух соседних букв. <see cref="End"/> — выход тела левой буквы (exit глифа), <see cref="Start"/> — вход тела правой
/// (entry глифа; у <c>t</c> — верх подъёма). Участок <c>End…Start</c> — соединительный штрих (если End = Start, его нет).
/// <see cref="Kind"/> следует из штрихов: «соединены» — один штрих; «отрыв» — End и Start в разных штрихах, точки могут стоять где угодно в своих штрихах.
/// </summary>
/// <param name="After">Между буквами <c>After</c> и <c>After + 1</c> (с 0).</param>
/// <param name="Kind">Соединены или отрыв.</param>
/// <param name="End">Где кончается тело левой буквы.</param>
/// <param name="Start">Где начинается тело правой буквы.</param>
/// <param name="Confidence">Уверенность разметчика.</param>
public sealed record Junction(int After, JunctionKind Kind, PointRef End, PointRef Start, Confidence Confidence = Confidence.High)
{
    /// <summary>Вид стыка по положению точек: один штрих — соединены, разные — отрыв.</summary>
    public static JunctionKind KindOf(StrokePoint end, StrokePoint start) =>
        end.Stroke == start.Stroke ? JunctionKind.Connected : JunctionKind.Lifted;
}

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
/// <param name="Timing">Время, ВЫВЕДЕННОЕ из порядка штрихов (<see cref="AnnotationFile.IsImmediate"/>); валидатор предупреждает, если оно устарело.</param>
/// <param name="TimingOpinion">Мнение разметчика о времени, только если он с выведенным не согласен; иначе <c>null</c>.</param>
public sealed record DeferredMark(int Stroke, int Letter, DeferredPartKind Kind, DeferredTiming Timing = DeferredTiming.AfterWord, DeferredTiming? TimingOpinion = null);

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

/// <summary>Сведения о штрихе записи на момент разметки: число точек и времена первой и последней (привязка к записи).</summary>
/// <param name="N">Число точек в штрихе.</param>
/// <param name="T0">Время первой точки, мкс.</param>
/// <param name="T1">Время последней точки, мкс.</param>
public sealed record StrokeInfo(int N, long T0, long T1)
{
    /// <summary>Сведения о штрихах записи.</summary>
    public static ImmutableArray<StrokeInfo> Of(IReadOnlyList<InkStroke> strokes)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        return
        [
            .. strokes.Select(s => s.Samples.Length == 0
                ? new StrokeInfo(0, 0, 0)
                : new StrokeInfo(s.Samples.Length, s.Samples[0].TimeUs, s.Samples[^1].TimeUs)),
        ];
    }
}

/// <summary>
/// Разметка одной записи пера одним разметчиком — формат <c>pfl.annotation/1</c> (файл
/// <c>&lt;запись&gt;.pfl.jsonl.gz.ann.&lt;разметчик&gt;.json</c> рядом с записью). Разметчик задаёт: что фактически написано, границы
/// букв и соединения, отложенные штрихи, годность записи.
/// </summary>
/// <remarks>
/// <para><b>Основной след</b> — все штрихи, кроме отложенных (<see cref="Deferred"/>), в порядке написания. Тело буквы <c>k</c> — от
/// <c>Start</c> стыка <c>k−1</c> (для первой — от начала следа) до <c>End</c> стыка <c>k</c> (для последней — до конца следа).</para>
/// <para><b>Привязка к записи.</b> <see cref="PenlogSha256"/> — SHA-256 несжатого JSONL; <see cref="Strokes"/> и время <c>t</c> точек
/// проверяются валидатором. Номера — по <see cref="Indexing"/>.</para>
/// <para><b>Версии.</b> Неизвестные поля и другая версия схемы — ошибка чтения (<see cref="AnnotationFormatException"/>).</para>
/// </remarks>
/// <param name="Schema">Всегда <see cref="AnnotationFormat.Schema"/>.</param>
/// <param name="Penlog">Имя файла записи (без пути; справочно).</param>
/// <param name="PenlogSha256">SHA-256 несжатого JSONL записи, 64 hex-символа.</param>
/// <param name="Indexing">Правило нумерации штрихов и точек (<see cref="AnnotationFormat.Indexing"/>).</param>
/// <param name="Target">Цель из <c>task.target</c> записи (<c>word:at</c>).</param>
/// <param name="Variant">Вариант написания из записи (<c>lift</c>, <c>joined</c>, <c>dot</c>) или <c>null</c>.</param>
/// <param name="Annotator">Кто размечал (идентификатор, без имён).</param>
/// <param name="AnnotatedAt">Когда сохранено (UTC, ISO 8601).</param>
/// <param name="Tool">Какой программой сделана (<c>InkLab 0.1.0</c>).</param>
/// <param name="Text">Что фактически написано (буквы по порядку, без пробелов).</param>
/// <param name="Quality">Годится или брак.</param>
/// <param name="Strokes">Сведения о штрихах записи на момент разметки.</param>
/// <param name="Junctions">Стыки между буквами.</param>
/// <param name="Deferred">Отложенные штрихи.</param>
public sealed record AnnotationFile(
    string Schema,
    string Penlog,
    string PenlogSha256,
    string Indexing,
    string? Target,
    string? Variant,
    string Annotator,
    string? AnnotatedAt,
    string Tool,
    string Text,
    QualityMark Quality,
    ImmutableArray<StrokeInfo> Strokes,
    ImmutableArray<Junction> Junctions,
    ImmutableArray<DeferredMark> Deferred)
{
    /// <summary>Число букв: длина <see cref="Text"/>.</summary>
    [JsonIgnore]
    public int LetterCount => Text.Length;

    /// <summary>Номера отложенных штрихов.</summary>
    [JsonIgnore]
    public IReadOnlySet<int> DeferredStrokes => Deferred.Select(d => d.Stroke).ToHashSet();

    /// <summary>
    /// Когда стоят отложенные штрихи: «сразу» — если после штриха пишется ещё основной штрих, иначе «после слова».
    /// Выводится из порядка штрихов, в файл не пишется.
    /// </summary>
    public static bool IsImmediate(int deferredStroke, IReadOnlyList<int> strokeSamples, IReadOnlySet<int> deferredStrokes)
    {
        ArgumentNullException.ThrowIfNull(strokeSamples);
        ArgumentNullException.ThrowIfNull(deferredStrokes);
        for (int s = deferredStroke + 1; s < strokeSamples.Count; s++)
        {
            if (!deferredStrokes.Contains(s) && strokeSamples[s] > 0)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Константы формата <c>pfl.annotation/1</c>.</summary>
public static class AnnotationFormat
{
    /// <summary>Значение поля <c>schema</c>.</summary>
    public const string Schema = "pfl.annotation/1";

    /// <summary>
    /// Правило нумерации: штрихи и точки — как в <c>PenLog.ToInkAttempt()</c> по умолчанию, то есть только штрихи, начатые после
    /// последнего события <c>restart-button</c>, с нуля; внутри штриха — точки контакта по порядку (hover не считается).
    /// </summary>
    public const string Indexing = "inkattempt-after-last-restart";

    /// <summary>Расширение файла разметки.</summary>
    public const string FileSuffix = ".json";
}
