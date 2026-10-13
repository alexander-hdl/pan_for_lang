using System.CommandLine;
using System.Text;
using PenForLang.Core;
using PenForLang.Glyphs;
using PenForLang.Glyphs.Loading;
using PenForLang.Glyphs.Rendering;

namespace PenForLang.Tools.Cli;

/// <summary>Команды работы со стиль-пакетами: <c>pfl validate</c> и <c>pfl render</c> (V0.4).</summary>
public static class StyleCommands
{
    /// <summary>Код завершения: успех.</summary>
    public const int ExitOk = 0;

    /// <summary>Код завершения: пакет некорректен или глиф не найден.</summary>
    public const int ExitInvalid = 1;

    /// <summary>Код завершения: ошибка использования (нет каталога, нельзя записать файл).</summary>
    public const int ExitUsage = 2;

    /// <summary>Команда <c>validate &lt;style-dir&gt;</c>: проверяет стиль-пакет и печатает замечания.</summary>
    public static Command CreateValidateCommand()
    {
        var directory = new Argument<string>("style-dir") { Description = "Style package directory, e.g. content/styles/zaner-bloser." };
        var command = new Command("validate", "Validate a style package: schema, invariants, label keys (ARCHITECTURE.md 3.2.7).");
        command.Arguments.Add(directory);
        command.SetAction(parseResult =>
            Validate(parseResult.GetRequiredValue(directory), parseResult.InvocationConfiguration.Output, parseResult.InvocationConfiguration.Error));
        return command;
    }

    /// <summary>Команда <c>render &lt;style&gt; &lt;glyph&gt; -o out.svg</c>: рисует глиф в SVG.</summary>
    public static Command CreateRenderCommand()
    {
        var style = new Argument<string>("style") { Description = "Style package directory, or a style id found under --content/styles." };
        var glyph = new Argument<string>("glyph") { Description = "Glyph: 'a', 'a.default' or 'zaner-bloser/a.default'." };
        var output = new Option<string?>("--output", "-o") { Description = "Output SVG file; standard output if omitted." };
        var content = new Option<string?>("--content") { Description = "Content root with styles/ (default: found upwards from the current directory)." };
        var keyPoints = new Option<bool>("--keypoints") { Description = "Draw key points with their types." };
        var noGuides = new Option<bool>("--no-guides") { Description = "Do not draw the ruler lines." };
        var scale = new Option<double>("--scale") { Description = "Pixels per x-height of the image.", DefaultValueFactory = _ => new SvgRenderOptions().PixelsPerXHeight };

        var command = new Command("render", "Render a glyph of a style package as SVG.");
        command.Arguments.Add(style);
        command.Arguments.Add(glyph);
        command.Options.Add(output);
        command.Options.Add(content);
        command.Options.Add(keyPoints);
        command.Options.Add(noGuides);
        command.Options.Add(scale);
        command.SetAction(parseResult =>
        {
            var options = new SvgRenderOptions
            {
                ShowKeyPoints = parseResult.GetValue(keyPoints),
                ShowGuides = !parseResult.GetValue(noGuides),
                PixelsPerXHeight = parseResult.GetValue(scale),
            };
            return Render(
                parseResult.GetRequiredValue(style),
                parseResult.GetRequiredValue(glyph),
                parseResult.GetValue(output),
                parseResult.GetValue(content),
                options,
                parseResult.InvocationConfiguration.Output,
                parseResult.InvocationConfiguration.Error);
        });
        return command;
    }

    /// <summary>Выполняет <c>validate</c>.</summary>
    /// <param name="styleDirectory">Каталог стиль-пакета.</param>
    /// <param name="output">Вывод результата.</param>
    /// <param name="error">Вывод ошибок.</param>
    public static int Validate(string styleDirectory, TextWriter output, TextWriter error)
    {
        if (!TryOpenPackage(styleDirectory, null, error, out DirectoryContentSource? source, out string? directory))
        {
            return ExitUsage;
        }

        StyleLoadResult result = StyleLoader.Load(source, directory);
        foreach (ValidationIssue warning in result.Warnings)
        {
            output.WriteLine($"warning: {warning}");
        }

        if (result.Pack is not { } pack)
        {
            foreach (ValidationIssue issue in result.Errors)
            {
                error.WriteLine($"error: {issue}");
            }

            error.WriteLine($"FAILED: {result.Errors.Count()} error(s) in {styleDirectory}");
            return ExitInvalid;
        }

        int reviewed = pack.Glyphs.Count(g => g.Meta.Reviewed);
        output.WriteLine($"OK: style '{pack.Manifest.Id.Value}' {pack.Manifest.Version}: {pack.Glyphs.Count} glyph(s) ({reviewed} reviewed), {pack.Labels.Count} label language(s).");
        return ExitOk;
    }

    /// <summary>Выполняет <c>render</c>.</summary>
    /// <param name="style">Каталог стиль-пакета или идентификатор стиля.</param>
    /// <param name="glyphSpec">Глиф: <c>a</c>, <c>a.default</c> или полный идентификатор.</param>
    /// <param name="outputFile">Файл SVG или <c>null</c> для стандартного вывода.</param>
    /// <param name="contentRoot">Корень контента или <c>null</c>.</param>
    /// <param name="options">Параметры рендера.</param>
    /// <param name="output">Стандартный вывод.</param>
    /// <param name="error">Вывод ошибок.</param>
    public static int Render(
        string style,
        string glyphSpec,
        string? outputFile,
        string? contentRoot,
        SvgRenderOptions options,
        TextWriter output,
        TextWriter error)
    {
        if (!TryOpenPackage(style, contentRoot, error, out DirectoryContentSource? source, out string? directory))
        {
            return ExitUsage;
        }

        StyleLoadResult result = StyleLoader.Load(source, directory);
        if (result.Pack is not { } pack)
        {
            foreach (ValidationIssue issue in result.Errors)
            {
                error.WriteLine($"error: {issue}");
            }

            error.WriteLine($"FAILED: the style package is invalid ({result.Errors.Count()} error(s)); run 'pfl validate'.");
            return ExitInvalid;
        }

        if (!TryFindGlyph(pack, glyphSpec, out Glyph? found))
        {
            error.WriteLine($"error: glyph '{glyphSpec}' is not in style '{pack.Manifest.Id.Value}'; available: {string.Join(", ", pack.Glyphs.Select(g => $"{g.Symbol}.{g.Variant}"))}");
            return ExitInvalid;
        }

        string svg = GlyphSvgRenderer.Render(found, pack.Manifest, options);
        if (outputFile is null)
        {
            output.Write(svg);
            return ExitOk;
        }

        try
        {
            string? folder = Path.GetDirectoryName(Path.GetFullPath(outputFile));
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            File.WriteAllText(outputFile, svg, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"error: cannot write '{outputFile}': {ex.Message}");
            return ExitUsage;
        }

        output.WriteLine($"wrote {outputFile} ({found.Id})");
        return ExitOk;
    }

    private static bool TryFindGlyph(StylePack pack, string spec, out Glyph glyph)
    {
        Glyph? found = null;
        if (spec.Contains('/', StringComparison.Ordinal))
        {
            if (GlyphId.TryParse(spec, out GlyphId id))
            {
                pack.TryGetGlyph(id, out found);
            }
        }
        else
        {
            int dot = spec.LastIndexOf('.');
            string symbol = dot > 0 ? spec[..dot] : spec;
            string variant = dot > 0 ? spec[(dot + 1)..] : "default";
            pack.TryGetGlyph(symbol, variant, out found);
        }

        glyph = found!;
        return found is not null;
    }

    private static bool TryOpenPackage(
        string styleArgument,
        string? contentRoot,
        TextWriter error,
        out DirectoryContentSource source,
        out string directory)
    {
        source = null!;
        directory = string.Empty;

        string? packageDirectory = null;
        if (Directory.Exists(styleArgument))
        {
            packageDirectory = Path.GetFullPath(styleArgument).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        else if (!styleArgument.Contains('/', StringComparison.Ordinal) && !styleArgument.Contains('\\', StringComparison.Ordinal))
        {
            string? root = contentRoot ?? FindContentRoot();
            if (root is not null && Directory.Exists(Path.Combine(root, "styles", styleArgument)))
            {
                packageDirectory = Path.GetFullPath(Path.Combine(root, "styles", styleArgument));
            }
        }

        if (packageDirectory is null)
        {
            error.WriteLine($"error: style package '{styleArgument}' not found (give a directory, or a style id with --content).");
            return false;
        }

        string? parent = Path.GetDirectoryName(packageDirectory);
        if (parent is null)
        {
            source = new DirectoryContentSource(packageDirectory);
            return true;
        }

        source = new DirectoryContentSource(parent);
        directory = Path.GetFileName(packageDirectory);
        return true;
    }

    private static string? FindContentRoot()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "content");
            if (Directory.Exists(Path.Combine(candidate, "styles")))
            {
                return candidate;
            }
        }

        return null;
    }
}
