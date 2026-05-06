namespace AgentTaskboard.UpdateService;

/// <summary>
/// What the update service tells the frontend on every poll. Designed to be
/// stable across versions of the main backend so the FE banner keeps
/// working even when /api/* is unreachable mid-update.
/// </summary>
public sealed record UpdateStatus(
    string Phase,                    // idle | preparing | pausing-runners | pulling | building | restarting | resuming | done | failed
    string? Message,                 // human-readable detail for the current phase
    string? CurrentRunId,            // null if no run in flight
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string HeadLocal,                // short SHA, e.g. "9973f03"
    string? HeadOrigin,              // short SHA from last fetch, null until first fetch
    int BehindBy,                    // commits stable is behind origin/main; 0 = up to date
    DateTime? LastFetchAt,
    DateTime? LastUpdateAt,          // last time an update completed (any status)
    DateTime? LastSuccessAt,         // last time an update completed with status=ok
    bool IsRunning,                  // shorthand: Phase != idle && Phase != done && Phase != failed
    bool BackendReachable,           // last health probe of the main backend
    string Version,                  // own version string (assembly version), so the FE can spot upgrades
    string Mode                      // "manual" | "scheduled"; phase-2 will use this
);

/// <summary>
/// One historical record per completed update attempt, append-only on disk.
/// </summary>
public sealed record UpdateHistoryEntry(
    string RunId,
    DateTime StartedAt,
    DateTime? FinishedAt,
    string Status,                   // ok | failed | aborted
    string HeadBefore,
    string HeadAfter,
    int DurationSeconds,
    string? Error,
    string Trigger                   // "manual" | "scheduled" | "api"
);

public sealed record TriggerRequest(
    string? Reason,                  // optional caller-supplied note for the history
    bool Force                       // override "already running" / "no commits behind" guards
);

public sealed record TriggerResponse(string RunId, string Phase, string Message);
