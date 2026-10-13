using System.Collections.Immutable;

namespace PenForLang.Glyphs.Loading;

/// <summary>Серьёзность замечания валидатора.</summary>
public enum IssueSeverity
{
    /// <summary>Пакет недопустим: загрузка не удалась.</summary>
    Error,

    /// <summary>Подозрительно, но пакет загружается.</summary>
    Warning,
}

/// <summary>
/// Замечание валидатора стиль-пакета: файл, путь к полю внутри файла (например <c>strokes[0].segments[2].c[1]</c>),
/// машинный код и понятное сообщение (на английском: домен не содержит пользовательских строк).
/// </summary>
/// <param name="Severity">Серьёзность.</param>
/// <param name="Code">Стабильный код замечания (<c>segment.gap</c>), по нему тесты и CI отличают замечания.</param>
/// <param name="File">Относительный путь файла в источнике контента.</param>
/// <param name="Path">Путь к полю внутри файла; пусто — файл целиком.</param>
/// <param name="Message">Сообщение.</param>
public sealed record ValidationIssue(IssueSeverity Severity, string Code, string File, string Path, string Message)
{
    /// <summary>Строка вида <c>glyphs/a.default.json: strokes[0].segments[1]: сообщение [code]</c>.</summary>
    public override string ToString() =>
        Path.Length == 0 ? $"{File}: {Message} [{Code}]" : $"{File}: {Path}: {Message} [{Code}]";
}

/// <summary>Результат загрузки стиль-пакета: пакет (если ошибок нет) и все замечания.</summary>
/// <param name="Pack">Загруженный пакет; <c>null</c>, если есть хотя бы одна ошибка.</param>
/// <param name="Issues">Замечания в порядке обнаружения (файлы — по возрастанию пути).</param>
public sealed record StyleLoadResult(StylePack? Pack, ImmutableArray<ValidationIssue> Issues)
{
    /// <summary>Загрузка успешна: пакет получен и ошибок нет.</summary>
    public bool Succeeded => Pack is not null;

    /// <summary>Только ошибки.</summary>
    public IEnumerable<ValidationIssue> Errors => Issues.Where(i => i.Severity == IssueSeverity.Error);

    /// <summary>Только предупреждения.</summary>
    public IEnumerable<ValidationIssue> Warnings => Issues.Where(i => i.Severity == IssueSeverity.Warning);
}

/// <summary>Исключение загрузки стиль-пакета с полным списком замечаний.</summary>
public sealed class StyleLoadException : Exception
{
    private const int MessageIssueLimit = 5;

    /// <summary>Создаёт исключение.</summary>
    /// <param name="issues">Замечания (хотя бы одна ошибка).</param>
    public StyleLoadException(ImmutableArray<ValidationIssue> issues)
        : base(BuildMessage(issues))
    {
        Issues = issues;
    }

    /// <summary>Создаёт исключение с сообщением.</summary>
    /// <param name="message">Сообщение.</param>
    public StyleLoadException(string message)
        : base(message)
    {
        Issues = [];
    }

    /// <summary>Создаёт исключение с сообщением и причиной.</summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="innerException">Причина.</param>
    public StyleLoadException(string message, Exception innerException)
        : base(message, innerException)
    {
        Issues = [];
    }

    /// <summary>Создаёт пустое исключение.</summary>
    public StyleLoadException()
        : this("Style pack failed to load.")
    {
    }

    /// <summary>Все замечания.</summary>
    public ImmutableArray<ValidationIssue> Issues { get; }

    private static string BuildMessage(ImmutableArray<ValidationIssue> issues)
    {
        var errors = issues.Where(i => i.Severity == IssueSeverity.Error).ToList();
        string head = $"Style pack is invalid: {errors.Count} error(s).";
        var lines = errors.Take(MessageIssueLimit).Select(i => i.ToString()).ToList();
        if (errors.Count > MessageIssueLimit)
        {
            lines.Add($"... and {errors.Count - MessageIssueLimit} more.");
        }

        return head + "\n" + string.Join("\n", lines);
    }
}
