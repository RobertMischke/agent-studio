

namespace AgentStudio.Tokens;

/// <summary>
/// Phase-4 bus-backed read path for the Project-Detail Token-Usage
/// surfaces (lifetime + last-24h summary, per-job × per-day heatmap,
/// top-N expensive jobs, per-call drill-down). Queries the bus for the
/// project's <c>kind=token-usage</c> messages across coding, supporting,
/// and orchestrator participants, converts them into transient
/// <see cref="OrchestratorLogEntry"/> records, and folds them through the
/// existing pure-function aggregators on
/// <see cref="ProjectTokenUsageService"/>.
/// </summary>
/// <remarks>
/// <para>
/// Runtime reads use the bus-native participant split
/// (participantId <c>agent:*</c> vs <c>support:*</c> vs
/// <c>orchestrator:*</c>). The static <c>*FromStore</c> parity helpers
/// intentionally keep the old orchestrator-only projection so historical
/// <c>orchestrator.jsonl</c> fixtures remain byte-comparable.
/// </para>
/// <para>
/// The parity test
/// (<c>ProjectTokenUsageBusParityTests</c>) drives all four surfaces
/// (Summary / Heatmap / Expensive / TaskDetail) over the same data set
/// and asserts numeric equality, including the deltas-vs-prior column
/// on the drill-down and the chronological day-list on the heatmap.
/// </para>
/// </remarks>
public sealed class BusBackedProjectTokenUsageReader
{
    private readonly AgentMessageBusStore _store;
    private readonly IConfiguration _config;
    private readonly JobStatsMetadataCache _jobStatsMetadata;

    public BusBackedProjectTokenUsageReader(
        AgentMessageBusStore store,
        IConfiguration config,
        JobStatsMetadataCache jobStatsMetadata)
    {
        _store = store;
        _config = config;
        _jobStatsMetadata = jobStatsMetadata;
    }

    public ProjectTokenUsageSummary BuildSummary(string projectName, string watchPath, DateTime? nowUtc = null)
    {
        var entries = LoadEntries(projectName);
        var jobsById = BuildJobsById(watchPath);
        return ProjectTokenUsageService.BuildSummaryFromEntries(projectName, entries, jobsById, nowUtc);
    }

    public ProjectTokenHeatmap BuildHeatmap(string projectName, string watchPath, int days, DateTime? nowUtc = null)
    {
        var entries = LoadEntries(projectName);
        var jobsById = BuildJobsById(watchPath);
        return ProjectTokenUsageService.BuildHeatmapFromEntries(projectName, entries, jobsById, days, nowUtc);
    }

    public IReadOnlyList<ProjectExpensiveJob> BuildExpensiveJobs(string projectName, string watchPath, int limit)
    {
        var entries = LoadEntries(projectName);
        var jobsById = BuildJobsById(watchPath);
        return ProjectTokenUsageService.BuildExpensiveJobsFromEntries(entries, jobsById, limit);
    }

    public ProjectJobTokenDetail? BuildJobDetail(string projectName, string watchPath, string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId)) return null;
        var entries = LoadEntries(projectName);
        var jobsById = BuildJobsById(watchPath);
        return ProjectTokenUsageService.BuildJobDetailFromEntries(projectName, entries, jobsById, jobId);
    }

    /// <summary>
    /// Pure entry point used by the parity test: reads the bus directly
    /// and runs the same legacy fold so divergence is impossible.
    /// </summary>
    public static ProjectTokenUsageSummary BuildSummaryFromStore(
        AgentMessageBusStore store, string workspaceRoot, string projectName,
        IReadOnlyDictionary<string, TaskInfo> jobsById, DateTime? nowUtc = null)
    {
        var entries = BusTokenEntryConverter.LoadOrchestratorEntries(store, workspaceRoot, projectName);
        return ProjectTokenUsageService.BuildSummaryFromEntries(projectName, entries, jobsById, nowUtc);
    }

    public static ProjectTokenHeatmap BuildHeatmapFromStore(
        AgentMessageBusStore store, string workspaceRoot, string projectName,
        IReadOnlyDictionary<string, TaskInfo> jobsById, int days, DateTime? nowUtc = null)
    {
        var entries = BusTokenEntryConverter.LoadOrchestratorEntries(store, workspaceRoot, projectName);
        return ProjectTokenUsageService.BuildHeatmapFromEntries(projectName, entries, jobsById, days, nowUtc);
    }

    public static IReadOnlyList<ProjectExpensiveJob> BuildExpensiveJobsFromStore(
        AgentMessageBusStore store, string workspaceRoot, string projectName,
        IReadOnlyDictionary<string, TaskInfo> jobsById, int limit)
    {
        var entries = BusTokenEntryConverter.LoadOrchestratorEntries(store, workspaceRoot, projectName);
        return ProjectTokenUsageService.BuildExpensiveJobsFromEntries(entries, jobsById, limit);
    }

    public static ProjectJobTokenDetail? BuildJobDetailFromStore(
        AgentMessageBusStore store, string workspaceRoot, string projectName,
        IReadOnlyDictionary<string, TaskInfo> jobsById, string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId)) return null;
        var entries = BusTokenEntryConverter.LoadOrchestratorEntries(store, workspaceRoot, projectName);
        return ProjectTokenUsageService.BuildJobDetailFromEntries(projectName, entries, jobsById, jobId);
    }

    private IReadOnlyList<OrchestratorLogEntry> LoadEntries(string projectName)
    {
        var workspace = _config["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace)) return Array.Empty<OrchestratorLogEntry>();
        return BusTokenEntryConverter.LoadTokenUsageEntries(_store, workspace!, projectName);
    }

    private IReadOnlyDictionary<string, TaskInfo> BuildJobsById(string watchPath)
        => _jobStatsMetadata.JobsById(watchPath);
}
