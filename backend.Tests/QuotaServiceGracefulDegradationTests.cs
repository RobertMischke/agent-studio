using System.Diagnostics;
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
                ["Quota:TtlSeconds"] = "600"
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
                CliVersion = "codex-cli 0.144.1",
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
        Assert.Equal("codex-cli 0.144.1", stale.CliVersion);
        Assert.Equal("codex-cli 0.149.0", stale.ProbeCliVersion);
        Assert.NotNull(stale.ProbeFailedAt);
        Assert.Equal("Quota probe timed out before the CLI panel rendered.", stale.Error);

        var reportSnapshot = Assert.Single(service.GetCached().Snapshots);
        Assert.Equal(fetchedAt, reportSnapshot.CapturedAt);
        Assert.True(reportSnapshot.Stale);
        Assert.True(reportSnapshot.AgeSeconds >= 120);
    }

    [Fact]
    public async Task SuccessfulClaudeAndCodexProbes_PersistAndServeFresh()
    {
        var capturedAt = DateTime.UtcNow.AddSeconds(-2);
        var service = NewService(
            new FixedProbe("claude", "claude 2.1.202", "Max", 37, capturedAt),
            new FixedProbe("codex", "codex-cli 0.149.0", "Pro", 61, capturedAt));

        var report = await service.RefreshAllAsync();

        Assert.Collection(
            report.Snapshots.OrderBy(snapshot => snapshot.CliType),
            snapshot => AssertFresh(snapshot, "claude", "claude 2.1.202", 37, capturedAt),
            snapshot => AssertFresh(snapshot, "codex", "codex-cli 0.149.0", 61, capturedAt));

        var cachePath = Path.Combine(_repoDir, ".runtime", "quota-cache.json");
        Assert.True(File.Exists(cachePath));
        var persisted = File.ReadAllText(cachePath);
        Assert.Contains("\"capturedAt\"", persisted, StringComparison.Ordinal);
        Assert.Contains("claude 2.1.202", persisted, StringComparison.Ordinal);
        Assert.Contains("codex-cli 0.149.0", persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ColdStartWithPersistedFile_ServesImmediatelyWithoutStartingProbe()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var capturedAt = DateTime.UtcNow.AddMinutes(-2);
        var warmService = NewService(new FixedProbe(
            "codex", "codex-cli 0.149.0", "Pro", 61, capturedAt));
        await warmService.RefreshAsync("codex");

        var coldService = NewService(new BlockingProbe(entered, release));

        var stopwatch = Stopwatch.StartNew();
        var report = coldService.GetCached();
        stopwatch.Stop();

        var snapshot = Assert.Single(report.Snapshots);
        Assert.Equal(61, Assert.Single(snapshot.Windows).UsedPct);
        Assert.Equal(capturedAt, snapshot.CapturedAt);
        Assert.False(entered.IsSet, "A cache-only read started the live probe.");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Cached GET took {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
    }

    [Fact]
    public async Task HostedRefresher_StartsProbeAwayFromRequestPath()
    {
        var probe = new SignalingProbe();
        var service = NewService(probe);
        using var hosted = new QuotaRefreshHostedService(
            service,
            _configuration,
            NullLogger<QuotaRefreshHostedService>.Instance);

        await hosted.StartAsync(CancellationToken.None);
        try
        {
            await probe.Called.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(44, Assert.Single(service.GetCached().Snapshots).Windows.Single().UsedPct);
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
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

    private static void AssertFresh(
        QuotaSnapshot snapshot,
        string cliType,
        string cliVersion,
        double usedPct,
        DateTime capturedAt)
    {
        Assert.Equal(cliType, snapshot.CliType);
        Assert.Equal(cliVersion, snapshot.CliVersion);
        Assert.Equal(usedPct, Assert.Single(snapshot.Windows).UsedPct);
        Assert.Equal(capturedAt, snapshot.CapturedAt);
        Assert.False(snapshot.Stale);
        Assert.NotNull(snapshot.AgeSeconds);
        Assert.InRange(snapshot.AgeSeconds.Value, 0, 10);
    }

    private sealed class ScriptedProbe(Func<int, QuotaSnapshot> script) : IQuotaProbe
    {
        private int _calls;
        public string CliType => "codex";
        public Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
            => Task.FromResult(script(Interlocked.Increment(ref _calls)));
    }

    private sealed class BlockingProbe(
        ManualResetEventSlim entered,
        ManualResetEventSlim release) : IQuotaProbe
    {
        public string CliType => "codex";

        public Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
        {
            entered.Set();
            release.Wait(ct);
            return Task.FromResult(new QuotaSnapshot { CliType = CliType });
        }
    }

    private sealed class FixedProbe(
        string cliType,
        string cliVersion,
        string plan,
        double usedPct,
        DateTime capturedAt) : IQuotaProbe
    {
        public string CliType => cliType;

        public Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
            => Task.FromResult(new QuotaSnapshot
            {
                CliType = cliType,
                CliVersion = cliVersion,
                FetchedAt = capturedAt,
                Plan = plan,
                Source = cliType == "codex" ? "/status" : "/usage",
                Windows = [new QuotaWindow { Label = "Weekly", UsedPct = usedPct }]
            });
    }

    private sealed class SignalingProbe : IQuotaProbe
    {
        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string CliType => "codex";

        public Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
        {
            Called.TrySetResult();
            return Task.FromResult(new QuotaSnapshot
            {
                CliType = CliType,
                CliVersion = "codex-cli 0.149.0",
                Plan = "Pro",
                Windows = [new QuotaWindow { Label = "Weekly", UsedPct = 44 }]
            });
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
