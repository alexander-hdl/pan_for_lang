namespace PenForLang.Architecture.Tests;

/// <summary>
/// Разрешённый граф прямых ссылок между проектами (R5, ARCHITECTURE.md §2.3), срез для вехи v0.
/// Список — верхняя граница: проект вправе ссылаться на подмножество. Новый проект или новая ссылка
/// требуют осознанной правки этого файла (и сверки с §2.3).
/// </summary>
/// <remarks>
/// Отличия от §2.3, вызванные отсутствием проектов v1 (все помечены «v0»):
/// Glyphs не ссылается на Curriculum; Evaluation ссылается на Ink и Glyphs напрямую (нет Evaluation.Abstractions);
/// Platform.Windows не ссылается на Application; Testing не ссылается на Lesson.
/// </remarks>
public static class AllowedGraph
{
    public const string Prefix = "PenForLang.";

    public static readonly IReadOnlyDictionary<string, IReadOnlyCollection<string>> Direct =
        new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
        {
            [Prefix + "Core"] = [],
            [Prefix + "Ink"] = [Prefix + "Core"],
            [Prefix + "Glyphs"] = [Prefix + "Core"], // v0: без Curriculum
            [Prefix + "Evaluation"] = [Prefix + "Core", Prefix + "Ink", Prefix + "Glyphs"], // v0: без Evaluation.Abstractions
            [Prefix + "Platform.Windows"] = [Prefix + "Core", Prefix + "Ink"], // v0: без Application
            [Prefix + "Wpf.Controls"] = [Prefix + "Core", Prefix + "Ink", Prefix + "Glyphs"],
            [Prefix + "App.Wpf"] =
            [
                Prefix + "Core", Prefix + "Ink", Prefix + "Glyphs", Prefix + "Evaluation",
                Prefix + "Platform.Windows", Prefix + "Wpf.Controls",
            ],
            [Prefix + "Testing"] = [Prefix + "Core", Prefix + "Ink", Prefix + "Glyphs", Prefix + "Evaluation"],
            [Prefix + "Tools.Cli"] =
            [
                Prefix + "Core", Prefix + "Ink", Prefix + "Glyphs", Prefix + "Evaluation", Prefix + "Testing",
            ],
            // InkLab (V0.9) — инструмент разработчика: может ссылаться на продуктовые сборки, продукт на него — нет.
            // Вкладки «Глифы» и «Разбор» (T0.1 и далее) добавят Evaluation; пока он не нужен.
            [Prefix + "Tools.InkLab"] =
            [
                Prefix + "Core", Prefix + "Ink", Prefix + "Glyphs", Prefix + "Platform.Windows", Prefix + "Wpf.Controls",
            ],
        };

    /// <summary>Доменные проекты (R2): ARCHITECTURE.md §2.4. Перечислены и будущие: проверяются те, что уже существуют.</summary>
    public static readonly IReadOnlyCollection<string> DomainProjects =
    [
        Prefix + "Core",
        Prefix + "Ink",
        Prefix + "Glyphs",
        Prefix + "Evaluation",
        Prefix + "Evaluation.Abstractions",
        Prefix + "Curriculum",
        Prefix + "Learner",
        Prefix + "Lesson",
        Prefix + "Reporting",
    ];

    /// <summary>Проекты с дополнительным запретом SIMD-интринсиков (A19): Evaluation и Core (Core.Geometry).</summary>
    public static readonly IReadOnlyCollection<string> IntrinsicsBannedProjects =
    [
        Prefix + "Core",
        Prefix + "Evaluation",
    ];

    /// <summary>Символы, которые обязаны быть в <c>BannedSymbols.Domain.txt</c> (R2, R3).</summary>
    public static readonly IReadOnlyCollection<string> RequiredDomainBans =
    [
        "P:System.DateTime.Now",
        "P:System.DateTime.UtcNow",
        "P:System.Environment.TickCount",
        "P:System.Environment.TickCount64",
        "T:System.Diagnostics.Stopwatch",
        "T:System.Random",
        "M:System.Guid.NewGuid()",
        "T:System.Threading.Tasks.Parallel",
        "M:System.Threading.Tasks.Task.Run(System.Action)",
        "T:System.IO.File",
        "T:System.IO.Directory",
        "T:System.Console",
        "N:System.Net.Http",
        "N:System.Net.Sockets",
    ];

    /// <summary>Символы, обязательные в <c>BannedSymbols.Numeric.txt</c> (A19).</summary>
    public static readonly IReadOnlyCollection<string> RequiredNumericBans =
    [
        "N:System.Runtime.Intrinsics",
        "T:System.Numerics.Vector`1",
    ];
}
