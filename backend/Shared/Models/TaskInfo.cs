namespace AgentStudio.Shared;

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
    /// <summary>CLI-native session identifier captured during streaming; reused on resume for follow-ups.</summary>
    public string? SessionName { get; init; }
    /// <summary>Preferred model for this job (e.g. <c>claude-sonnet-4.5</c>); passed via <c>--model</c> when supported.</summary>
    public string? Model { get; init; }
    /// <summary>
    /// True when the model was explicitly pinned on the card. Legacy tasks
    /// without provenance default to true so an upgrade never changes their
    /// execution model unexpectedly.
    /// </summary>
    public bool ModelExplicit { get; init; } = true;
    /// <summary>Optional thinking / reasoning effort level for the selected CLI model.</summary>
    public string? ThinkingLevel { get; init; }
    /// <summary>True when the card explicitly pins its reasoning level.</summary>
    public bool ThinkingLevelExplicit { get; init; } = true;
    /// <summary>Which CLI backend executes this job: <c>claude</c>, <c>codex</c>, or <c>gemini</c>. Defaults to <c>claude</c>.</summary>
    public string? CliType { get; init; }
    /// <summary>Effective fallback for the current run; null outside a quota-routed run.</summary>
    public QuotaFallbackStatus? QuotaFallback { get; init; }
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
    /// docs/concepts/planning-research-task-kinds-2026-05.md.
    /// </summary>
    public bool AllowWebAccess { get; init; }
    /// <summary>
    /// When <c>true</c>, this job uses its own dedicated session even if the project runner is
    /// configured for <see cref="SessionModes.ReuseProject"/>. Lets a one-off task isolate its
    /// context from the long-running project session.
    /// </summary>
    public bool? UseOwnSession { get; init; }
    /// <summary>
    /// Per-task context-mode override (T1b / ASS-1742): <c>clean</c> (isolated
    /// per-run CLI home) or <c>shared</c> (the operator's global CLI state). Null
    /// means "no task override" — the run falls back to the project setting and
    /// then the platform default (<see cref="CliContextModes.Clean"/>). See
    /// <see cref="CliContextModes"/>.
    /// </summary>
    public string? ContextMode { get; init; }
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
    /// <c>docs/concepts/expanded-lifecycle-lanes-plan-2026-05.md</c>).
    /// Application-owned: agents must not write to this field. Values come
    /// from <see cref="LifecyclePhases"/> and are constrained per state by
    /// <see cref="LifecyclePhases.AllowedByState"/>. Null means "no explicit
    /// phase on disk"; the frontend then falls back to
    /// <see cref="LifecyclePhases.DefaultFor"/> to pick a default lane. This
    /// keeps existing job folders that predate the field rendering correctly
    /// without rewriting every <c>job.json</c>.
    /// </summary>
    public string? Phase { get; init; }

    /// <summary>UTC timestamp written whenever <see cref="Phase"/> changes.</summary>
    public DateTime? PhaseEnteredAt { get; init; }

    /// <summary>
    /// Run-Liveness Slice B (concept Rule 2): when this <c>3-progress</c> card is
    /// waiting on an unanswered steer / NeedsInput question, the UTC time the wait
    /// started - read from the durable <c>steer-pending.json</c> marker. Null when
    /// the card is not steer-pending. Drives the board's "waiting for answer since
    /// mm:ss" pill so the wait is visible instead of an invisible hang. Paired
    /// with <see cref="Phase"/> == <see cref="LifecyclePhases.SteerPending"/>.
    /// </summary>
    public DateTime? SteerPendingSince { get; init; }

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
    /// Wiki pages associated with this task. The list is append-only and
    /// deliberately survives deletion of a target page; <c>Exists</c> is a
    /// read-time rendering hint and is never required for persistence.
    /// </summary>
    public List<RelatedWikiPage> RelatedWikiPages { get; init; } = [];

    /// <summary>
    /// AGT-2029 — read-time "waits-on" projection derived from
    /// <see cref="References"/>.<c>DependsOn</c> against the whole workspace
    /// (all projects, all lanes including archive). Tells the card which
    /// dependencies are fulfilled vs still open, whether the card is blocked,
    /// and whether it sits on a dependency cycle. Never persisted to
    /// <c>task.json</c>; folded on by the endpoint read overlay
    /// (<c>TaskEndpointHelpers.WithRuntime</c>) and computed independently by
    /// the runner pickup gate. Null when the task has no dependsOn edges. See
    /// <see cref="WaitsOnEvaluator"/>.
    /// </summary>
    public WaitsOnStatus? WaitsOn { get; init; }

    /// <summary>
    /// Append-only commit-provenance record (ASS-1724): the task's worktree
    /// branch, its fork-point base, the per-lane-transition anchors, and the
    /// develop-merge block. Written by the single recording hook in
    /// <c>TaskTransitionService.MoveAsync</c>; the derived landed-state is NOT
    /// stored here but recomputed live by the provenance read endpoint. Null on
    /// legacy <c>task.json</c> files that predate the field.
    /// </summary>
    public TaskProvenance? Provenance { get; init; }

    /// <summary>
    /// AGT-2046 — compact, always-on board merge signal: is this task's work
    /// folded into the integration branch (develop) and/or the release branch
    /// (main)? Computed batched + cached per repository by
    /// <c>BoardMergeStatusService</c> (O(repos) git spawns, never per card) and
    /// folded onto the board payload so the kanban card renders a two-segment
    /// [develop|main] indicator without a per-card graph query. Never persisted
    /// to <c>task.json</c>; null on cards with no committed/merged anchor yet.
    /// </summary>
    public TaskMergeSignal? MergeSignal { get; init; }

    /// <summary>
    /// AGT-2202 — honest, git-derived integration verdict for accepted cards
    /// (5-human-review / 6-completed / 7-archive): is this task's work actually in
    /// develop? Resolves the "Accept != Merge" blind spot by reading three
    /// independent git signals (curated <c>merge(&lt;KEY&gt;)</c> log commit, anchor
    /// ancestry, task-branch-tip ancestry) into one of four discrete states
    /// (<see cref="IntegrationStatuses"/>). Computed batched + cached per repository
    /// by <c>TaskIntegrationStatusService</c> (O(repos) git spawns, never per card)
    /// and folded onto the board payload. Never persisted to <c>task.json</c>; null
    /// on cards that are not in an accepted lane.
    /// </summary>
    public TaskIntegrationStatus? Integration { get; init; }

    /// <summary>
    /// PUB-1 — read-time "publishable to" projection for accepted (6-completed)
    /// tasks: which publish targets (npm / NuGet / website) this task's merged work
    /// touches, so the card / task-detail renders a "publishable: npm, website"
    /// chip. Computed batched per project by <c>TaskPublishableService</c> by
    /// set-membership of the task's mainline anchor against each target's pending
    /// commit set (O(projects), never per card) and folded onto the board payload.
    /// Never persisted to <c>task.json</c>; null on non-accepted cards and on cards
    /// whose work touches no derived publish target.
    /// </summary>
    public TaskPublishSignal? PublishSignal { get; init; }

    /// <summary>
    /// Read-time visibility projection (ASS-1751) for <c>3-progress</c> tasks
    /// that disambiguates a live run, a failed run waiting out the rapid-crash
    /// backoff, and an orphaned run killed by a backend restart. Folded on by
    /// <c>WithRuntime</c> only when <see cref="State"/> is
    /// <see cref="TaskStates.Progress"/>; null otherwise. Never persisted to
    /// <c>job.json</c>; carries no behavior. See <see cref="TaskRunActivity"/>.
    /// </summary>
    public TaskRunActivity? RunActivity { get; init; }

    /// <summary>
    /// Set when the task was completed out-of-band (operator chat, external
    /// agent, remote host) and reconciled through
    /// <c>POST /api/tasks/{id}/external-completion</c> instead of a runner run.
    /// Persisted as the <c>"externalCompletion"</c> object in <c>task.json</c>;
    /// null on every task that finished through the normal runner/review path.
    /// Drives the "extern erledigt" badge on the kanban card. See
    /// <c>docs/concepts/out-of-band-task-completion.md</c> §3.
    /// </summary>
    public ExternalCompletionInfo? ExternalCompletion { get; init; }

    /// <summary>
    /// AGT-2003 — read-time projection of the runner holding this task's active
    /// <b>run lease</b> (ADR-0060, <see cref="AgentStudio.Runner.RunLeaseService"/>).
    /// Folded on by <c>TaskEndpointHelpers.WithRuntime</c> only while the task is
    /// in <see cref="TaskStates.Progress"/> and a lease is held; null otherwise.
    /// Never persisted to <c>task.json</c>. A remote runner acquires the run
    /// lease before it spawns a CLI (the local in-process runner still uses the
    /// disk pickup-lock and holds no run lease), so a non-null value with
    /// <see cref="TaskRunnerInfo.IsRemote"/> is the signal the board card uses to
    /// show "executed by &lt;runner&gt;" instead of a plain local run. Drives the
    /// runner badge next to the CLI badge and the task-detail run header.
    /// </summary>
    public TaskRunnerInfo? Runner { get; init; }

    /// <summary>
    /// Canonical read-time projection of where the current execution actually
    /// runs. Runtime process and fenced lease facts lead; project routing is
    /// included only as configured context. Unlike <see cref="Runner"/>, this
    /// projection also represents queues, recovery, and stale remote owners.
    /// </summary>
    public TaskExecutionLocation? ExecutionLocation { get; init; }

    /// <summary>
    /// AGT-2069 — read-time spawn-visibility + spawn-contract projection for a
    /// planning task (<c>Mode == planning</c>): which follow-up cards it spawned
    /// (AGT-2028 ledger), whether the operator declared "no follow-up intended",
    /// and whether the spawn contract is satisfied. Folded on by
    /// <c>TaskEndpointHelpers.WithRuntime</c> only for planning-mode tasks (two
    /// small sidecar reads, gated so the perf contract holds); null on every
    /// coding / research / epic card. Never persisted to <c>task.json</c>. Drives
    /// the "spawnt: AGT-xxxx" chips, the "no follow-up cards" warning, and the
    /// accept-dialog guard against the AGT-1915 trap.
    /// </summary>
    public PlanningSpawnSummary? PlanningSpawn { get; init; }
}

/// <summary>
/// Card-renderable projection of the runner that holds a task's active run lease
/// (AGT-2003). Sourced from the canonical persisted RunAttempt lease; the attempt,
/// epoch, fencing token, and lease id ride along for the tooltip / audit trail but the card only needs
/// <see cref="RunnerName"/> and <see cref="IsRemote"/>.
/// </summary>
public record TaskRunnerInfo
{
    /// <summary>Stable runner id that acquired the lease (e.g. <c>dev@host</c> or a remote runner id).</summary>
    public string RunnerId { get; init; } = "";
    /// <summary>Human-facing runner name shown on the badge (e.g. <c>agent-runner-01</c>). Falls back to the id when unset.</summary>
    public string RunnerName { get; init; } = "";
    /// <summary>Host the runner runs on. Empty when the lease did not carry one.</summary>
    public string Hostname { get; init; } = "";
    /// <summary>Backend-name the lease owner reported (dev / stable / a remote backend name).</summary>
    public string BackendName { get; init; } = "";
    /// <summary>
    /// True when the lease owner is a different runner than this backend's own
    /// identity — i.e. the task is executing on a remote host, not in-process.
    /// The card shows the remote runner name only when this is true.
    /// </summary>
    public bool IsRemote { get; init; }
    /// <summary>Opaque lease id of the active grant (audit / tooltip only).</summary>
    public string LeaseId { get; init; } = "";
    /// <summary>Monotonic fencing token of the active grant (audit / tooltip only).</summary>
    public long FencingToken { get; init; }
    /// <summary>Canonical persisted RunAttempt identity.</summary>
    public string? AttemptId { get; init; }
    /// <summary>Authority epoch that issued the current fence.</summary>
    public long AuthorityEpoch { get; init; }
    /// <summary>UTC instant the active lease was acquired.</summary>
    public DateTime AcquiredAt { get; init; }
}

public static class TaskExecutionStates
{
    public const string LocalRunning = "local-running";
    public const string RemoteRunning = "remote-running";
    public const string RemoteDisconnected = "remote-disconnected";
    public const string QueuedRemote = "queued-remote";
    public const string Recovering = "recovering";
    public const string NoActiveExecution = "no-active-execution";
}

/// <summary>Canonical execution ownership and health projection for task APIs.</summary>
public record TaskExecutionLocation
{
    public string State { get; init; } = TaskExecutionStates.NoActiveExecution;
    public string ExecutionKind { get; init; } = "none";
    public string? RunnerId { get; init; }
    public string? ClientId { get; init; }
    public string? HostDisplayName { get; init; }
    public string? ConfiguredRunnerId { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? LastHeartbeat { get; init; }
    public DateTime? LastActivityAt { get; init; }
    public int? ProcessId { get; init; }
    public string? SessionId { get; init; }
    public string? Branch { get; init; }
    public string? WorktreePath { get; init; }
    public string ConnectionState { get; init; } = "none";
    public string LeaseState { get; init; } = "none";
    public string TrustReason { get; init; } = "No active runtime process or fenced run lease is present.";
    public bool Historical { get; init; }
}

/// <summary>
/// Provenance of an out-of-band task completion. Written by the external
/// completion endpoint into <c>task.json</c> so the board can render an
/// "extern erledigt" badge and attribute who/what finished the work. The
/// canonical narrative lives in <c>results/deliverables.md</c> and the
/// <c>external_completion</c> timeline event; this record is the small,
/// card-renderable summary.
/// </summary>
public record ExternalCompletionInfo
{
    /// <summary>Who or which channel completed the task (operator name, agent id, "chat", ...).</summary>
    public string Source { get; init; } = "";
    /// <summary>One-line result summary shown in the badge tooltip; may be empty.</summary>
    public string? Summary { get; init; }
    /// <summary>UTC instant the external completion was recorded.</summary>
    public DateTime CompletedAt { get; init; }
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
/// Slim projection of an archived (<c>7-archive</c>) task for the paged
/// archive read endpoint (ASS-1727). The terminal lane holds hundreds of
/// finished cards, so the board's <c>/grouped</c> response deliberately omits
/// it; the Archive view pages through <c>GET /api/tasks/archive</c> instead and
/// only needs the few fields an archived card renders - identity, title,
/// project, the lane-entry timestamp, and the commit count. Built from a
/// slim-hydrated <see cref="TaskInfo"/> (ASS-1649), so no live-card affordance
/// (outcome chip, token totals, execution) is carried here.
/// </summary>
public record ArchivedTaskInfo
{
    public string Id { get; init; } = "";
    public string TaskKey { get; init; } = "";
    public string? Key { get; init; }
    public string Title { get; init; } = "";
    public string State { get; init; } = TaskStates.Archive;
    public string ProjectName { get; init; } = "";
    public string WatchPath { get; init; } = "";
    /// <summary>Lane-entry anchor: when the task entered 7-archive (its effective "completed/archived at").</summary>
    public DateTime EnteredLaneAt { get; init; }
    public DateTime LastActivity { get; init; }
    public int CommitCount { get; init; }
    public bool CodeActivityDetected { get; init; }
    public string TaskType { get; init; } = TaskTypes.Chore;
    public string? CliType { get; init; }
    public string Agent { get; init; } = "";

    public static ArchivedTaskInfo From(TaskInfo job) => new()
    {
        Id = job.Id,
        TaskKey = job.TaskKey,
        Key = job.Key,
        Title = job.Title,
        State = job.State,
        ProjectName = job.ProjectName,
        WatchPath = job.WatchPath,
        EnteredLaneAt = job.EnteredLaneAt,
        LastActivity = job.LastActivity,
        CommitCount = job.CommitCount,
        CodeActivityDetected = job.CodeActivityDetected,
        TaskType = job.TaskType,
        CliType = job.CliType,
        Agent = job.Agent,
    };
}

/// <summary>
/// Paged response for <c>GET /api/tasks/archive</c>. <see cref="Total"/> is the
/// count after the (optional) <c>watchPath</c> + <c>search</c> filters but
/// before paging, so the frontend can drive infinite-scroll / "showing N of
/// Total" without a second round-trip.
/// </summary>
public record ArchivedTasksResponse
{
    public List<ArchivedTaskInfo> Items { get; init; } = [];
    public int Total { get; init; }
    public int Offset { get; init; }
    public int Limit { get; init; }
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
