namespace AgentStudio.Tokens;

/// <summary>
/// Maps the execution context already present on token events to the compact
/// per-type vocabulary used by task token surfaces. Pricing remains entirely
/// independent and continues to use the event timestamp.
/// </summary>
internal static class TaskTokenUsageClassifier
{
    public static string Classify(string? participantId, string? topic)
    {
        var participant = participantId?.Trim().ToLowerInvariant() ?? string.Empty;
        var context = topic?.Trim().ToLowerInvariant() ?? string.Empty;

        if (ContainsAny(context, "enrich", "prompt-preparation", "prompt-prep"))
            return TaskTokenUsageTypes.Enrichment;
        if (ContainsAny(context, "gate", "lint", "build-test", "quality-grade"))
            return TaskTokenUsageTypes.Gate;
        if (ContainsAny(context, "review", "aspect", "code-quality", "requirement-fit",
                "documentation-impact", "tests-and-evidence", "drift"))
            return TaskTokenUsageTypes.ReviewRun;
        if (participant.StartsWith("agent:", StringComparison.Ordinal))
            return TaskTokenUsageTypes.CodingRun;
        if (participant.StartsWith("support:", StringComparison.Ordinal))
            return TaskTokenUsageTypes.ReviewRun;
        if (participant.StartsWith("orchestrator:", StringComparison.Ordinal))
            return TaskTokenUsageTypes.Orchestration;
        return TaskTokenUsageTypes.Other;
    }

    private static bool ContainsAny(string value, params string[] candidates)
        => candidates.Any(candidate => value.Contains(candidate, StringComparison.Ordinal));
}
