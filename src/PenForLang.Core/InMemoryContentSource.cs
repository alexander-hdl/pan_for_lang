using System.Text;

namespace PenForLang.Core;

/// <summary>Источник контента в памяти — для тестов и фикстур. Потокобезопасность не гарантируется.</summary>
public sealed class InMemoryContentSource : IContentSource
{
    private readonly SortedDictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    /// <summary>Добавляет или заменяет файл; содержимое копируется.</summary>
    /// <returns>Этот же источник — для цепочки вызовов.</returns>
    /// <exception cref="ArgumentException">Путь некорректен.</exception>
    public InMemoryContentSource Add(string relativePath, ReadOnlySpan<byte> content)
    {
        ContentPath.Validate(relativePath);
        _files[relativePath] = content.ToArray();
        return this;
    }

    /// <summary>Добавляет или заменяет текстовый файл (UTF-8 без BOM).</summary>
    /// <returns>Этот же источник — для цепочки вызовов.</returns>
    /// <exception cref="ArgumentException">Путь некорректен.</exception>
    public InMemoryContentSource Add(string relativePath, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Add(relativePath, new UTF8Encoding(false).GetBytes(text));
    }

    /// <inheritdoc />
    public bool Exists(string relativePath)
    {
        ContentPath.Validate(relativePath);
        return _files.ContainsKey(relativePath);
    }

    /// <inheritdoc />
    public Stream Open(string relativePath)
    {
        ContentPath.Validate(relativePath);
        if (!_files.TryGetValue(relativePath, out byte[]? content))
        {
            throw new FileNotFoundException($"Content '{relativePath}' not found.", relativePath);
        }

        return new MemoryStream(content, writable: false);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> List(string relativeDirectory)
    {
        ContentPath.Validate(relativeDirectory, allowEmpty: true);
        string prefix = relativeDirectory.Length == 0 ? string.Empty : relativeDirectory + "/";
        return _files.Keys.Where(path => path.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
    }
}
