namespace AgentStudio.Docs;

/// <summary>
/// Resolves a project's repository checkout root. The docs/wiki surface is
/// always <c>&lt;repo&gt;/docs</c> by convention — the docs folder itself is
/// never a setting. Only the project↔repository association needs a source,
/// resolved in this order:
///
/// <list type="number">
/// <item>Registry record <see cref="ProjectRecord.RepositoryPath"/> — the
/// durable, API-mutable home for the association.</item>
/// <item>WatchPaths entry <c>RepositoryPath</c> / <c>RootPath</c> — legacy
/// appsettings fallback, kept so existing configs keep working.</item>
/// <item>Derivation from the storage layout: a task folder at
/// <c>&lt;repo&gt;/.orchestrator/jobs</c> implies the repository is its
/// grandparent — zero configuration for in-repo task storage.</item>
/// </list>
/// </summary>
public static class ProjectRepoResolver
{
    /// <summary>
    /// Name-based entry point shared by every consumer that starts from a
    /// project name (docs surface, git operations) so the write target and
    /// the commit root can never diverge. When a watch-path entry matches,
    /// its registry record is paired by STORAGE LOCATION — never by name —
    /// so a same-named registry record of a different project cannot capture
    /// this project's docs surface; the name lookup only applies to
    /// registry-only projects without a watch-path entry.
    /// </summary>
    public static string? ResolveForProject(string projectName, TaskScannerService scanner, ProjectRegistry registry)
    {
        if (string.IsNullOrWhiteSpace(projectName)) return null;
        var entry = scanner.GetWatchPaths().FirstOrDefault(e =>
            string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
        var record = entry != null
            ? registry.FindByStorageLocation(entry.Path)
            : registry.FindByIdOrDisplayName(projectName);
        if (record == null && entry == null) return null;
        return Resolve(record, entry);
    }

    public static string? Resolve(ProjectRecord? record, WatchPathEntry? entry)
    {
        if (!string.IsNullOrWhiteSpace(record?.RepositoryPath)) return record.RepositoryPath;
        if (!string.IsNullOrWhiteSpace(entry?.RepositoryPath)) return entry.RepositoryPath;
        if (!string.IsNullOrWhiteSpace(entry?.RootPath)) return entry.RootPath;
        return DeriveFromStorage(record?.StorageLocation) ?? DeriveFromStorage(entry?.Path);
    }

    /// <summary>
    /// <c>C:\x\repo\.orchestrator\jobs</c> → <c>C:\x\repo</c>; anything not
    /// matching the in-repo storage convention resolves to null.
    /// </summary>
    internal static string? DeriveFromStorage(string? storageLocation)
    {
        if (string.IsNullOrWhiteSpace(storageLocation)) return null;

        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(storageLocation)); }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }

        if (!string.Equals(Path.GetFileName(full), "jobs", StringComparison.OrdinalIgnoreCase))
            return null;
        var orchestratorDir = Path.GetDirectoryName(full);
        if (!string.Equals(Path.GetFileName(orchestratorDir), ".orchestrator", StringComparison.OrdinalIgnoreCase))
            return null;
        var repo = Path.GetDirectoryName(orchestratorDir);
        if (string.IsNullOrWhiteSpace(repo)) return null;
        // A filesystem root (C:\, /) above .orchestrator is not a repository.
        return string.Equals(repo, Path.GetPathRoot(repo), StringComparison.OrdinalIgnoreCase) ? null : repo;
    }
}
