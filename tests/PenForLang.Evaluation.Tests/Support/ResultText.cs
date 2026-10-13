using System.Globalization;
using System.Text;

namespace PenForLang.Evaluation.Tests.Support;

/// <summary>
/// Полное текстовое представление результата (все поля фасада и детализации с числами в формате «round-trip»).
/// Записи с <see cref="System.Collections.Immutable.ImmutableArray{T}"/> сравниваются по ссылке, поэтому детерминизм
/// проверяется по этому тексту.
/// </summary>
internal static class ResultText
{
    public static string Describe(EvaluationResult result)
    {
        var sb = new StringBuilder();
        EvaluationSummary s = result.Summary;
        sb.Append(CultureInfo.InvariantCulture, $"info={s.Info.Version}/{s.Info.ConfigHash};kind={s.Kind};target={s.TargetText};decision={s.Decision};written={s.WrittenText};confidence={s.Confidence}\n");
        foreach (LetterSummary l in s.Letters)
        {
            sb.Append(CultureInfo.InvariantCulture, $"letter {l.Index} {l.Target} {l.Written} {l.Context} {l.Identity} form={l.Form:R} processOk={l.ProcessOk} seg={l.SegmentationConfident} {l.Process}\n");
        }

        foreach (DeferredSummary d in s.Deferred)
        {
            sb.Append(CultureInfo.InvariantCulture, $"deferred {d}\n");
        }

        foreach (Finding f in s.Findings)
        {
            sb.Append(CultureInfo.InvariantCulture, $"finding {f.Code} {f.Kind} {f.Where} sev={f.Severity:R} span={f.Span} measured={f.Measured:R} threshold={f.Threshold:R}\n");
        }

        foreach (StrokeMatch m in result.Detail.Strokes)
        {
            sb.Append(CultureInfo.InvariantCulture, $"stroke {m}\n");
        }

        foreach (DeferredMatch m in result.Detail.Deferred)
        {
            sb.Append(CultureInfo.InvariantCulture, $"deferredMatch {m}\n");
        }

        foreach (FormComponent c in result.Detail.FormComponents)
        {
            sb.Append(CultureInfo.InvariantCulture, $"component {c}\n");
        }

        sb.Append(CultureInfo.InvariantCulture, $"normalization {result.Detail.Normalization}\n");
        foreach (KeyValuePair<string, double> kv in result.Detail.Diagnostics)
        {
            sb.Append(CultureInfo.InvariantCulture, $"diag {kv.Key}={kv.Value:R}\n");
        }

        return sb.ToString();
    }
}
