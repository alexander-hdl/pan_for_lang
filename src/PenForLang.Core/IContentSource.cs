namespace PenForLang.Core;

/// <summary>
/// Порт чтения контента: потоки по относительному пути (ARCHITECTURE.md §2.2, A13).
/// Домен не обращается к файловой системе (R2) — всё, что нужно прочитать, приходит через этот порт.
/// Реализации: <c>FileContentSource</c> в <c>Infrastructure</c>, <see cref="InMemoryContentSource"/> для тестов.
/// </summary>
/// <remarks>
/// Пути относительные, с разделителем <c>/</c>, регистр значим, без сегментов <c>.</c>, <c>..</c> и пустых.
/// Корректность пути проверяет <see cref="ContentPath.Validate"/>.
/// </remarks>
public interface IContentSource
{
    /// <summary>Есть ли файл по относительному пути.</summary>
    /// <exception cref="ArgumentException">Путь некорректен (<see cref="ContentPath.Validate"/>).</exception>
    bool Exists(string relativePath);

    /// <summary>Открывает файл для чтения. Каждый вызов возвращает новый независимый поток; владелец потока — вызывающий.</summary>
    /// <exception cref="ArgumentException">Путь некорректен.</exception>
    /// <exception cref="FileNotFoundException">Файла нет.</exception>
    Stream Open(string relativePath);

    /// <summary>
    /// Все файлы внутри каталога (рекурсивно), пути относительно корня источника, отсортированы по <see cref="StringComparer.Ordinal"/>.
    /// Пустая строка — весь источник. Отсутствующий каталог даёт пустой список.
    /// </summary>
    /// <exception cref="ArgumentException">Путь некорректен.</exception>
    IReadOnlyList<string> List(string relativeDirectory);
}
