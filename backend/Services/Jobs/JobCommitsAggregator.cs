using OrchestratorApi.Models;
using OrchestratorApi.Services.Runner;

namespace OrchestratorApi.Services.Jobs;

/// <summary>
/// One commit attributed to a job, augmented with the run index it was
/// authored in. Drives the protocol-pane "Commits and change set" panel
/// via the <c>/api/tasks/{id}/commits</c> endpoint. <c>RunIndex</c> is
/// null when the commit comes from the auto-commit transition rather
/// than a tracked run.
/// </summary>
public sealed record TaskCommitRecord
{
    public string Sha { get; init; } = "";
    public string ShortSha { get; init; } = "";
    public DateTime AuthorDateUtc { get; init; }
    public string Author { get; init; } = "";
    public string Subject { get; init; } = "";
    public int FilesChanged { get; init; }
    public int Added { get; init; }
    public int Removed { get; init; }
    public int? RunIndex { get; init; }
    /// <summary>
    /// Attribution kind overlaid from the persisted <see cref="TaskInfo.Commits"/>
    /// chain (one of <see cref="CommitAttributionKinds"/>). Defaults to
    /// <see cref="CommitAttributionKinds.Legacy"/> when the rule engine has
    /// not yet stamped this commit (e.g. a range commit surfaced before the
    /// post-step ran).
    /// </summary>
    public string Attribution { get; init; } = CommitAttributionKinds.Legacy;
    /// <summary>Confidence of an automatic attribution (0..1); null otherwise.</summary>
    public double? Confidence { get; init; }
}

public sealed record TaskCommitsAggregate
{
    public int Count { get; init; }
    public int TotalAdded { get; init; }
    public int TotalRemoved { get; init; }
    public int TotalFilesChanged { get; init; }
    public List<TaskCommitRecord> Commits { get; init; } = [];
    /// <summary>
    /// Commits the attribution rule withheld from this task (ADR
    /// "Commit-Attribution-Regel"). Surfaced under the "(N excluded)"
    /// expander in the protocol-pane git view; carries the reason so the
    /// operator can see why each was held back.
    /// </summary>
    public List<TaskExcludedCommitInfo> Excluded { get; init; } = [];
}

/// <summary>
/// Pure aggregator over a job's run timeline plus the auto-commit stamped
/// on <see cref="TaskInfo.Commit"/>. Walks every run with a non-trivial SHA
/// range, asks <see cref="GitService.GetCommitsInShaRange"/> for that
/// range's commits, dedupes by SHA so a commit can't double-count when
/// two runs claim overlapping ranges, and orders the result newest first.
///
/// <para>
/// Pure (no I/O of its own). Tests build a fake commit lookup via the
/// <c>fetchRangeCommits</c> delegate so the aggregation rules are pinned
/// without a real git repo. The thin endpoint wraps this with the
/// production <c>GitService</c> binding.
/// </para>
/// </summary>
public static class TaskCommitsAggregator
{
    public static TaskCommitsAggregate Aggregate(
        TaskInfo info,
        IReadOnlyList<RunRecord> runs,
        Func<string, string, List<GitCommitInfo>> fetchRangeCommits)
    {
        var ordered = new List<TaskCommitRecord>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // SHAs the attribution rule withheld: subtracted from every source
        // below so a range commit the rule excluded (e.g. a crash-recovery
        // for another task that landed inside this task's run window) never
        // re-surfaces just because it is still reachable in git history.
        var excludedShas = new HashSet<string>(
            info.ExcludedCommits.Where(e => !string.IsNullOrWhiteSpace(e.Sha)).Select(e => e.Sha),
            StringComparer.OrdinalIgnoreCase);

        // Attribution overlay keyed by SHA from the persisted chain. Last
        // write wins so a manual re-include refreshes the kind.
        var attrBySha = new Dictionary<string, TaskCommitInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var cm in info.Commits)
            if (!string.IsNullOrWhiteSpace(cm.Sha)) attrBySha[cm.Sha] = cm;

        foreach (var run in runs)
        {
            if (string.IsNullOrWhiteSpace(run.HeadShaBefore) || string.IsNullOrWhiteSpace(run.HeadShaAfter)) continue;
            if (string.Equals(run.HeadShaBefore, run.HeadShaAfter, StringComparison.OrdinalIgnoreCase)) continue;

            List<GitCommitInfo> commits;
            try { commits = fetchRangeCommits(run.HeadShaBefore!, run.HeadShaAfter!); }
            catch { continue; }

            foreach (var c in commits)
            {
                if (string.IsNullOrWhiteSpace(c.Sha)) continue;
                if (excludedShas.Contains(c.Sha)) continue;
                if (!seen.Add(c.Sha)) continue;
                attrBySha.TryGetValue(c.Sha, out var meta);
                ordered.Add(new TaskCommitRecord
                {
                    Sha = c.Sha,
                    ShortSha = c.ShortSha,
                    AuthorDateUtc = c.AuthorDateUtc,
                    Author = c.Author,
                    Subject = c.Subject,
                    FilesChanged = c.FilesChanged,
                    Added = c.Added,
                    Removed = c.Removed,
                    RunIndex = run.Index,
                    Attribution = CommitAttributionKinds.Normalize(meta?.Attribution),
                    Confidence = meta?.Confidence
                });
            }
        }

        // Fold in persisted chain entries that no run range surfaced - the
        // platform auto-commit and any operator manual-add. Attribution and
        // confidence come straight from the stored entry.
        foreach (var cm in info.Commits)
        {
            if (string.IsNullOrWhiteSpace(cm.Sha)) continue;
            if (excludedShas.Contains(cm.Sha)) continue;
            if (!seen.Add(cm.Sha)) continue;
            ordered.Add(new TaskCommitRecord
            {
                Sha = cm.Sha,
                ShortSha = cm.ShortSha,
                AuthorDateUtc = cm.At,
                Author = "",
                Subject = (cm.Message ?? "").Split('\n')[0],
                FilesChanged = cm.FilesChanged,
                Added = 0,
                Removed = 0,
                RunIndex = null,
                Attribution = CommitAttributionKinds.Normalize(cm.Attribution),
                Confidence = cm.Confidence
            });
        }

        // Legacy singular auto-commit fold. The scanner mirrors `commit` into
        // the `commits` chain, so in production this is already covered above;
        // it stays for callers that build TaskInfo with only the singular field
        // set (legacy job.json read paths and unit fixtures).
        if (info.Commit != null && !string.IsNullOrWhiteSpace(info.Commit.Sha)
            && !excludedShas.Contains(info.Commit.Sha) && seen.Add(info.Commit.Sha))
        {
            ordered.Add(new TaskCommitRecord
            {
                Sha = info.Commit.Sha,
                ShortSha = info.Commit.ShortSha,
                AuthorDateUtc = info.Commit.At,
                Author = "",
                Subject = (info.Commit.Message ?? "").Split('\n')[0],
                FilesChanged = info.Commit.FilesChanged,
                Added = 0,
                Removed = 0,
                RunIndex = null,
                Attribution = CommitAttributionKinds.Normalize(info.Commit.Attribution),
                Confidence = info.Commit.Confidence
            });
        }

        ordered.Sort((a, b) => b.AuthorDateUtc.CompareTo(a.AuthorDateUtc));

        return new TaskCommitsAggregate
        {
            Count = ordered.Count,
            TotalAdded = ordered.Sum(c => c.Added),
            TotalRemoved = ordered.Sum(c => c.Removed),
            TotalFilesChanged = ordered.Sum(c => c.FilesChanged),
            Commits = ordered,
            Excluded = info.ExcludedCommits.ToList()
        };
    }

}
