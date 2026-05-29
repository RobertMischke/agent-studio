using System.Collections.Concurrent;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Jobs.Audit;

/// <summary>
/// In-memory store of completed-lane audit runs keyed by runId. Trades
/// durability for simplicity: a backend restart loses in-flight runs.
/// The persistent audit trail is the per-card <c>quality_loop_reopened</c>
/// events in each timeline.jsonl; the report endpoint can also be rerun
/// at any time to regenerate the markdown view from the live lane state.
///
/// <para>Bounded retention: only the most recent 32 runs per project
/// stay in memory; older runs evict on insert.</para>
/// </summary>
public sealed class AuditRunStore
{
    private const int MaxRunsPerProject = 32;
    private readonly ConcurrentDictionary<string, CompletedLaneAuditRunStatus> _byRunId = new();

    public string Create(string projectId, string projectName, string watchPath)
    {
        var runId = Guid.NewGuid().ToString("N")[..12];
        _byRunId[runId] = new CompletedLaneAuditRunStatus
        {
            RunId = runId,
            ProjectId = projectId ?? "",
            ProjectName = projectName ?? "",
            WatchPath = watchPath ?? "",
            StartedAt = DateTime.UtcNow,
            Status = "running",
        };
        TrimPerProject(projectId ?? "");
        return runId;
    }

    public CompletedLaneAuditRunStatus? Get(string runId) =>
        _byRunId.TryGetValue(runId, out var s) ? s : null;

    public IEnumerable<CompletedLaneAuditRunStatus> ListForProject(string projectId) =>
        _byRunId.Values
            .Where(s => string.Equals(s.ProjectId, projectId, StringComparison.Ordinal))
            .OrderByDescending(s => s.StartedAt);

    public CompletedLaneAuditRunStatus? GetLatestForProject(string projectId) =>
        ListForProject(projectId).FirstOrDefault();

    public void Update(string runId, Func<CompletedLaneAuditRunStatus, CompletedLaneAuditRunStatus> mutate)
    {
        _byRunId.AddOrUpdate(runId,
            _ => mutate(new CompletedLaneAuditRunStatus { RunId = runId }),
            (_, existing) => mutate(existing));
    }

    private void TrimPerProject(string projectId)
    {
        var keep = ListForProject(projectId).Take(MaxRunsPerProject).Select(s => s.RunId).ToHashSet();
        foreach (var entry in _byRunId.Where(kv =>
                     string.Equals(kv.Value.ProjectId, projectId, StringComparison.Ordinal)
                     && !keep.Contains(kv.Key)).ToList())
        {
            _byRunId.TryRemove(entry.Key, out _);
        }
    }
}
