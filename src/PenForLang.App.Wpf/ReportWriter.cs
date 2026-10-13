using System.IO;
using System.Text;

namespace PenForLang.App.Wpf;

/// <summary>
/// Запись отчёта в файл. Папки пробуются по порядку: заданная параметром или рабочий стол, затем «Документы», затем папка
/// приложения; первая, куда удалось записать, побеждает — путь возвращается, чтобы показать его родителю.
/// </summary>
public static class ReportWriter
{
    /// <summary>Имя файла отчёта для момента <paramref name="started"/>.</summary>
    public static string FileName(DateTimeOffset started) =>
        $"PenForLang-V0-report-{started:yyyyMMdd-HHmmss}.md";

    /// <summary>Папки-кандидаты в порядке предпочтения; пустые и повторяющиеся пропускаются.</summary>
    public static IReadOnlyList<string> CandidateDirectories(string? preferred)
    {
        var result = new List<string>();
        void Add(string? dir)
        {
            if (!string.IsNullOrWhiteSpace(dir) && !result.Contains(dir, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(dir);
            }
        }

        Add(preferred);
        if (string.IsNullOrWhiteSpace(preferred))
        {
            Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        }

        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Add(AppContext.BaseDirectory);
        return result;
    }

    /// <summary>
    /// Пишет отчёт в первую доступную папку. Перезаписывает файл с тем же именем (снимки и итог идут в один файл).
    /// </summary>
    /// <param name="directories">Папки-кандидаты.</param>
    /// <param name="fileName">Имя файла.</param>
    /// <param name="content">Текст.</param>
    /// <param name="path">Куда записано.</param>
    /// <param name="error">Ошибки неудачных папок (если были), для отчёта.</param>
    /// <returns><c>true</c>, если запись удалась хотя бы в одну папку.</returns>
    public static bool TryWrite(IEnumerable<string> directories, string fileName, string content, out string? path, out string? error)
    {
        ArgumentNullException.ThrowIfNull(directories);
        var errors = new List<string>();
        foreach (string dir in directories)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string candidate = Path.Combine(dir, fileName);
                File.WriteAllText(candidate, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                path = candidate;
                error = errors.Count == 0 ? null : string.Join("; ", errors);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                errors.Add($"{dir}: {ex.Message}");
            }
        }

        path = null;
        error = string.Join("; ", errors);
        return false;
    }
}
