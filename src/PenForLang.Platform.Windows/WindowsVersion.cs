namespace PenForLang.Platform.Windows;

/// <summary>
/// Проверка версии Windows. Минимум — Windows 10 22H2 (сборка 19045); Windows 11 (22000+) поддерживается.
/// </summary>
public static class WindowsVersion
{
    /// <summary>Минимальная поддерживаемая сборка: Windows 10 22H2.</summary>
    public const int MinimumBuild = 19045;

    /// <summary>Поддерживается ли указанная версия ОС (чистая функция, без обращения к системе).</summary>
    public static bool IsSupported(int major, int minor, int build) =>
        (major, minor) switch
        {
            ( > 10, _) => true,
            (10, > 0) => true,
            (10, 0) => build >= MinimumBuild,
            _ => false,
        };

    /// <summary>Поддерживается ли ОС, на которой выполняется процесс.</summary>
    public static bool IsCurrentSupported() => OperatingSystem.IsWindowsVersionAtLeast(10, 0, MinimumBuild);
}
