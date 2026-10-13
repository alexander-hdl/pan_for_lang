using System.IO;
using PenForLang.Core;

namespace PenForLang.Tools.InkLab.Content;

/// <summary>
/// Источник контента поверх каталога файловой системы (<see cref="IContentSource"/>). Файловый ввод-вывод домену запрещён (R2), поэтому
/// класс живёт в корне композиции; в продукте эту роль будет играть <c>FileContentSource</c> из <c>Infrastructure</c> (§2.2), которого в
/// v0 ещё нет. Пути проходят <see cref="ContentPath.Validate"/>, выйти за корень нельзя.
/// </summary>
public sealed class DirectoryContentSource : IContentSource
{
    private readonly string _root;

    /// <summary>Создаёт источник.</summary>
    /// <param name="rootDirectory">Корневой каталог (должен существовать).</param>
    /// <exception cref="DirectoryNotFoundException">Каталога нет.</exception>
    public DirectoryContentSource(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);
        _root = Path.GetFullPath(rootDirectory);
        if (!Directory.Exists(_root))
        {
            throw new DirectoryNotFoundException($"Content directory '{_root}' does not exist.");
        }
    }

    /// <summary>Корневой каталог (абсолютный путь).</summary>
    public string Root => _root;

    /// <inheritdoc />
    public bool Exists(string relativePath)
    {
        ContentPath.Validate(relativePath);
        return File.Exists(Resolve(relativePath));
    }

    /// <inheritdoc />
    public Stream Open(string relativePath)
    {
        ContentPath.Validate(relativePath);
        string full = Resolve(relativePath);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"Content '{relativePath}' not found.", relativePath);
        }

        return new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> List(string relativeDirectory)
    {
        ContentPath.Validate(relativeDirectory, allowEmpty: true);
        string directory = relativeDirectory.Length == 0 ? _root : Resolve(relativeDirectory);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return
        [
            .. Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(_root, path).Replace('\\', '/'))
                .Order(StringComparer.Ordinal),
        ];
    }

    private string Resolve(string relativePath) => Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
}
