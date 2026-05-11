using OrchestratorApi.Models;
using OrchestratorApi.Services.Bus;
using OrchestratorApi.Services.Runner;

namespace OrchestratorApi.Services.Tokens;

/// <summary>
/// Phase-4 bus-backed read path for the per-project token summary
/// (lifetime totals + per-model split + estimated USD). Queries the bus
/// for the project's <c>kind=token-usage</c> messages emitted by the
/// orchestrator participant, converts them into transient
/// <see cref="OrchestratorLogEntry"/> records, and folds them through the
/// existing pure-function aggregator on <see cref="TokenSummaryService"/>.
/// </summary>
/// <remarks>
/// <para>
/// Reusing <see cref="TokenSummaryService.Summarize(string, IReadOnlyList{OrchestratorLogEntry})"/>
/// is what makes parity byte-exact: the model-key normalisation, the cost
/// estimation through <see cref="TokenPricing"/>, the "all priced" flag,
/// and the <c>byModel</c> ordering all flow through the same code path
/// as the legacy reader. The parity test in
/// <c>TokenSummaryBusParityTests</c> drives both readers over the same
/// data set and asserts numeric equality.
/// </para>
/// <para>
/// The legacy <c>orchestrator.jsonl</c> reader is the fallback for very
/// old records that predate the Phase-2 bus emit. New consumers always
/// go through <see cref="ITokenAggregator"/>, which the Phase-4 wiring
/// points at this reader.
/// </para>
/// </remarks>
public sealed class BusBackedTokenSummaryReader
{
    private readonly AgentMessageBusStore _store;
    private readonly IConfiguration _config;

    public BusBackedTokenSummaryReader(AgentMessageBusStore store, IConfiguration config)
    {
        _store = store;
        _config = config;
    }

    /// <summary>
    /// Read every orchestrator-driven <c>kind=token-usage</c> message for
    /// <paramref name="projectName"/> and fold them into a
    /// <see cref="TokenSummary"/>. Returns an empty summary when the
    /// workspace root is not configured.
    /// </summary>
    public TokenSummary Summarize(string projectName)
    {
        var workspace = _config["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace))
            return TokenSummaryService.Summarize(projectName, Array.Empty<OrchestratorLogEntry>());
        return SummarizeFromStore(_store, workspace!, projectName);
    }

    /// <summary>
    /// Per-job rollup used by the kanban card's token bubble. Walks the
    /// same bus projection the project summary reads from.
    /// </summary>
    public Dictionary<string, JobTokenSummary> SummarizePerJob(string projectName)
    {
        var workspace = _config["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace))
            return new Dictionary<string, JobTokenSummary>(StringComparer.Ordinal);
        return SummarizePerJobFromStore(_store, workspace!, projectName);
    }

    /// <summary>
    /// Workspace-wide aggregate over the supplied projects. Walks every
    /// project, runs <see cref="Summarize"/>, then folds through the
    /// shared <see cref="TokenSummaryService.AggregateSummaries"/> so the
    /// status-bar usage modal sees the same fold whether it came from
    /// the bus or from <c>orchestrator.jsonl</c>.
    /// </summary>
    public TokenSummaryAggregate Aggregate(IEnumerable<(string Name, string WatchPath)> projects, TokenSummaryCacheStore? cache = null)
    {
        var workspace = _config["TaskRepository"];
        var perProject = new List<(string Name, TokenSummary Summary)>();
        foreach (var (name, _) in projects)
        {
            var summary = string.IsNullOrWhiteSpace(workspace)
                ? TokenSummaryService.Summarize(name, Array.Empty<OrchestratorLogEntry>())
                : SummarizeFromStore(_store, workspace!, name);
            perProject.Add((name, summary));
        }
        return TokenSummaryService.AggregateSummaries(perProject, cache);
    }

    /// <summary>
    /// Pure overload used by the parity test. Reads the bus directly and
    /// folds through the legacy aggregator so divergence is impossible.
    /// </summary>
    public static TokenSummary SummarizeFromStore(AgentMessageBusStore store, string workspaceRoot, string projectName)
    {
        var entries = BusTokenEntryConverter.LoadOrchestratorEntries(store, workspaceRoot, projectName);
        return TokenSummaryService.Summarize(projectName, entries);
    }

    /// <summary>
    /// Pure overload for the per-job rollup. Same conversion contract as
    /// <see cref="SummarizeFromStore"/>.
    /// </summary>
    public static Dictionary<string, JobTokenSummary> SummarizePerJobFromStore(AgentMessageBusStore store, string workspaceRoot, string projectName)
    {
        var entries = BusTokenEntryConverter.LoadOrchestratorEntries(store, workspaceRoot, projectName);
        return TokenSummaryService.SummarizePerJob(entries);
    }
}
