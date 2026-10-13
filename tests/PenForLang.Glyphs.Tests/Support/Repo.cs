using PenForLang.Core;

namespace PenForLang.Glyphs.Tests.Support;

/// <summary>Доступ тестов к файлам репозитория (контент, схемы, снимки).</summary>
internal static class Repo
{
    public const string StyleDirectory = "styles/zaner-bloser";

    private static readonly Lazy<string> RootDirectory = new(FindRoot);

    public static string Root => RootDirectory.Value;

    public static string Path(params string[] segments) => System.IO.Path.Combine([Root, .. segments]);

    /// <summary>Содержимое каталога <c>content/</c> репозитория в памяти (пути относительно <c>content/</c>).</summary>
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

    public static string ReadText(string relativeToContent) =>
        File.ReadAllText(Path("content", relativeToContent.Replace('/', System.IO.Path.DirectorySeparatorChar)));

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
