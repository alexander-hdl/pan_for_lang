using System.IO;
using System.Text;

namespace PenForLang.Tools.InkLab.Corpus;

/// <summary>
/// Запись файлов без потери старой версии: данные пишутся во временный файл, сбрасываются на диск (<see cref="FileStream.Flush(bool)"/>
/// с <c>flushToDisk</c>) и только потом переименовываются. Сбой питания или падение посреди записи оставляют прежний файл целым.
/// </summary>
public static class AtomicFile
{
    /// <summary>Пишет текст (UTF-8 без BOM), заменяя файл; при <paramref name="keepBackup"/> прежняя версия остаётся как <c>.bak</c>.</summary>
    public static void WriteText(string path, string text, bool keepBackup = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(text);
        string temp = path + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(text);
                file.Write(bytes, 0, bytes.Length);
                file.Flush(flushToDisk: true);
            }

            if (keepBackup && File.Exists(path))
            {
                File.Copy(path, path + ".bak", overwrite: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            DeleteQuietly(temp);
        }
    }

    /// <summary>Пишет новый файл через <paramref name="write"/> (поток уже открыт), сбрасывает на диск и переименовывает; существующий файл не затирает.</summary>
    /// <exception cref="IOException">Файл с таким именем уже есть.</exception>
    public static void WriteNew(string path, Action<Stream> write)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(write);
        string temp = path + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                write(file);
                file.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: false);
        }
        finally
        {
            DeleteQuietly(temp);
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Временный файл не удалился — прежний результат записи важнее.
        }
    }
}
