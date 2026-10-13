using System.IO;
using System.Xml.Linq;

namespace PenForLang.Architecture.Tests.Support;

/// <summary>Расположение репозитория и перечисление проектов продукта.</summary>
public static class RepositoryLayout
{
    private static readonly Lazy<string> RootDirectory = new(FindRoot);

    /// <summary>Корень репозитория (каталог с <c>PenForLang.sln</c>).</summary>
    public static string Root => RootDirectory.Value;

    /// <summary>TFM по умолчанию из корневого <c>Directory.Build.props</c>.</summary>
    public static string DefaultTargetFramework { get; } = ReadDefaultTargetFramework();

    /// <summary>Проекты продукта: <c>src/*</c> и <c>tools/*</c> (без тестов и фикстур).</summary>
    public static IReadOnlyList<ProjectInfo> LoadProductProjects() =>
        EnumerateProjectFiles("src").Concat(EnumerateProjectFiles("tools"))
            .Select(path => ProjectInfo.Load(path, DefaultTargetFramework))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>Только проекты <c>src/*</c> (то, что попадает в установку).</summary>
    public static IReadOnlyList<ProjectInfo> LoadSrcProjects() =>
        EnumerateProjectFiles("src")
            .Select(path => ProjectInfo.Load(path, DefaultTargetFramework))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>Путь внутри репозитория.</summary>
    public static string PathOf(params string[] segments) => Path.Combine([Root, .. segments]);

    private static IEnumerable<string> EnumerateProjectFiles(string folder)
    {
        string dir = PathOf(folder);
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.csproj", SearchOption.AllDirectories)
            : [];
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PenForLang.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("PenForLang.sln not found above " + AppContext.BaseDirectory);
    }

    private static string ReadDefaultTargetFramework()
    {
        var props = XDocument.Load(Path.Combine(Root, "Directory.Build.props"));
        return props.Descendants("TargetFramework").Select(e => e.Value.Trim()).FirstOrDefault()
            ?? throw new InvalidOperationException("Directory.Build.props has no TargetFramework.");
    }
}
