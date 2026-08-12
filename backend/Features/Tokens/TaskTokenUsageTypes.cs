namespace AgentStudio.Tokens;

/// <summary>
/// Pure classification policy for task token calls. The durable participant
/// and source context is preferred over model-name inference so mixed-model
/// pipeline work remains attributable to the work that caused it.
/// </summary>
public static class TaskTokenUsageTypes
{
    public const string CodingRun = "coding-run";
    public const string ReviewRun = "review-run";
    public const string Gate = "gate";
    public const string Enrichment = "enrichment";
    public const string SupportingRun = "supporting-run";
    public const string Other = "other";

    private static readonly string[] ReviewMarkers =
        ["review", "drift", "grading", "aspect"];

    private static readonly string[] EnrichmentMarkers =
        ["enrich", "summary-generation", "title-generation", "task-spawner", "commit-message"];

    private static readonly string[] GateMarkers =
        ["gate", "qualification", "orchestrator-decision", "agent-needs-input", "soft-reasoning"];

    public static string Classify(
        string? participantId,
        string? source,
        string? explicitType = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitType))
            return explicitType.Trim().ToLowerInvariant();

        var normalizedSource = source?.Trim().ToLowerInvariant() ?? string.Empty;
        if (ReviewMarkers.Any(normalizedSource.Contains)) return ReviewRun;
        if (EnrichmentMarkers.Any(normalizedSource.Contains)) return Enrichment;
        if (GateMarkers.Any(normalizedSource.Contains)) return Gate;

        var participant = participantId?.Trim() ?? string.Empty;
        if (participant.StartsWith("agent:", StringComparison.OrdinalIgnoreCase)) return CodingRun;
        if (participant.StartsWith("orchestrator:", StringComparison.OrdinalIgnoreCase)) return Gate;
        if (participant.StartsWith("support:", StringComparison.OrdinalIgnoreCase)) return SupportingRun;
        return Other;
    }

    public static int SortOrder(string usageType) => usageType switch
    {
        CodingRun => 0,
        ReviewRun => 1,
        Gate => 2,
        Enrichment => 3,
        SupportingRun => 4,
        _ => 5,
    };
}
