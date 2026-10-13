using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using PenForLang.Ink;

namespace PenForLang.Tools.InkLab.Annotation;

/// <summary>Серьёзность замечания проверки разметки.</summary>
public enum IssueSeverity
{
    /// <summary>Разметка непригодна для корпуса, пока не исправлена.</summary>
    Error,

    /// <summary>Подозрительно, но не мешает.</summary>
    Warning,
}

/// <summary>Замечание проверки разметки.</summary>
/// <param name="Severity">Серьёзность.</param>
/// <param name="Code">Стабильный код (<see cref="IssueCodes"/>).</param>
/// <param name="Message">Пояснение для разметчика.</param>
public sealed record AnnotationIssue(IssueSeverity Severity, string Code, string Message);

/// <summary>Коды замечаний <see cref="AnnotationValidator"/>.</summary>
public static class IssueCodes
{
    /// <summary>Чужая схема.</summary>
    public const string Schema = "schema";

    /// <summary>Другое правило нумерации.</summary>
    public const string Indexing = "indexing";

    /// <summary>Хеш записи записан неверно (не 64 hex-символа).</summary>
    public const string HashFormat = "hash-format";

    /// <summary>Хеш записи не совпадает: запись заменена или изменена после разметки.</summary>
    public const string HashMismatch = "hash-mismatch";

    /// <summary>Текст пуст (для годной записи).</summary>
    public const string TextEmpty = "text-empty";

    /// <summary>В тексте пробелы или управляющие символы.</summary>
    public const string TextChars = "text-chars";

    /// <summary>Число стыков не равно числу букв минус один (для годной записи).</summary>
    public const string JunctionCount = "junction-count";

    /// <summary>Номера стыков (<c>after</c>) не образуют 0…n−2 без повторов.</summary>
    public const string JunctionIndex = "junction-index";

    /// <summary>Номер штриха или точки вне записи.</summary>
    public const string PointRange = "point-range";

    /// <summary>Время <c>t</c> точки не совпадает со временем точки записи.</summary>
    public const string PointTime = "point-time";

    /// <summary>Границы не идут по порядку вдоль следа.</summary>
    public const string NotMonotone = "not-monotone";

    /// <summary>Тело буквы пусто: границы совпадают или перепутаны.</summary>
    public const string EmptyLetter = "empty-letter";

    /// <summary>Стык отмечен на отложенном штрихе.</summary>
    public const string JunctionOnDeferred = "junction-on-deferred";

    /// <summary>У соединённого стыка начало и конец в разных штрихах.</summary>
    public const string ConnectedAcrossStrokes = "connected-across-strokes";

    /// <summary>Отрыв: конец и начало в одном штрихе, начало раньше конца или между ними есть основной штрих.</summary>
    public const string LiftGeometry = "lift-geometry";

    /// <summary>Номер отложенного штриха вне записи.</summary>
    public const string DeferredRange = "deferred-range";

    /// <summary>Один штрих отмечен отложенным дважды.</summary>
    public const string DeferredDuplicate = "deferred-duplicate";

    /// <summary>Отложенный штрих привязан к несуществующей букве.</summary>
    public const string DeferredLetter = "deferred-letter";

    /// <summary>Предупреждение: у буквы i, j, t или f нет отложенного штриха (точки или перекладины).</summary>
    public const string DeferredMissing = "deferred-missing";

    /// <summary>Предупреждение: вид отложенного штриха не подходит букве (точка у t, перекладина у i).</summary>
    public const string DeferredKind = "deferred-kind";

    /// <summary>Предупреждение: записанное время отложенного штриха не совпадает с выведенным из порядка штрихов.</summary>
    public const string TimingStale = "timing-stale";

    /// <summary>Предупреждение: мнение разметчика о времени расходится с выведенным.</summary>
    public const string TimingOpinion = "timing-opinion";

    /// <summary>Все штрихи отмечены отложенными: основного следа нет.</summary>
    public const string NoMainTrace = "no-main-trace";

    /// <summary>Сведения о штрихах (число точек, времена) не совпадают с записью.</summary>
    public const string StrokeMismatch = "stroke-mismatch";

    /// <summary>Неизвестный флаг качества.</summary>
    public const string UnknownFlag = "unknown-flag";

    /// <summary>Годная запись помечена флагом «не то написано».</summary>
    public const string QualityConsistency = "quality-consistency";
}

/// <summary>
/// Проверка разметки <c>pfl.annotation/1</c>. Привязка к записи: хеш, штрихи (число точек и времена), время <c>t</c> точек.
/// Для годной записи: текст из непробельных символов; стыков ровно «букв − 1»; границы идут по порядку вдоль основного следа
/// (конец левой буквы ≤ начало правой, тело каждой буквы непусто); стык не лежит на отложенном штрихе; «соединены» — конец и
/// начало в одном штрихе; «отрыв» — в разных, без основных штрихов между ними (точки — где угодно в своих штрихах); отложенные штрихи
/// существуют, различны и привязаны к существующим буквам. Для брака проверяются только схема, привязка и ссылки отложенных штрихов.
/// </summary>
public static partial class AnnotationValidator
{
    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Pattern();

    /// <summary>Проверяет разметку.</summary>
    /// <param name="annotation">Разметка.</param>
    /// <param name="strokes">Штрихи записи; <c>null</c> — проверять только внутреннюю согласованность (по <see cref="AnnotationFile.Strokes"/>).</param>
    /// <param name="actualSha256">Хеш записи; <c>null</c> — хеш не сверять.</param>
    public static IReadOnlyList<AnnotationIssue> Validate(AnnotationFile annotation, IReadOnlyList<InkStroke>? strokes = null, string? actualSha256 = null)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        var issues = new List<AnnotationIssue>();
        void Error(string code, string message) => issues.Add(new AnnotationIssue(IssueSeverity.Error, code, message));
        void Warn(string code, string message) => issues.Add(new AnnotationIssue(IssueSeverity.Warning, code, message));

        if (!string.Equals(annotation.Schema, AnnotationFormat.Schema, StringComparison.Ordinal))
        {
            Error(IssueCodes.Schema, $"Схема должна быть '{AnnotationFormat.Schema}', а не '{annotation.Schema}'.");
        }

        if (!string.Equals(annotation.Indexing, AnnotationFormat.Indexing, StringComparison.Ordinal))
        {
            Error(IssueCodes.Indexing, $"Правило нумерации должно быть '{AnnotationFormat.Indexing}', а не '{annotation.Indexing}'.");
        }

        if (!Sha256Pattern().IsMatch(annotation.PenlogSha256))
        {
            Error(IssueCodes.HashFormat, "Хеш записи (penlogSha256) — 64 строчные hex-цифры.");
        }
        else if (actualSha256 is not null && !string.Equals(annotation.PenlogSha256, actualSha256, StringComparison.Ordinal))
        {
            Error(IssueCodes.HashMismatch, "Хеш записи не совпадает: запись заменена или изменена после разметки.");
        }

        foreach (string flag in annotation.Quality.Flags.Where(f => !QualityFlags.All.Contains(f)))
        {
            Warn(IssueCodes.UnknownFlag, $"Неизвестный флаг качества '{flag}'.");
        }

        if (annotation.Quality.Usable && annotation.Quality.Flags.Contains(QualityFlags.WrongContent))
        {
            Warn(IssueCodes.QualityConsistency, "Запись помечена годной, но с флагом «написано не то»: укажите в тексте, что написано на самом деле.");
        }

        if (strokes is not null)
        {
            ImmutableArray<StrokeInfo> actual = StrokeInfo.Of(strokes);
            if (!annotation.Strokes.SequenceEqual(actual))
            {
                Error(
                    IssueCodes.StrokeMismatch,
                    $"Запись изменилась после разметки: в разметке {annotation.Strokes.Length} шт. штрихов ({Describe(annotation.Strokes)}), в записи {actual.Length} ({Describe(actual)}).");
            }
        }

        int[] sizes = strokes is not null ? [.. strokes.Select(s => s.Samples.Length)] : [.. annotation.Strokes.Select(s => s.N)];
        CheckDeferred(annotation, sizes, Error, Warn);

        if (!annotation.Quality.Usable)
        {
            return issues;
        }

        if (annotation.Text.Length == 0)
        {
            Error(IssueCodes.TextEmpty, "Не указано, что написано.");
            return issues;
        }

        if (annotation.Text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            Error(IssueCodes.TextChars, "В тексте только буквы, без пробелов и управляющих символов.");
            return issues;
        }

        CheckDeferredLetters(annotation, Warn);
        CheckJunctions(annotation, sizes, strokes, Error);
        return issues;
    }

    /// <summary>Нет замечаний уровня <see cref="IssueSeverity.Error"/>.</summary>
    public static bool IsValid(IEnumerable<AnnotationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        return issues.All(i => i.Severity != IssueSeverity.Error);
    }

    /// <summary>Какой вид отложенного штриха ожидается у буквы: точка у i и j, перекладина у t и f, иначе <c>null</c>.</summary>
    public static DeferredPartKind? ExpectedDeferredKind(char letter) => char.ToLowerInvariant(letter) switch
    {
        'i' or 'j' => DeferredPartKind.Dot,
        't' or 'f' => DeferredPartKind.Crossbar,
        _ => null,
    };

    private static string Describe(ImmutableArray<StrokeInfo> strokes) => string.Join(", ", strokes.Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.N}")));

    private static void CheckDeferred(AnnotationFile a, int[] sizes, Action<string, string> error, Action<string, string> warn)
    {
        var seen = new HashSet<int>();
        HashSet<int> deferred = [.. a.Deferred.Select(d => d.Stroke)];
        foreach (DeferredMark mark in a.Deferred)
        {
            if (mark.Stroke < 0 || mark.Stroke >= sizes.Length)
            {
                error(IssueCodes.DeferredRange, $"Отложенный штрих {mark.Stroke}: в записи штрихов {sizes.Length}.");
                continue;
            }

            if (!seen.Add(mark.Stroke))
            {
                error(IssueCodes.DeferredDuplicate, $"Штрих {mark.Stroke} отмечен отложенным дважды.");
            }

            if (a.Quality.Usable && (mark.Letter < 0 || mark.Letter >= a.Text.Length))
            {
                error(IssueCodes.DeferredLetter, $"Отложенный штрих {mark.Stroke} привязан к букве {mark.Letter}, а букв {a.Text.Length}.");
            }

            DeferredTiming derived = AnnotationFile.IsImmediate(mark.Stroke, sizes, deferred) ? DeferredTiming.Immediate : DeferredTiming.AfterWord;
            if (mark.Timing != derived)
            {
                warn(IssueCodes.TimingStale, $"Штрих {mark.Stroke}: записано время «{mark.Timing}», а по порядку штрихов выходит «{derived}».");
            }

            if (mark.TimingOpinion is { } opinion && opinion != derived)
            {
                warn(IssueCodes.TimingOpinion, $"Штрих {mark.Stroke}: мнение разметчика «{opinion}» расходится с выведенным по порядку «{derived}».");
            }
        }

        if (a.Quality.Usable && sizes.Length > 0 && seen.Count(s => s >= 0 && s < sizes.Length) >= sizes.Length)
        {
            error(IssueCodes.NoMainTrace, "Все штрихи отмечены отложенными: основного следа нет.");
        }
    }

    private static void CheckDeferredLetters(AnnotationFile a, Action<string, string> warn)
    {
        for (int i = 0; i < a.Text.Length; i++)
        {
            if (ExpectedDeferredKind(a.Text[i]) is not { } expected)
            {
                continue;
            }

            DeferredMark[] marks = [.. a.Deferred.Where(d => d.Letter == i)];
            if (marks.Length == 0)
            {
                string what = expected == DeferredPartKind.Dot ? "точка" : "перекладина";
                warn(IssueCodes.DeferredMissing, $"У буквы {i + 1} («{a.Text[i]}») не отмечена {what}: если она написана, отметьте её отложенным штрихом.");
            }
            else if (marks.All(m => m.Kind != expected))
            {
                warn(IssueCodes.DeferredKind, $"У буквы {i + 1} («{a.Text[i]}») вид отложенного штриха не подходит: ожидается {(expected == DeferredPartKind.Dot ? "точка" : "перекладина")}.");
            }
        }
    }

    private static void CheckJunctions(AnnotationFile a, int[] sizes, IReadOnlyList<InkStroke>? strokes, Action<string, string> error)
    {
        int letters = a.Text.Length;
        int expected = letters - 1;
        if (a.Junctions.Length != expected)
        {
            error(IssueCodes.JunctionCount, $"Букв {letters}, значит стыков должно быть {expected}, а размечено {a.Junctions.Length}.");
            return;
        }

        Junction[] ordered = [.. a.Junctions.OrderBy(j => j.After)];
        for (int i = 0; i < ordered.Length; i++)
        {
            if (ordered[i].After != i)
            {
                error(IssueCodes.JunctionIndex, $"Стыки должны идти между буквами 0|1, 1|2 …; найден стык after={ordered[i].After} на месте {i}.");
                return;
            }
        }

        HashSet<int> deferred = a.DeferredStrokes.ToHashSet();
        int[] main = [.. Enumerable.Range(0, sizes.Length).Where(s => !deferred.Contains(s) && sizes[s] > 0)];
        if (main.Length == 0)
        {
            return; // уже сообщено NoMainTrace; на пустом следе стыки проверять нечем
        }

        var first = new StrokePoint(main[0], 0);
        var last = new StrokePoint(main[^1], sizes[main[^1]] - 1);
        bool pointsOk = true;
        foreach (Junction j in ordered)
        {
            foreach ((PointRef point, string role) in new[] { (j.End, "конец"), (j.Start, "начало") })
            {
                if (point.Stroke < 0 || point.Stroke >= sizes.Length || point.Sample < 0 || point.Sample >= sizes[point.Stroke])
                {
                    error(IssueCodes.PointRange, $"Стык {Label(a, j)}: {role} ({point.Stroke}:{point.Sample}) вне записи.");
                    pointsOk = false;
                }
                else if (deferred.Contains(point.Stroke))
                {
                    error(IssueCodes.JunctionOnDeferred, $"Стык {Label(a, j)}: {role} лежит на отложенном штрихе {point.Stroke}.");
                    pointsOk = false;
                }
                else if (strokes is not null && strokes[point.Stroke].Samples[point.Sample].TimeUs != point.T)
                {
                    error(IssueCodes.PointTime, $"Стык {Label(a, j)}: время {role} (t={point.T}) не совпадает с точкой записи {point.Stroke}:{point.Sample}.");
                    pointsOk = false;
                }
            }
        }

        if (!pointsOk)
        {
            return;
        }

        // Тело буквы k: от начала стыка k−1 (для первой — от начала следа) до конца стыка k (для последней — до конца следа).
        StrokePoint bodyStart = first;
        foreach (Junction j in ordered)
        {
            StrokePoint end = j.End.Point;
            StrokePoint start = j.Start.Point;
            if (end > start)
            {
                error(IssueCodes.NotMonotone, $"Стык {Label(a, j)}: выход левой буквы позже входа правой.");
                return;
            }

            if (end < bodyStart)
            {
                error(IssueCodes.NotMonotone, $"Стык {Label(a, j)} стоит раньше начала буквы {j.After}: границы должны идти по порядку вдоль следа.");
                return;
            }

            if (end == bodyStart)
            {
                error(IssueCodes.EmptyLetter, $"Буква {j.After} ({a.Text[j.After]}) пуста: её тело состоит из одной точки, проверьте границы.");
                return;
            }

            CheckKind(a, j, deferred, sizes, error);
            bodyStart = start;
        }

        if (bodyStart >= last)
        {
            error(IssueCodes.EmptyLetter, $"Последняя буква ({a.Text[^1]}) пуста: последний стык лежит в самом конце следа.");
        }
    }

    private static void CheckKind(AnnotationFile a, Junction j, HashSet<int> deferred, int[] sizes, Action<string, string> error)
    {
        bool sameStroke = j.End.Stroke == j.Start.Stroke;
        if (j.Kind == JunctionKind.Connected)
        {
            if (!sameStroke)
            {
                error(IssueCodes.ConnectedAcrossStrokes, $"Стык {Label(a, j)} «соединены», но выход и вход в разных штрихах: между ними отрыв пера.");
            }

            return;
        }

        if (sameStroke)
        {
            error(IssueCodes.LiftGeometry, $"Стык {Label(a, j)} «отрыв», но выход и вход в одном штрихе: отрыва пера между ними нет.");
            return;
        }

        // Между выходом и входом — только отрыв (и, возможно, отложенные штрихи): основной штрих между ними принадлежал бы какой-то букве.
        for (int s = j.End.Stroke + 1; s < j.Start.Stroke; s++)
        {
            if (!deferred.Contains(s) && sizes[s] > 0)
            {
                error(IssueCodes.LiftGeometry, $"Стык {Label(a, j)} «отрыв»: между выходом и входом есть основной штрих {s}.");
                return;
            }
        }
    }

    /// <summary>Следующий после <paramref name="stroke"/> основной (не отложенный, непустой) штрих или -1.</summary>
    internal static int NextMainStroke(int stroke, IReadOnlyList<int> sizes, IReadOnlySet<int> deferred)
    {
        for (int s = stroke + 1; s < sizes.Count; s++)
        {
            if (!deferred.Contains(s) && sizes[s] > 0)
            {
                return s;
            }
        }

        return -1;
    }

    private static string Label(AnnotationFile a, Junction j)
    {
        string left = j.After >= 0 && j.After < a.Text.Length ? a.Text[j.After].ToString() : "?";
        string right = j.After + 1 >= 0 && j.After + 1 < a.Text.Length ? a.Text[j.After + 1].ToString() : "?";
        return string.Create(CultureInfo.InvariantCulture, $"{left}|{right}");
    }
}
