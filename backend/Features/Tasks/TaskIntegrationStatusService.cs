using System.Collections.Concurrent;
using AgentStudio.Registry;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tasks;

/// <summary>
/// AGT-2202 - computes the honest, git-derived integration verdict for delivered
/// cards (4-auto-review / 5-human-review / 5e-escalated / 6-completed / 7-archive): is the task's
/// work actually folded into the integration branch (develop)? The result is attached to
/// <see cref="TaskInfo.Integration"/> so the board renders a single, unambiguous
/// "integrated / not integrated / conflict / no branch" badge on every delivered
/// card, and so acceptance can refuse a delivery that has not passed the
/// integration boundary.
///
/// <para>
/// The verdict's anchor is the current immutable review result when one exists,
/// otherwise the integrable subset of the attributed <c>commits[]</c> list.
/// Zero-file runner lifecycle markers are not delivery expectations; every
/// commit that carries changed files remains an anchor within its current
/// review generation. This
/// keeps the badge aligned with delivered work (AGT-2171: the widget showed the
/// attributed commits on develop while the badge, keying off the branch
/// <em>tip</em> WIP snapshot, claimed "not integrated"). The signals are collapsed
/// into the five
/// <see cref="IntegrationStatuses"/> states:
/// <list type="number">
/// <item>ALL attributed commits are ancestors of develop → <c>integrated</c> (even
///   when the branch tip still carries further, un-integrated WIP commits the
///   widget never showed);</item>
/// <item>SOME attributed commits are ancestors → <c>partial</c>, with the missing
///   short-SHAs in the detail;</item>
/// <item>NONE are ancestors → <c>pending</c> (or <c>conflict-skipped</c> when a
///   typed accepted-integration failure was recorded);</item>
/// <item>no attributed commit and no evidenced delivery ref →
///   <c>no-branch</c>.</item>
/// </list>
/// </para>
///
/// <para>
/// Same design invariant as <see cref="BoardMergeStatusService"/>: <b>no per-card
/// git spawn on the hot path</b>. Per repository it computes one target-branch
/// ancestor SHA set, cached against the resolved target HEAD fingerprint, and
/// answers every card in that repo with in-memory lookups. Provenance merge
/// records, pipeline success, lane state, and curated merge subjects never
/// override commit membership. A current review subject is an ancestry fallback
/// only when no attributed commit exists; it cannot hide a missing current
/// commit. This also detects out-of-band merges on the next read. Per-card local
/// reads resolve the delivery ref from the same task card
/// and review-subject truth as acceptance; the not-integrated subset also reads
/// <c>pipeline-execution.json</c> best-effort (integration-failed vs. plain
/// pending). Never throws: a git failure yields the conservative reading.
/// </para>
/// </summary>
public sealed class TaskIntegrationStatusService
{
    private readonly GitService _git;
    private readonly ProjectSettingsService _settings;
    private readonly PipelineExecutionLog _pipelineLog;
    private readonly ILogger<TaskIntegrationStatusService> _logger;
    private readonly ProjectRegistry? _projects;

    /// <summary>The delivered lanes this verdict applies to. Cards outside get no entry.</summary>
    internal static readonly HashSet<string> DeliveredLanes = new(StringComparer.Ordinal)
    {
        TaskStates.AutoReview,
        TaskStates.Escalated,
        TaskStates.HumanReview,
        TaskStates.Completed,
        TaskStates.Archive,
    };

    internal static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan ShortFallbackTtl = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan FailureCacheTtl = TimeSpan.FromSeconds(1);

    private readonly GenerationSingleFlightCache<RepoIntegration> _cache;
    private int _computationCount;

    public TaskIntegrationStatusService(
        GitService git,
        ProjectSettingsService settings,
        PipelineExecutionLog pipelineLog,
        ILogger<TaskIntegrationStatusService> logger,
        ProjectRegistry? projects = null)
        : this(git, settings, pipelineLog, logger, TimeProvider.System, projects)
    {
    }

    internal TaskIntegrationStatusService(
        GitService git,
        ProjectSettingsService settings,
        PipelineExecutionLog pipelineLog,
        ILogger<TaskIntegrationStatusService> logger,
        TimeProvider timeProvider,
        ProjectRegistry? projects = null)
    {
        _git = git;
        _settings = settings;
        _pipelineLog = pipelineLog;
        _logger = logger;
        _projects = projects;
        _cache = new GenerationSingleFlightCache<RepoIntegration>(timeProvider);
    }

    /// <summary>
    /// Per-<see cref="TaskInfo.TaskKey"/> integration verdict for delivered cards
    /// in the given board set. Auto Review is included because a green Remote
    /// delivery now integrates before it moves to Human Review. Every earlier
    /// lane carries no verdict and the card renders none. Never throws.
    /// </summary>
    public Dictionary<string, TaskIntegrationStatus> BuildLookup(IReadOnlyCollection<TaskInfo> jobs)
    {
        var result = new Dictionary<string, TaskIntegrationStatus>(StringComparer.Ordinal);
        if (jobs.Count == 0) return result;

        using var _t = GitProcessTelemetry.BeginRequest("board/integration-status", _logger);

        var groupsByTask = new Dictionary<string, List<RepositoryCommitGroup>>(StringComparer.Ordinal);
        var repoKeys = new HashSet<RepoBranchKey>();
        foreach (var job in jobs)
        {
            if (!DeliveredLanes.Contains(job.State)) continue;
            var groups = RepositoryGroups(job);
            groupsByTask[job.TaskKey] = groups;
            foreach (var group in groups)
                if (!string.IsNullOrWhiteSpace(group.Root))
                    repoKeys.Add(new RepoBranchKey(
                        group.Root!, group.IntegrationBranch, group.ReleaseBranch));
        }

        var reaches = new ConcurrentDictionary<RepoBranchKey, RepoIntegration>();
        Parallel.ForEach(
            repoKeys,
            new ParallelOptions { MaxDegreeOfParallelism = ReadOnlyGitConcurrencyLimiter.MaxConcurrency },
            key =>
            {
                var cacheKey = $"{key.Root}\0{key.Branch}\0{key.ReleaseBranch}";
                var refFingerprint = ReadOnlyGitRefFingerprint.CaptureDetailed(
                    key.Root, [key.Branch, key.ReleaseBranch]);
                reaches[key] = _cache.GetOrCreateVersioned(
                    cacheKey,
                    refFingerprint.Value,
                    value => value.Succeeded
                        ? refFingerprint.RequiresShortFallback ? ShortFallbackTtl : CacheTtl
                        : FailureCacheTtl,
                    () => ComputeRepoIntegration(key.Root, key.Branch, key.ReleaseBranch));
            });

        foreach (var job in jobs)
        {
            if (!DeliveredLanes.Contains(job.State)) continue;
            result[job.TaskKey] = ClassifyWithRepositories(
                job,
                groupsByTask.GetValueOrDefault(job.TaskKey) ?? [],
                reaches);
        }

        return result;
    }

    /// <summary>
    /// Returns whether the exact fenced remote delivery reviewed for this task is
    /// already an ancestor of the configured integration branch. This is a
    /// recovery-only distinction: the attributed commit set can be present while
    /// a later fenced lifecycle snapshot is not itself an integration
    /// expectation. A process crash after the local merge can also leave the
    /// exact result SHA contained while losing the pipeline record and queued
    /// push.
    /// </summary>
    public bool IsFencedDeliveryIntegrated(TaskInfo job)
    {
        try
        {
            var subject = ReviewSubjectStore.Read(job.FolderPath);
            if (subject is null || !ReviewSubjectStore.IsValidResultSha(subject.ResultSha))
                return false;

            var root = _git.ResolveRepoRootForWatchPath(job.WatchPath);
            if (string.IsNullOrWhiteSpace(root))
                return false;

            var branch = _git.ResolveIntegrationBranch(
                root,
                ConfiguredIntegrationBranch(job));
            return _git.IsAncestor(root, subject.ResultSha, branch);
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "TaskIntegrationStatusService: fenced delivery ancestry is best-effort");
            return false;
        }
    }

    /// <summary>
    /// Resolves accepted-integration recovery from the same Git-derived status
    /// projected onto the board. Pipeline history describes the last attempt;
    /// it cannot turn a delivery missing from the target branch into an
    /// integrated delivery.
    /// </summary>
    internal AcceptedIntegrationRecoveryDecision ResolveAcceptedIntegrationRecovery(
        TaskInfo job,
        TaskIntegrationStatus? status)
    {
        var lastMerge = ReadLatestMergeStep(job);
        if (!AcceptanceIntegrationPolicy.IsIntegrationRequired(job)
            || string.Equals(lastMerge?.Verdict, "operator-override", StringComparison.OrdinalIgnoreCase))
        {
            return new AcceptedIntegrationRecoveryDecision(
                AcceptedIntegrationRecoveryAction.Ignore,
                "This acceptance explicitly expects no integration.",
                lastMerge);
        }
        if (status?.Status == IntegrationStatuses.Integrated
            && lastMerge?.Status != PipelineStepStatus.Pending)
        {
            return new AcceptedIntegrationRecoveryDecision(
                AcceptedIntegrationRecoveryAction.Finalize,
                "Git proves that the attributed delivery is integrated; no merge replay is required.",
                lastMerge);
        }

        // AGT-2688: the merge itself already succeeded and only the deferred
        // push is blocked (main/develop lineage, or a diverged remote). That is
        // not a merge-replay case - re-running the merge cannot fix a push
        // problem, and doing so on every backstop sweep is exactly the loop
        // that burned the window overnight. The integration push backstop owns
        // retrying the push; this recovery path must leave the card alone.
        if (lastMerge?.Status == PipelineStepStatus.Passed
            && status?.Status == IntegrationStatuses.ConflictSkipped
            && status.Failure?.Code == AcceptedIntegrationFailureCodes.IntegrationPushBlocked)
        {
            return new AcceptedIntegrationRecoveryDecision(
                AcceptedIntegrationRecoveryAction.Ignore,
                "The merge already succeeded; only the deferred push is blocked and is retried by the integration push backstop.",
                lastMerge);
        }

        // BP-02: a crash can leave the merge commit in local ancestry while the
        // exact-SHA gate verdict is still pending. That state must resume the
        // runner rather than treating ancestry as proof that the gate ran.

        if (IsDecidedIntegrationAttempt(lastMerge))
        {
            return new AcceptedIntegrationRecoveryDecision(
                AcceptedIntegrationRecoveryAction.ReturnToReview,
                "The latest integration attempt requires an operator or steer round.",
                lastMerge);
        }

        return new AcceptedIntegrationRecoveryDecision(
            AcceptedIntegrationRecoveryAction.Retry,
            lastMerge?.Status == PipelineStepStatus.Passed
                ? "The Passed step contradicts current Git truth and must be revalidated."
                : "The accepted integration has no terminal recovery decision.",
            lastMerge);
    }

    /// <summary>
    /// The verdict for one card given its repo's cached integration facts. A
    /// verdict is derived entirely from the target-branch ancestry of the card's attributed
    /// <c>commits[]</c> (the same list the commit widget shows): all landed →
    /// integrated, some → partial, none → pending/conflict, and no attributed
    /// commit plus no delivery ref → no-branch. The branch tip is deliberately
    /// NOT an ancestry anchor - it is a WIP snapshot the widget never shows,
    /// whose use was the AGT-2171 badge/widget self-contradiction. It remains
    /// valid evidence that a local delivery ref exists.
    /// </summary>
    private TaskIntegrationStatus ClassifyWithRepositories(
        TaskInfo job,
        IReadOnlyList<RepositoryCommitGroup> groups,
        IReadOnlyDictionary<RepoBranchKey, RepoIntegration> reaches)
    {
        var branchName = groups.FirstOrDefault()?.IntegrationBranch
            ?? ConfiguredIntegrationBranch(job);
        var deliveryRef = DeliveryRefFor(job);
        if (groups.Count == 0)
            return ClassifyNotIntegrated(job, branchName, deliveryRef);

        var entries = new List<TaskRepositoryIntegrationStatus>(groups.Count);
        var effectiveRecords = new List<TaskCommitInfo>();
        var anyCommitIntegrated = false;
        foreach (var group in groups)
        {
            RepoIntegration? reach = null;
            if (!string.IsNullOrWhiteSpace(group.Root))
                reaches.TryGetValue(
                    new RepoBranchKey(group.Root!, group.IntegrationBranch, group.ReleaseBranch),
                    out reach);

            var integrationAncestors = reach?.DevelopAncestors
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var releaseAncestors = reach?.ReleaseAncestors
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var effectiveCommits = group.Commits
                .Where(commit => !IsZeroFileLifecycleMarker(commit)
                    || AncestorSetContains(integrationAncestors, commit.Sha))
                .ToList();
            if (effectiveCommits.Count == 0) continue;
            effectiveRecords.AddRange(effectiveCommits);
            var commits = effectiveCommits.Select(commit => commit.Sha).ToList();
            var missing = commits
                .Where(sha => !AncestorSetContains(integrationAncestors, sha))
                .ToList();
            var missingRelease = commits
                .Where(sha => !AncestorSetContains(releaseAncestors, sha))
                .ToList();
            var integratedCount = commits.Count - missing.Count;
            anyCommitIntegrated |= integratedCount > 0;
            var detail = string.IsNullOrWhiteSpace(group.Root)
                ? $"{group.Label}: no registered checkout is available for repository '{group.Repository}'."
                : missing.Count == 0
                    ? $"{group.Label}: {commits.Count}/{commits.Count} attributed commits are in {group.IntegrationBranch}."
                    : $"{group.Label}: {integratedCount}/{commits.Count} attributed commits are in {group.IntegrationBranch}; "
                      + $"missing: {string.Join(", ", missing.Select(Short))}";

            entries.Add(new TaskRepositoryIntegrationStatus
            {
                Repository = group.Repository,
                Label = group.Label,
                Commits = commits,
                IntegrationBranch = group.IntegrationBranch,
                ReleaseBranch = group.ReleaseBranch,
                OnIntegrationBranch = commits.Count > 0 && missing.Count == 0,
                OnReleaseBranch = commits.Count > 0 && missingRelease.Count == 0,
                Detail = detail,
            });
        }

        if (entries.Count == 0)
            return ClassifyNotIntegrated(job, branchName, deliveryRef);

        var allIntegrated = entries.All(entry => entry.OnIntegrationBranch);
        var anyIntegrated = anyCommitIntegrated;
        var newest = effectiveRecords.OrderBy(commit => commit.At).Last();
        var detailText = string.Join(" · ", entries.Select(entry => entry.Detail));

        if (allIntegrated)
            return Integrated(
                Short(newest.Sha),
                branchName,
                deliveryRef,
                entries.Count == 1
                    ? string.Equals(newest.Attribution, "review-subject", StringComparison.Ordinal)
                        ? "reviewed-result-ancestor"
                        : "anchor-ancestor"
                    : detailText) with
            {
                Repositories = entries,
            };

        if (ReadIntegrationFailure(job) is { } failure && !anyIntegrated)
        {
            var visibleReason = VisibleFailureReason(job, branchName, failure);
            return new TaskIntegrationStatus
            {
                Status = IntegrationStatuses.ConflictSkipped,
                DeliveryRef = deliveryRef,
                IntegrationBranch = branchName,
                Detail = entries.Count == 1 ? visibleReason : $"{detailText} · {visibleReason}",
                Repositories = entries,
                Failure = new TaskIntegrationFailure
                {
                    Code = failure.Code,
                    Label = failure.Label,
                    Reason = visibleReason,
                    RebaseRecoveryAvailable = failure.RebaseRecoveryAvailable,
                },
            };
        }

        if (!anyIntegrated && entries.Count == 1)
            return ClassifyNotIntegrated(job, branchName, deliveryRef) with
            {
                Repositories = entries,
            };

        return new TaskIntegrationStatus
        {
            Status = anyIntegrated ? IntegrationStatuses.Partial : IntegrationStatuses.Pending,
            DeliveryRef = deliveryRef,
            IntegrationBranch = branchName,
            Detail = detailText,
            Repositories = entries,
        };
    }

    /// <summary>
    /// Splits a not-integrated card into conflict-skipped / pending / no-branch. A
    /// recorded merge-into-develop conflict / error wins (the work was NOT merged
    /// and needs a human); only a card with neither an attributed commit nor an
    /// evidenced delivery ref is no-branch; otherwise the work simply has not
    /// landed yet - pending.
    /// </summary>
    private TaskIntegrationStatus ClassifyNotIntegrated(
        TaskInfo job,
        string branchName,
        string? deliveryRef = null)
    {
        deliveryRef ??= DeliveryRefFor(job);
        var anchor = AnchorFor(job);
        var hasWork = anchor != null || deliveryRef != null;

        if (ReadIntegrationFailure(job) is { } failure)
        {
            var visibleReason = VisibleFailureReason(job, branchName, failure);
            return new TaskIntegrationStatus
            {
                Status = IntegrationStatuses.ConflictSkipped,
                DeliveryRef = deliveryRef,
                IntegrationBranch = branchName,
                Detail = visibleReason,
                Failure = new TaskIntegrationFailure
                {
                    Code = failure.Code,
                    Label = failure.Label,
                    Reason = visibleReason,
                    RebaseRecoveryAvailable = failure.RebaseRecoveryAvailable,
                },
            };
        }

        if (!hasWork)
            return new TaskIntegrationStatus
            {
                Status = IntegrationStatuses.NoBranch,
                DeliveryRef = null,
                IntegrationBranch = branchName,
                Detail = "No delivery ref or attributed commit to integrate.",
            };

        return new TaskIntegrationStatus
        {
            Status = IntegrationStatuses.Pending,
            DeliveryRef = deliveryRef,
            IntegrationBranch = branchName,
            Detail = deliveryRef is null
                ? $"Accepted work is not yet in {branchName}."
                : $"Delivery ref '{deliveryRef}' is not yet integrated into {branchName}.",
        };
    }

    private static TaskIntegrationStatus Integrated(
        string sha,
        string branchName,
        string? deliveryRef,
        string detail) => new()
    {
        Status = IntegrationStatuses.Integrated,
        Sha = sha,
        DeliveryRef = deliveryRef,
        IntegrationBranch = branchName,
        Detail = detail,
    };

    /// <summary>
    /// Projects the same delivery-ref resolution used by acceptance onto the
    /// card. Immutable result refs, attributed commit branches, and canonical
    /// runner refs are durable card truth. The resolver's final
    /// <c>task/&lt;slug&gt;</c> compatibility value is only surfaced when
    /// provenance proves that a local task branch actually existed; otherwise
    /// it would recreate the ghost badge this projection is meant to remove.
    /// </summary>
    internal static string? DeliveryRefFor(TaskInfo job)
    {
        var resolved = DeliveryRefResolver.Resolve(job.Id, job.FolderPath);
        if (resolved.Source != DeliveryRefSource.LocalTaskFallback)
            return resolved.Ref;

        var attributedBranch = job.Commits
            .LastOrDefault(commit => !string.IsNullOrWhiteSpace(commit.Branch))
            ?.Branch
            ?? job.Commit?.Branch;
        if (!string.IsNullOrWhiteSpace(attributedBranch))
            return TaskIntegrationBranch.Name(attributedBranch, resolved.Ref);

        var hasLocalBranchEvidence = job.Provenance?.Transitions.Any(transition =>
            !string.IsNullOrWhiteSpace(transition.BranchTip)) == true;
        if (!hasLocalBranchEvidence)
            return null;

        return string.IsNullOrWhiteSpace(job.Provenance?.Branch)
            ? resolved.Ref
            : TaskIntegrationBranch.Name(job.Provenance!.Branch, resolved.Ref);
    }

    /// <summary>
    /// Reads and classifies the deferred merge-into-develop step outcome from
    /// the card's local <c>pipeline-execution.json</c>. Local file read only (no
    /// git spawn); best-effort. Legacy steps without a persisted failure code
    /// are classified from their stable verdict and reason vocabulary.
    /// </summary>
    private AcceptedIntegrationFailure? ReadIntegrationFailure(TaskInfo job)
    {
        if (!AcceptanceIntegrationPolicy.IsIntegrationRequired(job)) return null;

        var step = ReadLatestMergeStep(job);
        if (string.Equals(step?.Verdict, "operator-override", StringComparison.OrdinalIgnoreCase))
            return null;

        if (step is not null)
        {
            var mergeFailure = AcceptedIntegrationFailurePolicy.Classify(
                step.Status,
                step.Verdict,
                step.Reason,
                step.VerdictSummary,
                step.FailureCode);
            if (mergeFailure is not null) return mergeFailure;
        }

        // AGT-2688: the merge into the integration branch can succeed locally
        // while the deferred push to origin does not (main/develop lineage
        // block, or a genuinely diverged remote). That must not fall through to
        // plain "pending" - surface the push step's own terminal failure so
        // acceptance alarms instead of looping.
        var pushStep = ReadLatestPushStep(job);
        if (pushStep is null) return null;
        return AcceptedIntegrationFailurePolicy.Classify(
            pushStep.Status,
            pushStep.Verdict,
            pushStep.Reason,
            pushStep.VerdictSummary,
            pushStep.FailureCode);
    }

    private static string VisibleFailureReason(
        TaskInfo job,
        string branchName,
        AcceptedIntegrationFailure failure)
    {
        if (failure.Code != AcceptedIntegrationFailureCodes.MergeConflict)
            return failure.Reason;

        var delivery = ReviewSubjectStore.Read(job.FolderPath)?.ResultRef
            ?? WorktreeTaskLifecycle.BranchFor(job.Id);
        return $"{failure.Reason} Start the integration recovery action to run a steer round: "
               + $"rebase '{delivery}' onto the current integration branch '{branchName}', "
               + "resolve the conflicts, and deliver the updated branch.";
    }

    internal PipelineStepExecution? ReadLatestMergeStep(TaskInfo job)
    {
        try
        {
            return _pipelineLog.Read(job.FolderPath)?.Steps.LastOrDefault(step =>
                string.Equals(
                    step.StepId,
                    PipelineCatalogue.MergeIntoDevelopStepId,
                    StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "integration-status pipeline read failed for project={Project} job={JobId}",
                job.ProjectName,
                job.Id);
            return null;
        }
    }

    private PipelineStepExecution? ReadLatestPushStep(TaskInfo job)
    {
        try
        {
            return _pipelineLog.Read(job.FolderPath)?.Steps.LastOrDefault(step =>
                string.Equals(
                    step.StepId,
                    PipelineCatalogue.MergeIntoDevelopPushStepId,
                    StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "integration-status push-step read failed for project={Project} job={JobId}",
                job.ProjectName,
                job.Id);
            return null;
        }
    }

    private static bool IsDecidedIntegrationAttempt(PipelineStepExecution? step)
    {
        if (step is null) return false;
        if (string.Equals(step.Verdict, "conflict", StringComparison.OrdinalIgnoreCase)
            || string.Equals(step.Verdict, "pushed-for-review", StringComparison.OrdinalIgnoreCase)
            || string.Equals(step.Verdict, "gate-failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(step.Verdict, "error", StringComparison.OrdinalIgnoreCase)
            || string.Equals(step.Verdict, "no-branch", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// The card anchor read entirely from the persisted board payload (no git
    /// spawn): the latest attributed TASK commit SHA. Null when the card committed
    /// nothing. Mirrors <see cref="BoardMergeStatusService.AnchorFor"/>.
    /// </summary>
    internal static string? AnchorFor(TaskInfo job)
    {
        var attributed = AttributedCommits(job);
        return attributed.Count == 0 ? null : attributed[^1];
    }

    /// <summary>
    /// The attributed commit SHAs the card's commit widget renders (oldest →
    /// newest), read entirely from the persisted board payload (no git spawn).
    /// Falls back to the legacy single <see cref="TaskInfo.Commit"/> when the list
    /// is empty, and drops blank SHAs plus zero-file platform lifecycle markers.
    /// A marker-shaped commit with changed files remains integrable work unless
    /// a later delivery attempt explicitly superseded it. Superseded entries
    /// remain in <c>commits[]</c> as history but are not current integration
    /// expectations. Empty when the card committed nothing.
    /// </summary>
    internal static IReadOnlyList<string> AttributedCommits(TaskInfo job)
        => AttributedCommits(job, null);

    /// <summary>
    /// Target-aware attribution filter. A reachable SHA is delivery truth even
    /// when stale persisted metadata makes it look like an empty lifecycle
    /// marker. The ancestry proof therefore outranks the marker heuristic.
    /// </summary>
    internal static IReadOnlyList<string> AttributedCommits(
        TaskInfo job,
        IReadOnlySet<string>? integrationAncestors)
    {
        var result = new List<string>(job.Commits.Count);
        if (job.Commits.Count > 0)
        {
            foreach (var c in job.Commits)
                if (!string.IsNullOrWhiteSpace(c.Sha)
                    && !TaskCommitSupersession.IsSuperseded(c)
                    && (!IsZeroFileLifecycleMarker(c)
                        || integrationAncestors is not null
                        && AncestorSetContains(integrationAncestors, c.Sha)))
                    result.Add(c.Sha);
        }
        else if (!string.IsNullOrWhiteSpace(job.Commit?.Sha)
                 && !TaskCommitSupersession.IsSuperseded(job.Commit!)
                 && (!IsZeroFileLifecycleMarker(job.Commit!)
                     || integrationAncestors is not null
                     && AncestorSetContains(integrationAncestors, job.Commit!.Sha)))
        {
            result.Add(job.Commit!.Sha);
        }
        return result;
    }

    /// <summary>
    /// Membership check for persisted task SHAs. Older task records can contain
    /// the seven-character display SHA rather than the full object id returned by
    /// <c>rev-list</c>. Treat a valid abbreviated SHA as landed when it prefixes a
    /// reachable full SHA; full SHAs still use exact membership.
    /// </summary>
    internal static bool AncestorSetContains(IReadOnlySet<string> ancestors, string sha)
    {
        if (string.IsNullOrWhiteSpace(sha)) return false;
        var candidate = sha.Trim();
        if (ancestors.Contains(candidate)) return true;
        if (candidate.Length is < 7 or >= 40 || !candidate.All(Uri.IsHexDigit))
            return false;

        return ancestors.Any(ancestor =>
            ancestor.StartsWith(candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsZeroFileLifecycleMarker(TaskCommitInfo commit)
    {
        if (commit.FilesChanged != 0 || commit.Files.Count != 0)
            return false;

        var subject = commit.Message
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()
            ?.Trim() ?? "";
        return subject.StartsWith("wip(runner): salvage before teardown", StringComparison.OrdinalIgnoreCase)
               || subject.StartsWith("chore: snapshot for review", StringComparison.OrdinalIgnoreCase);
    }

    private List<RepositoryCommitGroup> RepositoryGroups(TaskInfo job)
    {
        var primaryRoot = _git.ResolveRepoRootForWatchPath(job.WatchPath);
        var primaryOrigin = _git.ReadOriginUrl(primaryRoot);
        var primaryRepository = RepositoryIdentityContract.FromUrl(primaryOrigin)
            ?? primaryOrigin
            ?? primaryRoot
            ?? job.ProjectName;
        var configuredPrimaryBranch = ConfiguredIntegrationBranch(job);
        var primary = new RepositoryDescriptor(
            primaryRepository,
            CommitRepositoryMetadata.Label(primaryOrigin ?? primaryRoot ?? job.ProjectName),
            primaryRoot,
            string.IsNullOrWhiteSpace(primaryRoot)
                ? string.IsNullOrWhiteSpace(configuredPrimaryBranch) ? "main" : configuredPrimaryBranch
                : _git.ResolveIntegrationBranch(primaryRoot, configuredPrimaryBranch),
            BoardMergeStatusService.ReleaseBranch);

        var registered = (_projects?.List() ?? [])
            .Select(project =>
            {
                var url = CommitRepositoryMetadata.RepositoryUrl(project);
                var identity = RepositoryIdentityContract.FromUrl(url) ?? url ?? project.Id;
                var root = Directory.Exists(project.RepositoryPath) ? project.RepositoryPath : null;
                var configuredBranch = _settings.Get(project.DisplayName).IntegrationBranch;
                return new RegisteredRepository(
                    project,
                    new RepositoryDescriptor(
                        identity,
                        CommitRepositoryMetadata.Label(url ?? project.DisplayName),
                        root,
                        string.IsNullOrWhiteSpace(root)
                            ? string.IsNullOrWhiteSpace(configuredBranch) ? "main" : configuredBranch
                            : _git.ResolveIntegrationBranch(root, configuredBranch),
                        BoardMergeStatusService.ReleaseBranch));
            })
            .ToList();

        var commits = AttributedCommitRecords(job).ToList();
        if (commits.Count == 0
            && ReviewSubjectStore.Read(job.FolderPath)?.ResultSha is { } reviewed
            && ReviewSubjectStore.IsValidResultSha(reviewed))
        {
            commits.Add(new TaskCommitInfo
            {
                Sha = reviewed,
                ShortSha = Short(reviewed),
                Repository = primary.Repository,
                Branch = primary.IntegrationBranch,
                FilesChanged = 1,
                Attribution = "review-subject",
            });
        }

        return commits
            .Select(commit => (Commit: commit, Descriptor: ResolveRepository(commit, primary, registered)))
            .GroupBy(item => item.Descriptor.Repository, StringComparer.OrdinalIgnoreCase)
            .Select(group => new RepositoryCommitGroup(
                group.Key,
                group.First().Descriptor.Label,
                group.First().Descriptor.Root,
                group.First().Descriptor.IntegrationBranch,
                group.First().Descriptor.ReleaseBranch,
                group.Select(item => item.Commit).ToList()))
            .OrderBy(group => group.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private RepositoryDescriptor ResolveRepository(
        TaskCommitInfo commit,
        RepositoryDescriptor primary,
        IReadOnlyList<RegisteredRepository> registered)
    {
        var repository = commit.Repository
            ?? CommitRepositoryMetadata.LegacyRepositoryPrefix(commit.Message);
        if (string.IsNullOrWhiteSpace(repository)) return primary;

        var match = registered.FirstOrDefault(candidate =>
            CommitRepositoryMetadata.Matches(candidate.Project, repository));
        if (match is not null) return match.Descriptor;
        if (string.Equals(repository, primary.Repository, StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                CommitRepositoryMetadata.Label(repository),
                primary.Label,
                StringComparison.OrdinalIgnoreCase))
        {
            return primary;
        }

        var branch = string.Equals(commit.Branch, "develop", StringComparison.OrdinalIgnoreCase)
            || string.Equals(commit.Branch, "main", StringComparison.OrdinalIgnoreCase)
                ? commit.Branch!
                : "main";
        var remoteRoot = commit.Repository is null
            ? null
            : _git.PrepareRemoteReadRepository(
                commit.Repository,
                [branch, BoardMergeStatusService.ReleaseBranch]);
        return new RepositoryDescriptor(
            repository,
            CommitRepositoryMetadata.Label(repository),
            remoteRoot,
            branch,
            BoardMergeStatusService.ReleaseBranch);
    }

    private static IEnumerable<TaskCommitInfo> AttributedCommitRecords(TaskInfo job)
    {
        var commits = job.Commits.Count > 0
            ? job.Commits
            : job.Commit is null ? [] : [job.Commit];
        return commits.Where(commit =>
            !string.IsNullOrWhiteSpace(commit.Sha)
            && !TaskCommitSupersession.IsSuperseded(commit));
    }

    /// <summary>
    /// The target-branch ancestor SHA set for one repository, cached per target
    /// HEAD fingerprint and computed under the read-only concurrency limiter.
    /// </summary>
    private RepoIntegration ComputeRepoIntegration(
        string root,
        string configuredBranch,
        string releaseBranch)
    {
        Interlocked.Increment(ref _computationCount);
        return ReadOnlyGitConcurrencyLimiter.Run(() =>
        {
            var integrationRef = _git.ResolveIntegrationReadRef(root, configuredBranch);
            var integrationBranch = integrationRef.StartsWith("origin/", StringComparison.Ordinal)
                ? integrationRef["origin/".Length..]
                : integrationRef;

            var succeeded = _git.TryGetAncestorShaSet(
                root,
                [integrationBranch, "origin/" + integrationBranch],
                out var ancestors);

            var sameBranch = string.Equals(
                integrationBranch,
                releaseBranch,
                StringComparison.OrdinalIgnoreCase);
            var releaseSucceeded = succeeded;
            var releaseAncestors = ancestors;
            if (!sameBranch)
            {
                releaseSucceeded = _git.TryGetAncestorShaSet(
                    root,
                    [releaseBranch, "origin/" + releaseBranch],
                    out releaseAncestors);
            }

            return new RepoIntegration(
                integrationBranch,
                releaseBranch,
                ancestors,
                releaseAncestors,
                succeeded && releaseSucceeded);
        });
    }

    private string ConfiguredIntegrationBranch(TaskInfo task)
    {
        return _settings.Get(task.ProjectName).IntegrationBranch;
    }

    internal void InvalidateCache() => _cache.Invalidate();
    internal int ComputationCount => Volatile.Read(ref _computationCount);

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    private sealed record RepoIntegration(
        string IntegrationBranch,
        string ReleaseBranch,
        HashSet<string> DevelopAncestors,
        HashSet<string> ReleaseAncestors,
        bool Succeeded);

    private sealed record RepoBranchKey(string Root, string Branch, string ReleaseBranch);
    private sealed record RepositoryDescriptor(
        string Repository,
        string Label,
        string? Root,
        string IntegrationBranch,
        string ReleaseBranch);
    private sealed record RegisteredRepository(
        ProjectRecord Project,
        RepositoryDescriptor Descriptor);
    private sealed record RepositoryCommitGroup(
        string Repository,
        string Label,
        string? Root,
        string IntegrationBranch,
        string ReleaseBranch,
        IReadOnlyList<TaskCommitInfo> Commits);
}

internal enum AcceptedIntegrationRecoveryAction
{
    Ignore,
    Finalize,
    ReturnToReview,
    Retry,
}

internal sealed record AcceptedIntegrationRecoveryDecision(
    AcceptedIntegrationRecoveryAction Action,
    string Reason,
    PipelineStepExecution? LastMergeAttempt);
