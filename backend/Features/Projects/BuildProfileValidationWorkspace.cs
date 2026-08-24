namespace AgentStudio.Projects;

/// <summary>
/// Resolves the directory a build-profile validation dry-run must execute in
/// (AGT-2677).
///
/// <para>
/// A project entry carries three paths: <see cref="WatchPathEntry.Path"/> is where
/// its task folders live, <see cref="WatchPathEntry.RepositoryPath"/> is the git
/// checkout, and <see cref="WatchPathEntry.RootPath"/> is the workspace root above
/// it. Only the first is guaranteed to exist and only the last two are guaranteed
/// to hold source code. The validate endpoint used to hand the watch path to the
/// dry-run, which is why a proven Quality Studio profile still reported
/// "expected QualityStudio.slnx at the review workspace root": the commands ran in
/// a directory with no sources. The verify planner already resolved
/// repository-then-root; this centralises that same order so both agree.
/// </para>
/// </summary>
public static class BuildProfileValidationWorkspace
{
    /// <summary>
    /// Repository checkout first, then the workspace root, then the watch path as
    /// the last resort so a task-only project still gets a runnable directory.
    /// </summary>
    public static string Resolve(WatchPathEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!string.IsNullOrWhiteSpace(entry.RepositoryPath)) return entry.RepositoryPath;
        if (!string.IsNullOrWhiteSpace(entry.RootPath)) return entry.RootPath;
        return entry.Path;
    }
}
