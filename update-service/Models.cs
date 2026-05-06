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
    IReadOnlyList<CommitInfo> PendingCommits, // up to 50 commits HEAD..origin/main, newest first
    DateTime? LastFetchAt,
    DateTime? LastUpdateAt,          // last time an update completed (any status)
    DateTime? LastSuccessAt,         // last time an update completed with status=ok
    bool IsRunning,                  // shorthand: Phase != idle && Phase != done && Phase != failed
    bool BackendReachable,           // last health probe of the main backend
    string ServiceVersion,           // UpdateService assembly version (was Version)
    string ProductVersion,           // semver from the VERSION file at repo root
    string Mode                      // "manual" | "scheduled"; phase-2 will use this
);

/// <summary>
/// One git commit summary, used for "what's new in this update" lists.
/// </summary>
public sealed record CommitInfo(
    string Sha,                      // short SHA
    string Subject,                  // first line of the commit message
    string Author,                   // author name
    DateTime AuthorDate              // author date in UTC
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
