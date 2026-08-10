namespace AgentStudio.Docs;

/// <summary>
/// Selects the one repository view used by every Wiki-backed read surface.
/// Checkout mode reads the registered project checkout. A configured
/// <c>wikiSourceBranch</c> reads the immutable Git snapshot materialized by
/// <see cref="GitService"/>. Keeping this policy outside individual readers
/// prevents catalogue discovery and page serving from choosing different roots.
/// </summary>
internal static class ProjectWikiSourceResolver
{
    public static ProjectRecord? ResolveProject(
        string projectName,
        TaskScannerService scanner,
        ProjectRegistry registry)
    {
        var entry = scanner.GetWatchPaths().FirstOrDefault(candidate =>
            string.Equals(candidate.Name, projectName, StringComparison.OrdinalIgnoreCase));
        if (entry != null && registry.FindByStorageLocation(entry.Path) is { } registered)
            return registered;
        return registry.FindByIdOrDisplayName(projectName)
            ?? registry.FindByShortCode(projectName);
    }

    public static WikiSourceContext? Resolve(
        string projectName,
        TaskScannerService scanner,
        ProjectRegistry registry,
        GitService? git)
    {
        var project = ResolveProject(projectName, scanner, registry);
        var checkout = ProjectRepoResolver.ResolveForProject(projectName, scanner, registry);
        if (checkout == null && project != null)
        {
            var entry = scanner.GetWatchPaths().FirstOrDefault(candidate =>
                string.Equals(
                    registry.FindByStorageLocation(candidate.Path)?.Id,
                    project.Id,
                    StringComparison.OrdinalIgnoreCase));
            checkout = ProjectRepoResolver.Resolve(project, entry);
        }
        if (checkout == null) return null;

        var configured = project?.WikiSourceBranch;
        var repoRoot = git?.ResolveRepoRootForProject(projectName) ?? checkout;
        if (string.IsNullOrWhiteSpace(configured))
        {
            var status = git?.GetStatusForRepoRoot(repoRoot);
            var sha = git?.GetHeadShaCached(repoRoot);
            return new WikiSourceContext(checkout, new WikiSourceInfo(
                "checkout",
                status?.Branch ?? "checkout",
                sha,
                ShortSha(sha),
                true,
                null));
        }

        if (git == null)
        {
            return new WikiSourceContext(
                UnavailableRoot(),
                new WikiSourceInfo(
                    "branch",
                    configured,
                    null,
                    null,
                    false,
                    "Git service is unavailable."));
        }

        var snapshot = git.GetWikiBranchSnapshotCached(repoRoot, configured);
        var snapshotRoot = string.IsNullOrWhiteSpace(snapshot.RootPath)
            ? UnavailableRoot()
            : snapshot.RootPath;
        return new WikiSourceContext(snapshotRoot, new WikiSourceInfo(
            "branch",
            configured,
            snapshot.Sha,
            snapshot.ShortSha,
            false,
            snapshot.Error));
    }

    private static string UnavailableRoot() =>
        Path.Combine(Path.GetTempPath(), "agent-studio", "wiki-unavailable");

    private static string? ShortSha(string? sha) =>
        string.IsNullOrWhiteSpace(sha) ? null : sha[..Math.Min(8, sha.Length)];
}
