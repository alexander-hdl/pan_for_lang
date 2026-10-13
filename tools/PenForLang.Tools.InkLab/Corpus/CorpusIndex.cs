using System.Collections.Immutable;
using System.IO;
using System.Text;
using PenForLang.Ink;
using PenForLang.Tools.InkLab.Annotation;
using PenForLang.Tools.InkLab.Recording;
using PenForLang.Tools.InkLab.Scene;

namespace PenForLang.Tools.InkLab.Corpus;

/// <summary>Состояние разметки записи.</summary>
public enum AnnotationState
{
    /// <summary>Файла разметки нет.</summary>
    None,

    /// <summary>Файл есть, но в нём ошибки или он не читается (разметка не закончена).</summary>
    Draft,

    /// <summary>Разметка полна и верна, запись годна.</summary>
    Valid,

    /// <summary>Разметчик отметил запись браком (разметка верна).</summary>
    Rejected,
}

/// <summary>Запись корпуса с краткими сведениями о ней и о разметке.</summary>
/// <param name="Path">Полный путь записи.</param>
/// <param name="Subject">Субъект (из заголовка) или папка, если заголовок без субъекта.</param>
/// <param name="Target">Цель (<c>glyph:a</c>, <c>word:at</c>) или <c>?</c>, если запись не читается.</param>
/// <param name="Variant">Вариант написания (<c>lift</c>, <c>crossbar</c>) или <c>null</c>.</param>
/// <param name="TaskId">Идентификатор задания.</param>
/// <param name="Annotation">Состояние разметки.</param>
/// <param name="ErrorCount">Сколько ошибок нашла проверка (для <see cref="AnnotationState.Draft"/>).</param>
/// <param name="ReadError">Почему запись не читается, иначе <c>null</c>.</param>
public sealed record CorpusEntry(
    string Path,
    string Subject,
    string Target,
    string? Variant,
    string TaskId,
    AnnotationState Annotation,
    int ErrorCount,
    string? ReadError)
{
    /// <summary>Имя файла.</summary>
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>Состояние разметки словами для списка.</summary>
    public string StateText => ReadError is not null
        ? "не читается"
        : Annotation switch
        {
            AnnotationState.Valid => "размечено",
            AnnotationState.Rejected => "брак",
            AnnotationState.Draft => "начато, ошибок: " + ErrorCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => "без разметки",
        };

    /// <summary>Группа для сводки: цель и вариант.</summary>
    public string Group => Variant is null ? Target : Target + " · " + Variant;
}

/// <summary>Просмотр корпуса: список записей папки с состоянием разметки.</summary>
public static class CorpusIndex
{
    /// <summary>Читает все записи папки (и подпапок, кроме <c>superseded</c>) и проверяет их разметку.</summary>
    public static IReadOnlyList<CorpusEntry> Scan(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        return [.. CorpusStore.EnumeratePenLogs(folder).Select(Describe)];
    }

    /// <summary>Сведения об одной записи.</summary>
    public static CorpusEntry Describe(string penLogPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(penLogPath);
        string folder = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(penLogPath)) ?? string.Empty;
        PenLog log;
        try
        {
            log = CorpusStore.ReadPenLog(penLogPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PenLogFormatException or InvalidDataException)
        {
            return new CorpusEntry(penLogPath, folder, "?", null, string.Empty, AnnotationState.None, 0, ex.Message);
        }

        PenLogTask? task = log.Header.Task;
        string subject = log.Header.Subject?.Anon ?? folder;
        string? variant = null;
        string taskId = task?.TaskId ?? string.Empty;
        if (RecordingHeaders.TryParseTaskId(taskId, out _, out _, out string? parsed))
        {
            variant = parsed;
        }

        (AnnotationState state, int errors) = AnnotationStateOf(penLogPath, log);
        return new CorpusEntry(penLogPath, subject, task?.Target ?? "?", variant, taskId, state, errors, null);
    }

    /// <summary>Состояние разметки записи и число ошибок проверки.</summary>
    public static (AnnotationState State, int Errors) AnnotationStateOf(string penLogPath, PenLog log)
    {
        string annotationPath = CorpusStore.AnnotationPathFor(penLogPath);
        if (!File.Exists(annotationPath))
        {
            return (AnnotationState.None, 0);
        }

        try
        {
            AnnotationFile annotation = AnnotationJson.Load(annotationPath);
            ImmutableArray<int> counts = SceneBuilder.StrokeSampleCounts(log.ToInkAttempt().Strokes);
            IReadOnlyList<AnnotationIssue> issues = AnnotationValidator.Validate(annotation, counts);
            int errors = issues.Count(i => i.Severity == IssueSeverity.Error);
            if (errors > 0)
            {
                return (AnnotationState.Draft, errors);
            }

            return (annotation.Quality.Usable ? AnnotationState.Valid : AnnotationState.Rejected, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AnnotationFormatException)
        {
            return (AnnotationState.Draft, 1);
        }
    }
}

/// <summary>Строка сводки: сколько записей и как они размечены.</summary>
/// <param name="Name">Название группы (цель, вариант, субъект).</param>
/// <param name="Recordings">Записей.</param>
/// <param name="Valid">Размечено верно и годных.</param>
/// <param name="Rejected">Размечено как брак.</param>
/// <param name="Draft">Разметка начата, но с ошибками.</param>
/// <param name="None">Не размечено.</param>
public sealed record SummaryRow(string Name, int Recordings, int Valid, int Rejected, int Draft, int None);

/// <summary>Сводка корпуса: сколько записей по видам и сколько размечено.</summary>
/// <param name="Total">Всего записей (читаемых и нет).</param>
/// <param name="Unreadable">Записей, которые не читаются.</param>
/// <param name="ByGroup">Строки по цели и варианту.</param>
/// <param name="BySubject">Строки по субъектам.</param>
/// <param name="Overall">Итог по всем читаемым записям.</param>
public sealed record CorpusSummary(int Total, int Unreadable, IReadOnlyList<SummaryRow> ByGroup, IReadOnlyList<SummaryRow> BySubject, SummaryRow Overall)
{
    /// <summary>Строит сводку по записям.</summary>
    public static CorpusSummary Build(IReadOnlyList<CorpusEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        CorpusEntry[] readable = [.. entries.Where(e => e.ReadError is null)];
        return new CorpusSummary(
            entries.Count,
            entries.Count - readable.Length,
            Rows(readable, e => e.Group),
            Rows(readable, e => e.Subject),
            Row("Всего", readable));
    }

    /// <summary>Сводка текстом (таблицы Markdown) для вкладки и файла <c>corpus-summary.md</c>.</summary>
    public string ToMarkdown(string? corpusFolder = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Сводка корпуса InkLab");
        if (corpusFolder is not null)
        {
            sb.AppendLine().Append("Папка: `").Append(corpusFolder).AppendLine("`");
        }

        string unreadable = Unreadable > 0 ? $" (не читаются: {Unreadable})" : string.Empty;
        sb.AppendLine().AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Записей: {Total}{unreadable}");
        AppendTable(sb, "По видам", "Вид", ByGroup);
        AppendTable(sb, "По субъектам", "Субъект", BySubject);
        sb.AppendLine().AppendLine(System.Globalization.CultureInfo.InvariantCulture,
            $"Размечено верно: {Overall.Valid + Overall.Rejected} из {Overall.Recordings} (годных {Overall.Valid}, брак {Overall.Rejected}); начато с ошибками: {Overall.Draft}; без разметки: {Overall.None}.");
        return sb.ToString();
    }

    private static void AppendTable(StringBuilder sb, string title, string firstColumn, IReadOnlyList<SummaryRow> rows)
    {
        sb.AppendLine().Append("## ").AppendLine(title).AppendLine();
        sb.Append("| ").Append(firstColumn).AppendLine(" | Записей | Размечено (годных) | Брак | Начато (ошибки) | Без разметки |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (SummaryRow r in rows)
        {
            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"| {r.Name} | {r.Recordings} | {r.Valid} | {r.Rejected} | {r.Draft} | {r.None} |");
        }
    }

    private static List<SummaryRow> Rows(IEnumerable<CorpusEntry> entries, Func<CorpusEntry, string> key) =>
        [.. entries.GroupBy(key, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => Row(g.Key, g))];

    private static SummaryRow Row(string name, IEnumerable<CorpusEntry> entries)
    {
        CorpusEntry[] list = [.. entries];
        return new SummaryRow(
            name,
            list.Length,
            list.Count(e => e.Annotation == AnnotationState.Valid),
            list.Count(e => e.Annotation == AnnotationState.Rejected),
            list.Count(e => e.Annotation == AnnotationState.Draft),
            list.Count(e => e.Annotation == AnnotationState.None));
    }
}
