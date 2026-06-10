namespace AgentStudio.Shared;

/// <summary>
/// Whether a run sees a <b>clean</b> (isolated, freshly seeded) persistent CLI
/// state or the operator's <b>shared</b> global state (T1b / ASS-1742).
/// <para>
/// <b>CLEAN is the default for coding runs</b>: the agent sees only the prompt
/// plus the versioned repo files, so a run is reproducible and free of leftover
/// session history, accumulated memory, or one-off settings from earlier runs.
/// "clean" is <i>not</i> a CLI flag — each adapter implements it via the CLI's
/// own home / config-dir env var (Claude <c>CLAUDE_CONFIG_DIR</c>, Codex
/// <c>CODEX_HOME</c>), seeding only the auth + base config into a per-run temp
/// directory that is torn down when the run's tracking entry is evicted. CLIs
/// that expose no such redirect are honestly <see cref="SupportsClean"/> ==
/// false and always run shared (visible in the T1a execution-context panel).
/// </para>
/// <para>
/// Repo instruction files (<c>AGENTS.md</c> / <c>CLAUDE.md</c> in the working
/// tree) stay active in <b>both</b> modes — they are loaded from the checkout,
/// not from the CLI home, so clean mode never hides them.
/// </para>
/// </summary>
public static class CliContextModes
{
    /// <summary>Isolated per-run persistent state: only prompt + versioned repo files. Default.</summary>
    public const string Clean = "clean";

    /// <summary>The operator's shared global CLI state (session history, memory, settings).</summary>
    public const string Shared = "shared";

    public static readonly string[] All = [Clean, Shared];

    /// <summary>Modes surfaced in the project-settings dropdown (all of them today).</summary>
    public static readonly string[] UserVisible = All;

    public static bool IsValid(string? mode)
        => !string.IsNullOrWhiteSpace(mode)
           && All.Contains(mode, StringComparer.OrdinalIgnoreCase);

    /// <summary>Canonicalize a mode id; unknown / empty values fall back to <see cref="Clean"/>.</summary>
    public static string Normalize(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return Clean;
        var v = mode.Trim();
        foreach (var m in All)
            if (string.Equals(m, v, StringComparison.OrdinalIgnoreCase))
                return m;
        return Clean;
    }

    /// <summary>Short human label for the UI / panel.</summary>
    public static string DisplayName(string? mode) => Normalize(mode) switch
    {
        Shared => "Shared",
        _ => "Clean",
    };

    /// <summary>
    /// Whether the adapter for <paramref name="cliType"/> can actually isolate
    /// persistent state for a clean run. Claude (<c>CLAUDE_CONFIG_DIR</c>) and
    /// Codex (<c>CODEX_HOME</c>) redirect their whole config home to a per-run
    /// temp dir; Copilot and Gemini expose no such redirect, so they are
    /// shared-only — a clean selection is honoured as a no-op (still shared) and
    /// the panel shows no temp paths. This is the single source of truth shared
    /// by the adapters' <c>SupportsCleanContext</c> and the settings UI so they
    /// can never disagree.
    /// </summary>
    public static bool SupportsClean(string? cliType) => CliTypes.Normalize(cliType) switch
    {
        CliTypes.Claude => true,
        CliTypes.Codex => true,
        _ => false,
    };
}

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
