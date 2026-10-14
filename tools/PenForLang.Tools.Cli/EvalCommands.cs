using System.CommandLine;
using System.Globalization;
using System.IO.Compression;
using PenForLang.Evaluation;
using PenForLang.Evaluation.Config;
using PenForLang.Glyphs;
using PenForLang.Glyphs.Loading;
using PenForLang.Ink;

namespace PenForLang.Tools.Cli;

/// <summary>Команда <c>pfl eval</c>: оценка изолированной буквы (V0.7) или слова (V0.10) по записи пера (разбор логов XPPen).</summary>
public static class EvalCommands
{
    /// <summary>Код завершения: успех.</summary>
    public const int ExitOk = 0;

    /// <summary>Код завершения: пакет, конфигурация или лог некорректны, глифа нет.</summary>
    public const int ExitInvalid = 1;

    /// <summary>Код завершения: ошибка использования (файла или каталога нет).</summary>
    public const int ExitUsage = 2;

    /// <summary>Команда <c>eval &lt;penlog&gt; [--glyph a | --word at] [--style dir] [--content root] [--profile learning]</c>.</summary>
    public static Command CreateEvalCommand()
    {
        var penlog = new Argument<string>("penlog") { Description = "Pen log file (pfl.penlog/1: .jsonl or gzip-compressed .jsonl.gz, detected by signature)." };
        var glyph = new Option<string?>("--glyph", "-g") { Description = "Glyph to evaluate: 'a', 'a.default'. Default: the single-character target from the log header." };
        var word = new Option<string?>("--word", "-w") { Description = "Word to evaluate: 'at', 'it'. Default for logs whose task target is 'word:<text>'." };
        var style = new Option<string?>("--style", "-s") { Description = "Style package directory or style id (default: the style of the log's ruler)." };
        var content = new Option<string?>("--content") { Description = "Content root with styles/ and config/scoring.json (default: found upwards from the current directory)." };
        var profile = new Option<ToleranceProfile>("--profile") { Description = "Tolerance profile.", DefaultValueFactory = _ => ToleranceProfile.Learning };
        var command = new Command("eval", "Evaluate an isolated letter or a word written on the screen from a pen log: decision, form, process, joins and letter boundaries (words), findings with measured values and template spans.");
        command.Arguments.Add(penlog);
        command.Options.Add(glyph);
        command.Options.Add(word);
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
                parseResult.InvocationConfiguration.Error,
                parseResult.GetValue(word)));
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
    /// <param name="wordText">Слово (<c>at</c>) или <c>null</c> — слово из цели лога вида <c>word:at</c>, иначе буква.</param>
    public static int Evaluate(
        string penlogFile,
        string? glyphSpec,
        string? styleArgument,
        string? contentRoot,
        ToleranceProfile tolerance,
        TextWriter output,
        TextWriter error,
        string? wordText = null)
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
            using FileStream file = File.OpenRead(penlogFile);
            using Stream stream = IsGzip(file) ? new GZipStream(file, CompressionMode.Decompress) : file;
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

        string? logTarget = log.Header.Task?.Target;
        string? wordTarget = wordText ?? (glyphSpec is null && logTarget is not null && logTarget.StartsWith(WordTargetPrefix, StringComparison.Ordinal) ? logTarget[WordTargetPrefix.Length..] : null);
        if (wordTarget is not null)
        {
            return EvaluateWord(log, penlogFile, pack, source, wordTarget, tolerance, output, error);
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

    /// <summary>Файл начинается с сигнатуры gzip (<c>1f 8b</c>): запись корпуса <c>*.pfl.jsonl.gz</c>. Позиция потока возвращается в начало.</summary>
    private static bool IsGzip(FileStream file)
    {
        int first = file.ReadByte();
        int second = file.ReadByte();
        file.Seek(0, SeekOrigin.Begin);
        return first == 0x1f && second == 0x8b;
    }

    /// <summary>Префикс цели лога для слова (<c>word:at</c>).</summary>
    private const string WordTargetPrefix = "word:";

    private static int EvaluateWord(
        PenLog log,
        string penlogFile,
        StylePack pack,
        DirectoryContentSource source,
        string wordText,
        ToleranceProfile tolerance,
        TextWriter output,
        TextWriter error)
    {
        WordTemplate template;
        try
        {
            template = WordTemplateBuilder.Build(pack, wordText);
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
        {
            error.WriteLine($"error: cannot build the word '{wordText}' in style '{pack.Manifest.Id.Value}': {ex.Message}");
            return ExitInvalid;
        }

        WordEvaluator evaluator;
        try
        {
            evaluator = WordEvaluator.Create(source);
        }
        catch (ScoringConfigException ex)
        {
            error.WriteLine("error: " + ex.Message);
            return ExitInvalid;
        }

        RulerInk ink = RulerProjection.Project(log.ToInkAttempt());
        EvaluationResult result = evaluator.Evaluate(new WordEvaluationRequest(ink, template, tolerance));
        PrintWord(result, template, penlogFile, output);
        return ExitOk;
    }

    private static void PrintWord(EvaluationResult result, WordTemplate word, string penlogFile, TextWriter output)
    {
        EvaluationSummary s = result.Summary;
        CultureInfo c = CultureInfo.InvariantCulture;
        output.WriteLine(string.Create(c, $"{Path.GetFileName(penlogFile)}: word '{word.Text}', evaluator {s.Info.Version}, config {s.Info.ConfigHash[..12]}"));
        output.WriteLine($"decision {s.Decision}, confidence {s.Confidence}");
        foreach (LetterSummary letter in s.Letters)
        {
            string segmentation = letter.SegmentationConfident ? "confident" : "not confident";
            output.WriteLine(string.Create(c, $"letter {letter.Index} '{letter.Target}' ({letter.Context}): form {letter.Form:0.000}, process_ok {FormatBool(letter.ProcessOk)}, segmentation {segmentation}"));
        }

        foreach (JoinSummary join in s.Joins)
        {
            string family = join.Family ?? "break";
            string tall = join.Tall ? ", tall" : string.Empty;
            output.WriteLine(string.Create(c, $"join {join.Index} ({join.Left}->{join.Right}, {family}{tall}): present {FormatBool(join.Present)}, lift expected {FormatBool(join.LiftExpected)}, score {join.Score:0.00}"));
        }

        if (result.Detail.Word is { } detail)
        {
            foreach (WordBoundary b in detail.Boundaries)
            {
                string endUnsure = b.EndConfident ? string.Empty : " unsure";
                string startUnsure = b.StartConfident ? string.Empty : " unsure";
                string joined = b.Lifted ? "pen lifted" : "joined";
                output.WriteLine(string.Create(c, $"boundary {b.Join}: body end stroke {b.BodyEnd.InkStrokeIndex} point {b.BodyEnd.PointIndex} ({b.BodyEnd.U:0.00}, {b.BodyEnd.V:0.00}){endUnsure}; body start stroke {b.BodyStart.InkStrokeIndex} point {b.BodyStart.PointIndex} ({b.BodyStart.U:0.00}, {b.BodyStart.V:0.00}){startUnsure}; {joined}"));
            }

            foreach (WordLift lift in detail.Lifts)
            {
                string place = lift.Join >= 0 ? $"on join {lift.Join}" : $"inside letter {lift.Letter}";
                output.WriteLine($"lift before stroke {lift.BeforeInkStroke}: {place}{(lift.Expected ? ", expected" : string.Empty)}");
            }

            output.WriteLine(string.Create(c, $"lifts inside letters: {detail.LiftsInsideLetter}, alignment cost {detail.AlignmentCost:0.0000} per point"));
        }

        foreach (DeferredSummary d in s.Deferred)
        {
            output.WriteLine($"deferred {d.StrokeId} of letter {d.LetterIndex} ({d.Kind}): {d.Status}, {d.Timing}");
        }

        if (s.Findings.IsDefaultOrEmpty)
        {
            output.WriteLine("findings: none");
            return;
        }

        output.WriteLine("findings:");
        foreach (Finding f in s.Findings)
        {
            string stroke = f.Where.StrokeId is null ? string.Empty : " " + f.Where.StrokeId;
            string ink = f.Where.InkStrokeIndex is { } inkIndex ? $" (ink stroke {inkIndex})" : string.Empty;
            string measure = f.Measured is { } m ? string.Create(c, $", measured {m:0.###} vs {f.Threshold:0.###}") : string.Empty;
            string span = f.Span.StrokeId is null ? "whole letter" : string.Create(c, $"{f.Span.StrokeId}/{f.Span.Part} t {f.Span.FromT:0.##}..{f.Span.ToT:0.##}");
            output.WriteLine(string.Create(c, $"  {f.Code} [{f.Kind}] severity {f.Severity:0.00}: {f.Where.Place} {f.Where.LetterIndex}{stroke}{ink}{measure}; template {span}"));
        }
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
