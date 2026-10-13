using System.IO;
using System.IO.Compression;
using System.Text;
using PenForLang.Ink;
using PenForLang.Tools.InkLab.Protocol;

namespace PenForLang.Tools.InkLab.Corpus;

/// <summary>
/// Папка корпуса записей на диске: <c>&lt;корень&gt;/&lt;субъект&gt;/…</c>. Здесь же — чтение и запись penlog (gzip), файла прогресса
/// <c>progress.json</c> и пути файла разметки рядом с записью. Все записи атомарны: сначала временный файл, затем переименование.
/// </summary>
public sealed class CorpusStore
{
    /// <summary>Расширение записи пера.</summary>
    public const string PenLogExtension = ".pfl.jsonl.gz";

    /// <summary>Добавляется к имени файла записи целиком: <c>x.pfl.jsonl.gz</c> → <c>x.pfl.jsonl.gz.ann.json</c>.</summary>
    public const string AnnotationSuffix = ".ann.json";

    /// <summary>Файл прогресса протокола в папке субъекта.</summary>
    public const string ProgressFileName = "progress.json";

    /// <summary>Подпапка перезаписанных записей (остаются на диске, но в корпус не входят).</summary>
    public const string SupersededFolder = "superseded";

    /// <summary>Создаёт хранилище.</summary>
    /// <param name="root">Корень корпуса; создаётся при первой записи.</param>
    public CorpusStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    /// <summary>Корень корпуса (абсолютный путь).</summary>
    public string Root { get; }

    /// <summary>Корень по умолчанию: <c>%LOCALAPPDATA%\PenForLang\corpus</c>.</summary>
    public static string DefaultRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PenForLang", "corpus");

    /// <summary>Путь файла разметки рядом с записью.</summary>
    public static string AnnotationPathFor(string penLogPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(penLogPath);
        return penLogPath + AnnotationSuffix;
    }

    /// <summary>Путь записи по пути её файла разметки.</summary>
    public static string PenLogPathFor(string annotationPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(annotationPath);
        return annotationPath.EndsWith(AnnotationSuffix, StringComparison.OrdinalIgnoreCase)
            ? annotationPath[..^AnnotationSuffix.Length]
            : throw new ArgumentException($"'{annotationPath}' does not end with '{AnnotationSuffix}'.", nameof(annotationPath));
    }

    /// <summary>Файл — запись пера (по расширению).</summary>
    public static bool IsPenLogFile(string path) => path.EndsWith(PenLogExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Папка субъекта.</summary>
    public string SubjectDirectory(SubjectInfo subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return Path.Combine(Root, subject.FolderName);
    }

    /// <summary>Загружает прогресс субъекта или создаёт новый (на диск не пишет).</summary>
    /// <exception cref="InvalidOperationException">В папке лежит прогресс другого протокола.</exception>
    public ProtocolProgress LoadProgress(SubjectInfo subject, string protocolId)
    {
        ArgumentNullException.ThrowIfNull(subject);
        string path = Path.Combine(SubjectDirectory(subject), ProgressFileName);
        if (!File.Exists(path))
        {
            return new ProtocolProgress { ProtocolId = protocolId, Subject = subject };
        }

        ProtocolProgress progress = ProtocolProgress.FromJson(File.ReadAllText(path, Encoding.UTF8));
        if (!string.Equals(progress.ProtocolId, protocolId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Папка субъекта содержит прогресс протокола '{progress.ProtocolId}', а выбран '{protocolId}'.");
        }

        // Метаданные субъекта берутся из формы: рука или согласие могли уточниться между сеансами.
        progress.Subject = subject;
        return progress;
    }

    /// <summary>Сохраняет прогресс в папку субъекта.</summary>
    public void SaveProgress(ProtocolProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        string directory = SubjectDirectory(progress.Subject);
        Directory.CreateDirectory(directory);
        WriteTextAtomic(Path.Combine(directory, ProgressFileName), progress.ToJson());
    }

    /// <summary>
    /// Пишет запись в папку субъекта под именем <c>&lt;stem&gt;.pfl.jsonl.gz</c> (при занятом имени добавляется <c>-2</c>, <c>-3</c>…).
    /// </summary>
    /// <returns>Путь файла (абсолютный).</returns>
    public string WritePenLog(SubjectInfo subject, string stem, PenLog log)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentException.ThrowIfNullOrEmpty(stem);
        ArgumentNullException.ThrowIfNull(log);
        string directory = SubjectDirectory(subject);
        Directory.CreateDirectory(directory);

        string path = Path.Combine(directory, stem + PenLogExtension);
        for (int i = 2; File.Exists(path); i++)
        {
            path = Path.Combine(directory, stem + "-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + PenLogExtension);
        }

        WritePenLogFile(path, log);
        return path;
    }

    /// <summary>Пишет запись по указанному пути (атомарно, поверх существующего файла не пишет).</summary>
    public static void WritePenLogFile(string path, PenLog log)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(log);
        string temp = path + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
            {
                PenLogWriter.Write(gzip, log);
            }

            File.Move(temp, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    /// <summary>Читает запись (gzip или несжатый JSON Lines по расширению).</summary>
    /// <exception cref="PenLogFormatException">Не penlog.</exception>
    public static PenLog ReadPenLog(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            return PenLogReader.Read(gzip);
        }

        return PenLogReader.Read(file);
    }

    /// <summary>Читает только заголовок записи (быстро: без точек).</summary>
    public static PenLogHeader ReadHeader(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Stream stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(file, CompressionMode.Decompress) : file;
        using (stream)
        using (var reader = new PenLogReader(stream, leaveOpen: true))
        {
            return reader.ReadHeader();
        }
    }

    /// <summary>
    /// Убирает перезаписанную запись (и её разметку) из корпуса: переносит в подпапку <see cref="SupersededFolder"/>.
    /// Данные не удаляются.
    /// </summary>
    public static void Supersede(string penLogPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(penLogPath);
        string directory = Path.Combine(Path.GetDirectoryName(penLogPath)!, SupersededFolder);
        Directory.CreateDirectory(directory);
        MoveWithoutOverwrite(penLogPath, directory);
        string annotation = AnnotationPathFor(penLogPath);
        if (File.Exists(annotation))
        {
            MoveWithoutOverwrite(annotation, directory);
        }
    }

    /// <summary>Все записи корпуса в папке и подпапках (без <see cref="SupersededFolder"/>), по алфавиту.</summary>
    public static IReadOnlyList<string> EnumeratePenLogs(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        string marker = Path.DirectorySeparatorChar + SupersededFolder + Path.DirectorySeparatorChar;
        return
        [
            .. Directory.EnumerateFiles(folder, "*" + PenLogExtension, SearchOption.AllDirectories)
                .Where(p => !p.Contains(marker, StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>Пишет текст атомарно (временный файл и переименование).</summary>
    public static void WriteTextAtomic(string path, string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(text);
        string temp = path + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    private static void MoveWithoutOverwrite(string source, string targetDirectory)
    {
        string name = Path.GetFileName(source);
        string target = Path.Combine(targetDirectory, name);
        for (int i = 2; File.Exists(target); i++)
        {
            target = Path.Combine(targetDirectory, i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + name);
        }

        File.Move(source, target);
    }
}
