using System.Reflection;
using NetArchTest.Rules;

namespace PenForLang.Architecture.Tests.Rules;

/// <summary>
/// Правила уровня скомпилированных сборок: NetArchTest.Rules (зависимости типов) и рефлексия (P/Invoke, ссылки сборок).
/// Возвращают список нарушений; пустой список — правило соблюдено.
/// </summary>
public static class AssemblyRules
{
    /// <summary>Пространства имён, от которых зависит только Windows-код (R1).</summary>
    public static readonly string[] WindowsOnlyNamespaces =
    [
        "System.Windows.Forms",
        "Microsoft.Win32",
        "Microsoft.Data.Sqlite",
        "NAudio",
        "NVorbis",
        "PdfSharp",
    ];

    /// <summary>Префиксы имён сборок, на которые не должен ссылаться код <c>net10.0</c> (R1).</summary>
    public static readonly string[] WindowsOnlyAssemblyPrefixes =
    [
        "PresentationFramework",
        "PresentationCore",
        "PresentationUI",
        "WindowsBase",
        "System.Windows.Forms",
        "Microsoft.Win32.Registry",
        "Microsoft.Data.Sqlite",
        "NAudio",
        "NVorbis",
        "PdfSharp",
    ];

    /// <summary>Сетевые пространства имён и типы (R3).</summary>
    public static readonly string[] NetworkNamespaces =
    [
        "System.Net.Http",
        "System.Net.Sockets",
        "System.Net.WebClient",
        "System.Net.WebRequest",
        "System.Net.HttpWebRequest",
    ];

    /// <summary>R1: сборка <c>net10.0</c> не зависит от WPF, WinForms, Win32-реестра, Sqlite, NAudio, PDFsharp.</summary>
    public static IReadOnlyList<string> CheckNoWindowsDependencies(Assembly assembly)
    {
        var violations = new List<string>();

        NetArchTest.Rules.TestResult result = Types.InAssembly(assembly)
            .ShouldNot().HaveDependencyOnAny(WindowsOnlyNamespaces)
            .GetResult();
        violations.AddRange((result.FailingTypeNames ?? []).Select(t => $"R1: type {t} depends on a Windows-only namespace."));

        violations.AddRange(
            assembly.GetReferencedAssemblies()
                .Select(r => r.Name ?? string.Empty)
                .Where(name => WindowsOnlyAssemblyPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                .Select(name => $"R1: assembly {assembly.GetName().Name} references {name}."));

        return violations;
    }

    /// <summary>R3: нет <c>System.Net.Http</c>, <c>System.Net.Sockets</c>, <c>WebRequest</c> и аналогов.</summary>
    public static IReadOnlyList<string> CheckNoNetworkCode(Assembly assembly)
    {
        NetArchTest.Rules.TestResult result = Types.InAssembly(assembly)
            .ShouldNot().HaveDependencyOnAny(NetworkNamespaces)
            .GetResult();

        return (result.FailingTypeNames ?? []).Select(t => $"R3: type {t} uses network APIs.").ToList();
    }

    /// <summary>R4: в сборке нет методов P/Invoke (включая сгенерированные <c>LibraryImport</c>).</summary>
    public static IReadOnlyList<string> FindPInvokeMethods(Assembly assembly)
    {
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                                 | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var found = new List<string>();

        foreach (Type type in GetLoadableTypes(assembly))
        {
            foreach (MethodInfo method in type.GetMethods(All))
            {
                if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0)
                {
                    found.Add($"R4: P/Invoke {type.FullName}.{method.Name} in {assembly.GetName().Name}.");
                }
            }
        }

        return found;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }
}
