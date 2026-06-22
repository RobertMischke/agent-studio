namespace AgentStudio.Shared;

/// <summary>
/// Values for the <c>agent</c> field in <c>job.json</c>. The field controls
/// auto-pickup eligibility: <c>"human"</c> means the job is skipped by the
/// runner and must be started manually. Every other value maps 1:1 to a CLI
/// backend and is eligible for auto-pickup.
/// </summary>
public static class AgentTypes
{
    public const string Human   = "human";
    public const string Copilot = "copilot";
    public const string Claude  = "claude";
    public const string Codex   = "codex";
    public const string Gemini  = "gemini";

    public static bool IsAutoPickupEligible(string? agent) =>
        !string.Equals(agent, Human, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Identifiers for the supported CLI backends. The string values are persisted
/// to <c>job.json</c> and used as URL segments — keep them stable.
/// </summary>
public static class CliTypes
{
    public const string Copilot = "copilot";
    public const string Claude  = "claude";
    public const string Codex   = "codex";
    public const string Gemini  = "gemini";

    /// <summary>
    /// Sentinel for "no automated CLI resolver" (e.g. a router fallback that needs
    /// a human). Mirrors <see cref="AgentTypes.Human"/>. Deliberately NOT part of
    /// <see cref="All"/>/<see cref="IsValid"/> — it is a comparison sentinel, not a
    /// selectable backend.
    /// </summary>
    public const string Human   = "human";

    public static readonly string[] All = [Copilot, Claude, Codex, Gemini];

    public static bool IsValid(string? type) =>
        !string.IsNullOrWhiteSpace(type) && All.Contains(type, StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string? type) =>
        IsValid(type) ? type!.ToLowerInvariant() : Copilot;
}

/// <summary>
/// How a project's CLI sessions are managed when a job starts.
/// <para>
/// <c>reuse-project</c>: one persistent session per <c>(project, cliType)</c> tuple,
/// reused across jobs so the model keeps cache/context. A job can opt out via
/// <see cref="TaskInfo.UseOwnSession"/>.
/// </para>
/// <para><c>per-job</c>: every job gets its own session (current default behavior pre-refactor).</para>
/// </summary>
public static class SessionModes
{
    public const string ReuseProject = "reuse-project";
    public const string PerJob       = "per-job";

    public static readonly string[] All = [ReuseProject, PerJob];

    public static bool IsValid(string? mode) =>
        !string.IsNullOrWhiteSpace(mode) && All.Contains(mode, StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string? mode) =>
        IsValid(mode) ? mode!.ToLowerInvariant() : ReuseProject;
}

/// <summary>One known session belonging to a CLI for a given project (cwd).</summary>
public record CliSessionInfo
{
    /// <summary>CLI-native session identifier (Copilot name, Claude UUID/name, Codex UUID).</summary>
    public string Id { get; init; } = "";
    /// <summary>Display label — for Codex this is the auto-derived thread name.</summary>
    public string? Label { get; init; }
    /// <summary>Last-modified timestamp from the on-disk session record.</summary>
    public DateTime? UpdatedAt { get; init; }
    /// <summary>Working directory the session was last invoked in (when known).</summary>
    public string? Cwd { get; init; }
    /// <summary>Best-effort token / cost summary (may be null).</summary>
    public SessionUsage? LastUsage { get; init; }
    /// <summary>True when this is the project's persistent reuse session.</summary>
    public bool IsProjectDefault { get; init; }
    /// <summary>
    /// Owning task when this session id appears in a job's
    /// <c>SessionChain</c>. Null for orphan sessions (ad-hoc CLI use,
    /// pre-orchestrator sessions, or sessions from a different checkout).
    /// Surfaced as a chip on the row that jumps to the task detail.
    /// </summary>
    public LinkedJobRef? LinkedJob { get; init; }
}

/// <summary>
/// Back-reference from a CLI session row to the kanban task that owns it.
/// Embedded on <see cref="CliSessionInfo.LinkedJob"/>. Lean by design: the
/// frontend only needs the id, the title for the tooltip, the lane for the
/// chip colour rule, the watch path to route the click, and a boolean
/// "currently active" flag so the chip can render green when the runner is
/// live on this exact session.
/// </summary>
public record LinkedJobRef
{
    public string JobId { get; init; } = "";
    public string Title { get; init; } = "";
    public string WatchPath { get; init; } = "";
    public string ProjectName { get; init; } = "";
    /// <summary>One of <see cref="TaskStates"/>. Drives the chip colour rule.</summary>
    public string Lane { get; init; } = "";
    /// <summary>
    /// True when the owning job is in <see cref="TaskStates.Progress"/> AND
    /// the project's runner reports it as the active job. The chip renders
    /// green in this state and neutral in every other state.
    /// </summary>
    public bool IsActive { get; init; }
}

/// <summary>Per-CLI section of the global usage report shown in the right side-sheet.</summary>
public record CliUsageSection
{
    public string CliType { get; init; } = "";
    public bool Available { get; init; }
    public string? Version { get; init; }
    /// <summary>Resolved executable path used by the backend for this CLI.</summary>
    public string? Path { get; init; }
    public string? Error { get; init; }
    /// <summary>Sessions grouped by project (cwd or project name).</summary>
    public List<CliUsageProjectGroup> Projects { get; init; } = [];
}

public record CliUsageProjectGroup
{
    public string ProjectName { get; init; } = "";
    public string? RootPath { get; init; }
    public List<CliSessionInfo> Sessions { get; init; } = [];
}

public record CliUsageReport
{
    public DateTime At { get; init; } = DateTime.UtcNow;
    public List<CliUsageSection> Sections { get; init; } = [];
}
