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

        var reportSnapshot = Assert.Single(service.GetCached().Snapshots);
        Assert.Equal(fetchedAt, reportSnapshot.CapturedAt);
        Assert.True(reportSnapshot.Stale);
        Assert.NotNull(reportSnapshot.AgeSeconds);
        Assert.Equal(reportSnapshot.ProbeFailedAt, reportSnapshot.StaleSince);
    }

    [Fact]
    public async Task SuccessfulClaudeAndCodexProbes_PersistAndServeExplicitFreshness()
    {
        var capturedAt = DateTime.UtcNow.AddSeconds(-3);
        var claude = new NamedProbe("claude", new QuotaSnapshot
        {
            CliType = "claude",
            CliVersion = "2.1.202 (Claude Code)",
            FetchedAt = capturedAt,
            Plan = "Max",
            Windows = [new QuotaWindow { Label = "Current session (5h)", UsedPct = 24 }]
        });
        var codex = new NamedProbe("codex", new QuotaSnapshot
        {
            CliType = "codex",
            CliVersion = "codex-cli 0.149.0",
            FetchedAt = capturedAt,
            Plan = "Pro",
            Windows = [new QuotaWindow { Label = "Weekly", UsedPct = 61 }]
        });
        var service = NewService(claude, codex);

        await service.RefreshAllAsync();

        var persistedPath = Path.Combine(_repoDir, ".runtime", "cli-quota-last-good.json");
        Assert.True(File.Exists(persistedPath));
        var stored = new QuotaCacheStore(_configuration, NullLogger<QuotaCacheStore>.Instance).Read();
        Assert.Equal(2, stored.Count);
        Assert.Contains(stored, snapshot => snapshot.CliType == "claude" && snapshot.CliVersion == "2.1.202 (Claude Code)");
        Assert.Contains(stored, snapshot => snapshot.CliType == "codex" && snapshot.CliVersion == "codex-cli 0.149.0");

        var report = service.GetCached();
        Assert.All(report.Snapshots, snapshot =>
        {
            Assert.Equal(capturedAt, snapshot.CapturedAt);
            Assert.False(snapshot.Stale);
            Assert.InRange(snapshot.AgeSeconds!.Value, 0, 30);
            Assert.Null(snapshot.StaleSince);
        });
    }

    [Fact]
    public async Task ColdStart_HydratesPersistedLastGoodAndReturnsWithoutCallingProbe()
    {
        var capturedAt = DateTime.UtcNow.AddSeconds(-2);
        var seed = NewService(new NamedProbe("codex", new QuotaSnapshot
        {
            CliType = "codex",
            CliVersion = "codex-cli 0.149.0",
            FetchedAt = capturedAt,
            Plan = "Pro",
            Windows = [new QuotaWindow { Label = "Weekly", UsedPct = 61 }]
        }));
        await seed.RefreshAsync("codex");
        var coldProbe = new CountingProbe("codex");
        var restarted = NewService(coldProbe);

        var stopwatch = Stopwatch.StartNew();
        var report = restarted.GetWithBackgroundRefresh();
        stopwatch.Stop();

        var snapshot = Assert.Single(report.Snapshots);
        Assert.Equal(61, Assert.Single(snapshot.Windows).UsedPct);
        Assert.Equal(capturedAt, snapshot.CapturedAt);
        Assert.False(snapshot.Stale);
        Assert.Equal(0, coldProbe.Calls);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task FailedProbeWithoutLastGood_IsNotPersisted()
    {
        var service = NewService(new NamedProbe("codex", new QuotaSnapshot
        {
            CliType = "codex",
            CliVersion = "codex-cli 0.149.0",
            Error = "A task was canceled."
        }));

        await service.RefreshAsync("codex");

        var stored = new QuotaCacheStore(_configuration, NullLogger<QuotaCacheStore>.Instance).Read();
        Assert.Empty(stored);
        var snapshot = Assert.Single(service.GetCached().Snapshots);
        Assert.Null(snapshot.CapturedAt);
        Assert.True(snapshot.Stale);
        Assert.Null(snapshot.AgeSeconds);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task GetWithBackgroundRefresh_DoesNotWaitForSynchronousProbeStartup()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var service = NewService(new BlockingProbe(entered, release));

        var stopwatch = Stopwatch.StartNew();
        var request = Task.Run(() => service.GetWithBackgroundRefresh());
        try
        {
            var report = await request.WaitAsync(TimeSpan.FromSeconds(1));
            stopwatch.Stop();

            Assert.Single(report.Snapshots);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(1)), "Background probe never started.");
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Cached GET took {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
        }
        finally
        {
            release.Set();
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

    private sealed class NamedProbe(string cliType, QuotaSnapshot snapshot) : IQuotaProbe
    {
        public string CliType { get; } = cliType;
        public Task<QuotaSnapshot> ProbeAsync(CancellationToken ct) => Task.FromResult(snapshot);
    }

    private sealed class CountingProbe(string cliType) : IQuotaProbe
    {
        private int _calls;
        public string CliType { get; } = cliType;
        public int Calls => _calls;
        public Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new QuotaSnapshot { CliType = CliType });
        }
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
