using OrchestratorApi.Models;
using OrchestratorApi.Services.Bus;
using OrchestratorApi.Services.Runner;

namespace OrchestratorApi.Services.Tokens;

/// <summary>
/// Phase-4 bus-backed read path for the workspace tokens timeline
/// (<c>GET /api/workspace/tokens</c>). For every (project, watchPath)
/// pair, queries the bus for that project's orchestrator
/// <c>kind=token-usage</c> messages, converts them into transient
/// <see cref="OrchestratorLogEntry"/> records, and folds them through
/// the existing pure-function bucketing in
/// <see cref="WorkspaceTokensTimelineService.BuildFromEntries"/>.
/// </summary>
/// <remarks>
/// Reusing the legacy bucketer is what keeps the bus path byte-identical
/// to the JSONL path: window snapping, bucket alignment, the per-bucket
/// dollar accounting, and the per-project rollup ordering all flow
/// through the same code. The parity test
/// (<c>WorkspaceTokensTimelineBusParityTests</c>) drives both readers
/// over the same data set and asserts numeric equality for every cell
/// and every per-project total.
/// </remarks>
public sealed class BusBackedWorkspaceTimelineReader
{
    private readonly AgentMessageBusStore _store;
    private readonly IConfiguration _config;

    public BusBackedWorkspaceTimelineReader(AgentMessageBusStore store, IConfiguration config)
    {
        _store = store;
        _config = config;
    }

    /// <summary>
    /// Build the workspace timeline view across every supplied project.
    /// Returns an empty timeline when the workspace root is not configured.
    /// </summary>
    public TokenTimeline Build(
        IEnumerable<(string Name, string WatchPath)> projects,
        int windowHours,
        int bucketMinutes,
        DateTime? nowUtc = null)
    {
        var workspace = _config["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace))
        {
            return BuildFromStore(_store, workspaceRoot: "(unconfigured)", projects, windowHours, bucketMinutes, nowUtc);
        }
        return BuildFromStore(_store, workspace!, projects, windowHours, bucketMinutes, nowUtc);
    }

    /// <summary>
    /// Pure overload used by the parity test. The window math lives in
    /// <see cref="WorkspaceTokensTimelineService.BuildFromEntries"/> so
    /// the bus path cannot disagree with the legacy reader on snapping,
    /// bucket span, or the empty-bucket-count derivation.
    /// </summary>
    public static TokenTimeline BuildFromStore(
        AgentMessageBusStore store,
        string workspaceRoot,
        IEnumerable<(string Name, string WatchPath)> projects,
        int windowHours,
        int bucketMinutes,
        DateTime? nowUtc = null)
    {
        var w = WorkspaceTokensTimelineService.ResolveWindowHours(windowHours);
        var b = WorkspaceTokensTimelineService.ResolveBucketMinutes(bucketMinutes);
        var now = nowUtc ?? DateTime.UtcNow;
        var windowEnd = AlignDown(now, b);
        var windowStart = windowEnd.AddHours(-w);

        var perProjectEntries = new List<(string Project, IReadOnlyList<OrchestratorLogEntry> Entries)>();
        foreach (var (name, _) in projects)
        {
            var entries = BusTokenEntryConverter.LoadOrchestratorEntries(store, workspaceRoot, name);
            perProjectEntries.Add((name, entries));
        }

        return WorkspaceTokensTimelineService.BuildFromEntries(perProjectEntries, windowStart, windowEnd, b);
    }

    private static DateTime AlignDown(DateTime ts, int bucketMinutes)
    {
        var utc = ts.Kind == DateTimeKind.Utc ? ts : ts.ToUniversalTime();
        var minutesSinceEpoch = (long)Math.Floor((utc - DateTime.UnixEpoch).TotalMinutes);
        var aligned = minutesSinceEpoch - (minutesSinceEpoch % bucketMinutes);
        return DateTime.UnixEpoch.AddMinutes(aligned);
    }
}
