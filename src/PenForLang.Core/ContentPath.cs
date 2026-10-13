namespace PenForLang.Core;

/// <summary>Правила относительных путей контента (<see cref="IContentSource"/>).</summary>
public static class ContentPath
{
    /// <summary>
    /// Проверяет путь: не <c>null</c>, не пустой (кроме случая <paramref name="allowEmpty"/>), не абсолютный,
    /// без <c>\</c> и <c>:</c> (буквы дисков, альтернативные потоки NTFS), без пустых сегментов, <c>.</c> и <c>..</c>,
    /// без сегментов, оканчивающихся точкой или пробелом (Windows обрезает их молча), без управляющих символов.
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

        if (relativePath.Any(c => c is '\\' or ':' || char.IsControl(c)))
        {
            throw new ArgumentException($"Content path '{relativePath}' contains a backslash, a colon or a control character.", nameof(relativePath));
        }

        if (relativePath[0] == '/')
        {
            throw new ArgumentException($"Content path '{relativePath}' must be relative.", nameof(relativePath));
        }

        foreach (string segment in relativePath.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                throw new ArgumentException($"Content path '{relativePath}' has an invalid segment '{segment}'.", nameof(relativePath));
            }

            if (segment[^1] is '.' or ' ')
            {
                throw new ArgumentException($"Content path '{relativePath}': segment '{segment}' must not end with a dot or a space.", nameof(relativePath));
            }
        }
    }
}
