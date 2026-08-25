namespace AgentStudio.Security;

/// <summary>
/// Explicit path roots the public-demo edge accepts on GET/HEAD/OPTIONS.
/// Studio maps far more routes than the demo's browse story needs (crash
/// recovery, drift reports, security review, admin config, ...); those stay
/// mapped for the networked/local profiles but must not become free,
/// unauthenticated reads on a public host, since some carry non-trivial query
/// cost or diagnostic detail. Add a prefix only when a public-demo view
/// genuinely reads it - this list is a security boundary, not a convenience
/// default.
/// </summary>
public static class PublicDemoReadAllowlist
{
    public static readonly IReadOnlyList<string> PathRoots =
    [
        "/api/environment",
        "/api/system",
        "/api/auth/status",
        "/api/tasks",
        "/api/workbenches",
        "/api/search",
        "/api/watch-paths",
        "/api/runner",
        "/api/tags",
        "/api/clients",
    ];

    public const string JobsHubPath = "/hubs/jobs";
    public const string JobsHubNegotiatePath = JobsHubPath + "/negotiate";

    // "/api/projects" fans out into dozens of per-project sub-resources
    // (security review, token usage, proposals, settings, ...) that a plain
    // prefix match would open up wholesale. Only the project list itself and
    // the two sub-resources the S1 seed actually renders - the Wiki and the
    // Dossier/workbench gallery - belong to the demo's browse story.
    private const string ProjectsRoot = "/api/projects";

    private static readonly IReadOnlyList<string> ProjectScopedReadSuffixes =
    [
        "/wiki",
        "/workbenches",
    ];

    public static bool Allows(string path)
    {
        if (PathRoots.Any(root => IsRootOrDescendant(path, root)))
            return true;
        if (AllowsJobsHubPath(path)) return true;
        return AllowsProjectScopedRead(path);
    }

    public static bool AllowsJobsHubPath(string path)
        => path.Equals(JobsHubPath, StringComparison.OrdinalIgnoreCase)
           || path.Equals(JobsHubNegotiatePath, StringComparison.OrdinalIgnoreCase);

    public static bool AllowsJobsHubNegotiate(string path, string method)
        => HttpMethods.IsPost(method)
           && path.Equals(JobsHubNegotiatePath, StringComparison.OrdinalIgnoreCase);

    private static bool AllowsProjectScopedRead(string path)
    {
        if (path.Equals(ProjectsRoot, StringComparison.OrdinalIgnoreCase)) return true;

        var rootWithSlash = ProjectsRoot + "/";
        if (!path.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase)) return false;

        var afterProjectName = path[rootWithSlash.Length..];
        var subResourceStart = afterProjectName.IndexOf('/');
        if (subResourceStart < 0) return false;

        var subResource = afterProjectName[subResourceStart..];
        return ProjectScopedReadSuffixes.Any(suffix => IsRootOrDescendant(subResource, suffix));
    }

    private static bool IsRootOrDescendant(string path, string root)
        => path.Equals(root, StringComparison.OrdinalIgnoreCase)
           || (path.Length > root.Length
               && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
               && path[root.Length] == '/');
}
