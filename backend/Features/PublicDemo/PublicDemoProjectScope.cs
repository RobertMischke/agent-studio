namespace AgentStudio.PublicDemo;

/// <summary>
/// One project filter, applied identically to REST, search, and SignalR (dossier
/// AGT-W34 §6, "Visitor authorization"). The demo datastore holds only the
/// ADR-0056 projects, so this is defense in depth rather than the sole barrier:
/// it keeps a mis-seeded or drifted store from leaking a project the demo never
/// announced.
/// </summary>
public static class PublicDemoProjectScope
{
    public static bool Allows(IReadOnlyCollection<string> allowedProjects, string? handle)
    {
        if (string.IsNullOrWhiteSpace(handle)) return false;
        foreach (var allowed in allowedProjects)
        {
            if (string.Equals(allowed, handle, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static IReadOnlyList<string> Filter(
        IReadOnlyCollection<string> allowedProjects,
        IEnumerable<string> candidates)
        => candidates.Where(candidate => Allows(allowedProjects, candidate)).ToList();
}
