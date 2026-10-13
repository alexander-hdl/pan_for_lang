using PenForLang.Architecture.Tests.Support;

namespace PenForLang.Architecture.Tests.Rules;

/// <summary>
/// Правила уровня проектов (по .csproj). Все методы чистые: принимают список <see cref="ProjectInfo"/> и
/// возвращают список нарушений (пустой — правило соблюдено). Благодаря этому тесты проверяют их
/// и на реальных проектах, и на синтетических нарушающих.
/// </summary>
public static class ProjectRules
{
    /// <summary>Пакеты, запрещённые в сборках <c>net10.0</c> (R1). Значение — проект, которому они разрешены.</summary>
    public static readonly IReadOnlyDictionary<string, string?> ForbiddenPackagePrefixes =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Microsoft.Data.Sqlite"] = "PenForLang.Infrastructure",
            ["SQLitePCLRaw"] = "PenForLang.Infrastructure",
            ["PDFsharp"] = "PenForLang.Infrastructure",
            ["NAudio"] = null,
            ["NVorbis"] = null,
            ["Microsoft.Windows.SDK"] = null,
            ["System.Windows.Forms"] = null,
        };

    /// <summary>
    /// R1: проекты <c>net10.0</c> не ссылаются на <c>net10.0-windows</c>, WPF, WinForms, Sqlite, NAudio, PDFsharp
    /// (кроме Infrastructure для Sqlite и PDFsharp).
    /// </summary>
    public static IReadOnlyList<string> CheckNoWindowsDependencies(IReadOnlyCollection<ProjectInfo> projects)
    {
        var byName = projects.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var violations = new List<string>();

        foreach (ProjectInfo project in projects.Where(p => !p.IsWindowsTarget))
        {
            if (project.UsesWpf)
            {
                violations.Add($"R1: {project.Name} ({project.TargetFramework}) sets UseWPF.");
            }

            if (project.UsesWindowsForms)
            {
                violations.Add($"R1: {project.Name} ({project.TargetFramework}) sets UseWindowsForms.");
            }

            foreach (string reference in project.ProjectReferences)
            {
                if (byName.TryGetValue(reference, out ProjectInfo? target) && target.IsWindowsTarget)
                {
                    violations.Add($"R1: {project.Name} ({project.TargetFramework}) references {reference} ({target.TargetFramework}).");
                }
            }

            foreach (string package in project.PackageReferences)
            {
                foreach ((string prefix, string? allowedIn) in ForbiddenPackagePrefixes)
                {
                    if (package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(project.Name, allowedIn, StringComparison.Ordinal))
                    {
                        violations.Add($"R1: {project.Name} ({project.TargetFramework}) references package {package}.");
                    }
                }
            }
        }

        return violations;
    }

    /// <summary>
    /// R5: прямые ссылки каждого проекта входят в разрешённый список (<paramref name="allowed"/>, ARCHITECTURE.md §2.3),
    /// проект без записи в списке — нарушение (новый проект нужно внести в граф осознанно), циклов нет.
    /// </summary>
    public static IReadOnlyList<string> CheckDependencyGraph(
        IReadOnlyCollection<ProjectInfo> projects,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> allowed)
    {
        var violations = new List<string>();

        foreach (ProjectInfo project in projects)
        {
            if (!allowed.TryGetValue(project.Name, out IReadOnlyCollection<string>? allowedRefs))
            {
                violations.Add($"R5: {project.Name} is not in the allowed dependency graph (add it deliberately).");
                continue;
            }

            foreach (string reference in project.ProjectReferences.Where(r => !allowedRefs.Contains(r)))
            {
                violations.Add($"R5: {project.Name} -> {reference} is not allowed.");
            }
        }

        violations.AddRange(FindCycles(projects).Select(cycle => $"R5: dependency cycle {string.Join(" -> ", cycle)}."));
        return violations;
    }

    /// <summary>Находит циклы в графе ссылок (поиск в глубину). Каждый цикл — путь, замкнутый на первой вершине.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> FindCycles(IReadOnlyCollection<ProjectInfo> projects)
    {
        var edges = projects.ToDictionary(p => p.Name, p => p.ProjectReferences, StringComparer.Ordinal);
        var cycles = new List<IReadOnlyList<string>>();
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 — в стеке, 2 — готова
        var stack = new List<string>();

        void Visit(string node)
        {
            state[node] = 1;
            stack.Add(node);
            if (edges.TryGetValue(node, out IReadOnlyList<string>? next))
            {
                foreach (string target in next)
                {
                    if (!state.TryGetValue(target, out int s))
                    {
                        Visit(target);
                    }
                    else if (s == 1)
                    {
                        List<string> cycle = stack.Skip(stack.IndexOf(target)).ToList();
                        cycle.Add(target);
                        cycles.Add(cycle);
                    }
                }
            }

            stack.RemoveAt(stack.Count - 1);
            state[node] = 2;
        }

        foreach (string node in edges.Keys.OrderBy(k => k, StringComparer.Ordinal).Where(k => !state.ContainsKey(k)))
        {
            Visit(node);
        }

        return cycles;
    }

    /// <summary>
    /// R2 (конфигурация): доменные проекты включены в запрет API (<c>IsDomainProject</c>), а Core и Evaluation — ещё и в
    /// запрет SIMD-интринсиков.
    /// </summary>
    public static IReadOnlyList<string> CheckDomainBanConfiguration(
        IReadOnlyCollection<ProjectInfo> projects,
        IReadOnlyCollection<string> domainProjectNames,
        IReadOnlyCollection<string> intrinsicsBannedProjectNames)
    {
        var violations = new List<string>();
        foreach (ProjectInfo project in projects)
        {
            if (domainProjectNames.Contains(project.Name) && !project.IsDomain)
            {
                violations.Add($"R2: {project.Name} is a domain project but has no <IsDomainProject>true</IsDomainProject>.");
            }

            if (intrinsicsBannedProjectNames.Contains(project.Name) && !project.BansNumericIntrinsics)
            {
                violations.Add($"R2: {project.Name} must set <BanNumericIntrinsics>true</BanNumericIntrinsics> (A19).");
            }

            if (project.IsDomain && !domainProjectNames.Contains(project.Name))
            {
                violations.Add($"R2: {project.Name} sets IsDomainProject but is not in the list of domain projects.");
            }
        }

        return violations;
    }

    /// <summary>R2 (содержимое): в файле запретов есть все обязательные символы.</summary>
    public static IReadOnlyList<string> FindMissingBannedSymbols(string bannedSymbolsFileContent, IEnumerable<string> required)
    {
        string[] active = bannedSymbolsFileContent
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split(';')[0])
            .ToArray();

        return required
            .Where(symbol => !active.Contains(symbol, StringComparer.Ordinal))
            .Select(symbol => $"R2: banned symbol '{symbol}' is missing.")
            .ToList();
    }
}
