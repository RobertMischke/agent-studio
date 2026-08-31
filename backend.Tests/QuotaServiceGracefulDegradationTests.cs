using System.Diagnostics;
using System.Text.Json;
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
    public async Task RefreshAsync_SuccessPersistsAndServesFreshSnapshotMetadata()
    {
        var fetchedAt = DateTime.UtcNow.AddMilliseconds(-100);
        var service = NewService(new ScriptedProbe(_ => new QuotaSnapshot
        {
            CliType = "codex",
            CliVersion = "codex-cli 0.149.0",
            FetchedAt = fetchedAt,
            Plan = "Pro",
            Source = "/status",
            Windows =
            [
                new QuotaWindow { Label = "5-hour", UsedPct = 32 },
                new QuotaWindow { Label = "Weekly", UsedPct = 61 }
            ]
        }));

        await service.RefreshAsync("codex");

        var served = Assert.Single(service.GetCached().Snapshots);
        Assert.Equal(fetchedAt, served.CapturedAt);
        Assert.False(served.IsStale);
        Assert.InRange(served.AgeSeconds!.Value, 0, 5);
        Assert.Equal("codex-cli 0.149.0", served.CliVersion);

        var cachePath = Path.Combine(_repoDir, ".runtime", "quota-cache.json");
        using var persisted = JsonDocument.Parse(File.ReadAllText(cachePath));
        var codex = Assert.Single(persisted.RootElement.EnumerateArray());
        Assert.Equal("codex", codex.GetProperty("cliType").GetString());
        Assert.Equal("codex-cli 0.149.0", codex.GetProperty("cliVersion").GetString());
        Assert.Equal(fetchedAt, codex.GetProperty("capturedAt").GetDateTime());
        Assert.Equal(61, codex.GetProperty("windows")[1].GetProperty("usedPct").GetDouble());
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
        await service.RefreshAsync("codex");
        var stale = Assert.Single(service.GetCached().Snapshots);

        Assert.Equal(fetchedAt, stale.FetchedAt);
        Assert.Equal(fetchedAt, stale.CapturedAt);
        Assert.True(stale.IsStale);
        Assert.True(stale.AgeSeconds!.Value >= 120);
        Assert.Equal("Pro", stale.Plan);
        Assert.Equal(61, Assert.Single(stale.Windows).UsedPct);
        Assert.Equal("codex-cli 0.149.0", stale.CliVersion);
        Assert.NotNull(stale.ProbeFailedAt);
        Assert.Equal("Quota probe timed out before the CLI panel rendered.", stale.Error);
    }

    [Fact]
    public async Task GetWithBackgroundRefresh_ColdStartServesPersistedSnapshotImmediately()
    {
        var fetchedAt = DateTime.UtcNow.AddMinutes(-2);
        var writer = NewService(new ScriptedProbe(_ => new QuotaSnapshot
        {
            CliType = "codex",
            CliVersion = "codex-cli 0.149.0",
            FetchedAt = fetchedAt,
            Plan = "Pro",
            Source = "/status",
            Windows = [new QuotaWindow { Label = "Weekly", UsedPct = 61 }]
        }));
        await writer.RefreshAsync("codex");

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var restarted = NewService(new BlockingProbe(entered, release));

        try
        {
            var report = restarted.GetWithBackgroundRefresh();

            var served = Assert.Single(report.Snapshots);
            Assert.Equal(fetchedAt, served.CapturedAt);
            Assert.Equal(61, Assert.Single(served.Windows).UsedPct);
            Assert.True(served.IsStale);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(1)), "Cold-start background refresh never started.");
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

    private QuotaService NewService(IQuotaProbe probe)
    {
        var store = new QuotaCacheStore(_configuration, NullLogger<QuotaCacheStore>.Instance);
        return new QuotaService(
            NullLogger<QuotaService>.Instance,
            [probe],
            _configuration,
            store);
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
