namespace AgentStudio.Shared;

// CliContextModes now comes from the CodingAgentRunner package (aliased in the csproj).

/// <summary>Where an effective CLI context mode came from.</summary>
public static class CliContextModeSources
{
    /// <summary>An explicit per-task override (<see cref="TaskInfo.ContextMode"/>).</summary>
    public const string Task = "task";

    /// <summary>A per-project override (<see cref="ProjectSettings.CliContextModes"/>).</summary>
    public const string Project = "project";

    /// <summary>No task or project override — the platform default (CLEAN).</summary>
    public const string Default = "default";
}

/// <summary>
/// The resolved effective context mode for one CLI in one project / task, plus
/// where it came from and whether the CLI can actually honour clean isolation.
/// Returned by the per-project cli-context-modes endpoint and the runner's
/// spawn-time resolution.
/// </summary>
public record CliContextModeResolution
{
    public string CliType { get; init; } = "";
    public string Mode { get; init; } = CliContextModes.Clean;
    /// <summary>One of <see cref="CliContextModeSources"/>.</summary>
    public string Source { get; init; } = CliContextModeSources.Default;
    /// <summary>
    /// Whether the CLI can isolate persistent state (<see cref="CliContextModes.SupportsClean"/>).
    /// When false a <see cref="CliContextModes.Clean"/> mode still runs shared.
    /// </summary>
    public bool Supported { get; init; }
}
