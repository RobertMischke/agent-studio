using System.Collections.Concurrent;

namespace AgentStudio.Tasks;

/// <summary>
/// Builds the board's always-on merge signal (AGT-2046): for every card, is the
/// task's work folded into the integration branch (develop) and/or the release
/// branch (main)? The result is attached to <see cref="TaskInfo.MergeSignal"/> so
/// the kanban card renders a two-segment <c>[develop|main]</c> indicator.
///
/// <para>
/// The design invariant is <b>no per-card git spawn</b>. Instead of a
/// <c>merge-base --is-ancestor</c> fork per card (which would be a spawn orgy on a
/// 200-card board), the service computes ONE pair of reachability sets per
/// repository - the full ancestor SHA sets of develop and main
/// (<see cref="GitService.GetAncestorShaSet"/>) - and answers membership for every
/// card in that repo with an in-memory hash lookup. That is O(repos) git spawns,
/// matching the batched read strategy from AGT-2007 (the provenance view) and
/// AGT-2013 (git-info caching). The per-repo sets are cached for a short TTL so a
/// polling board reuses them across requests.
/// </para>
///
/// <para>
/// The card <b>anchor</b> is the latest attributed TASK commit on the board
/// payload (<see cref="AnchorFor"/>) - never a fresh per-card branch-tip read, and
/// never a branch tip / merge fact on their own (AGT-2063: a commit-less card gets
/// no signal). A recorded develop-merge fact
/// (<see cref="TaskProvenanceMerge.MergeCommit"/>) still short-circuits the develop
/// segment to <c>true</c> without any set lookup for an anchored card, because a
/// merge into develop is an append-only fact.
/// </para>
/// </summary>
public sealed class BoardMergeStatusService
{
    private readonly GitService _git;
    private readonly ProjectSettingsService _settings;
    private readonly ILogger<BoardMergeStatusService> _logger;

    public const string ReleaseBranch = "main";

    /// <summary>
    /// How long a repository's develop/main reachability sets are reused. Merges
    /// are infrequent relative to the board poll, so a few seconds of staleness is
    /// invisible while it collapses a burst of polls to a single pair of reads.
    /// </summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(8);

    private readonly ConcurrentDictionary<string, (DateTime At, RepoReachability Value)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public BoardMergeStatusService(
        GitService git,
        ProjectSettingsService settings,
        ILogger<BoardMergeStatusService> logger)
    {
        _git = git;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Per-<see cref="TaskInfo.TaskKey"/> merge signal for the given board. Only
    /// cards with an attributed task commit (see <see cref="AnchorFor"/>) get an
    /// entry - a card with nothing committed carries no signal and the card renders
    /// none. Never throws: a git failure yields the conservative "not merged"
    /// reading for that repo.
    /// </summary>
    public Dictionary<string, TaskMergeSignal> BuildLookup(IReadOnlyCollection<TaskInfo> jobs)
    {
        var result = new Dictionary<string, TaskMergeSignal>(StringComparer.Ordinal);
        if (jobs.Count == 0) return result;

        using var _t = GitProcessTelemetry.BeginRequest("board/merge-status", _logger);

        // Group the anchored cards by resolved repo root so the batched ancestor
        // sets are computed ONCE per repository, not per card.
        var byRepo = new Dictionary<string, List<TaskInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in jobs)
        {
            if (AnchorFor(job) is null) continue;
            var root = _git.ResolveRepoRootForWatchPath(job.WatchPath);
            if (string.IsNullOrWhiteSpace(root)) continue;
            if (!byRepo.TryGetValue(root!, out var list))
            {
                list = new List<TaskInfo>();
                byRepo[root!] = list;
            }
            list.Add(job);
        }

        foreach (var (root, repoJobs) in byRepo)
        {
            var integrationBranch = ResolveIntegrationBranch(repoJobs[0].ProjectName, root);
            var reach = GetReachability(root, integrationBranch);

            foreach (var job in repoJobs)
            {
                var anchor = AnchorFor(job)!;
                var mergeSha = job.Provenance?.Merge?.MergeCommit;
                var branch = !string.IsNullOrWhiteSpace(job.Provenance?.Branch)
                    ? job.Provenance!.Branch
                    : WorktreeTaskLifecycle.BranchFor(job.Id);

                // Develop: the recorded merge fact is authoritative (append-only,
                // zero-cost); otherwise the anchor's graph membership. Main: the
                // anchor - or the develop-merge commit - reaching the release line.
                var inIntegration =
                    (mergeSha is { Length: > 0 })
                    || reach.Integration.Contains(anchor);
                var inRelease =
                    reach.Release.Contains(anchor)
                    || (mergeSha is { Length: > 0 } && reach.Release.Contains(mergeSha));

                result[job.TaskKey] = new TaskMergeSignal
                {
                    Branch = branch,
                    InIntegration = inIntegration,
                    InRelease = inRelease,
                    IntegrationBranch = integrationBranch,
                    ReleaseBranch = ReleaseBranch,
                    IntegrationSha = inIntegration ? Short(mergeSha ?? anchor) : null,
                    ReleaseSha = inRelease ? Short(anchor) : null,
                };
            }
        }

        return result;
    }

    /// <summary>
    /// The card anchor read entirely from the persisted board payload (no git
    /// spawn): the latest attributed TASK commit SHA. Null when the card has
    /// committed nothing yet, which suppresses its signal.
    ///
    /// <para>
    /// AGT-2063: the anchor is the task's own commit and nothing else. A recorded
    /// <c>task/&lt;id&gt;</c> branch tip is deliberately NOT used: for a task that
    /// produced no commit the tip is just the branch base, which is trivially an
    /// ancestor of develop/main and would light the develop segment on a card that
    /// changed nothing (the "merge state on a commit-less card" bug). The recorded
    /// merge fact still proves the develop segment in <see cref="BuildLookup"/>
    /// (it reads <c>Merge.MergeCommit</c> directly), but on its own it does not
    /// manufacture an anchor: no task commit means no signal.
    /// </para>
    /// </summary>
    internal static string? AnchorFor(TaskInfo job)
    {
        var last = job.Commits.Count > 0 ? job.Commits[^1].Sha : job.Commit?.Sha;
        return string.IsNullOrWhiteSpace(last) ? null : last;
    }

    private RepoReachability GetReachability(string root, string integrationBranch)
    {
        if (_cache.TryGetValue(root, out var cached) && DateTime.UtcNow - cached.At < CacheTtl)
            return cached.Value;

        var fresh = ComputeReachability(root, integrationBranch);
        _cache[root] = (DateTime.UtcNow, fresh);
        return fresh;
    }

    /// <summary>
    /// The develop + main ancestor SHA sets for one repo. TWO (up to four with the
    /// <c>origin/</c> mirror) <c>rev-list</c> spawns per repo per TTL window, run in
    /// parallel - the whole batch that replaces a per-card ancestry fan-out. Both
    /// the local ref and its <c>origin/</c> mirror are unioned so a fresh clone with
    /// only remote-tracking branches still resolves, matching
    /// <see cref="TaskProvenanceService"/>'s local-or-origin semantics.
    /// </summary>
    private RepoReachability ComputeReachability(string root, string integrationBranch)
    {
        var tIntLocal = Task.Run(() => _git.GetAncestorShaSet(root, integrationBranch));
        var tIntOrigin = Task.Run(() => _git.GetAncestorShaSet(root, "origin/" + integrationBranch));
        var tRelLocal = Task.Run(() => _git.GetAncestorShaSet(root, ReleaseBranch));
        var tRelOrigin = Task.Run(() => _git.GetAncestorShaSet(root, "origin/" + ReleaseBranch));
        Task.WaitAll(tIntLocal, tIntOrigin, tRelLocal, tRelOrigin);

        var integration = tIntLocal.Result;
        integration.UnionWith(tIntOrigin.Result);
        var release = tRelLocal.Result;
        release.UnionWith(tRelOrigin.Result);
        return new RepoReachability(integration, release);
    }

    private string ResolveIntegrationBranch(string projectName, string? repoRoot)
    {
        var configured = _settings.Get(projectName).IntegrationBranch;
        if (!string.IsNullOrWhiteSpace(repoRoot))
            return _git.ResolveIntegrationBranch(repoRoot, configured);
        return string.IsNullOrWhiteSpace(configured) ? new ProjectSettings().IntegrationBranch : configured;
    }

    /// <summary>Drops the cached reachability sets. Tests use this to force a fresh read.</summary>
    internal void InvalidateCache() => _cache.Clear();

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    private sealed record RepoReachability(HashSet<string> Integration, HashSet<string> Release);
}
