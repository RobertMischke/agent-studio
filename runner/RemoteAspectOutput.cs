using System.Text.Json;

namespace AgentRunner;

/// <summary>Normalizes supported one-shot CLI envelopes to the model reply.</summary>
internal static class RemoteAspectOutput
{
    public const string SemanticAspectExecutionKind = "semantic-aspect";

    public static string Extract(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return output;
        string? agentMessage = null;
        string? result = null;

        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith('{')) continue;
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                var root = document.RootElement;
                var type = String(root, "type");
                if (string.Equals(type, "result", StringComparison.Ordinal)
                    && String(root, "result") is { } claudeResult)
                {
                    result = claudeResult;
                    continue;
                }

                if (!string.Equals(type, "item.completed", StringComparison.Ordinal)
                    || !root.TryGetProperty("item", out var item)
                    || !string.Equals(String(item, "type"), "agent_message", StringComparison.Ordinal))
                    continue;
                agentMessage = String(item, "text") ?? agentMessage;
            }
            catch (JsonException)
            {
                // Ordinary output remains a supported fallback below.
            }
        }

        return result ?? agentMessage ?? output;
    }

    private static string? String(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
