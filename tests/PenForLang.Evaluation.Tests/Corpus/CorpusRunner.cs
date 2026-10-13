using System.Globalization;
using PenForLang.Glyphs;
using PenForLang.Ink;

namespace PenForLang.Evaluation.Tests.Corpus;

/// <summary>Результат по одному стыку размеченной записи.</summary>
/// <param name="After">Стык между буквами <c>After</c> и <c>After + 1</c>.</param>
/// <param name="EndError">Ошибка конца тела левой буквы против разметки, x-height (<c>null</c> — точки в записи нет).</param>
/// <param name="StartError">То же для начала тела правой буквы.</param>
/// <param name="BoundariesOk">Обе границы в допуске.</param>
/// <param name="Lifted">Разметчик отметил отрыв.</param>
/// <param name="Exempt">Отрыв ожидаем: рядом сразу поставлен отложенный штрих своей буквы (A03).</param>
/// <param name="J1">Оценщик выдал J1 на этом стыке.</param>
internal sealed record CorpusJunctionResult(int After, double? EndError, double? StartError, bool BoundariesOk, bool Lifted, bool Exempt, bool J1)
{
    /// <summary>От оценщика ждут J1: отрыв размечен и не ожидаем.</summary>
    public bool J1Expected => Lifted && !Exempt;

    /// <summary>Лишний J1: слитно или отрыв ожидаем, а оценщик выдал J1.</summary>
    public bool J1Violation => J1 && !J1Expected;
}

/// <summary>Результат по одной записи корпуса.</summary>
/// <param name="Name">Имя файла записи.</param>
/// <param name="Word">Слово.</param>
/// <param name="Variant">Вариант задания по разметке (<c>lift</c>, <c>joined</c>, <c>dot</c>) или <c>null</c>.</param>
/// <param name="Junctions">Стыки.</param>
internal sealed record CorpusRecordResult(string Name, string Word, string? Variant, IReadOnlyList<CorpusJunctionResult> Junctions)
{
    public double WorstBoundary => Junctions.Select(j => Math.Max(j.EndError ?? double.PositiveInfinity, j.StartError ?? double.PositiveInfinity)).DefaultIfEmpty(0.0).Max();
}

/// <summary>Итог прогона по корпусу.</summary>
/// <param name="Records">Оценённые записи (цель <c>word:*</c>, не меньше двух букв, стыков ровно <c>n − 1</c>).</param>
/// <param name="Skipped">Пропущенные записи с причинами.</param>
internal sealed record CorpusReport(IReadOnlyList<CorpusRecordResult> Records, IReadOnlyList<(string Name, string Reason)> Skipped)
{
    private IEnumerable<CorpusJunctionResult> AllJunctions => Records.SelectMany(r => r.Junctions);

    /// <summary>Доля стыков, у которых обе границы в допуске (знаменатель — стыки оценённых записей).</summary>
    public double BoundaryPassRate => AllJunctions.Any() ? (double)AllJunctions.Count(j => j.BoundariesOk) / AllJunctions.Count() : 1.0;

    public int JunctionCount => AllJunctions.Count();

    /// <summary>Лишние J1: слитно размечено или отрыв ожидаем, а J1 выдан.</summary>
    public int J1Violations => AllJunctions.Count(j => j.J1Violation);

    /// <summary>Доля найденных J1 среди размеченных отрывов (без ожидаемых); 1, если отрывов нет.</summary>
    public double J1Recall => Recall(AllJunctions);

    /// <summary>То же только для записей варианта <paramref name="variant"/> (<c>lift</c> — «at-lift»).</summary>
    public double J1RecallOf(string variant) => Recall(Records.Where(r => r.Variant == variant).SelectMany(r => r.Junctions));

    public int J1ExpectedOf(string variant) => Records.Where(r => r.Variant == variant).SelectMany(r => r.Junctions).Count(j => j.J1Expected);

    public string Summary =>
        string.Create(CultureInfo.InvariantCulture, $"corpus: {Records.Count} word recordings ({JunctionCount} junctions) evaluated, {Skipped.Count} skipped; junction boundaries within tolerance {BoundaryPassRate:P0}; J1 recall {J1Recall:P0}, J1 violations {J1Violations}");

    private static double Recall(IEnumerable<CorpusJunctionResult> junctions)
    {
        CorpusJunctionResult[] expected = [.. junctions.Where(j => j.J1Expected)];
        return expected.Length == 0 ? 1.0 : (double)expected.Count(j => j.J1) / expected.Length;
    }
}

/// <summary>
/// Оценка слов по размеченному корпусу (V0.10, ARCHITECTURE.md §6.8, §12.2): границы букв против разметки по стыкам и J1 только
/// там, где размечен отрыв (и нет J1, где отрыв ожидаем: точка или перекладина «сразу»). В расчёт идут только записи с целью
/// <c>word:*</c>, не короче двух букв и с числом стыков ровно <c>n − 1</c>; брак, устаревшая разметка, чужие разметчики и
/// слова не из букв стиля пропускаются (с причиной).
/// </summary>
internal static class CorpusRunner
{
    public const double BoundaryTolerance = 0.2;

    public static CorpusReport Run(string root, string annotator, WordEvaluator evaluator, StylePack pack)
    {
        var results = new List<CorpusRecordResult>();
        var skipped = new List<(string, string)>();
        foreach (string path in CorpusFormat.FindRecordings(root))
        {
            string name = Path.GetFileName(path);
            CorpusRecording recording = CorpusFormat.ReadRecording(path);
            AnnotationLoad load = CorpusFormat.ReadAnnotation(recording, annotator);
            if (load.Annotation is not { } annotation)
            {
                skipped.Add((name, load.Problem is null ? $"no annotation of '{annotator}'" : "annotation does not match the recording: " + load.Problem));
                continue;
            }

            string? target = CorpusFormat.WordTarget(recording);
            string word = annotation.Text;
            if (!annotation.Usable)
            {
                skipped.Add((name, "marked as unusable"));
            }
            else if (target is null)
            {
                skipped.Add((name, $"the target '{recording.Target}' is not a word"));
            }
            else if (word.Length < 2)
            {
                skipped.Add((name, "fewer than two letters: there are no junctions"));
            }
            else if (annotation.Junctions.Length != word.Length - 1)
            {
                skipped.Add((name, $"{annotation.Junctions.Length} junctions for {word.Length} letters"));
            }
            else if (!word.All(c => pack.TryGetGlyph(c.ToString(), "default", out _)))
            {
                skipped.Add((name, "the word has letters outside the style"));
            }
            else
            {
                results.Add(Evaluate(name, recording.Ink, annotation, word, evaluator, pack));
            }
        }

        return new CorpusReport(results, skipped);
    }

    private static CorpusRecordResult Evaluate(string name, RulerInk ink, CorpusAnnotation annotation, string word, WordEvaluator evaluator, StylePack pack)
    {
        EvaluationResult result = evaluator.Evaluate(new WordEvaluationRequest(ink, WordTemplateBuilder.Build(pack, word)));
        var junctions = new List<CorpusJunctionResult>();
        foreach (CorpusJunction junction in annotation.Junctions)
        {
            double? endError = null;
            double? startError = null;
            if (result.Detail.Word is { } detail && junction.After < detail.Boundaries.Length)
            {
                WordBoundary b = detail.Boundaries[junction.After];
                endError = Error(ink, junction.End, b.BodyEnd);
                startError = Error(ink, junction.Start, b.BodyStart);
            }

            // Отрыв ожидаем, если рядом с ним сразу поставлен отложенный штрих своей буквы (до или после стыка): J1 там быть не должно.
            bool exempt = annotation.Deferred.Any(d => d.Immediate && (d.Letter == junction.After || d.Letter == junction.After + 1));
            bool j1 = result.Summary.Findings.Any(f => f.Code == ErrorCode.J1 && f.Where.LetterIndex == junction.After);
            bool ok = endError is <= BoundaryTolerance && startError is <= BoundaryTolerance;
            junctions.Add(new CorpusJunctionResult(junction.After, endError, startError, ok, junction.Lifted, exempt, j1));
        }

        return new CorpusRecordResult(name, word, annotation.Variant, junctions);
    }

    private static double? Error(RulerInk ink, CorpusPoint truth, InkPoint actual) =>
        CorpusFormat.Locate(ink, truth) is { } place ? Math.Sqrt(Math.Pow(actual.U - place.U, 2) + Math.Pow(actual.V - place.V, 2)) : null;
}
