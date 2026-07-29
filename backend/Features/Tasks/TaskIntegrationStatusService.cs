using System.Collections.Concurrent;

namespace AgentStudio.Tasks;

/// <summary>
/// AGT-2202 — computes the honest, git-derived integration verdict for accepted
/// cards (5-human-review / 6-completed / 7-archive): is the task's work actually
/// folded into the integration branch (develop)? The result is attached to
/// <see cref="TaskInfo.Integration"/> so the board renders a single, unambiguous
/// "integrated / not integrated / conflict / no branch" badge on every accepted
/// card, and so the accept flow can flag an accept-without-merge the moment it
/// happens.
///
/// <para>
/// This exists next to (not instead of) <see cref="BoardMergeStatusService"/>:
/// that service answers the always-on two-segment <c>[develop|main]</c> indicator
/// from <b>anchor ancestry</b>, which the async <b>curated</b> auto-integrator
/// defeats - it rewrites commits and lands them under a single
/// <c>merge(&lt;KEY&gt;)</c> commit, so a task's own SHAs are frequently NOT
/// ancestors of develop even though the work is in. Ground truth is only the
/// develop git-log.
/// </para>
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
/// <item>a curated <c>merge(&lt;KEY&gt;)</c> / <c>merge-recut(&lt;KEY&gt;)</c> commit
///   in the develop log, or the recorded develop-merge fact - either forces
///   <c>integrated</c> (authoritative for the curated integrator);</item>
/// <item>ALL attributed commits are ancestors of develop → <c>integrated</c> (even
///   when the branch tip still carries further, un-integrated WIP commits the
///   widget never showed - the detail then names how many);</item>
/// <item>SOME attributed commits are ancestors → <c>partial</c>, with the missing
///   short-SHAs in the detail;</item>
/// <item>NONE are ancestors → <c>pending</c> (or <c>conflict-skipped</c> when a
///   merge-into-develop conflict was recorded);</item>
/// <item>no attributed commit at all → <c>no-branch</c>.</item>
/// </list>
/// </para>
///
/// <para>
/// Same design invariant as <see cref="BoardMergeStatusService"/>: <b>no per-card
/// git spawn on the hot path</b>. Per repository it computes ONE develop ancestor
/// SHA set plus ONE bounded <c>git log --grep</c> curated-merge map, cached for a
/// ref-fingerprinted TTL, and answers every card in that repo with in-memory
/// lookups. The only per-card touches are best-effort and confined to small
/// subsets: a local <c>pipeline-execution.json</c> read for the not-integrated
/// subset (conflict-skipped vs. plain pending), and one bounded
/// <c>rev-list --count</c> for the rare fully-integrated-but-branch-tip-ahead
/// subset (to name the WIP-commit count). Never throws: a git failure yields the
/// conservative reading.
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
                result[job.TaskKey] = ClassifyWithRepo(job, reach, repoBranch.Root);
        }

        return result;
    }

    /// <summary>
    /// Returns whether the exact fenced remote delivery reviewed for this task is
    /// already an ancestor of the configured integration branch. This is a
    /// recovery-only distinction: a curated rebase can make the card truthfully
    /// <c>integrated</c> without preserving the original result SHA, while a
    /// process crash after the local merge leaves that exact SHA contained but
    /// may lose the pipeline record and queued push.
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
    /// The verdict for one card given its repo's cached integration facts. A
    /// curated / recorded merge forces <c>integrated</c>; otherwise the verdict is
    /// derived ENTIRELY from the develop-ancestry of the card's attributed
    /// <c>commits[]</c> (the same list the commit widget shows): all landed →
    /// integrated, some → partial, none → pending/conflict, and no attributed
    /// commit at all → no-branch. The branch tip is deliberately NOT an anchor - it
    /// is a WIP snapshot the widget never shows, whose use was the AGT-2171
    /// badge/widget self-contradiction.
    /// </summary>
    private TaskIntegrationStatus ClassifyWithRepo(TaskInfo job, RepoIntegration reach, string root)
    {
        var branchName = reach.IntegrationBranch;

        // (a) Curated integrator merge commit for this key on develop. The
        // authoritative signal a rewritten/curated merge leaves behind - it forces
        // integrated even when the task's own SHAs are not ancestors.
        if (!string.IsNullOrWhiteSpace(job.Key)
            && reach.MergeShaByKey.TryGetValue(job.Key!, out var curatedSha))
        {
            return Integrated(Short(curatedSha), branchName, "curated-merge");
        }

        // (b) Recorded develop-merge fact (append-only, written by the merge
        // post-step). Zero-cost and authoritative once present.
        var recordedMerge = job.Provenance?.Merge?.MergeCommit;
        if (recordedMerge is { Length: > 0 })
            return Integrated(Short(recordedMerge), branchName, "recorded-merge");

        // Anchor = integrable entries in the attributed commits[] list. Zero-file
        // runner lifecycle markers are metadata, not delivery expectations.
        var attributed = AttributedCommits(job);
        if (attributed.Count == 0)
            return ClassifyNotIntegrated(job, branchName, repoResolved: true);

        var missing = new List<string>();
        foreach (var sha in attributed)
            if (!AncestorSetContains(reach.DevelopAncestors, sha)) missing.Add(sha);

        // NONE of the attributed commits landed → conflict-skipped / pending
        // (no-branch is impossible here: there IS attributed work).
        if (missing.Count == attributed.Count)
            return ClassifyNotIntegrated(job, branchName, repoResolved: true);

        var newest = attributed[^1];

        // ALL attributed commits landed → integrated, even if the branch tip still
        // carries un-integrated WIP commits the widget never showed.
        if (missing.Count == 0)
        {
            var branchTip = RecordedBranchTip(job);
            if (branchTip != null && !AncestorSetContains(reach.DevelopAncestors, branchTip))
            {
                var wip = CountUnintegratedTipCommits(root, reach, branchTip);
                var detail = wip > 0
                    ? $"attributed commits integrated; branch tip has {wip} unintegrated WIP commit{(wip == 1 ? "" : "s")}"
                    : "attributed commits integrated; branch tip has unintegrated WIP commits";
                return Integrated(Short(newest), branchName, detail);
            }
            return Integrated(Short(newest), branchName, "anchor-ancestor");
        }

        // SOME landed, some did not → partial, naming the missing short-SHAs so the
        // tooltip says exactly which attributed commits are not in develop yet.
        var integratedCount = attributed.Count - missing.Count;
        var missingShort = string.Join(", ", missing.Select(Short));
        return new TaskIntegrationStatus
        {
            Status = IntegrationStatuses.Partial,
            IntegrationBranch = branchName,
            Detail = $"{integratedCount}/{attributed.Count} attributed commits integrated; "
                     + $"missing: {missingShort}",
        };
    }

    /// <summary>
    /// Splits a not-integrated card into conflict-skipped / pending / no-branch. A
    /// recorded merge-into-develop conflict / error wins (the work was NOT merged
    /// and needs a human); a card with no integrable work at all is no-branch;
    /// otherwise the work simply has not landed yet - pending.
    /// </summary>
    private TaskIntegrationStatus ClassifyNotIntegrated(TaskInfo job, string branchName, bool repoResolved)
    {
        var anchor = AnchorFor(job);
        var branchTip = RecordedBranchTip(job);
        var hasWork = anchor != null || branchTip != null || job.CodeActivityDetected;

        if (repoResolved && ReadMergeConflict(job) is { } conflictDetail)
            return new TaskIntegrationStatus
            {
                Status = IntegrationStatuses.ConflictSkipped,
                IntegrationBranch = branchName,
                Detail = conflictDetail,
            };

        if (!hasWork)
            return new TaskIntegrationStatus
            {
                Status = IntegrationStatuses.NoBranch,
                IntegrationBranch = branchName,
                Detail = "No task branch or attributed commit to integrate.",
            };

        return new TaskIntegrationStatus
        {
            Status = IntegrationStatuses.Pending,
            IntegrationBranch = branchName,
            Detail = $"Accepted work is not yet in {branchName}.",
        };
    }

    private static TaskIntegrationStatus Integrated(string sha, string branchName, string detail) => new()
    {
        Status = IntegrationStatuses.Integrated,
        Sha = sha,
        IntegrationBranch = branchName,
        Detail = detail,
    };

    /// <summary>
    /// Reads the deferred merge-into-develop step outcome from the card's local
    /// <c>pipeline-execution.json</c> and returns a conflict detail string when the
    /// merge was recorded conflicted / errored, else null. Local file read only (no
    /// git spawn); best-effort - any failure reads as "no recorded conflict".
    /// </summary>
    private string? ReadMergeConflict(TaskInfo job)
    {
        try
        {
            var record = _pipelineLog.Read(job.FolderPath);
            var step = record?.Steps.LastOrDefault(s =>
                string.Equals(s.StepId, PipelineCatalogue.MergeIntoDevelopStepId, StringComparison.Ordinal));
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
            if (step.Status == PipelineStepStatus.Failed
                && string.Equals(step.Verdict, "error", StringComparison.OrdinalIgnoreCase))
                return step.Reason ?? "Merge into develop failed; not merged.";
            return null;
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "TaskIntegrationStatusService: pipeline read is best-effort");
            return null;
        }
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
    /// Best-effort count of the un-integrated WIP commits the recorded branch tip
    /// carries beyond the integration branch (<c>&lt;branch&gt;..&lt;tip&gt;</c>,
    /// no-merges), for the rare fully-integrated-but-tip-ahead subset only. Uses the
    /// already-resolved repo root (no job re-discovery) and runs under the read-only
    /// git concurrency limiter. Never throws; a git failure reads as 0 (the detail
    /// then omits the number).
    /// </summary>
    private int CountUnintegratedTipCommits(string root, RepoIntegration reach, string branchTip)
    {
        try
        {
            return ReadOnlyGitConcurrencyLimiter.Run(
                () => _git.GetCommitsInRangeAtRoot(root, reach.IntegrationBranch, branchTip).Count);
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "TaskIntegrationStatusService: WIP-tip count is best-effort");
            return 0;
        }
    }

    /// <summary>
    /// The task-branch tip read from the persisted provenance transitions (newest
    /// recorded tip). No git spawn: a stale-but-real tip can only become MORE
    /// integrated over time, so using it for an ancestry check never over-reports.
    /// Null when no transition recorded a branch tip (sequential run).
    /// </summary>
    internal static string? RecordedBranchTip(TaskInfo job)
    {
        var transitions = job.Provenance?.Transitions;
        if (transitions is null || transitions.Count == 0) return null;
        for (var i = transitions.Count - 1; i >= 0; i--)
        {
            var tip = transitions[i].BranchTip;
            if (!string.IsNullOrWhiteSpace(tip)) return tip;
        }
        return null;
    }

    /// <summary>
    /// The develop ancestor SHA set + curated-merge map for one repo. A fixed,
    /// small number of git spawns per repo per TTL window (rev-list + a bounded
    /// grep-filtered log), run under the read-only concurrency limiter.
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
            var mergeShaByKey = _git.GetIntegrationMergeShaByKey(root, integrationRef);

            return new RepoIntegration(integrationBranch, ancestors, mergeShaByKey, succeeded);
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
        Dictionary<string, string> MergeShaByKey,
        bool Succeeded);

    private sealed record RepoBranchKey(string Root, string Branch);
}
