namespace AgentStudio.Shared;

/// <summary>
/// Rollout values for the local backend CLI execution engine. The process-wide
/// environment selector is shared with the standalone runner and precedes the
/// backend's persisted project and workspace tiers.
/// </summary>
public static class CliExecutionEngines
{
    public const string Car = "car";
    public const string Legacy = "legacy";
    public const string Default = Car;
    public const string EnvironmentVariable = "RUNNER_EXEC_ENGINE";

    public static readonly IReadOnlyList<string> All = [Car, Legacy];

    public static bool IsValid(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && All.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Canonicalizes a concrete engine value. Null or blank selects the
    /// platform default; an unknown non-blank value fails loud.
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Default;
        var trimmed = value.Trim();
        if (string.Equals(trimmed, Car, StringComparison.OrdinalIgnoreCase)) return Car;
        if (string.Equals(trimmed, Legacy, StringComparison.OrdinalIgnoreCase)) return Legacy;
        throw new ArgumentException($"Unsupported CLI execution engine '{value}'.", nameof(value));
    }

    /// <summary>
    /// Canonicalizes one optional project/workspace override. Blank clears the
    /// override instead of pinning the platform default explicitly.
    /// </summary>
    public static string? NormalizeOverride(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : Normalize(value);

    /// <summary>
    /// Reads the process-wide emergency rollback selector shared with the
    /// standalone runner. Blank means that the persisted settings hierarchy
    /// remains authoritative; an unknown value fails loud.
    /// </summary>
    public static string? ReadEnvironmentOverride()
        => NormalizeOverride(Environment.GetEnvironmentVariable(EnvironmentVariable));
}
