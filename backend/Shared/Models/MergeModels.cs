namespace OrchestratorApi.Models;

/// <summary>
/// Wire shapes for the consolidation/merge API. The merge endpoint folds a
/// secondary job into a primary one: the primary keeps living and moves
/// through the lanes; the secondary's history is appended to the primary's
/// <c>timeline.jsonl</c> and (for <see cref="MergeModes.Consolidate"/>) its
/// folder is archived to <c>&lt;workspace&gt;/.archive/merged/</c>.
///
/// The mutation surface is API-only: no script or operator should reach
/// past these endpoints to touch the lane folders directly. Every call
/// appends an audit row to <c>&lt;workspace&gt;/.audit/merges.jsonl</c>
/// with a one-time <c>restoreToken</c> that allows an undo within 24h.
/// </summary>
public static class MergeModes
{
    /// <summary>Default. Mirror the secondary's runs/timeline into the primary, then archive the secondary.</summary>
    public const string Consolidate = "consolidate";
    /// <summary>Same as <see cref="Consolidate"/>, but the archived secondary is marked for hard delete after the grace window.</summary>
    public const string Absorb = "absorb";
    /// <summary>Secondary stays in place but gains a <c>mergedInto</c> pointer; FE renders both as a single card.</summary>
    public const string LinkOnly = "link-only";

    public static readonly string[] All = [Consolidate, Absorb, LinkOnly];

    public static bool IsValid(string? mode) =>
        !string.IsNullOrWhiteSpace(mode) && All.Contains(mode);

    /// <summary>Number of days a <see cref="Consolidate"/> or <see cref="Absorb"/> merge may be undone.</summary>
    public const int UndoGraceDays = 1;
    /// <summary>Number of days an <see cref="Absorb"/> archive folder is kept before hard deletion.</summary>
    public const int AbsorbGraceDays = 7;
}

public record MergeRequest
{
    public string SecondaryId { get; init; } = "";
    public string? SecondaryWatchPath { get; init; }
    public string Mode { get; init; } = MergeModes.Consolidate;
    public string Reason { get; init; } = "";
}

public record MergeResponse
{
    public TaskInfo? Primary { get; init; }
    public int AbsorbedRuns { get; init; }
    public int TimelineEventsAppended { get; init; }
    public string Mode { get; init; } = "";
    public string RestoreToken { get; init; } = "";
    public DateTime UndoExpiresAt { get; init; }
    public string? ArchivedAt { get; init; }
}

public record MergePreviewResponse
{
    public string PrimaryId { get; init; } = "";
    public string SecondaryId { get; init; } = "";
    public string Mode { get; init; } = "";
    public int RunsToAbsorb { get; init; }
    public int TimelineEventsToAppend { get; init; }
    public List<TimelineEvent> ProposedTimelineEvents { get; init; } = [];
    public List<MergeConflict> Conflicts { get; init; } = [];
}

public record MergeConflict
{
    public string Kind { get; init; } = "";
    public string Description { get; init; } = "";
}

public record MergeCandidate
{
    public string Id { get; init; } = "";
    public string? Key { get; init; }
    public string Title { get; init; } = "";
    public string State { get; init; } = "";
    public string WatchPath { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public string Reason { get; init; } = "";
    public double Score { get; init; }
}

public record MergeCandidatesResponse
{
    public string PrimaryId { get; init; } = "";
    public List<MergeCandidate> Candidates { get; init; } = [];
}

public record MergeUndoRequest
{
    public string RestoreToken { get; init; } = "";
}

public record MergeUndoResponse
{
    public bool Restored { get; init; }
    public string PrimaryId { get; init; } = "";
    public string SecondaryId { get; init; } = "";
    public string Message { get; init; } = "";
}

/// <summary>
/// One row in <c>&lt;workspace&gt;/.audit/merges.jsonl</c>. The audit log
/// is workspace-API-owned (never edit by hand) and is the only durable
/// record that authorises an undo within the 24h window.
/// </summary>
public sealed record MergeAuditRecord
{
    public DateTime At { get; init; }
    public string Who { get; init; } = "";
    public string Mode { get; init; } = "";
    public string PrimaryId { get; init; } = "";
    public string PrimaryWatchPath { get; init; } = "";
    public string PrimaryFolderPath { get; init; } = "";
    public string SecondaryId { get; init; } = "";
    public string SecondaryWatchPath { get; init; } = "";
    public string SecondaryOriginalState { get; init; } = "";
    public string SecondaryOriginalFolderPath { get; init; } = "";
    public string? ArchivedFolderPath { get; init; }
    public string Reason { get; init; } = "";
    public string RestoreToken { get; init; } = "";
    public int AbsorbedRuns { get; init; }
    public int TimelineEventsAppended { get; init; }
    public DateTime? UndoneAt { get; init; }
    public string? UndoneBy { get; init; }
}
