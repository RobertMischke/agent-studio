using OrchestratorApi.Models;
using OrchestratorApi.Services.Runner;

namespace OrchestratorApi.Services.Jobs;

/// <summary>
/// One commit attributed to a job, augmented with the run index it was
/// authored in. Drives the protocol-pane "Commits and change set" panel
/// via the <c>/api/jobs/{id}/commits</c> endpoint. <c>RunIndex</c> is
/// null when the commit comes from the auto-commit transition rather
/// than a tracked run.
/// </summary>
public sealed record JobCommitRecord
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
}

public sealed record JobCommitsAggregate
{
    public int Count { get; init; }
    public int TotalAdded { get; init; }
    public int TotalRemoved { get; init; }
    public int TotalFilesChanged { get; init; }
    public List<JobCommitRecord> Commits { get; init; } = [];
}

/// <summary>
/// Pure aggregator over a job's run timeline plus the auto-commit stamped
/// on <see cref="JobInfo.Commit"/>. Walks every run with a non-trivial SHA
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
public static class JobCommitsAggregator
{
    public static JobCommitsAggregate Aggregate(
        JobInfo info,
        IReadOnlyList<RunRecord> runs,
        Func<string, string, List<GitCommitInfo>> fetchRangeCommits)
    {
        var ordered = new List<JobCommitRecord>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                if (!seen.Add(c.Sha)) continue;
                ordered.Add(new JobCommitRecord
                {
                    Sha = c.Sha,
                    ShortSha = c.ShortSha,
                    AuthorDateUtc = c.AuthorDateUtc,
                    Author = c.Author,
                    Subject = c.Subject,
                    FilesChanged = c.FilesChanged,
                    Added = c.Added,
                    Removed = c.Removed,
                    RunIndex = run.Index
                });
            }
        }

        if (info.Commit != null && !string.IsNullOrWhiteSpace(info.Commit.Sha) && seen.Add(info.Commit.Sha))
        {
            ordered.Add(new JobCommitRecord
            {
                Sha = info.Commit.Sha,
                ShortSha = info.Commit.ShortSha,
                AuthorDateUtc = info.Commit.At,
                Author = "",
                Subject = (info.Commit.Message ?? "").Split('\n')[0],
                FilesChanged = info.Commit.FilesChanged,
                Added = 0,
                Removed = 0,
                RunIndex = null
            });
        }

        ordered.Sort((a, b) => b.AuthorDateUtc.CompareTo(a.AuthorDateUtc));

        return new JobCommitsAggregate
        {
            Count = ordered.Count,
            TotalAdded = ordered.Sum(c => c.Added),
            TotalRemoved = ordered.Sum(c => c.Removed),
            TotalFilesChanged = ordered.Sum(c => c.FilesChanged),
            Commits = ordered
        };
    }

    /// <summary>
    /// Lower-bound count of commits a job has produced, derived without
    /// running git per range. Reads only the captured SHA-range pairs in
    /// session-events.jsonl plus the auto-commit on <see cref="JobInfo.Commit"/>;
    /// each non-trivial range counts as ≥ 1. Used by the kanban card to
    /// surface a "more than one commit" hint without paying per-render
    /// git costs.
    ///
    /// <para>
    /// The number is intentionally conservative: a single run that
    /// landed multiple commits is undercounted as 1 here. The endpoint
    /// path that calls <see cref="Aggregate"/> returns the precise count.
    /// The kanban card only uses this to decide whether more than one
    /// commit exists ("&gt; 1"), which is robust under the undercount.
    /// </para>
    /// </summary>
    public static int CountCommitRangesPlusAutoCommit(
        JobInfo info,
        IReadOnlyList<SessionEvent> sessionEvents)
    {
        var seenRanges = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenShas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var evt in sessionEvents)
        {
            if (string.IsNullOrWhiteSpace(evt.HeadShaBefore) || string.IsNullOrWhiteSpace(evt.HeadShaAfter)) continue;
            if (string.Equals(evt.HeadShaBefore, evt.HeadShaAfter, StringComparison.OrdinalIgnoreCase)) continue;
            var key = evt.HeadShaBefore + ".." + evt.HeadShaAfter;
            if (!seenRanges.Add(key)) continue;
            seenShas.Add(evt.HeadShaAfter!);
            count++;
        }
        if (info.Commit != null && !string.IsNullOrWhiteSpace(info.Commit.Sha) && seenShas.Add(info.Commit.Sha))
        {
            count++;
        }
        return count;
    }
}
