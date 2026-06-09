namespace OrchestratorApi.Models;

public record WatchPathEntry
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string RootPath { get; init; } = "";
    public string RepositoryPath { get; init; } = "";
}

public record CliExecution
{
    public string JobId { get; init; } = "";
    public string TaskKey { get; init; } = "";
    public int ProcessId { get; init; }
    public DateTime StartedAt { get; init; }
    public string Status { get; init; } = "";      // running | completed | failed | cancelled
    public int? ExitCode { get; init; }
    public double? DurationSeconds { get; init; }
    public string? Model { get; init; }
    public string? ThinkingLevel { get; init; }
    /// <summary>
    /// Canonical terminal run outcome once known: success, failed, noop,
    /// blocked, needs-input, interrupted, or unknown. Null while running and
    /// on legacy in-memory records.
    /// </summary>
    public string? RunOutcome { get; init; }
}

public static class TaskIdentity
{
    public static string CreateKey(string watchPath, string jobId) => $"{watchPath}::{jobId}";
}

public record RunnerStatus
{
    public Dictionary<string, ProjectRunnerStatus> Projects { get; init; } = new();
}

public record ProjectRunnerStatus
{
    public string ProjectName { get; init; } = "";
    public string Mode { get; init; } = "manual";
    public string? ActiveJobId { get; init; }
    public CliExecution? ActiveExecution { get; init; }
    public List<string> QueuedJobIds { get; init; } = [];
    /// <summary>
    /// Reason recorded the last time the runner mode changed. Mirrors the
    /// <c>reason</c> argument to <see cref="OrchestratorApi.Services.Runner.ProjectRunner.SetMode"/>;
    /// surfaces so the board can distinguish operator-initiated
    /// <c>manual</c> / <c>paused</c> transitions ("api-toggle", "api: POST /api/runner/{project}/stop")
    /// from system-initiated ones ("auto-failure circuit-breaker", "capture-fail circuit-breaker",
    /// "cross-slug infra circuit-breaker", "supervisor pause") in the lane pill chip.
    /// </summary>
    public string? ModeReason { get; init; }
    /// <summary>
    /// UTC timestamp when the mode last changed. Null on legacy in-memory
    /// records (before the backend started recording it).
    /// </summary>
    public DateTime? ModeChangedAt { get; init; }
    /// <summary>
    /// Coarse classification of where the current mode came from. One of
    /// <c>user</c>, <c>circuit-breaker</c>, <c>supervisor</c>, <c>system</c>.
    /// Derived from <see cref="ModeReason"/> at SetMode time so the frontend
    /// does not have to re-implement the heuristic on every render.
    /// </summary>
    public string? ModeSource { get; init; }
    /// <summary>
    /// Current automatic breaker state. Null when no breaker is active;
    /// <c>cooldown</c> when the global auto-failure safety net paused the
    /// project temporarily and will auto-resume.
    /// </summary>
    public string? BreakerState { get; init; }
    /// <summary>
    /// UTC instant when the global breaker cooldown expires and the runner may
    /// restore <c>auto-continuous</c>.
    /// </summary>
    public DateTime? BreakerCooldownUntil { get; init; }
    /// <summary>
    /// Human-readable reason for the active global breaker cooldown.
    /// </summary>
    public string? BreakerReason { get; init; }
    /// <summary>
    /// Number of global breaker trips since this runner instance started. Used
    /// to explain exponential cooldown backoff.
    /// </summary>
    public int BreakerTripCount { get; init; }
    /// <summary>
    /// Backend role assigned via <c>Runner:Role</c> config; one of
    /// <c>orchestrator</c> (the default — picks tasks automatically when mode is
    /// <c>auto-*</c>) or <c>test-subject</c> (pickup loop is structurally
    /// disabled — only explicit start endpoints can drive a job). The dev
    /// backend ships as <c>test-subject</c> so a shared workspace does not
    /// produce a parallel pickup race against stable.
    /// </summary>
    public string Role { get; init; } = "orchestrator";
    /// <summary>
    /// Mode the operator asked for while a job was still running. Non-null only
    /// when a <c>PUT /api/runner/{project}/mode</c> with <c>manual</c> /
    /// <c>paused</c> arrived while <see cref="ActiveJobId"/> was set. The
    /// runner applies the value the moment the active job clears; the frontend
    /// renders the lane pill as "MANUAL (after current)" while this field is
    /// populated.
    /// </summary>
    public string? PendingMode { get; init; }
    /// <summary>
    /// Job id the deferred mode change is waiting on. Mirrors
    /// <see cref="ActiveJobId"/> at the moment the deferred change was recorded
    /// so the UI can render "after &lt;slug&gt;" in the tooltip.
    /// </summary>
    public string? PendingModeWillApplyAfter { get; init; }
    /// <summary>
    /// ADR-0052: the project's configured concurrency cap (clamped to
    /// <c>&gt;= 1</c>). <c>1</c> is the sequential default. Surfaced so the
    /// project view can render slot occupancy ("<see cref="OccupiedSlots"/> /
    /// MaxParallelism") next to the lane pill.
    /// </summary>
    public int MaxParallelism { get; init; } = 1;
    /// <summary>
    /// ADR-0052: how many of the <see cref="MaxParallelism"/> slots are
    /// currently filled by a running task. <c>0</c> when idle, <c>1</c> while a
    /// task is active under the sequential model.
    /// </summary>
    public int OccupiedSlots { get; init; }
    /// <summary>
    /// ADR-0052: the most recent pick-gate rationale
    /// (<see cref="OrchestratorApi.Services.Runner.SlotAdmission.Reason"/>) the
    /// runner recorded when it admitted a task into a slot. Mirrors the
    /// <c>runner_slot_admission</c> timeline event so the UI can show "why this
    /// task was picked" without re-deriving it.
    /// </summary>
    public string? LastPickReason { get; init; }
}

public record CliOutputLine
{
    public DateTime Timestamp { get; init; }
    public string Stream { get; init; } = "stdout";  // stdout | stderr
    public string Text { get; init; } = "";
}
