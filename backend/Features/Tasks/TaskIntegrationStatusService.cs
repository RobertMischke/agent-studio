using System.Collections.Concurrent;
using AgentStudio.Registry;

namespace AgentStudio.Tasks;

/// <summary>
/// AGT-2202 and AGT-2718: computes the honest, Git-derived integration verdict
/// for delivered cards (4-auto-review / 5-human-review / 5e-escalated /
/// 6-completed / 7-archive). Each attributed repository is evaluated against
/// its own integration and release branches. The result is attached to
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
/// <item>ALL attributed commits are ancestors of their repository target branches → <c>integrated</c> (even
///   when the branch tip still carries further, un-integrated WIP commits the
///   widget never showed);</item>
/// <item>SOME attributed commits are ancestors → <c>partial</c>, with the missing
///   repository and short SHAs in the detail;</item>
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
    private readonly ProjectRegistry? _registry;

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
        ProjectRegistry? registry = null)
        : this(git, settings, pipelineLog, logger, TimeProvider.System, registry)
    {
    }

    internal TaskIntegrationStatusService(
        GitService git,
        ProjectSettingsService settings,
        PipelineExecutionLog pipelineLog,
        ILogger<TaskIntegrationStatusService> logger,
        TimeProvider timeProvider,
        ProjectRegistry? registry = null)
    {
        _git = git;
        _settings = settings;
        _pipelineLog = pipelineLog;
        _logger = logger;
        _registry = registry;
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

        foreach (var job in jobs)
        {
            if (!DeliveredLanes.Contains(job.State)) continue;
            result[job.TaskKey] = ClassifyRepositories(job);
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
    /// The verdict for one card given its repositories' cached integration facts.
    /// A verdict is derived entirely from target-branch ancestry of the card's attributed
    /// <c>commits[]</c> (the same list the commit widget shows): all landed →
    /// integrated, some → partial, none → pending/conflict, and no attributed
    /// commit plus no delivery ref → no-branch. The branch tip is deliberately
    /// NOT an ancestry anchor - it is a WIP snapshot the widget never shows,
    /// whose use was the AGT-2171 badge/widget self-contradiction. It remains
    /// valid evidence that a local delivery ref exists.
    /// </summary>
    private TaskIntegrationStatus ClassifyRepositories(TaskInfo job)
    {
        var entries = BuildRepositoryEntries(job);
        var deliveryRef = DeliveryRefFor(job);
        var primaryBranch = entries.FirstOrDefault()?.IntegrationBranch
                            ?? ConfiguredIntegrationBranch(job);

        // A current review subject remains a fallback only for old single-repo
        // cards whose attribution is empty.
        if (entries.Count == 0)
        {
            var target = ResolveRepositoryTarget(job, null, null);
            var reach = GetRepoIntegration(target);
            var reviewedResultSha = ReviewSubjectStore.Read(job.FolderPath)?.ResultSha;
            if (!string.IsNullOrWhiteSpace(reviewedResultSha)
                && AncestorSetContains(reach.IntegrationAncestors, reviewedResultSha))
            {
                return Integrated(
                    Short(reviewedResultSha),
                    reach.IntegrationBranch,
                    deliveryRef,
                    "reviewed-result-ancestor",
                    []);
            }
            return ClassifyNotIntegrated(job, reach.IntegrationBranch, deliveryRef, []);
        }

        if (entries.All(entry => entry.OnIntegrationBranch))
        {
            var expected = entries.SelectMany(entry => entry.Commits)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var newest = EffectiveCommits(job).Last(commit => expected.Contains(commit.Sha)).Sha;
            return Integrated(
                Short(newest),
                primaryBranch,
                deliveryRef,
                "anchor-ancestor",
                entries);
        }

        var anyIntegrated = entries.Any(entry => entry.IntegrationCommitCount > 0);
        if (!anyIntegrated)
            return ClassifyNotIntegrated(job, primaryBranch, deliveryRef, entries);

        var missingDetails = entries
            .Where(entry => !entry.OnIntegrationBranch)
            .Select(entry => entry.Detail);
        return new TaskIntegrationStatus
        {
            Status = IntegrationStatuses.Partial,
            DeliveryRef = deliveryRef,
            IntegrationBranch = primaryBranch,
            Detail = string.Join("; ", missingDetails),
            Repositories = entries,
        };
    }

    /// <summary>Repository membership used by both integration and merge projections.</summary>
    internal List<TaskRepositoryIntegrationStatus> BuildRepositoryEntries(TaskInfo job)
    {
        var commits = EffectiveCommits(job);
        var groups = commits
            .GroupBy(commit => string.IsNullOrWhiteSpace(commit.Repository)
                ? "\0primary"
                : commit.Repository!.Trim(), StringComparer.OrdinalIgnoreCase);
        var entries = new List<TaskRepositoryIntegrationStatus>();
        foreach (var group in groups)
        {
            var groupCandidates = group.ToList();
            var repository = group.Key == "\0primary" ? null : group.Key;
            var target = ResolveRepositoryTarget(job, repository, groupCandidates[^1].Branch);
            var reach = GetRepoIntegration(target);
            var groupCommits = groupCandidates
                .Where(commit => !IsZeroFileLifecycleMarker(commit)
                                 || AncestorSetContains(reach.IntegrationAncestors, commit.Sha))
                .ToList();
            if (groupCommits.Count == 0) continue;
            var shas = groupCommits.Select(commit => commit.Sha).ToList();
            var integrationCount = shas.Count(sha =>
                AncestorSetContains(reach.IntegrationAncestors, sha));
            var releaseCount = shas.Count(sha =>
                AncestorSetContains(reach.ReleaseAncestors, sha));
            var missing = shas
                .Where(sha => !AncestorSetContains(reach.IntegrationAncestors, sha))
                .Select(Short)
                .ToList();
            var detail = missing.Count == 0
                ? $"{target.Repository}: {integrationCount}/{shas.Count} attributed commits on {reach.IntegrationBranch}."
                : $"{target.Repository}: {integrationCount}/{shas.Count} attributed commits on {reach.IntegrationBranch}; missing: {string.Join(", ", missing)}";
            entries.Add(new TaskRepositoryIntegrationStatus
            {
                Repository = target.Repository,
                Commits = shas,
                IntegrationBranch = reach.IntegrationBranch,
                ReleaseBranch = reach.ReleaseBranch,
                IntegrationCommitCount = integrationCount,
                ReleaseCommitCount = releaseCount,
                OnIntegrationBranch = integrationCount == shas.Count,
                OnReleaseBranch = releaseCount == shas.Count,
                Detail = detail,
            });
        }
        return entries;
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
        string? deliveryRef = null,
        List<TaskRepositoryIntegrationStatus>? repositories = null)
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
                Repositories = repositories ?? [],
            };
        }

        if (!hasWork)
            return new TaskIntegrationStatus
            {
                Status = IntegrationStatuses.NoBranch,
                DeliveryRef = null,
                IntegrationBranch = branchName,
                Detail = "No delivery ref or attributed commit to integrate.",
                Repositories = repositories ?? [],
            };

        return new TaskIntegrationStatus
        {
            Status = IntegrationStatuses.Pending,
            DeliveryRef = deliveryRef,
            IntegrationBranch = branchName,
            Detail = deliveryRef is null
                ? $"Accepted work is not yet in {branchName}."
                : $"Delivery ref '{deliveryRef}' is not yet integrated into {branchName}.",
            Repositories = repositories ?? [],
        };
    }

    private static TaskIntegrationStatus Integrated(
        string sha,
        string branchName,
        string? deliveryRef,
        string detail,
        List<TaskRepositoryIntegrationStatus>? repositories = null) => new()
    {
        Status = IntegrationStatuses.Integrated,
        Sha = sha,
        DeliveryRef = deliveryRef,
        IntegrationBranch = branchName,
        Detail = detail,
        Repositories = repositories ?? [],
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

    private static List<TaskCommitInfo> EffectiveCommits(TaskInfo job)
    {
        var source = job.Commits.Count > 0
            ? job.Commits
            : job.Commit is null ? [] : [job.Commit];
        return source
            .Where(commit => !string.IsNullOrWhiteSpace(commit.Sha)
                             && !TaskCommitSupersession.IsSuperseded(commit))
            .ToList();
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

    /// <summary>
    /// The target-branch ancestor SHA set for one repository, cached per target
    /// HEAD fingerprint and computed under the read-only concurrency limiter.
    /// </summary>
    private RepoIntegration GetRepoIntegration(RepositoryTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.Root))
            return new RepoIntegration(
                target.ConfiguredBranch.Length == 0 ? "main" : target.ConfiguredBranch,
                "main", [], [], false);

        string[] refs = target.RemoteName is null
            ? [target.ConfiguredBranch, "main"]
            : [$"refs/remotes/{target.RemoteName}/{target.ConfiguredBranch}", $"refs/remotes/{target.RemoteName}/main"];
        var cacheKey = $"{target.Root}\0{target.RemoteName}\0{target.ConfiguredBranch}";
        var fingerprint = ReadOnlyGitRefFingerprint.CaptureDetailed(target.Root, refs);
        return _cache.GetOrCreateVersioned(
            cacheKey,
            fingerprint.Value,
            value => value.Succeeded
                ? fingerprint.RequiresShortFallback ? ShortFallbackTtl : CacheTtl
                : FailureCacheTtl,
            () => ComputeRepoIntegration(target));
    }

    private RepoIntegration ComputeRepoIntegration(RepositoryTarget target)
    {
        Interlocked.Increment(ref _computationCount);
        return ReadOnlyGitConcurrencyLimiter.Run(() =>
        {
            var root = target.Root!;
            var integrationRef = target.RemoteName is null
                ? _git.ResolveIntegrationReadRef(root, target.ConfiguredBranch)
                : $"{target.RemoteName}/{target.ConfiguredBranch}";
            var integrationBranch = integrationRef.Contains('/')
                ? integrationRef[(integrationRef.LastIndexOf('/') + 1)..]
                : integrationRef;
            var integrationRefs = target.RemoteName is null
                ? new[] { integrationBranch, "origin/" + integrationBranch }
                : new[] { $"{target.RemoteName}/{integrationBranch}" };
            var releaseRefs = target.RemoteName is null
                ? new[] { "main", "origin/main" }
                : new[] { $"{target.RemoteName}/main" };
            var integrationSucceeded = _git.TryGetAncestorShaSet(
                root, integrationRefs, out var integrationAncestors);
            var releaseSucceeded = _git.TryGetAncestorShaSet(
                root, releaseRefs, out var releaseAncestors);
            return new RepoIntegration(
                integrationBranch,
                "main",
                integrationAncestors,
                releaseAncestors,
                integrationSucceeded && releaseSucceeded);
        });
    }

    private RepositoryTarget ResolveRepositoryTarget(
        TaskInfo task,
        string? repository,
        string? branchHint)
    {
        var primaryRoot = _git.ResolveRepoRootForWatchPath(task.WatchPath);
        var primaryRepository = primaryRoot is null ? null : _git.ReadOriginUrlAt(primaryRoot);
        if (string.IsNullOrWhiteSpace(repository)
            || RepositoryMatches(repository, primaryRepository, primaryRoot, task.ProjectName))
        {
            return new RepositoryTarget(
                primaryRoot,
                repository ?? primaryRepository ?? task.ProjectName,
                ConfiguredIntegrationBranch(task),
                null);
        }

        var registered = _registry?.List().FirstOrDefault(project =>
            RepositoryMatches(repository, RegisteredRepository(project), project.RepositoryPath, project.DisplayName)
            || string.Equals(project.Id, repository, StringComparison.OrdinalIgnoreCase));
        if (registered is not null)
        {
            var root = registered.RepositoryPath ?? registered.RootPath;
            var configured = _settings.Get(registered.DisplayName).IntegrationBranch;
            return new RepositoryTarget(
                root,
                RegisteredRepository(registered) ?? registered.Id,
                string.IsNullOrWhiteSpace(configured) ? FallbackIntegrationBranch(branchHint) : configured,
                null);
        }

        if (Directory.Exists(repository) && _git.IsGitRepo(repository))
            return new RepositoryTarget(repository, repository, FallbackIntegrationBranch(branchHint), null);

        var remote = primaryRoot is null ? null : _git.ResolveRemoteNameAt(primaryRoot, repository);
        return new RepositoryTarget(
            remote is null ? null : primaryRoot,
            repository,
            FallbackIntegrationBranch(branchHint),
            remote);
    }

    private static string FallbackIntegrationBranch(string? attributedBranch)
    {
        var branch = TaskIntegrationBranch.Name(attributedBranch, fallback: "");
        return branch is "main" or "master" or "develop" ? branch : "main";
    }

    private static string? RegisteredRepository(ProjectRecord project)
        => project.Urls.FirstOrDefault(url =>
            string.Equals(url.Id, "repo", StringComparison.OrdinalIgnoreCase)
            || string.Equals(url.Label, "repo", StringComparison.OrdinalIgnoreCase)
            || string.Equals(url.Label, "repository", StringComparison.OrdinalIgnoreCase))?.Url;

    private static bool RepositoryMatches(
        string? candidate,
        string? registered,
        string? path,
        string? name)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (string.Equals(NormalizeRepository(candidate), NormalizeRepository(registered), StringComparison.OrdinalIgnoreCase))
            return true;
        var candidateName = RepositoryName(candidate);
        return string.Equals(candidateName, RepositoryName(registered), StringComparison.OrdinalIgnoreCase)
               || string.Equals(candidateName, RepositoryName(path), StringComparison.OrdinalIgnoreCase)
               || string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRepository(string? value)
        => (value ?? string.Empty).Trim().TrimEnd('/', '\\');

    private static string RepositoryName(string? value)
    {
        var normalized = NormalizeRepository(value);
        var separator = Math.Max(normalized.LastIndexOf('/'), Math.Max(normalized.LastIndexOf('\\'), normalized.LastIndexOf(':')));
        var name = separator >= 0 ? normalized[(separator + 1)..] : normalized;
        return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
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
        HashSet<string> IntegrationAncestors,
        HashSet<string> ReleaseAncestors,
        bool Succeeded);

    private sealed record RepositoryTarget(
        string? Root,
        string Repository,
        string ConfiguredBranch,
        string? RemoteName);
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
