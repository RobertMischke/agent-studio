namespace OrchestratorApi.Models;

public record TaskInfo
{
    public string Id { get; init; } = "";
    public string TaskKey { get; init; } = "";
    /// <summary>
    /// Stable Linear-style reference key minted from the project's prefix
    /// plus a monotonic counter (<c>ATP-130</c>, <c>RB-42</c>). Unique
    /// within one project; persisted as the <c>"key"</c> field in
    /// <c>job.json</c> and assigned at create time or by the F33 boot
    /// migration. Null when the job pre-dates the migration on a project
    /// that has not yet been swept; the UI falls back to <see cref="Id"/>
    /// in that window.
    /// </summary>
    public string? Key { get; init; }
    public string Title { get; init; } = "";
    public string State { get; init; } = "draft";
    public int Order { get; init; } = 999;
    public string Agent { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public string WatchPath { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public string FolderPath { get; init; } = "";
    public DateTime LastActivity { get; init; }
    /// <summary>
    /// Wall-clock UTC instant the task most recently entered its current lane.
    /// Stamped at create time and on every lane move. Legacy tasks written
    /// before this field existed fall back to <see cref="LastActivity"/> at
    /// scan time, so this is never default(DateTime) for a scanned task.
    /// Drives the <c>lane-entry</c> sort (newest entry on top).
    /// </summary>
    public DateTime EnteredLaneAt { get; init; }
    /// <summary>
    /// Per-job orchestrator token totals, surfaced on the kanban card as a
    /// small "token bubble". Populated at endpoint-read time from the
    /// project's <c>orchestrator.jsonl</c>, filtered by this job's id; null
    /// when the job has had no orchestrator LLM activity yet.
    /// </summary>
    public TaskTokenSummary? TokenSummary { get; init; }
    /// <summary>Name passed to Copilot CLI via <c>--name</c> on first start; reused with <c>--resume</c> for follow-ups.</summary>
    public string? SessionName { get; init; }
    /// <summary>Preferred model for this job (e.g. <c>claude-sonnet-4.5</c>); passed via <c>--model</c> when supported.</summary>
    public string? Model { get; init; }
    /// <summary>Optional thinking / reasoning effort level for the selected CLI model.</summary>
    public string? ThinkingLevel { get; init; }
    /// <summary>Which CLI backend executes this job: <c>copilot</c>, <c>claude</c>, or <c>codex</c>. Defaults to <c>copilot</c>.</summary>
    public string? CliType { get; init; }
    /// <summary>
    /// Card kind: <c>task</c> (default, a runnable unit of work) or <c>epic</c>
    /// (a container grouping sub-tasks under one overarching goal). An epic is
    /// not code-executed itself; only its sub-tasks run through the pipeline.
    /// </summary>
    public string Kind { get; init; } = TaskKinds.Task;
    /// <summary>
    /// Parent epic id when this task is a sub-task of an epic, else null. Set at
    /// create time, by the post-hoc assign endpoint, or by an epic's
    /// decomposition run.
    /// </summary>
    public string? EpicId { get; init; }
    /// <summary>
    /// Execution mode (orthogonal to <see cref="Kind"/>): <c>coding</c> (default,
    /// mutates source) | <c>planning</c> | <c>research</c> (read-only, produce a
    /// report). See <see cref="TaskModes"/>.
    /// </summary>
    public string Mode { get; init; } = TaskModes.Coding;
    /// <summary>
    /// Whether the agent may use web search / fetch for this run. Default off for
    /// coding/planning, on for research (set at create time). See decision 2 in
    /// docs/research/planning-research-task-kinds-2026-05.md.
    /// </summary>
    public bool AllowWebAccess { get; init; }
    /// <summary>
    /// When <c>true</c>, this job uses its own dedicated session even if the project runner is
    /// configured for <see cref="SessionModes.ReuseProject"/>. Lets a one-off task isolate its
    /// context from the long-running project session.
    /// </summary>
    public bool? UseOwnSession { get; init; }
    /// <summary>Last token / cost summary parsed from CLI output (best-effort).</summary>
    public SessionUsage? LastUsage { get; init; }
    public CliExecution? Execution { get; init; }
    /// <summary>
    /// Auto-commit produced on the progress→review transition; null when no commit recorded.
    /// Kept for backwards compatibility - when <see cref="Commits"/> is non-empty this
    /// mirrors its last (newest) entry. Read paths should prefer <see cref="Commits"/>;
    /// legacy <c>job.json</c> files that only carry a singular <c>commit</c> object are
    /// migrated on the fly by the scanner so consumers can rely on either field.
    /// </summary>
    public TaskCommitInfo? Commit { get; init; }
    /// <summary>
    /// Ordered chain of commits attributed to this task across iterations
    /// (oldest -&gt; newest). Tasks regularly produce more than one commit:
    /// continue-mode adds a new commit on top of the original, crash-recovery
    /// leaves a recovery commit plus a follow-up, operator-driven steers
    /// often produce a separate commit. Backwards compatible with the
    /// singular <see cref="Commit"/> field: legacy <c>job.json</c> files
    /// without <c>commits</c> are surfaced as <c>[commit]</c> by the scanner.
    /// </summary>
    public List<TaskCommitInfo> Commits { get; init; } = [];
    /// Client identity that owns this job. References
    /// <see cref="ClientIdentity.Id"/>. Defaults to
    /// <see cref="DefaultClientIdentity.Id"/> for legacy jobs whose
    /// <c>job.json</c> predates per-task attribution; the scanner
    /// rewrites the file with that value on first read so the field
    /// is non-null after migration.
    /// </summary>
    public string OwnerClientId { get; init; } = DefaultClientIdentity.Id;
    /// <summary>
    /// Count of commits attributed to this job. Derived strictly from
    /// <see cref="Commits"/> so it can never drift from the chain it is
    /// meant to summarize (ADR "Commit-Attribution-Regel"; the historic
    /// bug was a separately-computed session-events hint that read
    /// <c>commitCount: 1</c> while <c>commits: []</c>). <see cref="Commits"/>
    /// is the single source of truth; this is a convenience projection for
    /// the kanban card. Never persisted to <c>job.json</c>.
    /// </summary>
    public int CommitCount => Commits.Count;
    /// <summary>
    /// True when at least one run moved repo HEAD - a non-trivial
    /// <c>before..after</c> SHA range in <c>session-events.jsonl</c> - or an
    /// auto-commit was stamped. Derived cheaply by the scanner; never
    /// persisted. Lets the UI distinguish an analysis-only task (no activity
    /// -&gt; "no code changes") from a task where code landed but the
    /// attribution chain is still empty (activity present yet
    /// <see cref="Commits"/> is empty
    /// -&gt; "commit discovery pending / failed"). Without this signal the two
    /// -&gt; "commit discovery pending / failed"). Without this signal the two
    /// cases are indistinguishable, which is exactly bug (3) from the task.
    /// </summary>
    public bool CodeActivityDetected { get; init; }
    /// <summary>
    /// Ordered history of CLI session ids used by this job (oldest → newest). Each
    /// successful resume of a forking CLI (Claude / Codex / Gemini) appends a new
    /// id. <see cref="SessionName"/> is always the chain's last entry.
    /// A recovery-continue (session lost, reconstructed from job folder) breaks
    /// the chain; the next captured id will start a new logical chain segment.
    /// </summary>
    public List<string> SessionChain { get; init; } = [];
    /// <summary>
    /// Saved user intent waiting for the auto-pickup loop to run. Populated
    /// when the user sends a follow-up to a job that is not the project's
    /// current active job; cleared once the runner consumes it. See
    /// <see cref="PendingIntent"/>.
    /// </summary>
    public PendingIntent? PendingIntent { get; init; }

    /// <summary>
    /// Snapshot of the auto-mode "stuck loop" counter for this job, populated
    /// at endpoint-read time from the in-memory state on the project's
    /// runner. Null when no loop is in flight (the common case). When set,
    /// the UI surfaces a "auto-loop N/M" badge so the user can see how
    /// many orchestrator decisions have been spent on this job before the
    /// circuit breaker stops the loop.
    /// </summary>
    public AutoLoopSnapshot? AutoLoop { get; init; }

    /// <summary>
    /// Live summary-generation state for jobs in 4-review. Set when the
    /// post-completion Haiku summarizer is currently running for this
    /// job; the UI shows an "auto-reviewing" pill so the user can see
    /// that the orchestrator is still working on the card after it
    /// landed in review, instead of treating an empty status.md as a
    /// dead card.
    /// </summary>
    public TaskSummaryState? SummaryState { get; init; }

    /// <summary>
    /// Latest runner-outcome issue found in <c>logs/cli-output.log</c>.
    /// Derived at read time from orchestrator log lines, not stored in
    /// <c>job.json</c>. The UI uses this to surface permission blocks,
    /// watchdog timeouts, missing terminal sentinels, and classifier
    /// ambiguity directly on the card and protocol header.
    /// </summary>
    public TaskOutcomeIssue? OutcomeIssue { get; init; }

    /// <summary>
    /// Latest orchestrator-review verdict for this job, populated at
    /// endpoint-read time from the per-project decision journal at
    /// <c>{workspace}/logs/decisions/{project}.jsonl</c>. Drives the
    /// 4-review kanban swim-lane subdivision (orchestrator-review vs
    /// human-review) and the workspace top-banner. Values: <c>pending</c>
    /// (unresolved sentinel, not yet acted on), <c>reissue</c>,
    /// <c>escalate</c>, <c>accept</c>. Null when no orchestrator
    /// decision exists, e.g. a 4-review card that completed cleanly with
    /// <c>[[TASK_DONE]]</c> awaiting human accept.
    /// </summary>
    public string? OrchestratorVerdict { get; init; }

    /// <summary>
    /// True when this job was created by an E2E spec / Playwright fixture and
    /// should be hidden from the default kanban response. Stored as
    /// <c>"fixture": true</c> in <c>job.json</c>. Endpoints filter fixtures
    /// out of <c>/api/tasks</c> and <c>/api/tasks/grouped</c> by default;
    /// <c>?includeFixtures=true</c> exposes them for debugging.
    /// </summary>
    public bool Fixture { get; init; }

    /// <summary>
    /// Optional lifecycle substate, read from the <c>"phase"</c> field in
    /// <c>job.json</c>. Drives the kanban board's lane projection in the
    /// expanded-lifecycle-lanes model (see
    /// <c>docs/research/expanded-lifecycle-lanes-plan-2026-05.md</c>).
    /// Application-owned: agents must not write to this field. Values come
    /// from <see cref="LifecyclePhases"/> and are constrained per state by
    /// <see cref="LifecyclePhases.AllowedByState"/>. Null means "no explicit
    /// phase on disk"; the frontend then falls back to
    /// <see cref="LifecyclePhases.DefaultFor"/> to pick a default lane. This
    /// keeps existing job folders that predate the field rendering correctly
    /// without rewriting every <c>job.json</c>.
    /// </summary>
    public string? Phase { get; init; }

    /// <summary>
    /// Structural classification of the task. One of <see cref="TaskTypes.Bug"/>,
    /// <see cref="TaskTypes.Feature"/>, or <see cref="TaskTypes.Chore"/>
    /// (default for legacy and technical work). Stored in <c>job.json</c> as
    /// <c>"taskType"</c>. The kanban card renders a small chip; filters in
    /// the header narrow the board by type. Legacy <c>"user-story"</c> values
    /// on disk are silently normalised to <see cref="TaskTypes.Feature"/> on read.
    /// </summary>
    public string TaskType { get; init; } = TaskTypes.Chore;

    /// <summary>
    /// Lightweight tag identifiers attached to this job. The label and colour
    /// for each id come from the workspace-level <c>tags.json</c> registry
    /// served at <c>GET /api/tags</c>. Unknown ids (registry entries that
    /// were soft-deleted) render as a faint "ghost" chip on the card so the
    /// user can clear the stale reference. Stored in <c>job.json</c> as
    /// <c>"tags"</c>; absent or null on disk means an empty list.
    /// </summary>
    public List<string> Tags { get; init; } = [];

    /// <summary>
    /// F34: structured cross-references to other tasks, keyed by F33 stable
    /// keys (<c>ATP-19</c>). Four relation kinds — dependsOn / relatedTo /
    /// blockedBy / supersedes (see <see cref="TaskReferences"/>). Stored as the
    /// <c>"references"</c> object in <c>job.json</c>; the scanner surfaces an
    /// empty instance when the field is absent so consumers never see null.
    /// Set atomically through <c>PUT /api/tasks/{id}/references</c> after
    /// validation (keys must exist, no self-reference, dependsOn stays a DAG).
    /// </summary>
    public TaskReferences References { get; init; } = new();

    /// <summary>
    /// Append-only commit-provenance record (ASS-1724): the task's worktree
    /// branch, its fork-point base, the per-lane-transition anchors, and the
    /// develop-merge block. Written by the single recording hook in
    /// <c>TaskTransitionService.MoveAsync</c>; the derived landed-state is NOT
    /// stored here but recomputed live by the provenance read endpoint. Null on
    /// legacy <c>task.json</c> files that predate the field.
    /// </summary>
    public TaskProvenance? Provenance { get; init; }
}

public record TaskOutcomeIssue
{
    public string Kind { get; init; } = "";
    public string Label { get; init; } = "";
    public string Severity { get; init; } = "Info";
    public string Summary { get; init; } = "";
    public DateTime? LastSeenAt { get; init; }
}

/// <summary>
/// Pure rules for reconciling a derived <see cref="TaskOutcomeIssue"/> against a
/// task's final accept verdict. An accepted/completed task (moved to
/// 5-human-review / 6-completed after an <c>accept</c> verdict) must NOT carry a
/// Warn-class ambiguity outcome that contradicts that verdict: a
/// <c>classifier-unknown</c> / <c>heuristic-done</c> / <c>missing-terminal-sentinel</c>
/// chip is an intermediate-run-cycle artifact, not the final state. This is the
/// "Erfolg sieht aus wie classifier-unknown" gap (ASS-775). Shared by the
/// read-time scanner derivation, the endpoint overlay, and the boot backfill so
/// all three agree on which outcomes an accept supersedes.
/// </summary>
public static class TaskOutcomeIssueReconciliation
{
    /// <summary>
    /// Warn-class outcome kinds that an <c>accept</c> verdict supersedes. High
    /// severity infra failures (environment-blocker / permission-blocked /
    /// watchdog-timeout) are intentionally absent: those describe a real host
    /// condition worth surfacing even on a terminal card.
    /// </summary>
    public static readonly string[] VerdictContradictingKinds =
        ["classifier-unknown", "heuristic-done", "missing-terminal-sentinel"];

    /// <summary>True when the issue is a Warn-class ambiguity outcome that an accept verdict supersedes.</summary>
    public static bool IsVerdictContradicting(TaskOutcomeIssue? issue)
        => issue != null
           && VerdictContradictingKinds.Any(k => string.Equals(k, issue.Kind, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when a derived outcome issue should be dropped because the task was
    /// accepted: an <c>accept</c> verdict (<paramref name="verdictAccepted"/>)
    /// means the run's final disposition supersedes the intermediate-cycle Warn
    /// chip.
    /// </summary>
    public static bool ShouldSuppress(TaskOutcomeIssue? issue, bool verdictAccepted)
        => verdictAccepted && IsVerdictContradicting(issue);
}

/// <summary>
/// String constants for <see cref="TaskInfo.TaskType"/>. Kept as constants (not
/// an enum) so the JSON wire format is the literal string and stable across
/// enum renames. The default is <see cref="Chore"/>: existing technical work
/// that predates the field migrates to the safe neutral category.
/// </summary>
public static class TaskTypes
{
    public const string Bug = "bug";
    public const string Feature = "feature";
    public const string Chore = "chore";

    /// <summary>Legacy on-disk value for <see cref="Feature"/>; silently mapped on read.</summary>
    public const string LegacyUserStory = "user-story";

    public static readonly string[] All = [Bug, Feature, Chore];

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Chore;
        var v = value.Trim();
        // Migration: existing job.json files written as "user-story" map to
        // "feature" silently on read so no bulk re-write of disk is needed.
        if (string.Equals(v, LegacyUserStory, StringComparison.OrdinalIgnoreCase)) return Feature;
        foreach (var t in All)
            if (string.Equals(t, v, StringComparison.OrdinalIgnoreCase)) return t;
        return Chore;
    }
}
