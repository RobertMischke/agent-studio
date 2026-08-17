namespace AgentStudio.PublicDemo;

/// <summary>
/// The explicit endpoint allowlist of the public read-only demo edge (dossier
/// AGT-W34 slice S4, "Minimum mutation surface").
///
/// This list is the inventory, not a filter over one: anything absent is denied,
/// so a newly mapped route is unreachable from the public demo until somebody
/// adds it here deliberately. That is the S4 counterpart to the S2 server-side
/// route inventory guard - S2 proves no execution path admits work, S4 proves no
/// route outside this list is reachable at all.
///
/// Patterns use two wildcards:
/// <list type="bullet">
///   <item><c>*</c> matches exactly one path segment.</item>
///   <item><c>**</c> matches one or more trailing segments and may only appear last.</item>
/// </list>
/// </summary>
public static class PublicDemoRoutes
{
    /// <summary>
    /// GET/HEAD-reachable API surface. Grouped by the visitor story each entry
    /// serves so an addition has to justify itself against a demo need.
    /// </summary>
    public static readonly string[] Allowed =
    [
        // Shell boot: profile discovery and product identity.
        "/api/auth/status",
        "/api/system/about",
        "/api/system/version",
        "/api/environment",
        "/api/agent-rules",
        "/api/tags",

        // Board and project overview.
        "/api/projects",
        "/api/projects/settings",
        "/api/projects/*/snapshot",
        "/api/projects/*/throughput",
        "/api/projects/*/graph",
        "/api/projects/*/visual-evidence",
        "/api/projects/*/deployment/summary",
        "/api/projects/*/pipeline-health",
        "/api/projects/*/regression-radar",

        // Token Economy read views.
        "/api/projects/*/token-usage/summary",
        "/api/projects/*/token-usage/heatmap",
        "/api/projects/*/token-usage/expensive",
        "/api/projects/*/token-usage/pipeline-cost",
        "/api/projects/*/token-usage/job/*",

        // Wiki: the seeded two-project content tree.
        "/api/projects/*/wiki",
        "/api/projects/*/wiki/home",
        "/api/projects/*/wiki/tree",
        "/api/projects/*/wiki/recent",
        "/api/projects/*/wiki/pulse",
        "/api/projects/*/wiki/search",
        "/api/projects/*/wiki/files/**",
        "/api/projects/*/wiki/folder/**",
        "/api/projects/*/wiki/assets/**",
        "/api/projects/*/wiki/history/**",
        "/api/projects/*/wiki/revisions/**",

        // Dossier lifecycle gallery.
        "/api/workbenches",
        "/api/projects/*/workbenches",
        "/api/projects/*/workbenches/*",
        "/api/projects/*/workbenches/*/references",

        // Cards across every lane, plus their history and evidence.
        "/api/tasks",
        "/api/tasks/grouped",
        "/api/tasks/archive",
        "/api/tasks/*",
        "/api/tasks/*/output",
        "/api/tasks/*/plan",
        "/api/tasks/*/pipeline",
        "/api/tasks/*/provenance",
        "/api/tasks/*/dependents",
        "/api/tasks/*/conversation",
        "/api/tasks/*/agent-work-summary",
        "/api/tasks/*/agent-work-detail",
        "/api/tasks/*/artifacts",
        "/api/tasks/*/attachments/*",
        "/api/tasks/*/results/**",
        "/api/tasks/*/runs",
        "/api/tasks/*/runs/**",
        "/api/tasks/*/commit",
        "/api/tasks/*/commit/diff",
        "/api/tasks/*/commits",
        "/api/tasks/*/commits/**",
        "/api/tasks/*/code-review/**",
        "/api/tasks/*/regression-radar",
        "/api/epics",
        "/api/epics/*",
        "/api/epics/completed/count",

        // Cross-project navigation.
        "/api/search",
        "/api/concept-docs/*",

        // Replayed live scene: runner status, message bus, scripted chat history.
        "/api/runner/status",
        "/api/bus/*/summary",
        "/api/bus/*/recent",
        "/api/bus/*/messages",
        "/api/bus/*/messages/*",
        "/api/bus/*/token-aggregate",
        "/api/orchestrator/context/project:*",
        "/api/orchestrator/context/task:*/*",
    ];

    /// <summary>
    /// The single event stream. Everything else under <c>/hubs/</c> stays denied,
    /// and the hub itself scopes its groups through
    /// <see cref="PublicDemoProjectScope"/>.
    /// </summary>
    public const string EventHub = "/hubs/jobs";

    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    public static bool IsHealth(string normalizedPath)
        => normalizedPath.Equals("/healthz", StringComparison.OrdinalIgnoreCase)
           || normalizedPath.Equals("/healthz/drain", StringComparison.OrdinalIgnoreCase);

    public static bool IsHubNegotiate(string normalizedPath)
        => normalizedPath.Equals(EventHub + "/negotiate", StringComparison.OrdinalIgnoreCase);

    public static bool IsEventHub(string normalizedPath)
        => normalizedPath.Equals(EventHub, StringComparison.OrdinalIgnoreCase)
           || IsHubNegotiate(normalizedPath);

    /// <summary>
    /// True when the response body carries seeded document content (rendered
    /// Wiki/Dossier HTML, raw evidence files, screenshots) rather than the app's
    /// own JSON view models. Those responses get the sandboxed CSP so a script
    /// that somehow survived the scrub gate cannot run with the app's authority.
    /// </summary>
    public static bool IsSeededDocument(string normalizedPath)
        => Matches("/api/projects/*/wiki/files/**", normalizedPath)
           || Matches("/api/projects/*/wiki/assets/**", normalizedPath)
           || Matches("/api/projects/*/wiki/revisions/**", normalizedPath)
           || Matches("/api/projects/*/workbenches/*", normalizedPath)
           || Matches("/api/tasks/*/results/**", normalizedPath)
           || Matches("/api/tasks/*/attachments/*", normalizedPath)
           || Matches("/api/tasks/*/code-review/**", normalizedPath)
           || Matches("/api/concept-docs/*", normalizedPath);

    public static bool IsAllowed(string normalizedPath)
    {
        if (IsEventHub(normalizedPath)) return true;

        // Anything outside the API and the hub is the Angular bundle: index.html,
        // hashed assets, icons, the manifest. Those are static files served from
        // the image and carry no demo data beyond the shell itself.
        if (!normalizedPath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            && !normalizedPath.Equals("/api", StringComparison.OrdinalIgnoreCase)
            && !normalizedPath.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var pattern in Allowed)
        {
            if (Matches(pattern, normalizedPath)) return true;
        }
        return false;
    }

    /// <summary>Segment-wise pattern match. Case-insensitive, no regex, no backtracking.</summary>
    public static bool Matches(string pattern, string normalizedPath)
    {
        var patternSegments = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var pathSegments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < patternSegments.Length; i++)
        {
            if (patternSegments[i] == "**")
                return pathSegments.Length > i;
            if (i >= pathSegments.Length) return false;
            if (!SegmentMatches(patternSegments[i], pathSegments[i])) return false;
        }
        return patternSegments.Length == pathSegments.Length;
    }

    private static bool SegmentMatches(string patternSegment, string pathSegment)
    {
        if (patternSegment == "*") return true;
        // A trailing '*' inside a segment covers the composite route keys the
        // orchestrator context uses ("project:demo-app", "task:demo-app").
        if (patternSegment.EndsWith('*'))
            return pathSegment.StartsWith(patternSegment[..^1], StringComparison.OrdinalIgnoreCase);
        return patternSegment.Equals(pathSegment, StringComparison.OrdinalIgnoreCase);
    }
}
