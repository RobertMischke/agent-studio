using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Bus;
using OrchestratorApi.Services.Runner;
using OrchestratorApi.Services.Tokens;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Phase-5 parity test for <see cref="ProjectTokenUsageService"/>. Drives
/// every surface (Summary / Heatmap / ExpensiveJobs / JobDetail) through
/// both the legacy reader (<c>orchestrator.jsonl</c>) and the Phase-4
/// bus-backed reader (<see cref="BusBackedProjectTokenUsageReader"/>)
/// and asserts byte-identical output.
///
/// <para>
/// The legacy reader categorises each entry as Job / Supporting /
/// Orchestrator by walking <see cref="JobScannerService.ScanAllJobs"/>
/// and matching the job title prefix against
/// <see cref="ProjectTokenUsageService.SupportingJobTitlePrefixes"/>.
/// The Phase-4 bus-backed reader keeps the same categorisation rule by
/// reusing the legacy pure-function fold; this parity test confirms the
/// rule transfers cleanly across the source-of-truth change.
/// </para>
/// </summary>
public sealed class ProjectTokenUsageBusParityTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string ProjectName = "agent-taskboard";

    public ProjectTokenUsageBusParityTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "project-token-usage-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
        _watchPath = Path.Combine(_workspace, "watched");
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); }
        catch { /* best-effort */ }
    }

    private static readonly DateTime Now = new(2026, 5, 11, 15, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task BuildSummary_MultiJobMultiCategory_Parity()
    {
        var (log, bridge, store) = BuildStack();
        var jobsById = BuildJobs(
            ("real-job-a", "Add login screen"),
            ("real-job-b", "Refactor token aggregation"),
            ("support-1",  "Security audit of token flow"),
            ("support-2",  "Drift analysis on auth code"));

        await WriteAsync(log, bridge, store,
            MakeEntry("claude-opus-4-7",   50_000, 4_000, Now.AddHours(-23),                jobId: "real-job-a"),
            MakeEntry("claude-opus-4-7",   80_000, 6_000, Now.AddHours(-12),                jobId: "real-job-b"),
            MakeEntry("claude-haiku-4-5",  10_000, 1_200, Now.AddHours(-8),                 jobId: "support-1"),
            MakeEntry("claude-haiku-4-5",   5_000,   500, Now.AddHours(-2),                 jobId: "support-2"),
            // jobId=null -> orchestrator bucket
            MakeEntry("claude-haiku-4-5",   2_000,   100, Now.AddMinutes(-30),              jobId: null),
            // outside the 24h window, lifetime-only
            MakeEntry("claude-opus-4-7",   30_000, 2_500, Now.AddHours(-48),                jobId: "real-job-a"));

        var legacy = ProjectTokenUsageService.BuildSummaryFromEntries(ProjectName, log.Read(_watchPath), jobsById, Now);
        var bus    = BusBackedProjectTokenUsageReader.BuildSummaryFromStore(store, _workspace, ProjectName, jobsById, Now);

        AssertSummaryEquivalent(legacy, bus);
        Assert.True(legacy.HasData);
        Assert.True(legacy.LifetimeJobTokens > 0);
        Assert.True(legacy.LifetimeSupportingTokens > 0);
        Assert.True(legacy.LifetimeOrchestratorTokens > 0);
    }

    [Fact]
    public async Task BuildHeatmap_30Days_Parity()
    {
        var (log, bridge, store) = BuildStack();
        var jobsById = BuildJobs(
            ("hot-job", "Refactor token aggregation"),
            ("cool-job", "Add login screen"),
            ("audit-1", "Security audit of token flow"));

        await WriteAsync(log, bridge, store,
            MakeEntry("claude-opus-4-7",   60_000, 5_000, Now.AddDays(-1),  jobId: "hot-job"),
            MakeEntry("claude-opus-4-7",   55_000, 4_500, Now.AddDays(-1).AddHours(2), jobId: "hot-job"),
            MakeEntry("claude-opus-4-7",   30_000, 2_500, Now.AddDays(-3),  jobId: "hot-job"),
            MakeEntry("claude-haiku-4-5",  10_000, 1_000, Now.AddDays(-2),  jobId: "cool-job"),
            MakeEntry("claude-haiku-4-5",   8_000,   800, Now.AddDays(-5),  jobId: "audit-1"),
            // Outside the 30-day window
            MakeEntry("claude-opus-4-7",   90_000, 9_000, Now.AddDays(-45), jobId: "hot-job"));

        var legacy = ProjectTokenUsageService.BuildHeatmapFromEntries(ProjectName, log.Read(_watchPath), jobsById, 30, Now);
        var bus    = BusBackedProjectTokenUsageReader.BuildHeatmapFromStore(store, _workspace, ProjectName, jobsById, 30, Now);

        AssertHeatmapEquivalent(legacy, bus);
    }

    [Fact]
    public async Task BuildExpensiveJobs_TopN_Parity()
    {
        var (log, bridge, store) = BuildStack();
        var jobsById = BuildJobs(
            ("job-a", "Add login screen"),
            ("job-b", "Refactor token aggregation"),
            ("job-c", "Add settings page"),
            ("audit-x", "Security audit of token flow"));

        await WriteAsync(log, bridge, store,
            MakeEntry("claude-opus-4-7",  100_000, 8_000, Now.AddHours(-10), jobId: "job-a"),
            MakeEntry("claude-opus-4-7",  120_000, 9_000, Now.AddHours(-9),  jobId: "job-b"),
            MakeEntry("claude-opus-4-7",   80_000, 6_000, Now.AddHours(-8),  jobId: "job-b"),
            MakeEntry("claude-haiku-4-5", 200_000, 8_000, Now.AddHours(-7),  jobId: "audit-x"),
            MakeEntry("claude-haiku-4-5",  10_000, 1_000, Now.AddHours(-6),  jobId: "job-c"),
            // JobId that no longer resolves -> stays visible under raw id
            MakeEntry("claude-opus-4-7",   50_000, 4_000, Now.AddHours(-5),  jobId: "deleted-job"));

        var legacy = ProjectTokenUsageService.BuildExpensiveJobsFromEntries(log.Read(_watchPath), jobsById, 3);
        var bus    = BusBackedProjectTokenUsageReader.BuildExpensiveJobsFromStore(store, _workspace, ProjectName, jobsById, 3);

        AssertExpensiveListEquivalent(legacy, bus);
        Assert.Equal(3, legacy.Count);
    }

    [Fact]
    public async Task BuildJobDetail_DeltasVsPrior_Parity()
    {
        var (log, bridge, store) = BuildStack();
        var jobsById = BuildJobs(("job-a", "Add login screen"));

        await WriteAsync(log, bridge, store,
            MakeEntry("claude-opus-4-7", 10_000, 1_000, Now.AddHours(-5), jobId: "job-a"),
            MakeEntry("claude-opus-4-7", 12_000, 1_200, Now.AddHours(-4), jobId: "job-a"),
            MakeEntry("claude-opus-4-7",  8_000,   600, Now.AddHours(-3), jobId: "job-a"),
            MakeEntry("claude-opus-4-7", 25_000, 2_500, Now.AddHours(-2), jobId: "job-a"),
            // A different job's entry, must be excluded from job-a's detail
            MakeEntry("claude-opus-4-7", 50_000, 4_000, Now.AddHours(-1), jobId: "other-job"));

        var legacy = ProjectTokenUsageService.BuildJobDetailFromEntries(ProjectName, log.Read(_watchPath), jobsById, "job-a");
        var bus    = BusBackedProjectTokenUsageReader.BuildJobDetailFromStore(store, _workspace, ProjectName, jobsById, "job-a");

        Assert.NotNull(legacy);
        Assert.NotNull(bus);
        AssertJobDetailEquivalent(legacy!, bus!);
        // The first call must have a null delta; subsequent calls must have deltas.
        Assert.Null(legacy!.Runs[0].DeltaVsPrev);
        Assert.NotNull(legacy.Runs[1].DeltaVsPrev);
    }

    [Fact]
    public async Task BuildSummary_NoData_HasDataFalseOnBothReaders()
    {
        var (log, _bridge, store) = BuildStack();
        var jobsById = BuildJobs();

        await Task.CompletedTask;

        var legacy = ProjectTokenUsageService.BuildSummaryFromEntries(ProjectName, log.Read(_watchPath), jobsById, Now);
        var bus    = BusBackedProjectTokenUsageReader.BuildSummaryFromStore(store, _workspace, ProjectName, jobsById, Now);

        Assert.False(legacy.HasData);
        Assert.False(bus.HasData);
        AssertSummaryEquivalent(legacy, bus);
    }

    [Fact]
    public async Task BuildJobDetail_UnknownJob_NullOnBothReaders()
    {
        var (log, bridge, store) = BuildStack();
        var jobsById = BuildJobs(("job-a", "Add login screen"));

        await WriteAsync(log, bridge, store,
            MakeEntry("claude-opus-4-7", 5_000, 400, Now.AddHours(-1), jobId: "job-a"));

        var legacy = ProjectTokenUsageService.BuildJobDetailFromEntries(ProjectName, log.Read(_watchPath), jobsById, "no-such-job");
        var bus    = BusBackedProjectTokenUsageReader.BuildJobDetailFromStore(store, _workspace, ProjectName, jobsById, "no-such-job");

        Assert.Null(legacy);
        Assert.Null(bus);
    }

    private static void AssertSummaryEquivalent(ProjectTokenUsageSummary a, ProjectTokenUsageSummary b)
    {
        Assert.Equal(a.Project,                       b.Project);
        Assert.Equal(a.HasData,                       b.HasData);
        Assert.Equal(a.LifetimeTotalTokens,           b.LifetimeTotalTokens);
        Assert.Equal(a.LifetimeJobTokens,             b.LifetimeJobTokens);
        Assert.Equal(a.LifetimeSupportingTokens,      b.LifetimeSupportingTokens);
        Assert.Equal(a.LifetimeOrchestratorTokens,    b.LifetimeOrchestratorTokens);
        Assert.Equal(a.LifetimeCalls,                 b.LifetimeCalls);
        Assert.Equal(a.Last24hTotalTokens,            b.Last24hTotalTokens);
        Assert.Equal(a.Last24hJobTokens,              b.Last24hJobTokens);
        Assert.Equal(a.Last24hSupportingTokens,       b.Last24hSupportingTokens);
        Assert.Equal(a.Last24hOrchestratorTokens,     b.Last24hOrchestratorTokens);
        Assert.Equal(a.Last24hCalls,                  b.Last24hCalls);
        Assert.Equal(a.FirstActivity,                 b.FirstActivity);
        Assert.Equal(a.LastActivity,                  b.LastActivity);
    }

    private static void AssertHeatmapEquivalent(ProjectTokenHeatmap a, ProjectTokenHeatmap b)
    {
        Assert.Equal(a.Project, b.Project);
        Assert.Equal(a.HasData, b.HasData);
        Assert.Equal(a.Days,    b.Days);
        Assert.Equal(a.Jobs.Count, b.Jobs.Count);
        for (var i = 0; i < a.Jobs.Count; i++)
        {
            Assert.Equal(a.Jobs[i].JobId,        b.Jobs[i].JobId);
            Assert.Equal(a.Jobs[i].Title,        b.Jobs[i].Title);
            Assert.Equal(a.Jobs[i].State,        b.Jobs[i].State);
            Assert.Equal(a.Jobs[i].Category,     b.Jobs[i].Category);
            Assert.Equal(a.Jobs[i].Total,        b.Jobs[i].Total);
            Assert.Equal(a.Jobs[i].Calls,        b.Jobs[i].Calls);
            Assert.Equal(a.Jobs[i].LastActivity, b.Jobs[i].LastActivity);
            Assert.Equal(a.Jobs[i].Cells.Count,  b.Jobs[i].Cells.Count);
            for (var c = 0; c < a.Jobs[i].Cells.Count; c++)
            {
                Assert.Equal(a.Jobs[i].Cells[c].Day,   b.Jobs[i].Cells[c].Day);
                Assert.Equal(a.Jobs[i].Cells[c].Total, b.Jobs[i].Cells[c].Total);
            }
        }
    }

    private static void AssertExpensiveListEquivalent(IReadOnlyList<ProjectExpensiveJob> a, IReadOnlyList<ProjectExpensiveJob> b)
    {
        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].JobId,        b[i].JobId);
            Assert.Equal(a[i].Title,        b[i].Title);
            Assert.Equal(a[i].State,        b[i].State);
            Assert.Equal(a[i].Category,     b[i].Category);
            Assert.Equal(a[i].TotalTokens,  b[i].TotalTokens);
            Assert.Equal(a[i].Calls,        b[i].Calls);
            Assert.Equal(a[i].LastActivity, b[i].LastActivity);
            Assert.Equal(a[i].LastModel,    b[i].LastModel);
        }
    }

    private static void AssertJobDetailEquivalent(ProjectJobTokenDetail a, ProjectJobTokenDetail b)
    {
        Assert.Equal(a.Project,             b.Project);
        Assert.Equal(a.JobId,               b.JobId);
        Assert.Equal(a.Title,               b.Title);
        Assert.Equal(a.State,               b.State);
        Assert.Equal(a.Category,            b.Category);
        Assert.Equal(a.TotalTokens,         b.TotalTokens);
        Assert.Equal(a.InputTokens,         b.InputTokens);
        Assert.Equal(a.OutputTokens,        b.OutputTokens);
        Assert.Equal(a.CacheReadTokens,     b.CacheReadTokens);
        Assert.Equal(a.CacheCreationTokens, b.CacheCreationTokens);
        Assert.Equal(a.Calls,               b.Calls);
        Assert.Equal(a.FirstActivity,       b.FirstActivity);
        Assert.Equal(a.LastActivity,        b.LastActivity);
        Assert.Equal(a.LastModel,           b.LastModel);

        Assert.Equal(a.Runs.Count, b.Runs.Count);
        for (var i = 0; i < a.Runs.Count; i++)
        {
            Assert.Equal(a.Runs[i].Index,               b.Runs[i].Index);
            Assert.Equal(a.Runs[i].Ts,                  b.Runs[i].Ts);
            Assert.Equal(a.Runs[i].Model,               b.Runs[i].Model);
            Assert.Equal(a.Runs[i].InputTokens,         b.Runs[i].InputTokens);
            Assert.Equal(a.Runs[i].OutputTokens,        b.Runs[i].OutputTokens);
            Assert.Equal(a.Runs[i].CacheReadTokens,     b.Runs[i].CacheReadTokens);
            Assert.Equal(a.Runs[i].CacheCreationTokens, b.Runs[i].CacheCreationTokens);
            Assert.Equal(a.Runs[i].Total,               b.Runs[i].Total);
            Assert.Equal(a.Runs[i].DeltaVsPrev,         b.Runs[i].DeltaVsPrev);
            Assert.Equal(a.Runs[i].Topic,               b.Runs[i].Topic);
            // Per-row Summary is presentation metadata (the bus mints its own
            // "tokens: in=... out=..." headline at emit time; orchestrator.jsonl
            // carries the runner's own "Auto-decided for ..." text). Both
            // surface the same numeric reality through every other field, so
            // the Summary divergence is expected and excluded here, matching
            // the AdHocUsageBusParityTests exclusion of presentation-only
            // fields (LogPath / LogModifiedAt).
        }
    }

    private (OrchestratorLog log, AgentMessageBusBridge bridge, AgentMessageBusStore store) BuildStack()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _workspace })
            .Build();
        var log = new OrchestratorLog(NullLogger<OrchestratorLog>.Instance);
        var store = new AgentMessageBusStore();
        var bridge = new AgentMessageBusBridge(store, config, NullLogger<AgentMessageBusBridge>.Instance);
        return (log, bridge, store);
    }

    private static IReadOnlyDictionary<string, JobInfo> BuildJobs(params (string Id, string Title)[] jobs)
    {
        var map = new Dictionary<string, JobInfo>(StringComparer.Ordinal);
        foreach (var (id, title) in jobs)
        {
            map[id] = new JobInfo
            {
                Id = id,
                Title = title,
                State = "3-progress",
                ProjectName = ProjectName,
                WatchPath = "(test)",
                FolderPath = $"(test)/{id}",
            };
        }
        return map;
    }

    private async Task WriteAsync(OrchestratorLog log, AgentMessageBusBridge bridge, AgentMessageBusStore store, params OrchestratorLogEntry[] entries)
    {
        foreach (var e in entries)
        {
            log.Append(_watchPath, e);
            if (e.TokenUsage != null)
            {
                await bridge.EmitTokenUsageAsync(
                    project: ProjectName,
                    jobId: e.JobId,
                    participantId: AgentMessageBusBridge.ParticipantOrchestratorFor(ProjectName),
                    topic: e.Topic,
                    usage: e.TokenUsage,
                    createdAt: e.Ts);
            }
        }
        await WaitForBusCountAsync(store, entries.Count(x => x.TokenUsage != null));
    }

    private async Task WaitForBusCountAsync(AgentMessageBusStore store, int expected, int timeoutMs = 5_000)
    {
        var participant = AgentMessageBusBridge.ParticipantOrchestratorFor(ProjectName);
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var got = store.Query(_workspace, ProjectName, new AgentMessageQuery(
                ParticipantId: participant,
                Kind: "token-usage")).Count;
            if (got >= expected) return;
            await Task.Delay(25);
        }
        Assert.Fail($"Bus did not reach {expected} orchestrator token-usage messages within {timeoutMs}ms.");
    }

    private static OrchestratorLogEntry MakeEntry(string? model, int input, int output, DateTime ts, int cacheRead = 0, int cacheCreate = 0, string? jobId = null)
        => new()
        {
            Ts = ts,
            Kind = OrchestratorLogKinds.Decision,
            Topic = "orchestrator-decision",
            Summary = "parity entry",
            JobId = jobId,
            TokenUsage = new OrchestratorTokenUsage
            {
                Model = model,
                InputTokens = input,
                OutputTokens = output,
                CacheReadTokens = cacheRead,
                CacheCreationTokens = cacheCreate,
            },
        };
}
