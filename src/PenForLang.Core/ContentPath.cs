namespace PenForLang.Core;

/// <summary>Правила относительных путей контента (<see cref="IContentSource"/>).</summary>
public static class ContentPath
{
    /// <summary>
    /// Проверяет путь: не <c>null</c>, не пустой (кроме случая <paramref name="allowEmpty"/>), не абсолютный,
    /// без <c>\</c>, без пустых сегментов, <c>.</c> и <c>..</c>, без управляющих символов.
    /// </summary>
    /// <exception cref="ArgumentException">Путь некорректен.</exception>
    public static void Validate(string relativePath, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (relativePath.Length == 0)
        {
            if (allowEmpty)
            {
                return;
            }

            throw new ArgumentException("Content path must not be empty.", nameof(relativePath));
        }

        if (relativePath.Contains('\\', StringComparison.Ordinal) || relativePath.Any(char.IsControl))
        {
            throw new ArgumentException($"Content path '{relativePath}' contains a backslash or a control character.", nameof(relativePath));
        }

        if (relativePath[0] == '/' || (relativePath.Length > 1 && relativePath[1] == ':'))
        {
            throw new ArgumentException($"Content path '{relativePath}' must be relative.", nameof(relativePath));
        }

        foreach (string segment in relativePath.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                throw new ArgumentException($"Content path '{relativePath}' has an invalid segment '{segment}'.", nameof(relativePath));
            }
        }
    }
}
