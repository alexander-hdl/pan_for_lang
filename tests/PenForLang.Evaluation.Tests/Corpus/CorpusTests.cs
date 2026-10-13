using PenForLang.Evaluation.Tests.Support;

namespace PenForLang.Evaluation.Tests.Corpus;

/// <summary>
/// Корпусный тест слов (V0.10, <c>Category=Corpus</c>): записи и разметка основного разметчика (<c>A</c>) из
/// <c>tests/corpus/adults/</c> или из каталога переменной среды <c>PFL_CORPUS</c> (разметчик — <c>PFL_CORPUS_ANNOTATOR</c>).
/// Пропускается, если каталога корпуса нет или в нём нет размеченных записей. Критерии (ARCHITECTURE.md §12.2, V0.10): границы
/// букв в пределах 0,2 x-h в ≥ 90% записей; J1 только там, где размечен отрыв (и нет J1, где отрыв ожидаем при точке «сразу»).
/// Протокол записи V0.9: <c>at-lift</c> ×10, <c>at-joined</c> ×10, <c>it-dot</c> ×10.
/// </summary>
[Trait("Category", "Corpus")]
public class CorpusTests
{
    private const double RequiredBoundaryPassRate = 0.9;

    private static string? CorpusRoot()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable("PFL_CORPUS");
        string root = string.IsNullOrWhiteSpace(fromEnvironment) ? Repo.Path("tests", "corpus", "adults") : fromEnvironment;
        return Directory.Exists(root) ? root : null;
    }

    [Fact]
    public void Boundaries_and_j1_agree_with_the_annotation_of_the_adult_corpus()
    {
        string? root = CorpusRoot();
        if (root is null)
        {
            Assert.Skip("The corpus folder tests/corpus/adults (or PFL_CORPUS) does not exist.");
            return;
        }

        string annotator = Environment.GetEnvironmentVariable("PFL_CORPUS_ANNOTATOR") is { Length: > 0 } id ? id : CorpusFormat.DefaultAnnotator;
        CorpusReport report = CorpusRunner.Run(root, annotator, Tester.Words, Shipped.Pack);
        if (report.Records.Count == 0)
        {
            Assert.Skip($"The corpus has no annotated word recordings of annotator '{annotator}' ({report.Skipped} files skipped).");
            return;
        }

        TestContext.Current.SendDiagnosticMessage(report.Summary);
        foreach (CorpusRecordResult record in report.Records.Where(r => !r.BoundariesOk || r.J1Violations.Count > 0))
        {
            TestContext.Current.SendDiagnosticMessage($"  {record.Name} ({record.Word}): worst boundary {record.WorstBoundary:F2} x-h; {string.Join("; ", record.J1Violations)}");
        }

        Assert.True(report.BoundaryPassRate >= RequiredBoundaryPassRate, report.Summary);
        Assert.True(report.J1Violations == 0, report.Summary);
    }
}
