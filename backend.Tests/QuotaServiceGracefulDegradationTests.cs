using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class QuotaServiceGracefulDegradationTests : IDisposable
{
    private readonly string _repoDir = Path.Combine(
        Path.GetTempPath(),
        "agent-studio-quota-degrade-" + Guid.NewGuid().ToString("N"));
    private readonly IConfiguration _configuration;

    public QuotaServiceGracefulDegradationTests()
    {
        Directory.CreateDirectory(_repoDir);
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _repoDir,
                ["Quota:TtlSeconds"] = "1"
            })
            .Build();
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task RefreshAsync_FailedProbe_RetainsLastGoodValuesAndAddsFailureMetadata()
    {
        var fetchedAt = DateTime.UtcNow.AddMinutes(-2);
        var probe = new ScriptedProbe(call => call == 1
            ? new QuotaSnapshot
            {
                CliType = "codex",
                CliVersion = "codex-cli 0.149.0",
                FetchedAt = fetchedAt,
                Plan = "Pro",
                Source = "/status",
                Windows = [new QuotaWindow { Label = "Weekly", UsedPct = 61 }]
            }
            : new QuotaSnapshot
            {
                CliType = "codex",
                CliVersion = "codex-cli 0.149.0",
                Error = "A task was canceled."
            });
        var service = NewService(probe);

        await service.RefreshAsync("codex");
        var stale = await service.RefreshAsync("codex");

        Assert.NotNull(stale);
        Assert.Equal(fetchedAt, stale.FetchedAt);
        Assert.Equal("Pro", stale.Plan);
        Assert.Equal(61, Assert.Single(stale.Windows).UsedPct);
        Assert.Equal("codex-cli 0.149.0", stale.CliVersion);
        Assert.NotNull(stale.ProbeFailedAt);
        Assert.Equal("Quota probe timed out before the CLI panel rendered.", stale.Error);

        var served = Assert.Single(service.GetCached().Snapshots);
        Assert.Equal(fetchedAt, served.CapturedAt);
        Assert.True(served.Stale);
        Assert.True(served.AgeSeconds >= 120);
    }

    [Fact]
    public async Task GetCached_DoesNotWaitForLiveProbe()
    {
        var probe = new AwaitableProbe();
        var service = NewService(probe);
        var refresh = service.RefreshAsync("codex");
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var request = Task.Run(service.GetCached);
        var report = await request.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Single(report.Snapshots);
        probe.Release.TrySetResult(new QuotaSnapshot { CliType = "codex" });
        await refresh;
    }

    [Fact]
    public async Task RefreshAllAsync_SuccessPersistsCodexAndClaudeAsFreshWithVersions()
    {
        var capturedAt = DateTime.UtcNow.AddMilliseconds(-100);
        var codex = new ScriptedProbe("codex", _ => Successful("codex", "codex-cli 0.149.0", 37, capturedAt));
        var claude = new ScriptedProbe("claude", _ => Successful("claude", "2.1.202", 18, capturedAt));
        var service = NewService(codex, claude);

        await service.RefreshAllAsync();

        var served = service.GetCached().Snapshots.OrderBy(snapshot => snapshot.CliType).ToList();
        Assert.Collection(
            served,
            snapshot => AssertFresh(snapshot, "claude", "2.1.202", 18, capturedAt),
            snapshot => AssertFresh(snapshot, "codex", "codex-cli 0.149.0", 37, capturedAt));

        var persisted = new QuotaCacheStore(_configuration, NullLogger<QuotaCacheStore>.Instance)
            .Read()
            .OrderBy(snapshot => snapshot.CliType)
            .ToList();
        Assert.Collection(
            persisted,
            snapshot => Assert.Equal("2.1.202", snapshot.CliVersion),
            snapshot => Assert.Equal("codex-cli 0.149.0", snapshot.CliVersion));
    }

    [Fact]
    public async Task ColdStart_WithPersistedFile_ServesLastGoodImmediatelyWithoutStartingProbe()
    {
        var capturedAt = DateTime.UtcNow.AddMinutes(-5);
        var writer = NewService(new ScriptedProbe(
            "codex",
            _ => Successful("codex", "codex-cli 0.149.0", 61, capturedAt)));
        await writer.RefreshAsync("codex");

        var coldProbe = new ScriptedProbe("codex", _ => throw new InvalidOperationException("must not probe on GET"));
        var restarted = NewService(coldProbe);

        var served = Assert.Single(restarted.GetCached().Snapshots);
        Assert.Equal(0, coldProbe.Calls);
        Assert.Equal(capturedAt, served.CapturedAt);
        Assert.Equal(61, Assert.Single(served.Windows).UsedPct);
        Assert.Equal("codex-cli 0.149.0", served.CliVersion);
        Assert.True(served.Stale);
    }

    [Fact]
    public void CliVersionTracker_LogsOneAttributableChange()
    {
        var logger = new RecordingLogger<CliVersionTracker>();
        var tracker = new CliVersionTracker(logger);
        tracker.Seed("codex", "codex-cli 0.144.1");

        tracker.Observe("codex", "codex-cli 0.149.0", "startup");
        tracker.Observe("codex", "codex-cli 0.149.0", "periodic");

        var change = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, change.Level);
        Assert.Contains("CLI version changed", change.Message, StringComparison.Ordinal);
        Assert.Contains("0.144.1", change.Message, StringComparison.Ordinal);
        Assert.Contains("0.149.0", change.Message, StringComparison.Ordinal);
        Assert.Contains("startup", change.Message, StringComparison.Ordinal);
    }

    private QuotaService NewService(params IQuotaProbe[] probes)
    {
        var store = new QuotaCacheStore(_configuration, NullLogger<QuotaCacheStore>.Instance);
        return new QuotaService(
            NullLogger<QuotaService>.Instance,
            probes,
            _configuration,
            store);
    }

    private static QuotaSnapshot Successful(
        string cliType,
        string version,
        double weeklyPercent,
        DateTime capturedAt) => new()
    {
        CliType = cliType,
        CliVersion = version,
        FetchedAt = capturedAt,
        Plan = "Pro",
        Source = cliType == "codex" ? "/status" : "/usage",
        Windows = [new QuotaWindow { Label = "Weekly", UsedPct = weeklyPercent }]
    };

    private static void AssertFresh(
        QuotaSnapshot snapshot,
        string cliType,
        string version,
        double weeklyPercent,
        DateTime capturedAt)
    {
        Assert.Equal(cliType, snapshot.CliType);
        Assert.Equal(version, snapshot.CliVersion);
        Assert.Equal(capturedAt, snapshot.CapturedAt);
        Assert.Equal(weeklyPercent, Assert.Single(snapshot.Windows).UsedPct);
        Assert.False(snapshot.Stale);
        Assert.InRange(snapshot.AgeSeconds, 0, 5);
    }

    private sealed class ScriptedProbe : IQuotaProbe
    {
        private readonly Func<int, QuotaSnapshot> _script;
        private int _calls;

        public ScriptedProbe(Func<int, QuotaSnapshot> script)
            : this("codex", script) { }

        public ScriptedProbe(string cliType, Func<int, QuotaSnapshot> script)
        {
            CliType = cliType;
            _script = script;
        }

        public string CliType { get; }
        public int Calls => _calls;
        public Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
            => Task.FromResult(_script(Interlocked.Increment(ref _calls)));
    }

    private sealed class AwaitableProbe : IQuotaProbe
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<QuotaSnapshot> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string CliType => "codex";

        public async Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
        {
            Entered.TrySetResult();
            return await Release.Task.WaitAsync(ct);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
