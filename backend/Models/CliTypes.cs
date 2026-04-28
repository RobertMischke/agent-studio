namespace OrchestratorApi.Models;

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
/// <see cref="JobInfo.UseOwnSession"/>.
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
}

/// <summary>Per-CLI section of the global usage report shown in the right side-sheet.</summary>
public record CliUsageSection
{
    public string CliType { get; init; } = "";
    public bool Available { get; init; }
    public string? Version { get; init; }
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
