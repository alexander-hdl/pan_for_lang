using System.IO;
using System.Xml.Linq;

namespace PenForLang.Architecture.Tests.Support;

/// <summary>Разобранные из <c>.csproj</c> сведения, нужные архитектурным правилам.</summary>
/// <param name="Name">Имя проекта (имя файла без расширения), например <c>PenForLang.Core</c>.</param>
/// <param name="AssemblyName">Имя сборки (<c>pfl</c> для Tools.Cli).</param>
/// <param name="TargetFramework">TFM, например <c>net10.0</c> или <c>net10.0-windows</c>.</param>
/// <param name="ProjectReferences">Имена проектов из <c>ProjectReference</c>.</param>
/// <param name="PackageReferences">Идентификаторы <c>PackageReference</c>.</param>
public sealed record ProjectInfo(
    string Name,
    string AssemblyName,
    string TargetFramework,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> PackageReferences,
    bool UsesWpf = false,
    bool UsesWindowsForms = false,
    bool IsDomain = false,
    bool BansNumericIntrinsics = false)
{
    /// <summary>TFM привязан к Windows (<c>net10.0-windows</c>).</summary>
    public bool IsWindowsTarget => TargetFramework.Contains("-windows", StringComparison.OrdinalIgnoreCase);

    /// <summary>Читает <c>.csproj</c>; если TFM в файле не задан, берёт <paramref name="defaultTargetFramework"/>.</summary>
    public static ProjectInfo Load(string csprojPath, string defaultTargetFramework)
    {
        var doc = XDocument.Load(csprojPath);
        string name = Path.GetFileNameWithoutExtension(csprojPath);

        string? Property(string element) =>
            doc.Descendants(element).Select(e => e.Value.Trim()).FirstOrDefault();

        bool Flag(string element) => string.Equals(Property(element), "true", StringComparison.OrdinalIgnoreCase);

        string[] projectRefs = doc.Descendants("ProjectReference")
            .Select(e => (string?)e.Attribute("Include"))
            .OfType<string>()
            .Select(include => Path.GetFileNameWithoutExtension(include.Replace('\\', '/')))
            .ToArray();

        string[] packageRefs = doc.Descendants("PackageReference")
            .Select(e => (string?)e.Attribute("Include"))
            .OfType<string>()
            .ToArray();

        return new ProjectInfo(
            name,
            Property("AssemblyName") ?? name,
            Property("TargetFramework") ?? defaultTargetFramework,
            projectRefs,
            packageRefs,
            Flag("UseWPF"),
            Flag("UseWindowsForms"),
            Flag("IsDomainProject"),
            Flag("BanNumericIntrinsics"));
    }
}
