namespace AgentStudio.Tokens;

/// <summary>
/// Pure projection from durable bus context to the compact type groups used by
/// task token surfaces. Topics carry the most specific step meaning; the
/// participant role is the backwards-compatible fallback for older events.
/// </summary>
public static class TaskTokenUsageTypePolicy
{
    public const string CodingRun = "coding-run";
    public const string ReviewRun = "review-run";
    public const string Gate = "gate";
    public const string Enrichment = "enrichment";
    public const string Other = "other";

    public static string Resolve(string? participantId, string? topic)
    {
        var normalizedTopic = (topic ?? string.Empty).Trim().ToLowerInvariant();

        if (ContainsAny(normalizedTopic,
                "enrich", "enhance", "title", "summary", "wiki-sync", "commit-message", "prompt"))
            return Enrichment;

        if (ContainsAny(normalizedTopic,
                "gate", "decision", "verdict", "orchestrator", "steer", "completion"))
            return Gate;

        if (ContainsAny(normalizedTopic,
                "review", "aspect", "audit", "quality", "security", "drift", "analysis", "test"))
            return ReviewRun;

        if (TokenModelDisplay.IsAgentParticipant(participantId)
            || normalizedTopic.EndsWith("-turn", StringComparison.Ordinal))
            return CodingRun;

        if (TokenModelDisplay.IsOrchestratorParticipant(participantId))
            return Gate;

        if (TokenModelDisplay.IsSupportingParticipant(participantId))
            return Enrichment;

        return Other;
    }

    private static bool ContainsAny(string value, params string[] fragments)
        => fragments.Any(fragment => value.Contains(fragment, StringComparison.Ordinal));
}
