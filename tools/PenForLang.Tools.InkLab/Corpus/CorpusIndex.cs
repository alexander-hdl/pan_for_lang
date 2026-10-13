using System.Collections.Immutable;
using System.Globalization;
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
    /// <summary>Файлов разметки нет.</summary>
    None,

    /// <summary>Файл есть, но в нём ошибки или он не читается (разметка не закончена).</summary>
    Draft,

    /// <summary>Разметка полна и верна, запись годна.</summary>
    Valid,

    /// <summary>Разметчик отметил запись браком (разметка верна).</summary>
    Rejected,
}

/// <summary>Разметка одного разметчика.</summary>
/// <param name="Annotator">Идентификатор разметчика (из файла; для нечитаемого — из имени файла).</param>
/// <param name="State">Состояние.</param>
/// <param name="Errors">Число ошибок проверки.</param>
/// <param name="Path">Файл разметки.</param>
/// <param name="Legacy">Файл прежнего вида <c>x.pfl.jsonl.gz.ann.json</c>.</param>
public sealed record AnnotatorState(string Annotator, AnnotationState State, int Errors, string Path, bool Legacy);

/// <summary>Запись корпуса с краткими сведениями о ней и о разметке.</summary>
/// <param name="Path">Полный путь записи.</param>
/// <param name="Subject">Субъект (из заголовка) или папка, если заголовок без субъекта.</param>
/// <param name="Target">Цель (<c>glyph:a</c>, <c>word:at</c>) или <c>?</c>, если запись не читается.</param>
/// <param name="Variant">Вариант написания (<c>lift</c>, <c>joined</c>, <c>dot</c>) или <c>null</c>.</param>
/// <param name="TaskId">Идентификатор задания.</param>
/// <param name="Annotation">Лучшее состояние разметки среди разметчиков.</param>
/// <param name="ErrorCount">Сколько ошибок у лучшей из неполных разметок (для <see cref="AnnotationState.Draft"/>).</param>
/// <param name="ReadError">Почему запись не читается, иначе <c>null</c>.</param>
/// <param name="Source">Источник пера из заголовка (<c>Win32Pointer</c>, <c>Mouse</c>…) или <c>?</c>.</param>
/// <param name="Annotators">Разметки по разметчикам.</param>
/// <param name="DuplicateTaskId">Тот же <c>taskId</c> у другой записи этого субъекта (дубль).</param>
public sealed record CorpusEntry(
    string Path,
    string Subject,
    string Target,
    string? Variant,
    string TaskId,
    AnnotationState Annotation,
    int ErrorCount,
    string? ReadError,
    string Source = "?",
    IReadOnlyList<AnnotatorState>? Annotators = null,
    bool DuplicateTaskId = false)
{
    /// <summary>Имя файла.</summary>
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>Разметки по разметчикам (пусто, если нет).</summary>
    public IReadOnlyList<AnnotatorState> AnnotatorStates => Annotators ?? [];

    /// <summary>Состояние разметки словами для списка.</summary>
    public string StateText => ReadError is not null
        ? "не читается"
        : Annotation switch
        {
            AnnotationState.Valid => "размечено",
            AnnotationState.Rejected => "брак",
            AnnotationState.Draft => "начато, ошибок: " + ErrorCount.ToString(CultureInfo.InvariantCulture),
            _ => "без разметки",
        } + (DuplicateTaskId ? ", дубль" : string.Empty);

    /// <summary>Группа для сводки: цель и вариант.</summary>
    public string Group => Variant is null ? Target : Target + " · " + Variant;
}

/// <summary>Просмотр корпуса: список записей папки с состоянием разметки.</summary>
public static class CorpusIndex
{
    /// <summary>Читает все записи папки (и подпапок, кроме <c>superseded</c>) и проверяет их разметку; помечает дубли <c>taskId</c>.</summary>
    public static IReadOnlyList<CorpusEntry> Scan(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        CorpusEntry[] entries = [.. CorpusStore.EnumeratePenLogs(folder).Select(Describe)];

        // Дубль: тот же taskId у другой записи в той же папке (субъекте).
        var duplicates = entries
            .Where(e => e.ReadError is null && e.TaskId.Length > 0)
            .GroupBy(e => (System.IO.Path.GetDirectoryName(e.Path), e.TaskId))
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            .Select(e => e.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. entries.Select(e => duplicates.Contains(e.Path) ? e with { DuplicateTaskId = true } : e)];
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

        IReadOnlyList<AnnotatorState> annotators = AnnotationStates(penLogPath, log);
        (AnnotationState best, int errors) = Best(annotators);
        return new CorpusEntry(
            penLogPath, subject, task?.Target ?? "?", variant, taskId, best, errors, null, log.Header.Device.Source.ToString(), annotators);
    }

    /// <summary>Разметки всех разметчиков записи: состояние каждой и число ошибок проверки.</summary>
    public static IReadOnlyList<AnnotatorState> AnnotationStates(string penLogPath, PenLog log)
    {
        ArgumentException.ThrowIfNullOrEmpty(penLogPath);
        ArgumentNullException.ThrowIfNull(log);
        IReadOnlyList<string> files = CorpusStore.FindAnnotationFiles(penLogPath);
        if (files.Count == 0)
        {
            return [];
        }

        string? sha = null;
        ImmutableArray<InkStroke> strokes = log.ToInkAttempt().Strokes;
        var result = new List<AnnotatorState>();
        foreach (string file in files)
        {
            string name = System.IO.Path.GetFileName(file);
            string prefix = System.IO.Path.GetFileName(penLogPath) + ".ann.";
            bool legacy = name.Length < prefix.Length + ".json".Length;
            string middle = legacy ? string.Empty : name[prefix.Length..^".json".Length];
            try
            {
                AnnotationFile annotation = AnnotationJson.Load(file);
                sha ??= CorpusStore.ComputeSha256(penLogPath);
                IReadOnlyList<AnnotationIssue> issues = AnnotationValidator.Validate(annotation, strokes, sha);
                int errors = issues.Count(i => i.Severity == IssueSeverity.Error);
                AnnotationState state = errors > 0 ? AnnotationState.Draft : (annotation.Quality.Usable ? AnnotationState.Valid : AnnotationState.Rejected);
                result.Add(new AnnotatorState(annotation.Annotator.Length > 0 ? annotation.Annotator : middle, state, errors, file, legacy));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AnnotationFormatException or InvalidDataException)
            {
                result.Add(new AnnotatorState(legacy ? "?" : middle, AnnotationState.Draft, 1, file, legacy));
            }
        }

        return result;
    }

    private static (AnnotationState State, int Errors) Best(IReadOnlyList<AnnotatorState> annotators)
    {
        if (annotators.Count == 0)
        {
            return (AnnotationState.None, 0);
        }

        foreach (AnnotationState wanted in new[] { AnnotationState.Valid, AnnotationState.Rejected })
        {
            if (annotators.Any(a => a.State == wanted))
            {
                return (wanted, 0);
            }
        }

        return (AnnotationState.Draft, annotators.Min(a => a.Errors));
    }
}

/// <summary>Строка сводки: сколько записей и как они размечены.</summary>
/// <param name="Name">Название группы (цель, вариант, субъект, источник).</param>
/// <param name="Recordings">Записей.</param>
/// <param name="Valid">Размечено верно и годных.</param>
/// <param name="Rejected">Размечено как брак.</param>
/// <param name="Draft">Разметка начата, но с ошибками.</param>
/// <param name="None">Не размечено.</param>
public sealed record SummaryRow(string Name, int Recordings, int Valid, int Rejected, int Draft, int None);

/// <summary>Сводка корпуса: сколько записей по видам и сколько размечено, с предупреждениями о пригодности корпуса.</summary>
/// <param name="Total">Всего записей (читаемых и нет).</param>
/// <param name="Unreadable">Записей, которые не читаются.</param>
/// <param name="ByGroup">Строки по цели и варианту.</param>
/// <param name="BySubject">Строки по субъектам.</param>
/// <param name="BySource">Строки по источнику пера.</param>
/// <param name="Overall">Итог по всем читаемым записям.</param>
/// <param name="Warnings">Предупреждения: запись мышью, дубли <c>taskId</c>, нечитаемые файлы.</param>
public sealed record CorpusSummary(
    int Total,
    int Unreadable,
    IReadOnlyList<SummaryRow> ByGroup,
    IReadOnlyList<SummaryRow> BySubject,
    IReadOnlyList<SummaryRow> BySource,
    SummaryRow Overall,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Источники пера, записи которых нельзя брать в корпус как эталоны с планшета.</summary>
    public static bool IsDevelopmentSource(string source) => string.Equals(source, nameof(PenSourceKind.Mouse), StringComparison.Ordinal);

    /// <summary>Строит сводку по записям.</summary>
    public static CorpusSummary Build(IReadOnlyList<CorpusEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        CorpusEntry[] readable = [.. entries.Where(e => e.ReadError is null)];
        CultureInfo inv = CultureInfo.InvariantCulture;
        var warnings = new List<string>();

        int unreadable = entries.Count - readable.Length;
        if (unreadable > 0)
        {
            warnings.Add(string.Create(inv, $"Не читаются файлов: {unreadable}."));
        }

        int mouse = readable.Count(e => IsDevelopmentSource(e.Source));
        if (mouse > 0)
        {
            warnings.Add(string.Create(inv, $"Записей мышью (источник Mouse): {mouse} — это пробные записи, в корпус эталонов они не годятся."));
        }

        foreach (IGrouping<string, CorpusEntry> g in readable.Where(e => e.DuplicateTaskId).GroupBy(e => e.Subject + " / " + e.TaskId, StringComparer.Ordinal))
        {
            warnings.Add(string.Create(inv, $"Дубль задания {g.Key}: {g.Count()} файлов — оставьте одну запись (остальные в superseded)."));
        }

        return new CorpusSummary(
            entries.Count,
            unreadable,
            Rows(readable, e => e.Group),
            Rows(readable, e => e.Subject),
            Rows(readable, e => e.Source),
            Row("Всего", readable),
            warnings);
    }

    /// <summary>Сводка текстом (таблицы Markdown) для вкладки и файла <c>corpus-summary.md</c>.</summary>
    public string ToMarkdown(string? corpusFolder = null)
    {
        var sb = new StringBuilder();
        CultureInfo inv = CultureInfo.InvariantCulture;
        sb.AppendLine("# Сводка корпуса InkLab");
        if (corpusFolder is not null)
        {
            sb.AppendLine().Append("Папка: `").Append(corpusFolder).AppendLine("`");
        }

        string unreadableText = Unreadable > 0 ? $" (не читаются: {Unreadable})" : string.Empty;
        sb.AppendLine().AppendLine(inv, $"Записей: {Total}{unreadableText}");
        if (Warnings.Count > 0)
        {
            sb.AppendLine().AppendLine("## Предупреждения").AppendLine();
            foreach (string warning in Warnings)
            {
                sb.Append("- ").AppendLine(warning);
            }
        }

        AppendTable(sb, "По видам", "Вид", ByGroup);
        AppendTable(sb, "По субъектам", "Субъект", BySubject);
        AppendTable(sb, "По источнику пера", "Источник", BySource);
        sb.AppendLine().AppendLine(
            inv,
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
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {r.Name} | {r.Recordings} | {r.Valid} | {r.Rejected} | {r.Draft} | {r.None} |");
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
