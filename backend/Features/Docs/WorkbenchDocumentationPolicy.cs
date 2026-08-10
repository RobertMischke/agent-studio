namespace AgentStudio.Docs;

/// <summary>
/// Pure lifecycle decision for the documented end state. A decided item stays
/// current while any referenced card is unresolved or outside a terminal lane.
/// </summary>
public static class WorkbenchDocumentationPolicy
{
    public static WorkbenchDocumentationProjection Evaluate(
        string status,
        IEnumerable<WorkbenchDocumentationReference> references)
    {
        var normalized = references
            .Where(reference => !string.IsNullOrWhiteSpace(reference.Key))
            .DistinctBy(reference => reference.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(reference => reference.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var terminal = normalized.Count(reference => reference.Exists && reference.Terminal);
        var missing = normalized.Count(reference => !reference.Exists);
        var open = normalized.Count - terminal - missing;
        var eligible = status == "decided"
            && normalized.Count > 0
            && terminal == normalized.Count;

        return new WorkbenchDocumentationProjection(
            eligible,
            normalized.Count,
            terminal,
            open,
            missing,
            normalized);
    }
}

public sealed record WorkbenchDocumentationReference(
    string Key,
    bool Exists,
    bool Terminal,
    string? Lane);

public sealed record WorkbenchDocumentationProjection(
    bool Eligible,
    int TotalCount,
    int TerminalCount,
    int OpenCount,
    int MissingCount,
    IReadOnlyList<WorkbenchDocumentationReference> References);
