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
/// develop git-log. This service reads three independent signals and collapses
/// them into the four <see cref="IntegrationStatuses"/> states:
/// <list type="number">
/// <item>a curated <c>merge(&lt;KEY&gt;)</c> / <c>merge-recut(&lt;KEY&gt;)</c> commit
///   in the develop log (authoritative for the curated integrator), or the
///   recorded develop-merge fact;</item>
/// <item>the task anchor commit is an ancestor of develop;</item>
/// <item>the recorded task-branch tip is an ancestor of develop.</item>
/// </list>
/// </para>
///
/// <para>
/// Same design invariant as <see cref="BoardMergeStatusService"/>: <b>no per-card
/// git spawn</b>. Per repository it computes ONE develop ancestor SHA set plus ONE
/// bounded <c>git log --grep</c> curated-merge map, cached for a ref-fingerprinted
/// TTL, and answers every card in that repo with in-memory lookups. The only
/// per-card disk touch is a local <c>pipeline-execution.json</c> read, and only for
/// the small not-integrated subset, to distinguish a conflict-skipped card from a
/// plain pending one. Never throws: a git failure yields the conservative
/// "pending / no-branch" reading.
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

        var byRepo = new Dictionary<string, List<TaskInfo>>(StringComparer.OrdinalIgnoreCase);
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
            if (!byRepo.TryGetValue(root!, out var list))
            {
                list = new List<TaskInfo>();
                byRepo[root!] = list;
            }
            list.Add(job);
        }

        // Cards with no resolvable repo can still be honestly classified: a card
        // with no anchor commit and no branch is no-branch, anything else pending.
        foreach (var job in noRepo)
            result[job.TaskKey] = ClassifyNotIntegrated(job, "develop", repoResolved: false);

        var reaches = new ConcurrentDictionary<string, RepoIntegration>(StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(
            byRepo,
            new ParallelOptions { MaxDegreeOfParallelism = ReadOnlyGitConcurrencyLimiter.MaxConcurrency },
            pair =>
            {
                var configuredBranch = ConfiguredIntegrationBranch(pair.Value[0].ProjectName);
                var cacheKey = $"{pair.Key}\0{configuredBranch}";
                var refFingerprint = ReadOnlyGitRefFingerprint.CaptureDetailed(pair.Key, [configuredBranch]);
                reaches[pair.Key] = _cache.GetOrCreateVersioned(
                    cacheKey,
                    refFingerprint.Value,
                    value => value.Succeeded
                        ? refFingerprint.RequiresShortFallback ? ShortFallbackTtl : CacheTtl
                        : FailureCacheTtl,
                    () => ComputeRepoIntegration(pair.Key, configuredBranch));
            });

        foreach (var (root, repoJobs) in byRepo)
        {
            var reach = reaches[root];
            foreach (var job in repoJobs)
                result[job.TaskKey] = ClassifyWithRepo(job, reach);
        }

        return result;
    }

    /// <summary>
    /// The four-state verdict for one card given its repo's cached integration
    /// facts. The three integrated signals are checked in authority order; a
    /// non-integrated card is then split into conflict-skipped / pending /
    /// no-branch.
    /// </summary>
    private TaskIntegrationStatus ClassifyWithRepo(TaskInfo job, RepoIntegration reach)
    {
        var branchName = reach.IntegrationBranch;

        // (1a) Curated integrator merge commit for this key on develop. This is
        // the authoritative signal a rewritten/curated merge leaves behind.
        if (!string.IsNullOrWhiteSpace(job.Key)
            && reach.MergeShaByKey.TryGetValue(job.Key!, out var curatedSha))
        {
            return Integrated(Short(curatedSha), branchName, "curated-merge");
        }

        // (1b) Recorded develop-merge fact (append-only, written by the merge
        // post-step). Zero-cost and authoritative once present.
        var recordedMerge = job.Provenance?.Merge?.MergeCommit;
        if (recordedMerge is { Length: > 0 })
            return Integrated(Short(recordedMerge), branchName, "recorded-merge");

        // (2) The task anchor commit is an ancestor of develop.
        var anchor = AnchorFor(job);
        if (anchor != null && reach.DevelopAncestors.Contains(anchor))
            return Integrated(Short(anchor), branchName, "anchor-ancestor");

        // (3) The recorded task-branch tip is an ancestor of develop.
        var branchTip = RecordedBranchTip(job);
        if (branchTip != null && reach.DevelopAncestors.Contains(branchTip))
            return Integrated(Short(branchTip), branchName, "branch-tip-ancestor");

        return ClassifyNotIntegrated(job, branchName, repoResolved: true);
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
                return step.VerdictSummary ?? step.Reason ?? "Merge into develop hit a conflict; not merged.";
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
        var last = job.Commits.Count > 0 ? job.Commits[^1].Sha : job.Commit?.Sha;
        return string.IsNullOrWhiteSpace(last) ? null : last;
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

    private string ConfiguredIntegrationBranch(string projectName)
    {
        var configured = _settings.Get(projectName).IntegrationBranch;
        return string.IsNullOrWhiteSpace(configured)
            ? new ProjectSettings().IntegrationBranch
            : configured.Trim();
    }

    internal void InvalidateCache() => _cache.Invalidate();
    internal int ComputationCount => Volatile.Read(ref _computationCount);

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    private sealed record RepoIntegration(
        string IntegrationBranch,
        HashSet<string> DevelopAncestors,
        Dictionary<string, string> MergeShaByKey,
        bool Succeeded);
}
