namespace AgentStudio.WindowsTunnelSupervision;

/// <summary>
/// Registration and liveness of one Windows Scheduled Task, read without
/// elevation via <c>schtasks /Query</c>. Registration itself needs an
/// elevated session; querying does not.
/// </summary>
public enum ScheduledTaskPresence
{
    /// <summary>The Task Server process is not running on Windows.</summary>
    NotApplicable,
    NotRegistered,
    Registered,
    Running,
    Disabled,
    Unknown,
}

public sealed record ScheduledTaskStatus(
    string TaskName,
    ScheduledTaskPresence Presence,
    string? LastRunResult,
    string? LastRunAt);

/// <summary>
/// Response contract for <c>GET /api/v1/windows-tunnel-supervision/status</c>.
/// Mirrors the two scheduled tasks registered by
/// <c>deploy/windows/agent-runner-tunnel/install-tunnel-supervision.ps1</c>:
/// the tunnel keeper and its watchdog.
/// </summary>
public sealed record WindowsTunnelSupervisionStatus(
    bool IsWindowsHost,
    ScheduledTaskStatus Keeper,
    ScheduledTaskStatus Watchdog,
    string? LastHealAt,
    string? LastHealDetail,
    int ConsecutiveHealFailures,
    string Detail);
