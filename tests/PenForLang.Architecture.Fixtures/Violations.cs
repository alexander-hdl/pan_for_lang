using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace PenForLang.Architecture.Fixtures;

/// <summary>R1: зависимость от WPF (сборка WindowsBase / PresentationCore).</summary>
public sealed class WpfViolation
{
    public Point Origin { get; } = new(1, 2);
}

/// <summary>R1: зависимость от Microsoft.Win32 (реестр) — API только для Windows.</summary>
public sealed class RegistryViolation
{
    public static RegistryKey Root => Registry.CurrentUser;
}

/// <summary>R3: сетевой код (System.Net.Http).</summary>
public sealed class NetworkViolation
{
    public HttpClient Client { get; } = new();
}

/// <summary>R4: P/Invoke вне Platform.Windows.</summary>
public static partial class PInvokeViolation
{
    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial uint GetTickCount();
}

/// <summary>Образец без нарушений: проверки не должны его отмечать.</summary>
public sealed class CleanType
{
    public int Value { get; } = 42;
}
