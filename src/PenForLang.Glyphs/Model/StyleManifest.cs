using System.Collections.Immutable;
using PenForLang.Core;

namespace PenForLang.Glyphs;

/// <summary>Роль стиль-пакета (ARCHITECTURE.md §3.2).</summary>
public enum StyleRole
{
    /// <summary>Стиль, которому учат (<c>zaner-bloser</c>).</summary>
    Teach,

    /// <summary>Печатный стиль: чтение, диагностика, листы.</summary>
    Print,

    /// <summary>Пакет ловушек (русские буквы для различения).</summary>
    Confuser,
}

/// <summary>Форма соединения букв (<c>JoinGenerator</c>; ARCHITECTURE.md §3.2.2).</summary>
public enum JoinShape
{
    /// <summary>Кубический Эрмит между выходом и входом с G1-непрерывностью.</summary>
    Hermite,

    /// <summary>«Перекат» к старту овала для входа E-C.</summary>
    HermiteOver,

    /// <summary>Ожидаемый отрыв пера.</summary>
    Break,
}

/// <summary>Режим соединения заглавной буквы с последующей.</summary>
public enum CapitalJoinMode
{
    /// <summary>Заглавная всегда отрывается (ядро v1).</summary>
    Break,

    /// <summary>Соединяется (уровень 2).</summary>
    Join,
}

/// <summary>Когда анимация ставит отложенные штрихи.</summary>
public enum DeferredTeachTiming
{
    /// <summary>После всего слова (<c>afterWord</c>; решение заказчика).</summary>
    AfterWord,

    /// <summary>Сразу после буквы (<c>afterLetter</c>).</summary>
    AfterLetter,
}

/// <summary>Как оценщик использует наклон стиля (<c>slantScoring</c>).</summary>
public enum SlantScoring
{
    /// <summary>Только постоянство наклона: абсолютное значение <see cref="StyleManifest.SlantDeg"/> не сверено с прописью.</summary>
    Consistency,

    /// <summary>Абсолютный наклон: допустим, когда <see cref="StyleManifest.SlantDeg"/> сверен человеком.</summary>
    Absolute,
}

/// <summary>Линии линейки стиля в x-height относительно базовой линии (ARCHITECTURE.md §3.1).</summary>
/// <param name="Ascender">Линия асцендеров.</param>
/// <param name="Cap">Линия заглавных.</param>
/// <param name="XHeight">Линия x-height (всегда 1).</param>
/// <param name="Baseline">Базовая линия (всегда 0).</param>
/// <param name="Descender">Линия десцендеров (отрицательная).</param>
public sealed record GuideLines(double Ascender, double Cap, double XHeight, double Baseline, double Descender)
{
    /// <summary>Значение v линии <paramref name="line"/>.</summary>
    /// <param name="line">Линия.</param>
    public double ValueOf(LineName line) => line switch
    {
        LineName.Ascender => Ascender,
        LineName.Cap => Cap,
        LineName.XHeight => XHeight,
        LineName.Baseline => Baseline,
        LineName.Descender => Descender,
        _ => throw new ArgumentOutOfRangeException(nameof(line), line, "Unknown line."),
    };
}

/// <summary>Правило соединения: класс выхода предыдущей буквы × классы входа следующей.</summary>
/// <param name="Family">Семейство <c>JF1</c>…<c>JF9</c> или <c>null</c> для правила разрыва.</param>
/// <param name="Exit">Класс выхода.</param>
/// <param name="Entry">Классы входа, к которым правило применимо.</param>
/// <param name="AnyEntry">Правило применимо к любому входу (<c>"*"</c> в JSON).</param>
/// <param name="Shape">Форма соединения.</param>
/// <param name="High">Высокое соединение (не опускается к базовой линии).</param>
/// <param name="Params">Числовые параметры формы (например <c>overshootU</c>).</param>
public sealed record JoinRule(
    string? Family,
    ExitClass Exit,
    ImmutableArray<EntryClass> Entry,
    bool AnyEntry,
    JoinShape Shape,
    bool High,
    IReadOnlyDictionary<string, double> Params);

/// <summary>Правила соединений стиля.</summary>
/// <param name="Rules">Правила (порядок значим только для читаемости; пары выход × вход уникальны).</param>
/// <param name="TallEntryClass">Класс входа «высоких» букв (<c>E-UP-TALL</c>).</param>
public sealed record JoinPolicy(ImmutableArray<JoinRule> Rules, EntryClass TallEntryClass);

/// <summary>Правило варианта глифа: <c>when prevExit = X → variant</c> для символа.</summary>
/// <param name="Symbol">Символ (<c>e</c>).</param>
/// <param name="PrevExit">Класс выхода предыдущей буквы.</param>
/// <param name="Variant">Вариант глифа (<c>after-high</c>).</param>
public sealed record VariantRule(string Symbol, ExitClass PrevExit, string Variant);

/// <summary>
/// Настройки анимации стиля (ARCHITECTURE.md §3.2.2, <c>animation</c>). Значения задаёт только <c>style.json</c>
/// (единственная точка истины): блок и все поля обязательны, значений по умолчанию в коде нет.
/// </summary>
/// <param name="SpeedXhPerSec">Скорость пера, x-height в секунду.</param>
/// <param name="CuspPauseMs">Пауза в точке возврата и резком изгибе, мс.</param>
/// <param name="JoinSlowdown">Множитель скорости на входящем и выходящем штрихах, (0, 1].</param>
/// <param name="StrokeGapMs">Пауза между основными штрихами (перо в воздухе), мс.</param>
/// <param name="DeferredGapMs">Пауза перед отложенным штрихом, мс.</param>
/// <param name="StartHoldMs">Выдержка в начале (видна точка старта), мс.</param>
/// <param name="EndHoldMs">Выдержка в конце, мс.</param>
/// <param name="DotMs">Длительность постановки точки, мс.</param>
public sealed record AnimationSettings(
    double SpeedXhPerSec,
    int CuspPauseMs,
    double JoinSlowdown,
    int StrokeGapMs,
    int DeferredGapMs,
    int StartHoldMs,
    int EndHoldMs,
    int DotMs);

/// <summary>Описание стиля письма (<c>style.json</c>, схема <c>pfl.style/1</c>).</summary>
/// <param name="Id">Внутренний идентификатор стиля (в UI не показывается).</param>
/// <param name="Version">Версия пакета (semver).</param>
/// <param name="Role">Роль.</param>
/// <param name="Script">Тип письма: <c>cursive</c> или <c>print</c>.</param>
/// <param name="Language">Язык алфавита (<c>en</c>).</param>
/// <param name="DisplayName">Отображаемое имя по языкам UI (<c>ru</c>, <c>en</c>) — без товарного знака.</param>
/// <param name="Lines">Линии линейки.</param>
/// <param name="SlantDeg">Наклон в градусах от вертикали вправо (для геометрии и линеек) или <c>null</c>.</param>
/// <param name="SlantScoring">Что оценщик делает с наклоном: <see cref="SlantScoring.Consistency"/> — только постоянство, пока наклон не сверен.</param>
/// <param name="LetterSpacing">Зазор между буквами, x-height: следующий глиф ставится в <c>u = advance + letterSpacing</c> предыдущего.</param>
/// <param name="WordSpacing">Пробел между словами, x-height.</param>
/// <param name="Joins">Правила соединений.</param>
/// <param name="VariantRules">Правила вариантов глифов.</param>
/// <param name="MotorFamilies">Моторные семьи символов (подмены внутри семьи — класс L2m).</param>
/// <param name="CapitalJoinMode">Режим соединения заглавных.</param>
/// <param name="DeferredTeachTiming">Когда анимация ставит отложенные штрихи.</param>
/// <param name="Animation">Настройки анимации.</param>
public sealed record StyleManifest(
    StyleId Id,
    string Version,
    StyleRole Role,
    string Script,
    string Language,
    IReadOnlyDictionary<string, string> DisplayName,
    GuideLines Lines,
    double? SlantDeg,
    SlantScoring SlantScoring,
    double LetterSpacing,
    double WordSpacing,
    JoinPolicy Joins,
    ImmutableArray<VariantRule> VariantRules,
    ImmutableArray<ImmutableArray<string>> MotorFamilies,
    CapitalJoinMode CapitalJoinMode,
    DeferredTeachTiming DeferredTeachTiming,
    AnimationSettings Animation);
