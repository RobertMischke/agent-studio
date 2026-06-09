using Microsoft.Extensions.Configuration;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Pipeline;
using OrchestratorApi.Services.Tasks;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Categories for a system-initiated escalation to <c>5e-escalated</c>. The
/// value is carried in the <see cref="ReviewDecisionRecord.Reason"/> (see
/// <see cref="HumanReviewEscalation.FormatReason"/>) and in the status.md stub
/// so the board can say WHY a card was parked even though no agent review ran.
/// </summary>
public static class HumanReviewEscalationCategories
{
    public const string WatchdogKill = "watchdog-kill";
    public const string PermissionBlocked = "permission-blocked";
    public const string EnvironmentBlocker = "environment-blocker";
    public const string AutoFailurePark = "auto-failure-park";
    public const string PickupZombie = "pickup-zombie";
    public const string EmptyFastExit = "empty-fast-exit";

    /// <summary>The run exceeded the model's input window (prompt too long /
    /// context length). Non-retryable, so it is routed straight to human review
    /// instead of being re-issued into the same overflow.</summary>
    public const string ContextOverflow = "context-overflow";

    /// <summary>The per-task circuit breaker tripped after N consecutive failed
    /// runs without progress; the task was parked to stop an endless reissue
    /// loop.</summary>
    public const string Quarantined = "quarantined";

    /// <summary>The worker CLI advanced git history during its own run, bypassing
    /// the platform-owned commit/push boundary.</summary>
    public const string AgentGitViolation = "agent-git-violation";

    /// <summary>A card carrying the human-decision-needed marker: it exists for
    /// a person to decide, never for an agent to run. Routed to 5e-escalated
    /// after the retired 1b-needs-human-review lane was removed.</summary>
    public const string HumanDecisionNeeded = "human-decision-needed";

    /// <summary>Retroactive category for cards parked in 5-human-review before
    /// the escalation funnel existed (boot-time backfill).</summary>
    public const string UnknownLegacy = "unknown-legacy";
}

/// <summary>
/// The single funnel every SYSTEM-initiated move into <c>5e-escalated</c>
/// must pass through. It writes both halves of the board contract that the
/// agent-driven <see cref="ReviewDecisionOrchestrator"/> already writes for its
/// own promotions:
/// <list type="number">
///   <item>an <see cref="ReviewDecisionKind.Escalate"/>
///   <see cref="ReviewDecisionRecord"/> in the per-project decision journal, so
///   the endpoint-derived <c>OrchestratorVerdict</c> is <c>escalate</c> rather
///   than <c>null</c>; and</item>
///   <item>a minimal <c>status.md</c> stub in the moved folder, so
///   <c>StatusMarkdown</c> is not empty - written only when the folder has no
///   real summary yet, so a genuine summary is never clobbered.</item>
/// </list>
///
/// <para>Before this funnel existed, three <see cref="ProjectRunner"/> paths
/// (watchdog kill / permission / environment block, the auto-failure park, and
/// the over-budget pickup zombie escalation) moved a folder straight from
/// 3-progress into 5-human-review without either half, producing cards the
/// board could not explain - the bug this funnel fixes. The
/// <c>HumanReviewVerdictDriftTest</c> mechanically forbids any new move into the
/// lane from outside this file and the orchestrator.</para>
/// </summary>
public sealed class HumanReviewEscalation
{
    private const string SystemPrompt = "(deterministic system escalation)";
    private const string NoModelResponse = "(no fast-model call)";

    private readonly TaskStateMachine _states;
    private readonly TaskTransitionService _transitions;
    private readonly string? _workspaceRoot;
    private readonly ILogger _logger;
    private readonly TaskScannerService? _scanner;
    private readonly WorkspaceArtifactCommitService? _workspaceArtifactCommits;

    /// <summary>DI ctor: the workspace root is the same <c>TaskRepository</c> the
    /// verdict-read side (<c>TaskEndpointHelpers.BuildOrchestratorVerdictLookup</c>)
    /// uses, so the journal this funnel writes is the journal the board reads.</summary>
    public HumanReviewEscalation(
        TaskStateMachine states,
        TaskTransitionService transitions,
        IConfiguration configuration,
        ILogger<HumanReviewEscalation> logger,
        TaskScannerService? scanner = null,
        WorkspaceArtifactCommitService? workspaceArtifactCommits = null)
        : this(states, transitions, configuration["TaskRepository"], logger, scanner, workspaceArtifactCommits)
    {
    }

    /// <summary>Explicit-root ctor for tests and any caller that already resolved
    /// the workspace root.</summary>
    public HumanReviewEscalation(
        TaskStateMachine states,
        TaskTransitionService transitions,
        string? workspaceRoot,
        ILogger logger,
        TaskScannerService? scanner = null,
        WorkspaceArtifactCommitService? workspaceArtifactCommits = null)
    {
        _states = states;
        _transitions = transitions;
        _workspaceRoot = workspaceRoot;
        _logger = logger;
        _scanner = scanner;
        _workspaceArtifactCommits = workspaceArtifactCommits;
    }

    /// <summary>
    /// Move <paramref name="jobId"/> into 5e-escalated through
    /// <see cref="TaskTransitionService.MoveAsync"/> (so the
    /// <c>OnJobMoved</c> side effects fire: the runner's active-job latch is
    /// cleared and the board gets a live SignalR push), then record the verdict
    /// and status stub. Used by the in-runner escalation paths that run inside
    /// the async completion flow.
    /// </summary>
    public async Task<MoveJobOutcome> EscalateAsync(
        string jobId, string watchPath, string project,
        string category, string reason, CancellationToken ct = default)
    {
        var beforeFolder = _scanner?.FindJob(jobId, watchPath)?.FolderPath;
        var outcome = await _transitions.MoveAsync(jobId, TaskStates.Escalated, watchPath, ct);
        if (outcome.Status == MoveJobStatus.Success)
        {
            RecordVerdictAndStatus(project, jobId, outcome.NewFolderPath, category, reason);
            TryCommitArtifacts(project, jobId, beforeFolder, outcome.NewFolderPath);
        }
        else
            _logger.LogWarning(
                "HumanReviewEscalation: move of {Project}/{JobId} to 5e-escalated failed: {Status} {Message}",
                project, jobId, outcome.Status, outcome.Message);
        return outcome;
    }

    /// <summary>
    /// Synchronous variant for the pickup loop, which is sync and already moved
    /// folders through the state machine directly. Records the verdict and
    /// status stub on success.
    /// </summary>
    public MoveJobOutcome Escalate(
        string jobId, string watchPath, string project,
        string category, string reason)
    {
        var beforeFolder = _scanner?.FindJob(jobId, watchPath)?.FolderPath;
        var outcome = _states.MoveJob(jobId, TaskStates.Escalated, watchPath);
        if (outcome.Status == MoveJobStatus.Success)
        {
            RecordVerdictAndStatus(project, jobId, outcome.NewFolderPath, category, reason);
            TryCommitArtifacts(project, jobId, beforeFolder, outcome.NewFolderPath);
        }
        else
            _logger.LogWarning(
                "HumanReviewEscalation: move of {Project}/{JobId} to 5e-escalated failed: {Status} {Message}",
                project, jobId, outcome.Status, outcome.Message);
        return outcome;
    }

    /// <summary>
    /// Verdict + status only, no move. Used by the boot-time backfill for cards
    /// that are ALREADY parked in 5-human-review with no verdict (the legacy
    /// cards this fix repairs). Idempotent on the status half (it never
    /// overwrites a non-empty status.md); the journal append is additive, so the
    /// caller must gate on "no existing verdict" before calling.
    /// </summary>
    public void RecordVerdictAndStatus(
        string project, string jobId, string? folderPath, string category, string reason)
    {
        if (!string.IsNullOrWhiteSpace(_workspaceRoot) && !string.IsNullOrWhiteSpace(project))
        {
            try
            {
                ReviewDecisionLog.Append(_workspaceRoot!, new ReviewDecisionRecord(
                    CreatedAt: DateTime.UtcNow,
                    JobId: jobId,
                    Project: project,
                    Kind: ReviewDecisionKind.Escalate,
                    Reason: FormatReason(category, reason),
                    Prompt: SystemPrompt,
                    Response: NoModelResponse,
                    FollowUp: string.Empty));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "HumanReviewEscalation: failed to append Escalate verdict for {Project}/{JobId} (category={Category})",
                    project, jobId, category);
            }
        }
        else
        {
            _logger.LogDebug(
                "HumanReviewEscalation: TaskRepository not configured or project empty; skipped verdict journal for {JobId} (category={Category}).",
                jobId, category);
        }

        if (!string.IsNullOrWhiteSpace(folderPath))
            WriteStatusStubIfMissing(folderPath!, category, reason);
    }

    /// <summary>Encodes the category into the verdict reason so the decision
    /// journal carries the cause, e.g. <c>[watchdog-kill] CLI exceeded the
    /// watchdog deadline</c>.</summary>
    public static string FormatReason(string category, string reason)
    {
        var c = string.IsNullOrWhiteSpace(category) ? HumanReviewEscalationCategories.UnknownLegacy : category.Trim();
        var r = (reason ?? string.Empty).Trim();
        return r.Length == 0 ? $"[{c}]" : $"[{c}] {r}";
    }

    /// <summary>Builds the minimal status.md the board renders for an
    /// escalated-without-review card: a <c>- Result:</c> line (same shape the
    /// generated summaries use), the category, the reason, and a pointer to the
    /// logs and the decision journal.</summary>
    public static string BuildStatusStub(string category, string reason)
    {
        var c = string.IsNullOrWhiteSpace(category) ? HumanReviewEscalationCategories.UnknownLegacy : category.Trim();
        var r = (reason ?? string.Empty).Trim();
        var nl = Environment.NewLine;
        var sb = new System.Text.StringBuilder();
        sb.Append("# Status").Append(nl).Append(nl);
        sb.Append("- Result: Escalated to human decision (").Append(c).Append(')').Append(nl).Append(nl);
        sb.Append("This card was routed to 5e-escalated by the orchestrator runtime without an automated quality review, so there is no agent-written summary.")
          .Append(nl).Append(nl);
        sb.Append("- Category: ").Append(c).Append(nl);
        if (r.Length > 0)
            sb.Append("- Reason: ").Append(r).Append(nl);
        sb.Append("- See `logs/` in this folder for the run output, and the project decision journal (`logs/decisions/<project>.jsonl`) for the escalation record.")
          .Append(nl);
        return sb.ToString();
    }

    private void WriteStatusStubIfMissing(string folderPath, string category, string reason)
    {
        var path = Path.Combine(folderPath, "status.md");
        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(existing)) return; // never clobber a real summary
            }
            Directory.CreateDirectory(folderPath);
            File.WriteAllText(path, BuildStatusStub(category, reason));
        }
        catch (Exception ex)
        {
            // Best-effort: the verdict already records the escalation; an
            // unwritable status.md must not crash the runner.
            _logger.LogWarning(ex, "HumanReviewEscalation: failed to write status.md stub at {Path}", path);
        }
    }

    private void TryCommitArtifacts(string project, string jobId, string? beforeFolderPath, string? afterFolderPath)
    {
        var result = _workspaceArtifactCommits?.TryCommitRunBoundary(
            _workspaceRoot,
            jobId,
            beforeFolderPath,
            afterFolderPath,
            ReviewDecisionKind.Escalate);
        if (result is { Success: false })
        {
            _logger.LogWarning(
                "HumanReviewEscalation: workspace artifact commit failed for {Project}/{JobId}: {Error}",
                project, jobId, result.Error);
        }
    }
}
