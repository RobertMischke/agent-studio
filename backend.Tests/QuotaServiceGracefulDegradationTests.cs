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
                ["Quota:TtlSeconds"] = "1"
            })
            .Build();
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task SuccessfulClaudeAndCodexProbes_PersistAndServeFresh()
    {
        var capturedAt = DateTime.UtcNow;
        var codex = new ScriptedProbe("codex", _ => GoodSnapshot(
            "codex", "codex-cli 0.149.0", capturedAt, 34, 61));
        var claude = new ScriptedProbe("claude", _ => GoodSnapshot(
            "claude", "2.1.202", capturedAt, 22, 47));
        var service = NewService(codex, claude);

        var report = await service.RefreshAllAsync();

        Assert.Collection(
            report.Snapshots.OrderBy(snapshot => snapshot.CliType),
            snapshot => AssertFresh(snapshot, "claude", "2.1.202", 22, 47),
            snapshot => AssertFresh(snapshot, "codex", "codex-cli 0.149.0", 34, 61));

        var persisted = new QuotaCacheStore(
            _configuration,
            NullLogger<QuotaCacheStore>.Instance).Read();
        Assert.Collection(
            persisted.OrderBy(snapshot => snapshot.CliType),
            snapshot => AssertLastGood(snapshot, "claude", "2.1.202", 22, 47),
            snapshot => AssertLastGood(snapshot, "codex", "codex-cli 0.149.0", 34, 61));
    }

    [Fact]
    public async Task RefreshAsync_FailedProbe_RetainsLastGoodValuesAndAddsFailureMetadata()
    {
        var fetchedAt = DateTime.UtcNow.AddMinutes(-2);
        var probe = new ScriptedProbe("codex", call => call == 1
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
        Assert.True(reportSnapshot.Stale is true);
        Assert.Equal(fetchedAt, reportSnapshot.CapturedAt);
        Assert.True(reportSnapshot.AgeSeconds is >= 120);
    }

    [Fact]
    public async Task ColdStart_LoadsPersistedLastGoodBeforeBackgroundProbeCompletes()
    {
        var capturedAt = DateTime.UtcNow.AddMinutes(-5);
        var store = new QuotaCacheStore(_configuration, NullLogger<QuotaCacheStore>.Instance);
        store.Write([GoodSnapshot("codex", "codex-cli 0.149.0", capturedAt, 34, 61)]);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var service = NewService(new BlockingProbe("codex", entered, release));

        try
        {
            var report = service.GetWithBackgroundRefresh();
            var snapshot = Assert.Single(report.Snapshots);

            Assert.Equal(capturedAt, snapshot.CapturedAt);
            Assert.Equal(61, snapshot.Windows.Single(window => window.Label == "Weekly").UsedPct);
            Assert.True(snapshot.Stale is true);
            Assert.True(snapshot.AgeSeconds is >= 300);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(1)), "Background refresh never started.");
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task GetWithBackgroundRefresh_DoesNotWaitForSynchronousProbeStartup()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var service = NewService(new BlockingProbe("codex", entered, release));

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

    private static QuotaSnapshot GoodSnapshot(
        string cliType,
        string cliVersion,
        DateTime capturedAt,
        double fiveHourPercent,
        double weeklyPercent) => new()
    {
        CliType = cliType,
        CliVersion = cliVersion,
        FetchedAt = capturedAt,
        Plan = "Pro",
        Source = cliType == "codex" ? "/status" : "/usage",
        Windows =
        [
            new QuotaWindow { Label = "5-hour", UsedPct = fiveHourPercent },
            new QuotaWindow { Label = "Weekly", UsedPct = weeklyPercent }
        ]
    };

    private static void AssertFresh(
        QuotaSnapshot snapshot,
        string cliType,
        string cliVersion,
        double fiveHourPercent,
        double weeklyPercent)
    {
        AssertLastGood(snapshot, cliType, cliVersion, fiveHourPercent, weeklyPercent);
        Assert.NotNull(snapshot.CapturedAt);
        Assert.True(snapshot.Stale is false);
        Assert.InRange(snapshot.AgeSeconds!.Value, 0, 2);
    }

    private static void AssertLastGood(
        QuotaSnapshot snapshot,
        string cliType,
        string cliVersion,
        double fiveHourPercent,
        double weeklyPercent)
    {
        Assert.Equal(cliType, snapshot.CliType);
        Assert.Equal(cliVersion, snapshot.CliVersion);
        Assert.Equal("Pro", snapshot.Plan);
        Assert.Equal(fiveHourPercent, snapshot.Windows.Single(window => window.Label == "5-hour").UsedPct);
        Assert.Equal(weeklyPercent, snapshot.Windows.Single(window => window.Label == "Weekly").UsedPct);
    }

    private sealed class ScriptedProbe(string cliType, Func<int, QuotaSnapshot> script) : IQuotaProbe
    {
        private int _calls;
        public string CliType => cliType;
        public Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
            => Task.FromResult(script(Interlocked.Increment(ref _calls)));
    }

    private sealed class BlockingProbe(
        string cliType,
        ManualResetEventSlim entered,
        ManualResetEventSlim release) : IQuotaProbe
    {
        public string CliType => cliType;

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
