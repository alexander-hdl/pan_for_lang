using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using PenForLang.Ink;
using PenForLang.Tools.InkLab.Protocol;

namespace PenForLang.Tools.InkLab.Corpus;

/// <summary>Результат загрузки прогресса субъекта.</summary>
/// <param name="Progress">Прогресс (новый, если файла не было).</param>
/// <param name="Existed">Файл прогресса был (читался основной или резервный).</param>
/// <param name="RecoveredFromBackup">Основной файл не читался, взят <c>progress.json.bak</c>.</param>
/// <param name="Unreadable">Основной файл есть, но не читался.</param>
public sealed record ProgressLoad(ProtocolProgress Progress, bool Existed, bool RecoveredFromBackup, bool Unreadable);

/// <summary>
/// Папка корпуса записей на диске: <c>&lt;корень&gt;/&lt;субъект&gt;/…</c>. Здесь же — чтение и запись penlog (gzip), файла прогресса
/// <c>progress.json</c> (с резервной копией) и пути файлов разметки рядом с записью. Все записи атомарны и сбрасываются на диск
/// (<see cref="AtomicFile"/>).
/// </summary>
public sealed class CorpusStore
{
    /// <summary>Расширение записи пера.</summary>
    public const string PenLogExtension = ".pfl.jsonl.gz";

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

    /// <summary>Идентификатор разметчика в имени файла: так же очищается, как имя папки субъекта.</summary>
    public static string AnnotatorFileId(string annotatorId) => SubjectInfo.SanitizeForFolder(annotatorId);

    /// <summary>
    /// Путь файла разметки одного разметчика рядом с записью: <c>x.pfl.jsonl.gz</c> → <c>x.pfl.jsonl.gz.ann.&lt;разметчик&gt;.json</c>.
    /// У каждого разметчика свой файл: чужая разметка не перезаписывается.
    /// </summary>
    public static string AnnotationPathFor(string penLogPath, string annotatorId)
    {
        ArgumentException.ThrowIfNullOrEmpty(penLogPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(annotatorId);
        return penLogPath + ".ann." + AnnotatorFileId(annotatorId) + ".json";
    }

    /// <summary>Путь файла разметки прежнего вида <c>x.pfl.jsonl.gz.ann.json</c> (автор — по содержимому файла).</summary>
    public static string LegacyAnnotationPathFor(string penLogPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(penLogPath);
        return penLogPath + ".ann.json";
    }

    /// <summary>Все файлы разметки записи (всех разметчиков и прежнего вида), по алфавиту.</summary>
    public static IReadOnlyList<string> FindAnnotationFiles(string penLogPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(penLogPath);
        string directory = Path.GetDirectoryName(penLogPath) ?? string.Empty;
        if (!Directory.Exists(directory))
        {
            return [];
        }

        string prefix = Path.GetFileName(penLogPath) + ".ann.";
        return
        [
            .. Directory.EnumerateFiles(directory, Path.GetFileName(penLogPath) + ".ann*.json")
                .Where(p => Path.GetFileName(p).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(p).EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>Путь записи по пути её файла разметки (любого вида).</summary>
    public static string PenLogPathFor(string annotationPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(annotationPath);
        int index = annotationPath.LastIndexOf(PenLogExtension + ".ann", StringComparison.OrdinalIgnoreCase);
        return index >= 0 && annotationPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? annotationPath[..(index + PenLogExtension.Length)]
            : throw new ArgumentException($"'{annotationPath}' is not an annotation file of a recording.", nameof(annotationPath));
    }

    /// <summary>Файл — запись пера (по расширению).</summary>
    public static bool IsPenLogFile(string path) => path.EndsWith(PenLogExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Папка субъекта.</summary>
    public string SubjectDirectory(SubjectInfo subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return Path.Combine(Root, subject.FolderName);
    }

    /// <summary>
    /// Загружает прогресс субъекта: основной файл, а если он не читается — резервный <c>progress.json.bak</c>; иначе — новый прогресс
    /// (на диск не пишет). Субъект в прогрессе — тот, что записан в файле (не из формы): метаданные не должны расходиться между записями.
    /// </summary>
    /// <exception cref="InvalidOperationException">В папке лежит прогресс другого протокола.</exception>
    public ProgressLoad LoadProgress(SubjectInfo subject, string protocolId)
    {
        ArgumentNullException.ThrowIfNull(subject);
        string path = Path.Combine(SubjectDirectory(subject), ProgressFileName);
        bool unreadable = false;
        foreach ((string file, bool backup) in new[] { (path, false), (path + ".bak", true) })
        {
            if (!File.Exists(file))
            {
                continue;
            }

            try
            {
                ProtocolProgress progress = ProtocolProgress.FromJson(File.ReadAllText(file, Encoding.UTF8));
                if (!string.Equals(progress.ProtocolId, protocolId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Папка субъекта содержит прогресс протокола '{progress.ProtocolId}', а выбран '{protocolId}'.");
                }

                return new ProgressLoad(progress, Existed: true, RecoveredFromBackup: backup, Unreadable: unreadable);
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                unreadable = true;
            }
        }

        return new ProgressLoad(new ProtocolProgress { ProtocolId = protocolId, Subject = subject }, Existed: false, RecoveredFromBackup: false, Unreadable: unreadable);
    }

    /// <summary>Сохраняет прогресс в папку субъекта (атомарно, прежняя версия — в <c>progress.json.bak</c>).</summary>
    public void SaveProgress(ProtocolProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        string directory = SubjectDirectory(progress.Subject);
        Directory.CreateDirectory(directory);
        AtomicFile.WriteText(Path.Combine(directory, ProgressFileName), progress.ToJson(), keepBackup: true);
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
            path = Path.Combine(directory, stem + "-" + i.ToString(CultureInfo.InvariantCulture) + PenLogExtension);
        }

        WritePenLogFile(path, log);
        return path;
    }

    /// <summary>Пишет запись по указанному пути (атомарно, с принудительным сбросом на диск; поверх существующего файла не пишет).</summary>
    public static void WritePenLogFile(string path, PenLog log)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(log);
        AtomicFile.WriteNew(path, stream =>
        {
            using var gzip = new GZipStream(stream, CompressionLevel.Optimal, leaveOpen: true);
            PenLogWriter.Write(gzip, log);
        });
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

    /// <summary>SHA-256 несжатого JSONL записи (строчные hex-цифры): по нему разметка привязывается к записи.</summary>
    public static string ComputeSha256(string penLogPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(penLogPath);
        using var file = new FileStream(penLogPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Stream stream = penLogPath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(file, CompressionMode.Decompress) : file;
        using (stream)
        {
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
    }

    /// <summary>
    /// Убирает перезаписанную запись (со всеми файлами разметки) из корпуса: переносит в подпапку <see cref="SupersededFolder"/>.
    /// Пара «запись + разметки» получает ОДИН общий префикс при совпадении имён, чтобы файлы не разошлись. Данные не удаляются.
    /// </summary>
    public static void Supersede(string penLogPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(penLogPath);
        string directory = Path.Combine(Path.GetDirectoryName(penLogPath)!, SupersededFolder);
        Directory.CreateDirectory(directory);
        string[] files = [penLogPath, .. FindAnnotationFiles(penLogPath)];

        string prefix = string.Empty;
        for (int i = 2; files.Any(f => File.Exists(Path.Combine(directory, prefix + Path.GetFileName(f)))); i++)
        {
            prefix = i.ToString(CultureInfo.InvariantCulture) + "-";
        }

        foreach (string file in files)
        {
            File.Move(file, Path.Combine(directory, prefix + Path.GetFileName(file)));
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

    /// <summary>Пишет текст атомарно (временный файл, сброс на диск, переименование).</summary>
    public static void WriteTextAtomic(string path, string text) => AtomicFile.WriteText(path, text);
}
