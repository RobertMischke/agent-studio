namespace AgentStudio.Security;

/// <summary>
/// Explicit path prefixes the public-demo edge accepts on GET/HEAD/OPTIONS.
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
    public static readonly IReadOnlyList<string> PathPrefixes =
    [
        "/api/environment",
        "/api/system",
        "/api/auth/status",
        "/api/tasks",
        "/api/projects",
        "/api/workbenches",
        "/api/search",
        "/api/watch-paths",
        "/api/runner",
        "/api/tags",
        "/api/clients",
        "/hubs/",
    ];

    public static bool Allows(string path) =>
        PathPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
