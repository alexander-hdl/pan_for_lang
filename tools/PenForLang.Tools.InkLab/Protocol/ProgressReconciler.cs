using System.Globalization;
using System.IO;
using PenForLang.Ink;
using PenForLang.Tools.InkLab.Corpus;
using PenForLang.Tools.InkLab.Recording;

namespace PenForLang.Tools.InkLab.Protocol;

/// <summary>Итог сверки прогресса с файлами записей.</summary>
/// <param name="AdoptedDone">Подсказок отмечено записанными по найденным файлам (прогресс их не знал).</param>
/// <param name="ResetToPending">Подсказок возвращено в «не записано»: файл из прогресса пропал.</param>
/// <param name="Superseded">Лишних файлов (дубли и сироты) перенесено в <c>superseded</c>.</param>
/// <param name="Unreadable">Файлов с нечитаемым заголовком (не тронуты).</param>
/// <param name="Foreign">Файлов другого протокола или без задания (не тронуты).</param>
/// <param name="Messages">Пояснения для записывающего.</param>
public sealed record ReconcileReport(int AdoptedDone, int ResetToPending, int Superseded, int Unreadable, int Foreign, IReadOnlyList<string> Messages)
{
    /// <summary>Прогресс изменился (его нужно сохранить).</summary>
    public bool ProgressChanged => AdoptedDone > 0 || ResetToPending > 0;

    /// <summary>Что-то нашлось и исправлено или нечитаемо.</summary>
    public bool HasFindings => ProgressChanged || Superseded > 0 || Unreadable > 0;
}

/// <summary>
/// Сверка прогресса протокола с файлами записей в папке субъекта (запись должна переживать сбой посреди «Готово»): по <c>task.taskId</c>
/// из заголовков penlog находятся записи каждой подсказки. Прогресс ссылается на пропавший файл — подсказка снова «не записана»;
/// файл есть, а прогресс о нём не знает (потерян или сбой при сохранении) — подсказка отмечается записанной; несколько файлов на одну
/// подсказку — остаётся принятый (или самый новый), остальные переносятся в <c>superseded</c>.
/// </summary>
public static class ProgressReconciler
{
    /// <summary>Сверяет прогресс (изменяется на месте; сохранять — забота вызывающего).</summary>
    public static ReconcileReport Reconcile(CorpusStore store, SubjectInfo subject, RecordingProtocol protocol, ProtocolProgress progress)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(progress);
        CultureInfo inv = CultureInfo.InvariantCulture;
        string directory = store.SubjectDirectory(subject);
        var messages = new List<string>();
        int adopted = 0;
        int reset = 0;
        int superseded = 0;
        int unreadable = 0;
        int foreign = 0;

        var byPrompt = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string file in CorpusStore.EnumeratePenLogs(directory))
        {
            PenLogHeader header;
            try
            {
                header = CorpusStore.ReadHeader(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PenLogFormatException or InvalidDataException)
            {
                unreadable++;
                messages.Add($"Файл {Path.GetFileName(file)} не читается и не учтён: {ex.Message}");
                continue;
            }

            if (!RecordingHeaders.TryParseTaskId(header.Task?.TaskId, out string protocolId, out string promptId, out _)
                || !string.Equals(protocolId, protocol.Id, StringComparison.Ordinal)
                || protocol.IndexOf(promptId) < 0)
            {
                foreign++;
                continue;
            }

            if (!byPrompt.TryGetValue(promptId, out List<string>? list))
            {
                list = [];
                byPrompt[promptId] = list;
            }

            list.Add(file);
        }

        foreach (RecordingPrompt prompt in protocol.Prompts)
        {
            byPrompt.TryGetValue(prompt.Id, out List<string>? files);
            files ??= [];
            PromptProgress? entry = progress.Prompts.TryGetValue(prompt.Id, out PromptProgress? e) ? e : null;
            if (entry is { Status: PromptStatus.Done, File: { } recorded })
            {
                string full = Path.GetFullPath(Path.Combine(directory, recorded.Replace('/', Path.DirectorySeparatorChar)));
                if (!files.Any(f => string.Equals(Path.GetFullPath(f), full, StringComparison.OrdinalIgnoreCase)))
                {
                    messages.Add(string.Create(inv, $"Файл подсказки {prompt.Id} ({recorded}) пропал или не опознан."));
                }
            }

            // Принимается самый новый файл подсказки: перезапись после сбоя при сохранении прогресса новее той, что записана в прогрессе.
            string? accepted = files.OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc).ThenByDescending(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (accepted is not null)
            {
                PromptProgress target = progress.Entry(prompt.Id);
                bool wasDone = target.Status == PromptStatus.Done;
                target.Status = PromptStatus.Done;
                target.File = Path.GetRelativePath(directory, accepted).Replace('\\', '/');
                target.Takes = Math.Max(target.Takes, files.Count);
                if (!wasDone)
                {
                    adopted++;
                }
            }
            else if (entry is { Status: PromptStatus.Done })
            {
                entry.Status = PromptStatus.Pending;
                entry.File = null;
                reset++;
            }

            foreach (string extra in files.Where(f => !string.Equals(f, accepted, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    CorpusStore.Supersede(extra);
                    superseded++;
                    messages.Add($"Лишний файл подсказки {prompt.Id} перенесён в superseded: {Path.GetFileName(extra)}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    messages.Add($"Не удалось перенести {Path.GetFileName(extra)} в superseded: {ex.Message}");
                }
            }
        }

        if (adopted > 0)
        {
            messages.Add(string.Create(inv, $"По файлам записей восстановлено записанных подсказок: {adopted}."));
        }

        return new ReconcileReport(adopted, reset, superseded, unreadable, foreign, messages);
    }
}
