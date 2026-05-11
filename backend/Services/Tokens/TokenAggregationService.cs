using OrchestratorApi.Models;
using OrchestratorApi.Services.AdHoc;
using OrchestratorApi.Services.Bus;
using OrchestratorApi.Services.Runner;

namespace OrchestratorApi.Services.Tokens;

/// <summary>
/// Phase-3 delegating implementation of <see cref="ITokenAggregator"/>.
/// Forwards every call to the legacy per-surface aggregator that owns the
/// math today. Phase 4 will replace each forward one at a time with a
/// direct <see cref="BusAggregationCache"/> read, gated by Phase 5
/// parity tests.
/// </summary>
public sealed class TokenAggregationService : ITokenAggregator
{
    private readonly BusAggregationCache _bus;
    private readonly IConfiguration _config;
    private readonly ProjectTokenUsageService _projectUsage;
    private readonly TokenSummaryService _summary;
    private readonly WorkspaceTokensTimelineService _timeline;
    private readonly AdHocUsageService _adHoc;

    public TokenAggregationService(
        BusAggregationCache bus,
        IConfiguration config,
        ProjectTokenUsageService projectUsage,
        TokenSummaryService summary,
        WorkspaceTokensTimelineService timeline,
        AdHocUsageService adHoc)
    {
        _bus = bus;
        _config = config;
        _projectUsage = projectUsage;
        _summary = summary;
        _timeline = timeline;
        _adHoc = adHoc;
    }

    public TokenAggregateResponse ForProject(string project, DateTime? since = null, DateTime? until = null, CancellationToken ct = default)
    {
        var workspace = _config["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace))
        {
            return new TokenAggregateResponse(
                Project: project,
                TotalMessages: 0,
                Since: since,
                Until: until,
                ByModel: Array.Empty<TokenAggregateBucket>(),
                ByParticipant: Array.Empty<TokenAggregateBucket>(),
                ByDay: Array.Empty<TokenAggregateBucket>(),
                Totals: new TokenAggregateTotals(0, 0, 0, 0, 0, null));
        }
        return _bus.Aggregate(workspace!, project, since, until, ct);
    }

    public ProjectTokenUsageSummary ProjectSummary(string projectName, string watchPath, DateTime? nowUtc = null)
        => _projectUsage.BuildSummary(projectName, watchPath, nowUtc);

    public ProjectTokenHeatmap ProjectHeatmap(string projectName, string watchPath, int days, DateTime? nowUtc = null)
        => _projectUsage.BuildHeatmap(projectName, watchPath, days, nowUtc);

    public IReadOnlyList<ProjectExpensiveJob> ProjectExpensiveJobs(string projectName, string watchPath, int limit)
        => _projectUsage.BuildExpensiveJobs(projectName, watchPath, limit);

    public ProjectJobTokenDetail? ProjectJobDetail(string projectName, string watchPath, string jobId)
        => _projectUsage.BuildJobDetail(projectName, watchPath, jobId);

    public TokenSummary LifetimeSummary(string projectName, string watchPath)
        => _summary.Summarize(projectName, watchPath);

    public TokenSummaryAggregate WorkspaceAggregate(IEnumerable<(string Name, string WatchPath)> projects)
        => _summary.Aggregate(projects);

    public Dictionary<string, JobTokenSummary> WorkspacePerJob(string watchPath)
        => _summary.SummarizePerJob(watchPath);

    public TokenTimeline WorkspaceTimeline(IEnumerable<(string Name, string WatchPath)> projects, int windowHours, int bucketMinutes, DateTime? nowUtc = null)
        => _timeline.Build(projects, windowHours, bucketMinutes, nowUtc);

    public AdHocUsageAggregate AdHocAggregate(DateTime? since = null)
        => _adHoc.Aggregate(since);
}
