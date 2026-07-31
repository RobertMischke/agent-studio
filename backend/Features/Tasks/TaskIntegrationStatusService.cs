using System.Collections.Concurrent;

namespace AgentStudio.Tasks;

/// <summary>
/// AGT-2202 - computes the honest, git-derived integration verdict for accepted
/// cards (5-human-review / 6-completed / 7-archive): is the task's work actually
/// folded into the integration branch (develop)? The result is attached to
/// <see cref="TaskInfo.Integration"/> so the board renders a single, unambiguous
/// "integrated / not integrated / conflict / no branch" badge on every accepted
/// card, and so the accept flow can flag an accept-without-merge the moment it
/// happens.
///
/// <para>
/// The verdict's <b>anchor is the integrable subset of the attributed
/// <c>commits[]</c> list</b>. Zero-file runner lifecycle markers are not delivery
/// expectations; every commit that carries changed files remains an anchor. This
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
///   merge-into-develop conflict was recorded);</item>
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
/// override commit membership. This also detects out-of-band merges on the next
/// read. Per-card local reads resolve the delivery ref from the same task card
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

    /// <summary>The accepted lanes this verdict applies to. Cards outside get no entry.</summary>
    internal static readonly HashSet<string> AcceptedLanes = new(StringComparer.Ordinal)
    {
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
        ILogger<TaskIntegrationStatusService> logger)
        : this(git, settings, pipelineLog, logger, TimeProvider.System)
    {
    }

    internal TaskIntegrationStatusService(
        GitService git,
        ProjectSettingsService settings,
        PipelineExecutionLog pipelineLog,
        ILogger<TaskIntegrationStatusService> logger,
        TimeProvider timeProvider)
    {
        _git = git;
        _settings = settings;
        _pipelineLog = pipelineLog;
        _logger = logger;
        _cache = new GenerationSingleFlightCache<RepoIntegration>(timeProvider);
    }

    /// <summary>
    /// Per-<see cref="TaskInfo.TaskKey"/> integration verdict for the accepted cards
    /// in the given board set. Only cards in <see cref="AcceptedLanes"/> get an
    /// entry; every other card carries no verdict and the card renders none. Never
    /// throws.
    /// </summary>
    public Dictionary<string, TaskIntegrationStatus> BuildLookup(IReadOnlyCollection<TaskInfo> jobs)
    {
        var result = new Dictionary<string, TaskIntegrationStatus>(StringComparer.Ordinal);
        if (jobs.Count == 0) return result;

        using var _t = GitProcessTelemetry.BeginRequest("board/integration-status", _logger);

        var byRepo = new Dictionary<RepoBranchKey, List<TaskInfo>>();
        var noRepo = new List<TaskInfo>();
        foreach (var job in jobs)
        {
            if (!AcceptedLanes.Contains(job.State)) continue;
            var root = _git.ResolveRepoRootForWatchPath(job.WatchPath);
            if (string.IsNullOrWhiteSpace(root))
            {
                noRepo.Add(job);
                continue;
            }
            var key = new RepoBranchKey(root!, ConfiguredIntegrationBranch(job));
            if (!byRepo.TryGetValue(key, out var list))
            {
                list = new List<TaskInfo>();
                byRepo[key] = list;
            }
            list.Add(job);
        }

        // Cards with no resolvable repo can still be honestly classified: a card
        // with no anchor commit and no branch is no-branch, anything else pending.
        foreach (var job in noRepo)
            result[job.TaskKey] = ClassifyNotIntegrated(
                job,
                ConfiguredIntegrationBranch(job),
                repoResolved: false);

        var reaches = new ConcurrentDictionary<RepoBranchKey, RepoIntegration>();
        Parallel.ForEach(
            byRepo,
            new ParallelOptions { MaxDegreeOfParallelism = ReadOnlyGitConcurrencyLimiter.MaxConcurrency },
            pair =>
            {
                var cacheKey = $"{pair.Key.Root}\0{pair.Key.Branch}";
                var refFingerprint = ReadOnlyGitRefFingerprint.CaptureDetailed(pair.Key.Root, [pair.Key.Branch]);
                reaches[pair.Key] = _cache.GetOrCreateVersioned(
                    cacheKey,
                    refFingerprint.Value,
                    value => value.Succeeded
                        ? refFingerprint.RequiresShortFallback ? ShortFallbackTtl : CacheTtl
                        : FailureCacheTtl,
                    () => ComputeRepoIntegration(pair.Key.Root, pair.Key.Branch));
            });

        foreach (var (repoBranch, repoJobs) in byRepo)
        {
            var reach = reaches[repoBranch];
            foreach (var job in repoJobs)
                result[job.TaskKey] = ClassifyWithRepo(job, reach);
        }

        return result;
    }

    /// <summary>
    /// Resolves restart recovery for accepted integrations from the same
    /// target-branch truth used by <see cref="BuildLookup"/>. Pipeline attempts
    /// remain durable recovery facts, but a stale Passed step cannot overrule
    /// missing target-branch ancestry.
    /// </summary>
    internal Dictionary<string, AcceptedIntegrationRecoveryDecision> BuildAcceptedRecoveryLookup(
        IReadOnlyCollection<TaskInfo> jobs)
    {
        var candidates = jobs
            .Where(job => AcceptedLanes.Contains(job.State))
            .Select(job => new AcceptedIntegrationRecoveryCandidate(
                job,
                ReadLatestMergeStep(job)))
            .ToList();
        if (candidates.Count == 0)
            return new Dictionary<string, AcceptedIntegrationRecoveryDecision>(StringComparer.Ordinal);

        var statusByKey = BuildLookup(candidates.Select(candidate => candidate.Job).ToList());
        var result = new Dictionary<string, AcceptedIntegrationRecoveryDecision>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            statusByKey.TryGetValue(candidate.Job.TaskKey, out var status);
            result[candidate.Job.TaskKey] = ResolveAcceptedRecovery(candidate, status);
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
            _logger.LogWarning(
                ex,
                "integration-status fenced ancestry read failed for project={Project} job={JobId}",
                job.ProjectName,
                job.Id);
            return false;
        }
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
    private TaskIntegrationStatus ClassifyWithRepo(TaskInfo job, RepoIntegration reach)
    {
        var branchName = reach.IntegrationBranch;
        var deliveryRef = DeliveryRefFor(job);

        // Anchor = integrable entries in the attributed commits[] list. Zero-file
        // runner lifecycle markers are metadata, not delivery expectations.
        var attributed = AttributedCommits(job);
        if (attributed.Count == 0)
            return ClassifyNotIntegrated(job, branchName, deliveryRef, repoResolved: true);

        var missing = new List<string>();
        foreach (var sha in attributed)
            if (!AncestorSetContains(reach.DevelopAncestors, sha)) missing.Add(sha);

        // NONE of the attributed commits landed → conflict-skipped / pending
        // (no-branch is impossible here: there IS attributed work).
        if (missing.Count == attributed.Count)
            return ClassifyNotIntegrated(job, branchName, deliveryRef, repoResolved: true);

        var newest = attributed[^1];

        // ALL attributed commits landed. Attempt history and recorded merge
        // provenance are deliberately irrelevant to this result.
        if (missing.Count == 0)
            return Integrated(Short(newest), branchName, deliveryRef, "anchor-ancestor");

        // SOME landed, some did not → partial, naming the missing short-SHAs so the
        // tooltip says exactly which attributed commits are not in develop yet.
        var integratedCount = attributed.Count - missing.Count;
        var missingShort = string.Join(", ", missing.Select(Short));
        return new TaskIntegrationStatus
        {
            Status = IntegrationStatuses.Partial,
            DeliveryRef = deliveryRef,
            IntegrationBranch = branchName,
            Detail = $"{integratedCount}/{attributed.Count} attributed commits integrated; "
                     + $"missing: {missingShort}",
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
        string? deliveryRef = null,
        bool repoResolved = false)
    {
        deliveryRef ??= DeliveryRefFor(job);
        var anchor = AnchorFor(job);
        var hasWork = anchor != null || deliveryRef != null;

        if (repoResolved && ReadMergeConflict(job) is { } conflictDetail)
            return new TaskIntegrationStatus
            {
                Status = IntegrationStatuses.ConflictSkipped,
                DeliveryRef = deliveryRef,
                IntegrationBranch = branchName,
                Detail = conflictDetail,
            };

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
    /// Reads the deferred merge-into-develop step outcome from the card's local
    /// <c>pipeline-execution.json</c> and returns a conflict detail string when the
    /// merge was recorded conflicted / errored, else null. Local file read only (no
    /// git spawn); best-effort - any failure reads as "no recorded conflict".
    /// </summary>
    private string? ReadMergeConflict(TaskInfo job)
    {
        var step = ReadLatestMergeStep(job);
        if (step is null) return null;
        if (string.Equals(step.Verdict, "conflict", StringComparison.OrdinalIgnoreCase))
        {
            var evidence = step.VerdictSummary ?? step.Reason ?? "Merge into develop hit a conflict; not merged.";
            var delivery = ReviewSubjectStore.Read(job.FolderPath)?.ResultRef
                ?? WorktreeTaskLifecycle.BranchFor(job.Id);
            return $"{evidence} Start the integration recovery action to run a steer round: "
                   + $"rebase '{delivery}' onto the current integration branch '{ConfiguredIntegrationBranch(job)}', "
                   + "resolve the conflicts, and deliver the updated branch.";
        }
        // The pre-develop build gate found the merge result red and rolled the
        // integration branch back: the work is genuinely not in develop, so the
        // card must read as not-integrated with the gate's reason attached.
        if (string.Equals(step.Verdict, "gate-failed", StringComparison.OrdinalIgnoreCase))
            return step.Reason ?? "The build gate blocked the merge into develop; not merged.";
        if (string.Equals(step.Verdict, "no-branch", StringComparison.OrdinalIgnoreCase))
            return step.Reason ?? "The delivery ref could not be resolved or fetched; not merged.";
        if (step.Status == PipelineStepStatus.Failed
            && string.Equals(step.Verdict, "error", StringComparison.OrdinalIgnoreCase))
            return step.Reason ?? "Merge into develop failed; not merged.";
        return null;
    }

    private PipelineStepExecution? ReadLatestMergeStep(TaskInfo job)
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

    private AcceptedIntegrationRecoveryDecision ResolveAcceptedRecovery(
        AcceptedIntegrationRecoveryCandidate candidate,
        TaskIntegrationStatus? status)
    {
        var job = candidate.Job;
        var lastMerge = candidate.LastMergeAttempt;
        if (status?.Status == IntegrationStatuses.Integrated)
        {
            if (lastMerge?.Status == PipelineStepStatus.Passed)
            {
                var requiresFinalization = (job.Tags ?? []).Any(IntegrationStatuses.IsPendingTag)
                                           || (job.State == TaskStates.HumanReview
                                               && string.Equals(
                                                   job.Phase,
                                                   LifecyclePhases.Integrating,
                                                   StringComparison.Ordinal));
                return new AcceptedIntegrationRecoveryDecision(
                    requiresFinalization
                        ? AcceptedIntegrationRecoveryAction.Finalize
                        : AcceptedIntegrationRecoveryAction.None,
                    lastMerge?.Verdict ?? "already-integrated",
                    lastMerge?.Reason ?? lastMerge?.VerdictSummary);
            }

            if (!IsFencedDeliveryIntegrated(job))
            {
                return new AcceptedIntegrationRecoveryDecision(
                    AcceptedIntegrationRecoveryAction.Finalize,
                    "already-integrated");
            }

            // The fenced SHA is already present but the terminal merge/push
            // record is missing. Replay the idempotent runner to restore it.
            return new AcceptedIntegrationRecoveryDecision(
                AcceptedIntegrationRecoveryAction.Retry,
                "missing-terminal-record");
        }

        if (IsDecidedIntegrationAttempt(lastMerge, job))
        {
            return new AcceptedIntegrationRecoveryDecision(
                AcceptedIntegrationRecoveryAction.ReturnToReview,
                lastMerge?.Verdict ?? "integration-failed",
                lastMerge?.Reason ?? lastMerge?.VerdictSummary);
        }

        // This includes a stale Passed step that contradicts current Git truth.
        // The idempotent runner must revalidate it instead of completing a lying
        // transaction.
        return new AcceptedIntegrationRecoveryDecision(
            AcceptedIntegrationRecoveryAction.Retry,
            lastMerge?.Status == PipelineStepStatus.Passed
                ? "passed-step-without-target-ancestry"
                : "unfinished-integration");
    }

    private static bool IsDecidedIntegrationAttempt(
        PipelineStepExecution? step,
        TaskInfo job)
    {
        if (step is null) return false;
        if (string.Equals(step.Verdict, "conflict", StringComparison.OrdinalIgnoreCase)
            || string.Equals(step.Verdict, "pushed-for-review", StringComparison.OrdinalIgnoreCase)
            || string.Equals(step.Verdict, "gate-failed", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(step.Verdict, "no-branch", StringComparison.OrdinalIgnoreCase)
               && ReviewSubjectStore.Read(job.FolderPath) is null;
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
    /// A marker-shaped commit with changed files remains integrable work. Empty
    /// when the card committed nothing.
    /// </summary>
    internal static IReadOnlyList<string> AttributedCommits(TaskInfo job)
    {
        var result = new List<string>(job.Commits.Count);
        if (job.Commits.Count > 0)
        {
            foreach (var c in job.Commits)
                if (!string.IsNullOrWhiteSpace(c.Sha) && !IsZeroFileLifecycleMarker(c))
                    result.Add(c.Sha);
        }
        else if (!string.IsNullOrWhiteSpace(job.Commit?.Sha)
                 && !IsZeroFileLifecycleMarker(job.Commit!))
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

    /// <summary>
    /// The target-branch ancestor SHA set for one repository, cached per target
    /// HEAD fingerprint and computed under the read-only concurrency limiter.
    /// </summary>
    private RepoIntegration ComputeRepoIntegration(string root, string configuredBranch)
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

            return new RepoIntegration(integrationBranch, ancestors, succeeded);
        });
    }

    private string ConfiguredIntegrationBranch(TaskInfo task)
    {
        return TaskIntegrationBranch.Resolve(
            task,
            _settings.Get(task.ProjectName).IntegrationBranch);
    }

    internal void InvalidateCache() => _cache.Invalidate();
    internal int ComputationCount => Volatile.Read(ref _computationCount);

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    private sealed record RepoIntegration(
        string IntegrationBranch,
        HashSet<string> DevelopAncestors,
        bool Succeeded);

    private sealed record RepoBranchKey(string Root, string Branch);

    private sealed record AcceptedIntegrationRecoveryCandidate(
        TaskInfo Job,
        PipelineStepExecution? LastMergeAttempt);
}

internal enum AcceptedIntegrationRecoveryAction
{
    None,
    Finalize,
    ReturnToReview,
    Retry,
}

internal sealed record AcceptedIntegrationRecoveryDecision(
    AcceptedIntegrationRecoveryAction Action,
    string Outcome,
    string? Detail = null);
