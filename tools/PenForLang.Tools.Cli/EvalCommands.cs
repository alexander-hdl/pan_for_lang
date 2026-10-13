using System.CommandLine;
using System.Globalization;
using PenForLang.Evaluation;
using PenForLang.Evaluation.Config;
using PenForLang.Glyphs;
using PenForLang.Glyphs.Loading;
using PenForLang.Ink;

namespace PenForLang.Tools.Cli;

/// <summary>Команда <c>pfl eval</c>: оценка изолированной буквы по записи пера (разбор логов XPPen, V0.7).</summary>
public static class EvalCommands
{
    /// <summary>Код завершения: успех.</summary>
    public const int ExitOk = 0;

    /// <summary>Код завершения: пакет, конфигурация или лог некорректны, глифа нет.</summary>
    public const int ExitInvalid = 1;

    /// <summary>Код завершения: ошибка использования (файла или каталога нет).</summary>
    public const int ExitUsage = 2;

    /// <summary>Команда <c>eval &lt;penlog&gt; [--glyph a] [--style dir] [--content root] [--profile learning]</c>.</summary>
    public static Command CreateEvalCommand()
    {
        var penlog = new Argument<string>("penlog") { Description = "Pen log file (pfl.penlog/1, .jsonl)." };
        var glyph = new Option<string?>("--glyph", "-g") { Description = "Glyph to evaluate: 'a', 'a.default'. Default: the single-character target from the log header." };
        var style = new Option<string?>("--style", "-s") { Description = "Style package directory or style id (default: the style of the log's ruler)." };
        var content = new Option<string?>("--content") { Description = "Content root with styles/ and config/scoring.json (default: found upwards from the current directory)." };
        var profile = new Option<ToleranceProfile>("--profile") { Description = "Tolerance profile.", DefaultValueFactory = _ => ToleranceProfile.Learning };
        var command = new Command("eval", "Evaluate an isolated letter written on the screen from a pen log: decision, form, process, findings with measured values and template spans.");
        command.Arguments.Add(penlog);
        command.Options.Add(glyph);
        command.Options.Add(style);
        command.Options.Add(content);
        command.Options.Add(profile);
        command.SetAction(parseResult =>
            Evaluate(
                parseResult.GetRequiredValue(penlog),
                parseResult.GetValue(glyph),
                parseResult.GetValue(style),
                parseResult.GetValue(content),
                parseResult.GetValue(profile),
                parseResult.InvocationConfiguration.Output,
                parseResult.InvocationConfiguration.Error));
        return command;
    }

    /// <summary>Выполняет <c>eval</c>.</summary>
    /// <param name="penlogFile">Файл лога пера.</param>
    /// <param name="glyphSpec">Глиф (<c>a</c>, <c>a.default</c>) или <c>null</c> — цель из заголовка лога.</param>
    /// <param name="styleArgument">Каталог стиля или идентификатор или <c>null</c> — стиль линейки лога.</param>
    /// <param name="contentRoot">Корень контента или <c>null</c>.</param>
    /// <param name="tolerance">Профиль допусков.</param>
    /// <param name="output">Вывод результата.</param>
    /// <param name="error">Вывод ошибок.</param>
    public static int Evaluate(
        string penlogFile,
        string? glyphSpec,
        string? styleArgument,
        string? contentRoot,
        ToleranceProfile tolerance,
        TextWriter output,
        TextWriter error)
    {
        if (!File.Exists(penlogFile))
        {
            error.WriteLine($"error: pen log '{penlogFile}' not found.");
            return ExitUsage;
        }

        string? root = contentRoot ?? StyleCommands.FindContentRoot();
        if (root is null || !Directory.Exists(root))
        {
            error.WriteLine("error: content root not found (give --content, or run inside the repository).");
            return ExitUsage;
        }

        PenLog log;
        try
        {
            using FileStream stream = File.OpenRead(penlogFile);
            log = PenLogReader.Read(stream);
        }
        catch (Exception ex) when (ex is PenLogFormatException or IOException)
        {
            error.WriteLine($"error: cannot read '{penlogFile}': {ex.Message}");
            return ExitInvalid;
        }

        var source = new DirectoryContentSource(root);
        string styleId = styleArgument ?? log.Header.Guides.Style.Value;
        string styleDirectory = Directory.Exists(styleId) ? Path.GetFileName(Path.GetFullPath(styleId).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) : styleId;
        StyleLoadResult loaded = StyleLoader.Load(source, "styles/" + styleDirectory);
        if (loaded.Pack is not { } pack)
        {
            foreach (ValidationIssue issue in loaded.Errors)
            {
                error.WriteLine($"error: {issue}");
            }

            error.WriteLine($"FAILED: style '{styleDirectory}' under '{root}' is invalid or missing.");
            return ExitInvalid;
        }

        string? target = glyphSpec ?? (log.Header.Task is { Target.Length: 1 } task ? task.Target : null);
        if (target is null)
        {
            error.WriteLine("error: no glyph: give --glyph (the log header has no single-character target).");
            return ExitUsage;
        }

        int dot = target.LastIndexOf('.');
        string symbol = dot > 0 ? target[..dot] : target;
        string variant = dot > 0 ? target[(dot + 1)..] : "default";
        if (!pack.TryGetGlyph(symbol, variant, out Glyph? glyph))
        {
            error.WriteLine($"error: glyph '{target}' is not in style '{pack.Manifest.Id.Value}'; available: {string.Join(", ", pack.Glyphs.Select(g => $"{g.Symbol}.{g.Variant}"))}");
            return ExitInvalid;
        }

        LetterEvaluator evaluator;
        try
        {
            evaluator = LetterEvaluator.Create(source);
        }
        catch (ScoringConfigException ex)
        {
            error.WriteLine("error: " + ex.Message);
            return ExitInvalid;
        }

        RulerInk ink = RulerProjection.Project(log.ToInkAttempt());
        EvaluationResult result = evaluator.Evaluate(new LetterEvaluationRequest(ink, glyph, pack.Manifest, tolerance));
        Print(result, glyph, penlogFile, output);
        return ExitOk;
    }

    private static void Print(EvaluationResult result, Glyph glyph, string penlogFile, TextWriter output)
    {
        EvaluationSummary s = result.Summary;
        CultureInfo c = CultureInfo.InvariantCulture;
        output.WriteLine(string.Create(c, $"{Path.GetFileName(penlogFile)}: glyph {glyph.Id}, evaluator {s.Info.Version}, config {s.Info.ConfigHash[..12]}"));
        LetterSummary letter = s.Letters[0];
        output.WriteLine(string.Create(c, $"decision {s.Decision}, confidence {s.Confidence}, form {letter.Form:0.000}, process_ok {FormatBool(letter.ProcessOk)}"));
        ProcessSummary p = letter.Process;
        output.WriteLine(string.Create(c, $"process: start {FormatBool(p.StartOk)}, direction {FormatBool(p.DirectionOk)}, order {FormatBool(p.OrderOk)}, stroke_count_delta {(p.StrokeCountDelta?.ToString(c) ?? "-")}, retrace {FormatBool(p.RetraceOk)}"));
        foreach (DeferredSummary d in s.Deferred)
        {
            output.WriteLine($"deferred {d.StrokeId} ({d.Kind}): {d.Status}, {d.Timing}");
        }

        if (s.Findings.IsDefaultOrEmpty)
        {
            output.WriteLine("findings: none");
        }
        else
        {
            output.WriteLine("findings:");
            foreach (Finding f in s.Findings)
            {
                string where = $"{f.Where.Place}{(f.Where.StrokeId is null ? string.Empty : " " + f.Where.StrokeId)}{(f.Where.InkStrokeIndex is { } ink ? $" (ink stroke {ink})" : string.Empty)}";
                string measure = f.Measured is { } m ? string.Create(c, $", measured {m:0.###} vs {f.Threshold:0.###}") : string.Empty;
                string span = f.Span.StrokeId is null ? "whole letter" : string.Create(c, $"{f.Span.StrokeId}/{f.Span.Part} t {f.Span.FromT:0.##}..{f.Span.ToT:0.##}");
                output.WriteLine(string.Create(c, $"  {f.Code} [{f.Kind}] severity {f.Severity:0.00}: {where}{measure}; template {span}"));
            }
        }

        output.WriteLine("form components:");
        foreach (FormComponent component in result.Detail.FormComponents)
        {
            output.WriteLine(string.Create(c, $"  {component.Name}: value {component.Value:0.###}, score {component.Score:0.###}, weight {component.Weight:0.###}"));
        }
    }

    private static string FormatBool(bool? value) => value is { } v ? (v ? "yes" : "no") : "-";
}
