using System.IO;
using System.Reflection;
using PenForLang.Architecture.Tests.Rules;
using PenForLang.Architecture.Tests.Support;

namespace PenForLang.Architecture.Tests;

/// <summary>Правила R1-R5 на реальных проектах репозитория.</summary>
public class ProductArchitectureTests
{
    private static readonly IReadOnlyList<ProjectInfo> Product = RepositoryLayout.LoadProductProjects();

    [Fact]
    public void Product_projects_are_discovered()
    {
        // Страховка от «зелёных» тестов на пустом наборе.
        Assert.True(Product.Count >= 9, $"Expected at least 9 product projects, found {Product.Count}.");
        Assert.All(AllowedGraph.Direct.Keys, name => Assert.Contains(Product, p => p.Name == name));
    }

    [Fact]
    public void R1_net10_projects_do_not_depend_on_windows_in_csproj()
    {
        Assert.Empty(ProjectRules.CheckNoWindowsDependencies(Product));
    }

    [Fact]
    public void R5_project_references_match_the_allowed_graph_and_have_no_cycles()
    {
        Assert.Empty(ProjectRules.CheckDependencyGraph(Product, AllowedGraph.Direct));
    }

    [Fact]
    public void R2_domain_projects_enable_banned_api_analyzers()
    {
        Assert.Empty(ProjectRules.CheckDomainBanConfiguration(
            Product, AllowedGraph.DomainProjects, AllowedGraph.IntrinsicsBannedProjects));

        string targets = File.ReadAllText(RepositoryLayout.PathOf("Directory.Build.targets"));
        Assert.Contains("Microsoft.CodeAnalysis.BannedApiAnalyzers", targets, StringComparison.Ordinal);
        Assert.Contains("BannedSymbols.Domain.txt", targets, StringComparison.Ordinal);
        Assert.Contains("BannedSymbols.Numeric.txt", targets, StringComparison.Ordinal);

        string packages = File.ReadAllText(RepositoryLayout.PathOf("Directory.Packages.props"));
        Assert.Contains("Microsoft.CodeAnalysis.BannedApiAnalyzers", packages, StringComparison.Ordinal);
    }

    [Fact]
    public void R2_banned_symbol_files_contain_the_required_entries()
    {
        string domain = File.ReadAllText(RepositoryLayout.PathOf("BannedSymbols.Domain.txt"));
        string numeric = File.ReadAllText(RepositoryLayout.PathOf("BannedSymbols.Numeric.txt"));

        Assert.Empty(ProjectRules.FindMissingBannedSymbols(domain, AllowedGraph.RequiredDomainBans));
        Assert.Empty(ProjectRules.FindMissingBannedSymbols(numeric, AllowedGraph.RequiredNumericBans));
    }

    [Fact]
    public void Every_product_project_has_a_test_project()
    {
        foreach (ProjectInfo project in Product)
        {
            string testProject = RepositoryLayout.PathOf("tests", project.Name + ".Tests", project.Name + ".Tests.csproj");
            Assert.True(File.Exists(testProject), $"Missing test project for {project.Name}: {testProject}");
        }
    }

    [Fact]
    public void R1_net10_assemblies_do_not_depend_on_windows()
    {
        var violations = new List<string>();
        foreach (ProjectInfo project in Product.Where(p => !p.IsWindowsTarget))
        {
            violations.AddRange(AssemblyRules.CheckNoWindowsDependencies(Load(project)));
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void R3_product_has_no_network_code()
    {
        var violations = new List<string>();
        foreach (ProjectInfo project in RepositoryLayout.LoadSrcProjects())
        {
            violations.AddRange(AssemblyRules.CheckNoNetworkCode(Load(project)));
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void R4_pinvoke_exists_only_in_platform_windows()
    {
        var violations = new List<string>();
        foreach (ProjectInfo project in Product.Where(p => p.Name != AllowedGraph.Prefix + "Platform.Windows"))
        {
            violations.AddRange(AssemblyRules.FindPInvokeMethods(Load(project)));
        }

        Assert.Empty(violations);
    }

    private static Assembly Load(ProjectInfo project) => Assembly.Load(new AssemblyName(project.AssemblyName));
}
