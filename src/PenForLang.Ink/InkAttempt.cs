using System.Collections.Immutable;

namespace PenForLang.Ink;

/// <summary>Причина завершения попытки (поле <c>by</c> события <c>completed</c>).</summary>
public enum InkCompletion
{
    /// <summary>Автозавершение по паузе (<c>pause</c>).</summary>
    Pause,

    /// <summary>Кнопка «Готово» (<c>done-button</c>).</summary>
    DoneButton,

    /// <summary>Истёк таймер (<c>timer</c>).</summary>
    Timer,

    /// <summary>Подтверждено на вопрос «Дописал?» (<c>asked-confirmed</c>).</summary>
    AskedConfirmed,

    /// <summary>Отменена или не завершена.</summary>
    Cancelled,
}

/// <summary>
/// Сырая попытка письма: штрихи, линейка, устройство и события (ARCHITECTURE.md §3.7).
/// Живёт в памяти текущей сессии; на диск попадает как penlog только при согласии (§3.4).
/// </summary>
/// <param name="Strokes">Штрихи по порядку касаний.</param>
/// <param name="Guides">Линейка попытки.</param>
/// <param name="Device">Устройство ввода.</param>
/// <param name="StimulusEndUs">Время окончания показа стимула, мкс, если был.</param>
/// <param name="Completion">Причина завершения.</param>
/// <param name="Restarts">Сколько раз нажато «Заново» (A10).</param>
/// <param name="Events">События попытки в хронологическом порядке.</param>
public sealed record InkAttempt(
    ImmutableArray<InkStroke> Strokes,
    GuideFrame Guides,
    PenDeviceInfo Device,
    long? StimulusEndUs,
    InkCompletion Completion,
    int Restarts,
    ImmutableArray<InkEvent> Events);
