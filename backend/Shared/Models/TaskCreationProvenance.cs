using System.Text.Json.Serialization;

namespace AgentStudio.Shared;

/// <summary>
/// Server-authored provenance for a task created as part of a larger goal.
/// This is separate from <see cref="TaskProvenance"/>, which records Git and
/// lane-transition facts after execution. Legacy and directly user-created
/// tasks have no creation provenance.
/// </summary>
public sealed record TaskCreationProvenance
{
    /// <summary>Actor that initiated creation, currently orchestrator or operator.</summary>
    [JsonPropertyName("initiator")]
    public string Initiator { get; init; } = TaskCreationInitiators.Operator;

    /// <summary>Creation workflow, currently goal-decomposition.</summary>
    [JsonPropertyName("method")]
    public string Method { get; init; } = TaskCreationMethods.GoalDecomposition;

    /// <summary>Owning goal container id, normally the parent epic id.</summary>
    [JsonPropertyName("goalId")]
    public string GoalId { get; init; } = "";

    /// <summary>Stable display key of the goal when one is available.</summary>
    [JsonPropertyName("goalKey")]
    public string? GoalKey { get; init; }

    /// <summary>Orchestrator context that planned the work.</summary>
    [JsonPropertyName("contextKey")]
    public string? ContextKey { get; init; }

    /// <summary>Whether the card delivers the goal or independently verifies it.</summary>
    [JsonPropertyName("purpose")]
    public string Purpose { get; init; } = GoalTaskPurposes.Delivery;

    /// <summary>UTC time at which the server materialized the card.</summary>
    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

public static class TaskCreationInitiators
{
    public const string Orchestrator = "orchestrator";
    public const string Operator = "operator";

    public static string Normalize(string? value) =>
        string.Equals(value, Orchestrator, StringComparison.OrdinalIgnoreCase)
            ? Orchestrator
            : Operator;
}

public static class TaskCreationMethods
{
    public const string GoalDecomposition = "goal-decomposition";
}

public static class GoalTaskPurposes
{
    public const string Delivery = "delivery";
    public const string Verification = "verification";

    public static string Normalize(string? value) =>
        string.Equals(value, Verification, StringComparison.OrdinalIgnoreCase)
            ? Verification
            : Delivery;
}
