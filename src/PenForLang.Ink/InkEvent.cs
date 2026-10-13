using System.Globalization;

namespace PenForLang.Ink;

/// <summary>
/// Событие попытки (<c>stimulus-start</c>, <c>restart-button</c>, <c>completed</c> и т. п.) — запись типа <c>ev</c> в penlog.
/// Сравнение значений учитывает содержимое <see cref="Data"/> независимо от порядка ключей.
/// </summary>
/// <param name="TimeUs">Время в микросекундах от начала задания.</param>
/// <param name="Name">Имя события.</param>
/// <param name="Data">Дополнительные поля события (ключ → текст) или <c>null</c>.</param>
public sealed record InkEvent(long TimeUs, string Name, IReadOnlyDictionary<string, string>? Data = null)
{
    /// <summary>Имена событий, которые понимает ядро (остальные сохраняются как есть).</summary>
    public static class Names
    {
        /// <summary>Начало показа стимула.</summary>
        public const string StimulusStart = "stimulus-start";

        /// <summary>Конец показа стимула.</summary>
        public const string StimulusEnd = "stimulus-end";

        /// <summary>Нажата кнопка «Заново» (A10).</summary>
        public const string RestartButton = "restart-button";

        /// <summary>Попытка завершена; поле <c>by</c> — причина.</summary>
        public const string Completed = "completed";
    }

    /// <summary>Читает поле данных как целое (инвариантная культура, только цифры и необязательный минус).</summary>
    /// <returns><c>false</c>, если поля нет или текст не целое.</returns>
    public bool TryGetInt64(string key, out long value)
    {
        value = 0;
        return Data is not null && Data.TryGetValue(key, out string? text)
            && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Читает поле данных как число с плавающей точкой (инвариантная культура, точка как разделитель, без экспоненты тысяч).</summary>
    /// <returns><c>false</c>, если поля нет, текст не число или число не конечно.</returns>
    public bool TryGetDouble(string key, out double value)
    {
        value = 0;
        return Data is not null && Data.TryGetValue(key, out string? text)
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value);
    }

    /// <inheritdoc />
    public bool Equals(InkEvent? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (TimeUs != other.TimeUs || !string.Equals(Name, other.Name, StringComparison.Ordinal))
        {
            return false;
        }

        if (Data is null || other.Data is null)
        {
            return (Data?.Count ?? 0) == 0 && (other.Data?.Count ?? 0) == 0;
        }

        if (Data.Count != other.Data.Count)
        {
            return false;
        }

        foreach (KeyValuePair<string, string> pair in Data)
        {
            if (!other.Data.TryGetValue(pair.Key, out string? value) || !string.Equals(pair.Value, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(TimeUs, Name, Data?.Count ?? 0);
}
