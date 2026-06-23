namespace AgentStudio.Shared;

// CliPermissionModes now comes from the CodingAgentRunner package (aliased in the csproj).

/// <summary>Where an effective CLI permission mode came from.</summary>
public static class CliPermissionSources
{
    /// <summary>An explicit per-project override (<see cref="ProjectSettings.CliModes"/>).</summary>
    public const string Project = "project";

    /// <summary>Detected from the CLI's own global config file on disk.</summary>
    public const string Global = "global";

    /// <summary>No project override and no detected global config — the platform default (YOLO).</summary>
    public const string Default = "default";
}

/// <summary>
/// The resolved effective permission mode for one CLI in one project, plus
/// where it came from. Returned by the effective-mode probe and the per-project
/// cli-modes endpoint.
/// </summary>
public record CliPermissionResolution
{
    public string CliType { get; init; } = "";
    public string Mode { get; init; } = CliPermissionModes.Yolo;
    /// <summary>One of <see cref="CliPermissionSources"/>.</summary>
    public string Source { get; init; } = CliPermissionSources.Default;
    /// <summary>The concrete CLI flags this mode renders (for display / verification).</summary>
    public IReadOnlyList<string> Args { get; init; } = [];
}

// CliPermissionFlags now comes from the CodingAgentRunner package (aliased in the csproj).
