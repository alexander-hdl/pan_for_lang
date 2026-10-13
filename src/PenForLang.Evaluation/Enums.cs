namespace PenForLang.Evaluation;

/// <summary>Что оценивается (ARCHITECTURE.md §6.2). В v0 реализованы <see cref="Letter"/> и <see cref="Word"/>.</summary>
public enum TargetKind
{
    /// <summary>Изолированная буква.</summary>
    Letter,

    /// <summary>Пара букв.</summary>
    Pair,

    /// <summary>Слово.</summary>
    Word,

    /// <summary>Фраза.</summary>
    Phrase,

    /// <summary>Последовательность (спринт).</summary>
    Sequence,
}

/// <summary>Контекст буквы (ARCHITECTURE.md §6.4.9): изолированная, первая буква слова, буква внутри слова.</summary>
public enum LetterContext
{
    /// <summary>Изолированная буква.</summary>
    Isolated,

    /// <summary>Первая буква слова.</summary>
    WordFirst,

    /// <summary>Буква внутри слова.</summary>
    WordInner,
}

/// <summary>
/// Профиль допусков (ARCHITECTURE.md §6.2). Профиль масштабирует допуски формы множителем
/// <c>profiles.&lt;имя&gt;.toleranceScale</c> из <c>scoring.json</c>.
/// </summary>
public enum ToleranceProfile
{
    /// <summary>Изучается.</summary>
    Learning,

    /// <summary>Знакома.</summary>
    Familiar,

    /// <summary>Освоена.</summary>
    Mastered,

    /// <summary>Устойчива.</summary>
    Stable,

    /// <summary>Спринт.</summary>
    Sprint,

    /// <summary>Заглавная.</summary>
    Capital,
}

/// <summary>Какое время постановки отложенных штрихов принимается (<c>scoring.json → deferred.acceptTiming</c>, §3.2.3).</summary>
public enum DeferredAcceptPolicy
{
    /// <summary>Принимается и после слова, и сразу: «сразу» даёт только информационную находку D4.</summary>
    Either,

    /// <summary>Строгий режим: принимается только после слова, «сразу» — находка D4 уровня ошибки.</summary>
    AfterWord,
}

/// <summary>Решение оценщика о написанном (ARCHITECTURE.md §6.4.7).</summary>
public enum Decision
{
    /// <summary>Написана цель.</summary>
    Target,

    /// <summary>Принята гипотеза-замена (в v0 не используется: гипотезы появятся с локальной переоценкой).</summary>
    Hypothesis,

    /// <summary>Не распознано (U0): попытка не засчитывается и не штрафуется.</summary>
    Unrecognized,
}

/// <summary>Уверенность оценщика в результате.</summary>
public enum Confidence
{
    /// <summary>Высокая.</summary>
    High,

    /// <summary>Средняя.</summary>
    Medium,

    /// <summary>Низкая: негативной обратной связи быть не должно.</summary>
    Low,
}

/// <summary>Статус отложенного штриха.</summary>
public enum DeferredStatus
{
    /// <summary>Поставлен на месте.</summary>
    Present,

    /// <summary>Не поставлен (D1).</summary>
    Missing,

    /// <summary>Поставлен далеко от якоря (D2).</summary>
    Misplaced,
}

/// <summary>Когда поставлен отложенный штрих (ARCHITECTURE.md §6.4.8).</summary>
public enum DeferredTiming
{
    /// <summary>Штриха нет, время неприменимо.</summary>
    None,

    /// <summary>После основного следа.</summary>
    AfterWord,

    /// <summary>Раньше конца основного следа («сразу»).</summary>
    Immediate,
}

/// <summary>Серьёзность находки по виду.</summary>
public enum FindingKind
{
    /// <summary>Ошибка: снижает оценку и может быть показана ребёнку.</summary>
    Error,

    /// <summary>Информационная находка (D4, K4): не ошибка, не снижает <c>form</c>.</summary>
    Info,
}

/// <summary>Где находка.</summary>
public enum FindingPlace
{
    /// <summary>Буква целиком.</summary>
    Letter,

    /// <summary>Основной штрих (эталона и/или рукописи).</summary>
    Stroke,

    /// <summary>Слот отложенного штриха.</summary>
    Deferred,

    /// <summary>Соединение между буквами слова (<see cref="FindingLocation.LetterIndex"/> — левая буква).</summary>
    Join,
}

/// <summary>Часть штриха эталона, к которой относится участок.</summary>
public enum TemplatePart
{
    /// <summary>Тело штриха (<c>t</c> — доля длины дуги тела).</summary>
    Body,

    /// <summary>Входящий штрих изолированной буквы (<c>t</c> — доля его длины).</summary>
    LeadIn,

    /// <summary>Выходящий штрих изолированной буквы (<c>t</c> — доля его длины).</summary>
    LeadOut,

    /// <summary>Соединение между буквами слова (<c>t</c> — доля его длины).</summary>
    Join,
}
