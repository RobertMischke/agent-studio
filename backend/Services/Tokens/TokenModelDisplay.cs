using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Tokens;

internal static class TokenModelDisplay
{
    public static string? Label(string? modelId)
    {
        var id = ModelMetadataRegistry.NormalizeId(modelId);
        if (string.IsNullOrWhiteSpace(id)) return null;
        return ModelMetadataRegistry.Find(id)?.Label ?? id;
    }

    public static bool IsAgentParticipant(string? participantId)
        => HasPrefix(participantId, "agent:");

    public static bool IsSupportingParticipant(string? participantId)
        => HasPrefix(participantId, "support:");

    public static bool IsOrchestratorParticipant(string? participantId)
        => HasPrefix(participantId, "orchestrator:");

    private static bool HasPrefix(string? value, string prefix)
        => !string.IsNullOrWhiteSpace(value)
           && value.Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
}
