using PenForLang.Core;
using PenForLang.Evaluation.Config;
using PenForLang.Glyphs;
using PenForLang.Glyphs.Loading;

namespace PenForLang.Evaluation.Tests.Support;

/// <summary>Файлы репозитория для тестов (контент, снимки).</summary>
internal static class Repo
{
    public const string StyleDirectory = "styles/zaner-bloser";

    private static readonly Lazy<string> RootDirectory = new(FindRoot);

    public static string Root => RootDirectory.Value;

    public static string Path(params string[] segments) => System.IO.Path.Combine([Root, .. segments]);

    /// <summary>Каталог <c>content/</c> репозитория в памяти (пути относительно <c>content/</c>).</summary>
    public static InMemoryContentSource LoadContent()
    {
        var source = new InMemoryContentSource();
        string content = Path("content");
        foreach (string file in Directory.EnumerateFiles(content, "*", SearchOption.AllDirectories))
        {
            string relative = System.IO.Path.GetRelativePath(content, file).Replace('\\', '/');
            source.Add(relative, File.ReadAllBytes(file));
        }

        return source;
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "PenForLang.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("PenForLang.sln not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>Стиль-пакет <c>zaner-bloser</c> и конфигурация оценщика, загруженные из репозитория один раз.</summary>
internal static class Shipped
{
    private static readonly Lazy<InMemoryContentSource> Content = new(Repo.LoadContent);
    private static readonly Lazy<StylePack> PackValue = new(() => StyleLoader.LoadOrThrow(Content.Value, Repo.StyleDirectory));
    private static readonly Lazy<ScoringConfig> ConfigValue = new(() => ScoringConfigLoader.Load(Content.Value));

    public static InMemoryContentSource Source => Content.Value;

    public static StylePack Pack => PackValue.Value;

    public static StyleManifest Style => Pack.Manifest;

    public static ScoringConfig Config => ConfigValue.Value;

    public static Glyph Glyph(string symbol) => Pack.GetGlyph(symbol);

    public static LetterEvaluator NewEvaluator() => new(Config);

    public static string ConfigText => System.Text.Encoding.UTF8.GetString(ReadBytes(ScoringConfig.DefaultPath));

    private static byte[] ReadBytes(string path)
    {
        using Stream stream = Content.Value.Open(path);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
