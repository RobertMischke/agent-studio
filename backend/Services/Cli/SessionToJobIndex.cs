using OrchestratorApi.Models;
using OrchestratorApi.Services.Jobs;

namespace OrchestratorApi.Services.Cli;

/// <summary>
/// In-memory inverse index <c>sessionId -> owning JobInfo summary</c>. Built
/// from <see cref="JobScannerService.ScanAllJobs"/> by walking each job's
/// <see cref="JobInfo.SessionChain"/>. Lets the right-hand session list show
/// a chip pointing back to the kanban task that originated the session.
///
/// <para>
/// The index is pure derived state. It owns no on-disk state and no
/// schema; <see cref="JobInfo.SessionChain"/> remains the source of truth.
/// A rebuild is O(jobs * chain length); the chain is small in practice
/// (one or a handful of ids), so the cost is effectively O(jobs).
/// </para>
///
/// <para>
/// Recovery sentinels (<c>"(recovery)"</c>) are skipped: they mark a
/// chain break, not a session id. Multi-checkout collisions (the same
/// session id appearing in jobs from two different watch paths, e.g.
/// dev/ and stable/ sharing a <c>~/.claude</c> store) are resolved at
/// lookup time by preferring the candidate whose <see cref="JobInfo.WatchPath"/>
/// equals the session row's <c>cwd</c>. The contract is intentionally
/// permissive on read: an unknown session id returns <c>null</c> rather
/// than throwing so orphan sessions render cleanly.
/// </para>
/// </summary>
public sealed class SessionToJobIndex
{
    /// <summary>Sentinel entry in <see cref="JobInfo.SessionChain"/> marking a recovery break.</summary>
    public const string RecoverySentinel = "(recovery)";

    /// <summary>
    /// Snapshot of the owning job, sufficient to render the chip and
    /// route the click. Intentionally a value record - the consumer
    /// does not need the full <see cref="JobInfo"/>.
    /// </summary>
    public sealed record LinkEntry(
        string JobId,
        string Title,
        string WatchPath,
        string ProjectName,
        string Lane);

    private Dictionary<string, List<LinkEntry>> _bySession = new(StringComparer.Ordinal);

    /// <summary>
    /// Replaces the index with entries built from <paramref name="jobs"/>.
    /// Atomic from a reader's perspective: a new dictionary is built and
    /// then swapped in; partial-rebuild visibility is not exposed.
    /// </summary>
    public void Rebuild(IEnumerable<JobInfo> jobs)
    {
        var next = new Dictionary<string, List<LinkEntry>>(StringComparer.Ordinal);
        foreach (var job in jobs)
        {
            if (job.SessionChain is null || job.SessionChain.Count == 0) continue;
            var lane = job.State;
            var entry = new LinkEntry(job.Id, job.Title, job.WatchPath, job.ProjectName, lane);
            foreach (var sessionId in job.SessionChain)
            {
                if (string.IsNullOrWhiteSpace(sessionId)) continue;
                if (string.Equals(sessionId, RecoverySentinel, StringComparison.Ordinal)) continue;
                if (!next.TryGetValue(sessionId, out var bucket))
                {
                    bucket = new List<LinkEntry>(1);
                    next[sessionId] = bucket;
                }
                bucket.Add(entry);
            }
        }
        // Single-reference swap so concurrent readers either see the old
        // map or the new one; we never expose a half-built dictionary.
        Volatile.Write(ref _bySession, next);
    }

    /// <summary>
    /// Look up the owning task for a session id. <paramref name="sessionCwd"/>
    /// is the session row's working directory (when known) and disambiguates
    /// the rare case where the same session id is referenced by jobs in
    /// multiple checkouts. Returns <c>null</c> for orphan / unknown ids.
    /// </summary>
    public LinkEntry? Lookup(string sessionId, string? sessionCwd = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        var map = Volatile.Read(ref _bySession);
        if (!map.TryGetValue(sessionId, out var bucket) || bucket.Count == 0) return null;
        if (bucket.Count == 1) return bucket[0];

        if (!string.IsNullOrWhiteSpace(sessionCwd))
        {
            foreach (var entry in bucket)
            {
                if (string.Equals(entry.WatchPath, sessionCwd, StringComparison.OrdinalIgnoreCase))
                    return entry;
            }
        }
        // No cwd hit: prefer the candidate currently in 3-progress; otherwise
        // first hit. The list is built oldest-first because jobs are scanned
        // in folder order, which is fine for tie-break determinism.
        foreach (var entry in bucket)
        {
            if (string.Equals(entry.Lane, JobStates.Progress, StringComparison.Ordinal))
                return entry;
        }
        return bucket[0];
    }

    /// <summary>
    /// Diagnostic: number of session ids currently in the index. Used by
    /// tests and the future supervisor reports surface.
    /// </summary>
    public int Count => Volatile.Read(ref _bySession).Count;
}
