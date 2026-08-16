namespace AgentStudio.Tokens;

/// <summary>
/// Pure classification policy for the per-task token popover. It uses only
/// recorded participant and topic context and leaves context-free legacy rows
/// as <see cref="Other"/> instead of guessing from the model name.
/// </summary>
public static class TaskTokenUsageType
{
    public const string Coding = "coding";
    public const string Review = "review";
    public const string Gate = "gate";
    public const string Enrichment = "enrichment";
    public const string Orchestration = "orchestration";
    public const string Supporting = "supporting";
    public const string Other = "other";

    public static string Classify(string? participantId, string? topic)
    {
        var normalizedTopic = topic?.Trim().ToLowerInvariant() ?? string.Empty;
        if (ContainsAny(normalizedTopic, "review", "aspect", "critic", "grade")) return Review;
        if (ContainsAny(normalizedTopic, "gate", "verdict", "decision")) return Gate;
        if (ContainsAny(normalizedTopic, "enrich", "intake", "prompt-selection")) return Enrichment;

        if (TokenModelDisplay.IsAgentParticipant(participantId)) return Coding;
        if (TokenModelDisplay.IsOrchestratorParticipant(participantId)) return Orchestration;
        if (TokenModelDisplay.IsSupportingParticipant(participantId)) return Supporting;
        return Other;
    }

    private static bool ContainsAny(string value, params string[] candidates)
        => candidates.Any(value.Contains);
}
