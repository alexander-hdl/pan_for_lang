using System.Collections.Immutable;
using PenForLang.Evaluation;

namespace PenForLang.App.Wpf.Lesson;

/// <summary>Запись о том, что сказано ребёнку после одной попытки.</summary>
/// <param name="Target">Целевой элемент (буква).</param>
/// <param name="StepCode">Код упражнения шага.</param>
/// <param name="AttemptNo">Номер попытки в шаге.</param>
/// <param name="Reason">Причина плана.</param>
/// <param name="HintKey">Показанная подсказка или <c>null</c>.</param>
/// <param name="Code">Код находки.</param>
/// <param name="Highlight">Показанное наложение (для зелёной подсветки исправленного).</param>
/// <param name="ProcessError">В попытке была процессная ошибка (P1–P6), показанная или нет.</param>
public sealed record HintRecord(
    string Target,
    string StepCode,
    int AttemptNo,
    FeedbackReason Reason,
    string? HintKey,
    ErrorCode? Code,
    HighlightSpec? Highlight,
    bool ProcessError);

/// <summary>
/// История подсказок сессии: нужна для защиты от перегруза (одна подсказка 3 раза подряд), U0 и затухания частоты
/// (METHODOLOGY.md §7.2, §7.4, §7.6). Неизменяема: <see cref="Add"/> возвращает новую историю.
/// </summary>
public sealed class HintHistory
{
    private readonly ImmutableList<HintRecord> _records;

    private HintHistory(ImmutableList<HintRecord> records) => _records = records;

    /// <summary>Пустая история.</summary>
    public static HintHistory Empty { get; } = new([]);

    /// <summary>Записи по порядку.</summary>
    public IReadOnlyList<HintRecord> Records => _records;

    /// <summary>Добавляет запись.</summary>
    public HintHistory Add(HintRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new HintHistory(_records.Add(record));
    }

    /// <summary>Последняя запись по элементу или <c>null</c>.</summary>
    public HintRecord? Last(string target) => _records.FindLast(r => string.Equals(r.Target, target, StringComparison.Ordinal));

    /// <summary>Сколько последних попыток подряд по элементу закончились именно этой подсказкой.</summary>
    public int ConsecutiveRepeats(string target, string hintKey)
    {
        int count = 0;
        for (int i = _records.Count - 1; i >= 0; i--)
        {
            HintRecord record = _records[i];
            if (!string.Equals(record.Target, target, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(record.HintKey, hintKey, StringComparison.Ordinal))
            {
                break;
            }

            count++;
        }

        return count;
    }

    /// <summary>Сколько последних попыток подряд по элементу были «не распознано».</summary>
    public int UnrecognizedStreak(string target) =>
        Streak(target, r => r.Reason is FeedbackReason.Unrecognized);

    /// <summary>Сколько последних попыток подряд по элементу содержали процессную ошибку.</summary>
    public int ProcessErrorStreak(string target) => Streak(target, r => r.ProcessError);

    /// <summary>Хвалили ли уже за чистую попытку в этом шаге.</summary>
    public bool PraisedInStep(string target, string stepCode) =>
        _records.Exists(r => r.Reason == FeedbackReason.Praise
            && string.Equals(r.Target, target, StringComparison.Ordinal)
            && string.Equals(r.StepCode, stepCode, StringComparison.Ordinal));

    private int Streak(string target, Func<HintRecord, bool> predicate)
    {
        int count = 0;
        for (int i = _records.Count - 1; i >= 0; i--)
        {
            HintRecord record = _records[i];
            if (!string.Equals(record.Target, target, StringComparison.Ordinal))
            {
                continue;
            }

            if (!predicate(record))
            {
                break;
            }

            count++;
        }

        return count;
    }
}
