

namespace AgentStudio.Tokens;

/// <summary>
/// Hybrid read path for the Project-Detail Token-Usage surfaces. It keeps
/// historical <c>kind=token-usage</c> bus messages and merges the durable
/// per-task receipts written by the current remote execution path, converts
/// both into transient
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
    private readonly ProjectTokenReceiptReader _receipts;

    public BusBackedProjectTokenUsageReader(
        AgentMessageBusStore store,
        IConfiguration config,
        JobStatsMetadataCache jobStatsMetadata,
        ProjectTokenReceiptReader receipts)
    {
        _store = store;
        _config = config;
        _jobStatsMetadata = jobStatsMetadata;
        _receipts = receipts;
    }

    public ProjectTokenUsageSummary BuildSummary(string projectName, string watchPath, DateTime? nowUtc = null)
    {
        var snapshot = LoadSnapshot(projectName, watchPath);
        var jobsById = BuildJobsById(watchPath);
        return ProjectTokenUsageService.BuildSummaryFromEntries(projectName, snapshot.Entries, jobsById, nowUtc) with
        {
            Freshness = snapshot.Freshness,
        };
    }

    public ProjectTokenHeatmap BuildHeatmap(string projectName, string watchPath, int days, DateTime? nowUtc = null)
    {
        var snapshot = LoadSnapshot(projectName, watchPath);
        var jobsById = BuildJobsById(watchPath);
        return ProjectTokenUsageService.BuildHeatmapFromEntries(projectName, snapshot.Entries, jobsById, days, nowUtc) with
        {
            Freshness = snapshot.Freshness,
        };
    }

    public IReadOnlyList<ProjectExpensiveJob> BuildExpensiveJobs(string projectName, string watchPath, int limit)
    {
        var entries = LoadSnapshot(projectName, watchPath).Entries;
        var jobsById = BuildJobsById(watchPath);
        return ProjectTokenUsageService.BuildExpensiveJobsFromEntries(entries, jobsById, limit);
    }

    public ProjectJobTokenDetail? BuildJobDetail(string projectName, string watchPath, string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId)) return null;
        var entries = LoadSnapshot(projectName, watchPath).Entries;
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

    public TokenSummary BuildLifetimeSummary(string projectName, string watchPath)
        => TokenSummaryService.Summarize(projectName, LoadSnapshot(projectName, watchPath).Entries);

    public Dictionary<string, TaskTokenSummary> BuildPerJob(string projectName, string watchPath)
        => TokenSummaryService.SummarizePerJob(LoadSnapshot(projectName, watchPath).Entries);

    internal ProjectTokenUsageSnapshot LoadSnapshot(string projectName, string watchPath)
    {
        var warnings = new List<string>();
        var sources = new List<string>();
        IReadOnlyList<OrchestratorLogEntry> historical = [];
        var workspace = _config["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace))
        {
            warnings.Add("The historical token bus is unavailable because TaskRepository is not configured.");
        }
        else
        {
            try
            {
                historical = BusTokenEntryConverter.LoadTokenUsageEntries(_store, workspace!, projectName);
                sources.Add("historical-token-bus");
            }
            catch (Exception)
            {
                warnings.Add("The historical token bus could not be read. Lifetime values may be incomplete.");
            }
        }

        var receiptRead = _receipts.Read(watchPath);
        if (receiptRead.SourceAvailable) sources.Add("task-token-receipts");
        if (!string.IsNullOrWhiteSpace(receiptRead.Warning)) warnings.Add(receiptRead.Warning!);

        var entries = ProjectTokenReceiptReader.MergeWithoutDuplicates(historical, receiptRead.Entries);
        var asOf = entries
            .Where(entry => entry.TokenUsage is not null)
            .Select(entry => (DateTime?)entry.Ts.ToUniversalTime())
            .Max();
        var status = warnings.Count == 0
            ? "complete"
            : entries.Count > 0 ? "partial" : "unavailable";
        var freshness = new ProjectTokenDataFreshness
        {
            Status = status,
            AsOf = asOf?.ToString("o"),
            Warning = warnings.Count == 0 ? null : string.Join(" ", warnings),
            Sources = sources,
        };
        return new ProjectTokenUsageSnapshot(entries, receiptRead.Summaries, freshness);
    }

    private IReadOnlyDictionary<string, TaskInfo> BuildJobsById(string watchPath)
        => _jobStatsMetadata.JobsById(watchPath);
}

internal sealed record ProjectTokenUsageSnapshot(
    IReadOnlyList<OrchestratorLogEntry> Entries,
    IReadOnlyDictionary<string, TaskTokenSummary> ReceiptSummaries,
    ProjectTokenDataFreshness Freshness);
