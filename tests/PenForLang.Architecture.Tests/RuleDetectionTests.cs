using System.Reflection;
using PenForLang.Architecture.Fixtures;
using PenForLang.Architecture.Tests.Rules;
using PenForLang.Architecture.Tests.Support;

namespace PenForLang.Architecture.Tests;

/// <summary>
/// Доказательство того, что проверки не «пустые»: каждое правило падает на намеренно нарушающих данных —
/// синтетических списках проектов и сборке-фикстуре <c>PenForLang.Architecture.Fixtures</c>.
/// Эти тесты проходят, когда нарушение найдено.
/// </summary>
public class RuleDetectionTests
{
    private static readonly Assembly Fixtures = typeof(CleanType).Assembly;

    private static ProjectInfo Project(string name, string tfm, string[]? projectRefs = null, string[]? packages = null,
        bool wpf = false, bool winForms = false) =>
        new(name, name, tfm, projectRefs ?? [], packages ?? [], wpf, winForms);

    // ---- R1 / csproj ----

    [Fact]
    public void R1_csproj_rule_flags_net10_project_referencing_windows_project()
    {
        ProjectInfo[] projects =
        [
            Project("Bad.Domain", "net10.0", ["Win.Lib"]),
            Project("Win.Lib", "net10.0-windows"),
        ];

        IReadOnlyList<string> violations = ProjectRules.CheckNoWindowsDependencies(projects);

        string violation = Assert.Single(violations);
        Assert.Contains("Bad.Domain", violation, StringComparison.Ordinal);
        Assert.Contains("Win.Lib", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void R1_csproj_rule_flags_wpf_winforms_and_forbidden_packages_in_net10_project()
    {
        ProjectInfo[] projects =
        [
            Project("Bad.Wpf", "net10.0", wpf: true),
            Project("Bad.Forms", "net10.0", winForms: true),
            Project("Bad.Packages", "net10.0", packages: ["Microsoft.Data.Sqlite", "NAudio", "PDFsharp"]),
        ];

        IReadOnlyList<string> violations = ProjectRules.CheckNoWindowsDependencies(projects);

        Assert.Equal(5, violations.Count);
    }

    [Fact]
    public void R1_csproj_rule_allows_windows_projects_and_infrastructure_packages()
    {
        ProjectInfo[] projects =
        [
            Project("App", "net10.0-windows", ["Core"], ["NAudio"], wpf: true),
            Project("Core", "net10.0"),
            Project("PenForLang.Infrastructure", "net10.0", packages: ["Microsoft.Data.Sqlite", "PDFsharp"]),
        ];

        Assert.Empty(ProjectRules.CheckNoWindowsDependencies(projects));
    }

    // ---- R5 ----

    [Fact]
    public void R5_flags_reference_that_is_not_in_the_allowed_graph()
    {
        var allowed = new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["A"] = ["B"],
            ["B"] = [],
            ["C"] = [],
        };
        ProjectInfo[] projects =
        [
            Project("A", "net10.0", ["B", "C"]),
            Project("B", "net10.0"),
            Project("C", "net10.0"),
        ];

        string violation = Assert.Single(ProjectRules.CheckDependencyGraph(projects, allowed));
        Assert.Contains("A -> C", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void R5_flags_project_missing_from_the_allowed_graph()
    {
        var allowed = new Dictionary<string, IReadOnlyCollection<string>> { ["A"] = [] };
        ProjectInfo[] projects = [Project("A", "net10.0"), Project("Surprise", "net10.0")];

        string violation = Assert.Single(ProjectRules.CheckDependencyGraph(projects, allowed));
        Assert.Contains("Surprise", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void R5_flags_dependency_cycle()
    {
        var allowed = new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["A"] = ["B"],
            ["B"] = ["C"],
            ["C"] = ["A"],
        };
        ProjectInfo[] projects =
        [
            Project("A", "net10.0", ["B"]),
            Project("B", "net10.0", ["C"]),
            Project("C", "net10.0", ["A"]),
        ];

        IReadOnlyList<string> violations = ProjectRules.CheckDependencyGraph(projects, allowed);

        string violation = Assert.Single(violations);
        Assert.Contains("cycle", violation, StringComparison.Ordinal);
    }

    // ---- R2 ----

    [Fact]
    public void R2_flags_domain_project_without_banned_api_flag()
    {
        ProjectInfo[] projects = [Project("PenForLang.Core", "net10.0")];

        IReadOnlyList<string> violations = ProjectRules.CheckDomainBanConfiguration(
            projects, ["PenForLang.Core"], ["PenForLang.Core"]);

        Assert.Equal(2, violations.Count);
    }

    [Fact]
    public void R2_flags_banned_symbols_file_that_lacks_required_entries()
    {
        const string File = "# comment\nP:System.DateTime.Now;msg\n  T:System.Random;msg\nT:System.Console\n";

        IReadOnlyList<string> missing = ProjectRules.FindMissingBannedSymbols(
            File, ["P:System.DateTime.Now", "T:System.Random", "P:System.DateTime.UtcNow", "T:System.IO.File"]);

        Assert.Equal(2, missing.Count);
        Assert.Contains(missing, m => m.Contains("UtcNow", StringComparison.Ordinal));
        Assert.Contains(missing, m => m.Contains("System.IO.File", StringComparison.Ordinal));
    }

    // ---- R1 / R3 / R4 на скомпилированной фикстуре ----

    [Fact]
    public void R1_assembly_rule_flags_the_fixture()
    {
        IReadOnlyList<string> violations = AssemblyRules.CheckNoWindowsDependencies(Fixtures);

        // NetArchTest: зависимость типа от Microsoft.Win32.
        Assert.Contains(violations, v => v.Contains(nameof(RegistryViolation), StringComparison.Ordinal));
        // Ссылка сборки на WPF (WindowsBase / PresentationCore).
        Assert.Contains(violations, v => v.Contains("WindowsBase", StringComparison.Ordinal)
                                         || v.Contains("PresentationCore", StringComparison.Ordinal));
        // Чистый тип не отмечается.
        Assert.DoesNotContain(violations, v => v.Contains(nameof(CleanType), StringComparison.Ordinal));
    }

    [Fact]
    public void R3_rule_flags_http_client_in_the_fixture_and_only_there()
    {
        IReadOnlyList<string> violations = AssemblyRules.CheckNoNetworkCode(Fixtures);

        string violation = Assert.Single(violations);
        Assert.Contains(nameof(NetworkViolation), violation, StringComparison.Ordinal);
    }

    [Fact]
    public void R4_rule_flags_pinvoke_in_the_fixture_and_not_in_clean_assemblies()
    {
        IReadOnlyList<string> violations = AssemblyRules.FindPInvokeMethods(Fixtures);

        Assert.Contains(violations, v => v.Contains("GetTickCount", StringComparison.Ordinal));
        // Core заведомо чист: контроль от ложных срабатываний.
        Assert.Empty(AssemblyRules.FindPInvokeMethods(typeof(PenForLang.Core.Pcg32).Assembly));
    }

    [Fact]
    public void Fixture_project_is_not_part_of_the_product_projects()
    {
        Assert.DoesNotContain(RepositoryLayout.LoadProductProjects(),
            p => p.Name.Contains("Fixtures", StringComparison.Ordinal));
    }
}
