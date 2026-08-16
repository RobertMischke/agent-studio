namespace AgentStudio.Runner;

/// <summary>
/// Classifies token events from their durable bus participant and topic.
/// Topic checks precede participant checks because a supporting or remote
/// executor can run a review, gate, or enrichment step.
/// </summary>
public static class TokenUsageTypeClassifier
{
    private static readonly string[] ReviewMarkers =
    [
        "review", "aspect", "grade", "audit", "drift", "alignment",
        "code-quality", "requirement-fit", "documentation-impact", "tests-and-evidence",
    ];

    private static readonly string[] GateMarkers =
    [
        "gate", "verification", "build-test", "test-gate",
    ];

    private static readonly string[] EnrichmentMarkers =
    [
        "enrich", "enhance", "prompt", "title", "summary", "wiki",
        "clarification", "skill-readiness",
    ];

    public static string Classify(string? participantId, string? topic)
    {
        var normalizedTopic = topic?.Trim().ToLowerInvariant() ?? string.Empty;
        if (ContainsAny(normalizedTopic, GateMarkers)) return TokenUsageTypes.Gate;
        if (ContainsAny(normalizedTopic, ReviewMarkers)) return TokenUsageTypes.Review;
        if (ContainsAny(normalizedTopic, EnrichmentMarkers)) return TokenUsageTypes.Enrichment;

        var participant = participantId?.Trim() ?? string.Empty;
        if (participant.StartsWith("agent:", StringComparison.OrdinalIgnoreCase))
            return TokenUsageTypes.Coding;
        if (participant.StartsWith("orchestrator:", StringComparison.OrdinalIgnoreCase))
            return TokenUsageTypes.Orchestration;
        if (participant.StartsWith("support:", StringComparison.OrdinalIgnoreCase))
            return TokenUsageTypes.Supporting;
        return TokenUsageTypes.Other;
    }

    private static bool ContainsAny(string value, IReadOnlyList<string> markers)
        => markers.Any(marker => value.Contains(marker, StringComparison.Ordinal));
}
