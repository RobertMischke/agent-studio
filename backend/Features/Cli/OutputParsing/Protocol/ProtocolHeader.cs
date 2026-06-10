using System.Text.Json.Serialization;

namespace AgentStudio.Cli;

/// <summary>
/// In-memory shape of the structured header carried at the top of a
/// job's <c>status.md</c>. Mirrors
/// <c>docs/schemas/protocol-header.schema.json</c>. Every field except
/// <see cref="Phase"/> and <see cref="Summary"/> is optional, matching
/// the schema's tolerance contract.
/// </summary>
public sealed record ProtocolHeader(
    [property: JsonPropertyName("phase")] ProtocolPhase Phase,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("nextAction")] string? NextAction = null,
    [property: JsonPropertyName("decisionsOpen")] int DecisionsOpen = 0,
    [property: JsonPropertyName("lastDecisionAt")] DateTime? LastDecisionAt = null,
    [property: JsonPropertyName("correlationId")] string? CorrelationId = null,
    [property: JsonPropertyName("agent")] string? Agent = null,
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("runs")] int? Runs = null,
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion = "1");

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProtocolPhase
{
    Analysis,
    Plan,
    Implementing,
    Testing,
    Review,
    Blocked,
    Done,
}

/// <summary>
/// Serialised lowercase phase values matching the schema enum. Use
/// <see cref="ProtocolPhases.Parse"/> to read; the C# enum values are
/// PascalCase by convention but the wire form is lowercase.
/// </summary>
public static class ProtocolPhases
{
    public static string ToWire(ProtocolPhase phase) => phase switch
    {
        ProtocolPhase.Analysis => "analysis",
        ProtocolPhase.Plan => "plan",
        ProtocolPhase.Implementing => "implementing",
        ProtocolPhase.Testing => "testing",
        ProtocolPhase.Review => "review",
        ProtocolPhase.Blocked => "blocked",
        ProtocolPhase.Done => "done",
        _ => "analysis",
    };

    public static bool TryParse(string? value, out ProtocolPhase phase)
    {
        phase = ProtocolPhase.Analysis;
        if (string.IsNullOrWhiteSpace(value)) return false;
        switch (value.Trim().ToLowerInvariant())
        {
            case "analysis": phase = ProtocolPhase.Analysis; return true;
            case "plan": phase = ProtocolPhase.Plan; return true;
            case "implementing": phase = ProtocolPhase.Implementing; return true;
            case "testing": phase = ProtocolPhase.Testing; return true;
            case "review": phase = ProtocolPhase.Review; return true;
            case "blocked": phase = ProtocolPhase.Blocked; return true;
            case "done": phase = ProtocolPhase.Done; return true;
            default: return false;
        }
    }
}
