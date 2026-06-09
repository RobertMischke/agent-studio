namespace OrchestratorApi.Models;

/// <summary>
/// "Agent claimed done but isn't" verdicts emitted by the completed-lane
/// audit (Part 3 of the consolidation/audit task). The orchestrator
/// reopens cards in <see cref="OrchestratorApi.Models.TaskStates.Completed"/>
/// + <see cref="OrchestratorApi.Models.TaskStates.Archive"/>, runs cheap
/// heuristics against the prompt/status/commits, and flips
/// <see cref="NotReallyDone"/> verdicts back into
/// <see cref="OrchestratorApi.Models.TaskStates.Ready"/> with a
/// <c>quality_loop_reopened</c> timeline event (the retired
/// 1b-needs-human-review lane previously held these reopened cards).
/// </summary>
public static class AuditVerdicts
{
    /// <summary>Heuristics agree the card is genuinely done; stays in completed.</summary>
    public const string Ok = "ok";
    /// <summary>At least one acceptance claim cannot be verified; reopened.</summary>
    public const string NotReallyDone = "not-really-done";
    /// <summary>Heuristics could not decide; card stays in completed but gains a triage tag.</summary>
    public const string Inconclusive = "inconclusive";

    public static readonly string[] All = [Ok, NotReallyDone, Inconclusive];
}

public record ReEvaluateResponse
{
    public string JobId { get; init; } = "";
    public string Verdict { get; init; } = "";
    public string NewState { get; init; } = "";
    public List<EvidenceDiagnostic> Diagnostics { get; init; } = [];
}

public record EvidenceDiagnostic
{
    /// <summary>One of <c>prompt-asks-commit</c>, <c>missing-commit</c>, <c>claimed-file-missing</c>, etc.</summary>
    public string Kind { get; init; } = "";
    public string Detail { get; init; } = "";
    /// <summary>One of <see cref="AuditSignalLevels"/> values.</summary>
    public string Level { get; init; } = AuditSignalLevels.Info;
}

public static class AuditSignalLevels
{
    /// <summary>Background context; never on its own changes the verdict.</summary>
    public const string Info = "info";
    /// <summary>Soft warning; multiple warnings push to inconclusive.</summary>
    public const string Warn = "warn";
    /// <summary>Hard problem; forces <see cref="AuditVerdicts.NotReallyDone"/>.</summary>
    public const string Fail = "fail";
}

public record CompletedLaneAuditRunStatus
{
    public string RunId { get; init; } = "";
    public string ProjectId { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public string WatchPath { get; init; } = "";
    public DateTime StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
    public int Total { get; init; }
    public int Processed { get; init; }
    public int TrulyDone { get; init; }
    public int NotReallyDone { get; init; }
    public int Inconclusive { get; init; }
    /// <summary>One of <c>running</c>, <c>finished</c>, <c>failed</c>.</summary>
    public string Status { get; init; } = "running";
    public string? Error { get; init; }
    public List<CompletedLaneAuditEntry> Entries { get; init; } = [];
}

public record CompletedLaneAuditEntry
{
    public string JobId { get; init; } = "";
    public string? Key { get; init; }
    public string Title { get; init; } = "";
    public string Verdict { get; init; } = "";
    public string Reason { get; init; } = "";
    public DateTime EvaluatedAt { get; init; }
}

public record CompletedLaneAuditReport
{
    public string ProjectId { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public string RunId { get; init; } = "";
    public DateTime GeneratedAt { get; init; }
    public string Markdown { get; init; } = "";
}
