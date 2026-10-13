using System.Globalization;
using PenForLang.Glyphs;
using PenForLang.Ink;

namespace PenForLang.Evaluation.Tests.Corpus;

/// <summary>Результат по одной записи корпуса.</summary>
/// <param name="Name">Имя файла записи.</param>
/// <param name="Word">Слово.</param>
/// <param name="WorstBoundary">Наибольшая ошибка границы (конец или начало тела) против разметки, x-height.</param>
/// <param name="BoundariesOk">Все границы в допуске.</param>
/// <param name="J1Expected">Сколько J1 ждём: размеченных отрывов без ожидаемого отрыва рядом с отложенным штрихом «сразу».</param>
/// <param name="J1Found">Сколько ждали и нашли.</param>
/// <param name="J1Violations">Лишние J1: там, где размечено слитно или отрыв ожидаем.</param>
internal sealed record CorpusRecordResult(string Name, string Word, double WorstBoundary, bool BoundariesOk, int J1Expected, int J1Found, IReadOnlyList<string> J1Violations);

/// <summary>Итог прогона по корпусу.</summary>
/// <param name="Records">Оценённые записи.</param>
/// <param name="Skipped">Записи без разметки разметчика, брак и слова не из букв стиля.</param>
internal sealed record CorpusReport(IReadOnlyList<CorpusRecordResult> Records, int Skipped)
{
    public double BoundaryPassRate => Records.Count == 0 ? 1.0 : (double)Records.Count(r => r.BoundariesOk) / Records.Count;

    public int J1Violations => Records.Sum(r => r.J1Violations.Count);

    public double J1Recall => Records.Sum(r => r.J1Expected) is var expected and > 0 ? (double)Records.Sum(r => r.J1Found) / expected : 1.0;

    public string Summary =>
        string.Create(CultureInfo.InvariantCulture, $"corpus: {Records.Count} records evaluated, {Skipped} skipped; boundaries within tolerance in {BoundaryPassRate:P0}; J1 recall {J1Recall:P0}, J1 violations {J1Violations}");
}

/// <summary>
/// Оценка слов по размеченному корпусу (V0.10, ARCHITECTURE.md §6.8, §12.2): границы букв против разметки и J1 только там,
/// где размечен отрыв (и нет J1, где отрыв ожидаем: точка или перекладина «сразу»).
/// </summary>
internal static class CorpusRunner
{
    public const double BoundaryTolerance = 0.2;

    public static CorpusReport Run(string root, string annotator, WordEvaluator evaluator, StylePack pack)
    {
        var results = new List<CorpusRecordResult>();
        int skipped = 0;
        foreach (string recording in CorpusFormat.FindRecordings(root))
        {
            RulerInk ink = CorpusFormat.ReadInk(recording);
            CorpusAnnotation? annotation = CorpusFormat.ReadAnnotation(recording, annotator, ink);
            string? word = annotation?.Text is { Length: > 0 } text ? text : CorpusFormat.ReadTargetWord(recording);
            if (annotation is null || !annotation.Usable || word is null || !word.All(c => pack.TryGetGlyph(c.ToString(), "default", out _)))
            {
                skipped++;
                continue;
            }

            results.Add(Evaluate(Path.GetFileName(recording), ink, annotation, word, evaluator, pack));
        }

        return new CorpusReport(results, skipped);
    }

    private static CorpusRecordResult Evaluate(string name, RulerInk ink, CorpusAnnotation annotation, string word, WordEvaluator evaluator, StylePack pack)
    {
        EvaluationResult result = evaluator.Evaluate(new WordEvaluationRequest(ink, WordTemplateBuilder.Build(pack, word)));
        double worst = 0.0;
        bool anyMissing = false;
        int expected = 0;
        int found = 0;
        var violations = new List<string>();
        foreach (CorpusJunction junction in annotation.Junctions)
        {
            if (result.Detail.Word is not { } detail || junction.After >= detail.Boundaries.Length)
            {
                anyMissing = true;
                continue;
            }

            WordBoundary b = detail.Boundaries[junction.After];
            foreach ((CorpusPoint truth, InkPoint actual) in new[] { (junction.End, b.BodyEnd), (junction.Start, b.BodyStart) })
            {
                if (CorpusFormat.Locate(ink, truth) is not { } place)
                {
                    anyMissing = true;
                    continue;
                }

                worst = Math.Max(worst, Math.Sqrt(Math.Pow(actual.U - place.U, 2) + Math.Pow(actual.V - place.V, 2)));
            }

            // Отрыв ожидаем, если рядом с ним сразу поставлен отложенный штрих своей буквы (A03): J1 там быть не должно.
            bool exempt = annotation.Deferred.Any(d => d.Immediate && d.Letter == junction.After);
            bool j1 = result.Summary.Findings.Any(f => f.Code == ErrorCode.J1 && f.Where.LetterIndex == junction.After);
            if (junction.Lifted && !exempt)
            {
                expected++;
                found += j1 ? 1 : 0;
            }
            else if (j1)
            {
                violations.Add(string.Create(CultureInfo.InvariantCulture, $"J1 at join {junction.After} where the annotation says {(junction.Lifted ? "an expected lift (deferred stroke put immediately)" : "connected")}"));
            }
        }

        return new CorpusRecordResult(name, word, worst, !anyMissing && worst <= BoundaryTolerance, expected, found, violations);
    }
}
