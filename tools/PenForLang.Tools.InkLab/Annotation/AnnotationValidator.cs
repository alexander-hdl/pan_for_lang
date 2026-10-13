using System.Collections.Immutable;
using System.Globalization;

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

    /// <summary>Границы не идут по порядку вдоль следа.</summary>
    public const string NotMonotone = "not-monotone";

    /// <summary>Тело буквы пусто: границы совпадают или перепутаны.</summary>
    public const string EmptyLetter = "empty-letter";

    /// <summary>Стык отмечен на отложенном штрихе.</summary>
    public const string JunctionOnDeferred = "junction-on-deferred";

    /// <summary>У соединённого стыка начало и конец в разных штрихах.</summary>
    public const string ConnectedAcrossStrokes = "connected-across-strokes";

    /// <summary>Отрыв: конец не в последней точке штриха или начало не в первой точке следующего основного штриха.</summary>
    public const string LiftGeometry = "lift-geometry";

    /// <summary>Номер отложенного штриха вне записи.</summary>
    public const string DeferredRange = "deferred-range";

    /// <summary>Один штрих отмечен отложенным дважды.</summary>
    public const string DeferredDuplicate = "deferred-duplicate";

    /// <summary>Отложенный штрих привязан к несуществующей букве.</summary>
    public const string DeferredLetter = "deferred-letter";

    /// <summary>Все штрихи отмечены отложенными: основного следа нет.</summary>
    public const string NoMainTrace = "no-main-trace";

    /// <summary>Число штрихов или точек в записи не совпадает с сохранённым в разметке.</summary>
    public const string StrokeMismatch = "stroke-mismatch";

    /// <summary>Неизвестный флаг качества.</summary>
    public const string UnknownFlag = "unknown-flag";

    /// <summary>Годная запись помечена флагом «не то написано» или брак без причины.</summary>
    public const string QualityConsistency = "quality-consistency";
}

/// <summary>
/// Проверка файла разметки <c>pfl.annotation/1</c>. Для годной записи требуется: текст из непробельных символов; стыков
/// ровно «букв − 1», по одному на каждую пару соседних букв; точки существуют в записи; границы идут по порядку вдоль основного
/// следа (конец левой буквы ≤ начало правой, тело каждой буквы непусто); стык не лежит на отложенном штрихе; «соединён» — стык
/// внутри одного штриха; «отрыв» — конец в последней точке штриха, начало в первой точке следующего основного штриха;
/// отложенные штрихи существуют, различны и привязаны к существующим буквам. Для брака проверяются только схема и ссылки.
/// </summary>
public static class AnnotationValidator
{
    /// <summary>Проверяет разметку.</summary>
    /// <param name="annotation">Разметка.</param>
    /// <param name="actualStrokeSamples">Сколько точек в штрихах записи; <c>null</c> — проверять только внутреннюю согласованность.</param>
    public static IReadOnlyList<AnnotationIssue> Validate(AnnotationFile annotation, IReadOnlyList<int>? actualStrokeSamples = null)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        var issues = new List<AnnotationIssue>();
        void Error(string code, string message) => issues.Add(new AnnotationIssue(IssueSeverity.Error, code, message));
        void Warn(string code, string message) => issues.Add(new AnnotationIssue(IssueSeverity.Warning, code, message));

        if (!string.Equals(annotation.Schema, AnnotationFormat.Schema, StringComparison.Ordinal))
        {
            Error(IssueCodes.Schema, $"Схема должна быть '{AnnotationFormat.Schema}', а не '{annotation.Schema}'.");
        }

        foreach (string flag in annotation.Quality.Flags.Where(f => !QualityFlags.All.Contains(f)))
        {
            Warn(IssueCodes.UnknownFlag, $"Неизвестный флаг качества '{flag}'.");
        }

        if (annotation.Quality.Usable && annotation.Quality.Flags.Contains(QualityFlags.WrongContent))
        {
            Warn(IssueCodes.QualityConsistency, "Запись помечена годной, но с флагом «написано не то»: укажите в тексте, что написано на самом деле.");
        }

        // Длины штрихов: из записи, иначе из разметки.
        ImmutableArray<int> counts = annotation.StrokeSamples;
        if (actualStrokeSamples is not null && !counts.IsEmpty && !counts.SequenceEqual(actualStrokeSamples))
        {
            Error(
                IssueCodes.StrokeMismatch,
                $"Запись изменилась после разметки: в разметке {counts.Length} шт. штрихов ({string.Join(",", counts)}), в записи {actualStrokeSamples.Count} ({string.Join(",", actualStrokeSamples)}).");
        }

        IReadOnlyList<int> sizes = actualStrokeSamples ?? counts;
        CheckDeferred(annotation, sizes, Error);

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

        CheckJunctions(annotation, sizes, Error);
        return issues;
    }

    /// <summary>Нет замечаний уровня <see cref="IssueSeverity.Error"/>.</summary>
    public static bool IsValid(IEnumerable<AnnotationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        return issues.All(i => i.Severity != IssueSeverity.Error);
    }

    private static void CheckDeferred(AnnotationFile a, IReadOnlyList<int> sizes, Action<string, string> error)
    {
        var seen = new HashSet<int>();
        foreach (DeferredMark mark in a.Deferred)
        {
            if (mark.Stroke < 0 || mark.Stroke >= sizes.Count)
            {
                error(IssueCodes.DeferredRange, $"Отложенный штрих {mark.Stroke}: в записи штрихов {sizes.Count}.");
            }

            if (!seen.Add(mark.Stroke))
            {
                error(IssueCodes.DeferredDuplicate, $"Штрих {mark.Stroke} отмечен отложенным дважды.");
            }

            if (a.Quality.Usable && (mark.Letter < 0 || mark.Letter >= a.Text.Length))
            {
                error(IssueCodes.DeferredLetter, $"Отложенный штрих {mark.Stroke} привязан к букве {mark.Letter}, а букв {a.Text.Length}.");
            }
        }

        if (a.Quality.Usable && sizes.Count > 0 && seen.Count(s => s >= 0 && s < sizes.Count) >= sizes.Count)
        {
            error(IssueCodes.NoMainTrace, "Все штрихи отмечены отложенными: основного следа нет.");
        }
    }

    private static void CheckJunctions(AnnotationFile a, IReadOnlyList<int> sizes, Action<string, string> error)
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
        int[] main = [.. Enumerable.Range(0, sizes.Count).Where(s => !deferred.Contains(s) && sizes[s] > 0)];
        if (main.Length == 0)
        {
            return; // уже сообщено NoMainTrace; на пустом следе стыки проверять нечем
        }

        var first = new StrokePoint(main[0], 0);
        var last = new StrokePoint(main[^1], sizes[main[^1]] - 1);
        bool pointsOk = true;
        foreach (Junction j in ordered)
        {
            foreach ((StrokePoint point, string role) in new[] { (j.End, "конец"), (j.Start, "начало") })
            {
                if (point.Stroke < 0 || point.Stroke >= sizes.Count || point.Sample < 0 || point.Sample >= sizes[point.Stroke])
                {
                    error(IssueCodes.PointRange, $"Стык {Label(a, j)}: {role} ({point.Stroke}:{point.Sample}) вне записи.");
                    pointsOk = false;
                }
                else if (deferred.Contains(point.Stroke))
                {
                    error(IssueCodes.JunctionOnDeferred, $"Стык {Label(a, j)}: {role} лежит на отложенном штрихе {point.Stroke}.");
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
            if (j.End > j.Start)
            {
                error(IssueCodes.NotMonotone, $"Стык {Label(a, j)}: конец левой буквы позже начала правой.");
                return;
            }

            if (j.End < bodyStart)
            {
                error(IssueCodes.NotMonotone, $"Стык {Label(a, j)} стоит раньше начала буквы {j.After}: границы должны идти по порядку вдоль следа.");
                return;
            }

            if (j.End == bodyStart)
            {
                error(IssueCodes.EmptyLetter, $"Буква {j.After} ({a.Text[j.After]}) пуста: её тело состоит из одной точки, проверьте границы.");
                return;
            }

            CheckKind(a, j, sizes, deferred, error);
            bodyStart = j.Start;
        }

        if (bodyStart >= last)
        {
            error(IssueCodes.EmptyLetter, $"Последняя буква ({a.Text[^1]}) пуста: последний стык лежит в самом конце следа.");
        }
    }

    private static void CheckKind(AnnotationFile a, Junction j, IReadOnlyList<int> sizes, HashSet<int> deferred, Action<string, string> error)
    {
        if (j.Kind == JunctionKind.Connected)
        {
            if (j.End.Stroke != j.Start.Stroke)
            {
                error(IssueCodes.ConnectedAcrossStrokes, $"Стык {Label(a, j)} «соединён», но конец и начало в разных штрихах: это отрыв пера.");
            }

            return;
        }

        int next = NextMainStroke(j.End.Stroke, sizes, deferred);
        bool endsStroke = j.End.Sample == sizes[j.End.Stroke] - 1;
        bool startsNext = next >= 0 && j.Start.Stroke == next && j.Start.Sample == 0;
        if (!endsStroke || !startsNext)
        {
            error(
                IssueCodes.LiftGeometry,
                $"Стык {Label(a, j)} «отрыв»: левая буква должна кончаться в последней точке штриха, правая — начинаться с первой точки следующего основного штриха.");
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
