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
        Assert.True(served.IsStale);
        Assert.Equal(fetchedAt, served.CapturedAt);
        Assert.True(served.AgeSeconds >= 120);
        Assert.Equal(stale.ProbeFailedAt, served.StaleSince);
    }

    [Fact]
    public async Task SuccessfulClaudeAndCodexProbes_PersistAndServeFreshReadings()
    {
        var capturedAt = DateTime.UtcNow;
        var service = NewService(
            new ScriptedProbe("claude", _ => SuccessfulSnapshot("claude", "claude 2.1.202", 34, capturedAt)),
            new ScriptedProbe("codex", _ => SuccessfulSnapshot("codex", "codex-cli 0.149.0", 61, capturedAt)));

        await service.RefreshAllAsync();

        var served = service.GetCached().Snapshots.OrderBy(snapshot => snapshot.CliType).ToList();
        Assert.Collection(
            served,
            claude => AssertFreshReading(claude, "claude", "claude 2.1.202", capturedAt),
            codex => AssertFreshReading(codex, "codex", "codex-cli 0.149.0", capturedAt));
        Assert.True(File.Exists(Path.Combine(_repoDir, ".runtime", "cli-quota-last-good.json")));
    }

    [Fact]
    public async Task ColdStart_HydratesPersistedLastGoodBeforeAnyProbeCompletes()
    {
        var capturedAt = DateTime.UtcNow;
        var first = NewService(new ScriptedProbe(
            "codex",
            _ => SuccessfulSnapshot("codex", "codex-cli 0.149.0", 61, capturedAt)));
        await first.RefreshAsync("codex");

        var cold = NewService(new NeverCompletingProbe("codex"));
        var served = Assert.Single(cold.GetWithBackgroundRefresh().Snapshots);

        Assert.Equal(61, Assert.Single(served.Windows, window => window.Label == "Weekly").UsedPct);
        Assert.Equal(capturedAt, served.CapturedAt);
        Assert.Equal("codex-cli 0.149.0", served.CliVersion);
    }

    [Fact]
    public void ColdStart_LegacyCacheNormalizesRawCancellationText()
    {
        var runtime = Path.Combine(_repoDir, ".runtime");
        Directory.CreateDirectory(runtime);
        File.WriteAllText(
            Path.Combine(runtime, "quota-cache.json"),
            """
            [{
              "cliType": "codex",
              "fetchedAt": "2026-08-28T10:00:00Z",
              "probeFailedAt": "2026-08-28T10:10:00Z",
              "plan": "Pro",
              "windows": [{ "label": "Weekly", "usedPct": 61 }],
              "error": "A task was canceled."
            }]
            """);

        var cold = NewService(new NeverCompletingProbe("codex"));
        var served = Assert.Single(cold.GetCached().Snapshots);

        Assert.Equal("Quota probe timed out before the CLI panel rendered.", served.Error);
        Assert.DoesNotContain("task was canceled", served.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(served.IsStale);
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

    private static QuotaSnapshot SuccessfulSnapshot(
        string cliType,
        string cliVersion,
        double weeklyPercent,
        DateTime capturedAt) => new()
    {
        CliType = cliType,
        CliVersion = cliVersion,
        FetchedAt = capturedAt,
        Plan = "Pro",
        Source = cliType == "claude" ? "/usage" : "/status",
        Windows =
        [
            new QuotaWindow { Label = "5-hour", UsedPct = 22, Unit = "%" },
            new QuotaWindow { Label = "Weekly", UsedPct = weeklyPercent, Unit = "%" }
        ]
    };

    private static void AssertFreshReading(
        QuotaSnapshot snapshot,
        string cliType,
        string cliVersion,
        DateTime capturedAt)
    {
        Assert.Equal(cliType, snapshot.CliType);
        Assert.Equal(cliVersion, snapshot.CliVersion);
        Assert.Equal(capturedAt, snapshot.CapturedAt);
        Assert.False(snapshot.IsStale);
        Assert.Null(snapshot.StaleSince);
        Assert.InRange(snapshot.AgeSeconds, 0, 10);
    }

    private sealed class ScriptedProbe(string cliType, Func<int, QuotaSnapshot> script) : IQuotaProbe
    {
        private int _calls;
        public ScriptedProbe(Func<int, QuotaSnapshot> script) : this("codex", script) { }
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

    private sealed class NeverCompletingProbe(string cliType) : IQuotaProbe
    {
        public string CliType => cliType;

        public async Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new QuotaSnapshot { CliType = CliType };
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
