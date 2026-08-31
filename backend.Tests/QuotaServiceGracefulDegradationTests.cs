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
    public async Task RefreshAsync_Success_PersistsLastGoodAndServesFreshResponse()
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
                new QuotaWindow { Label = "5-hour", UsedPct = 27 },
                new QuotaWindow { Label = "Weekly", UsedPct = 61 }
            ]
        }));

        await service.RefreshAsync("codex");
        var response = QuotaReportResponse.From(service.GetCached(), DateTime.UtcNow);
        var snapshot = Assert.Single(response.Snapshots);

        Assert.False(snapshot.Stale);
        Assert.False(snapshot.ProbeFailed);
        Assert.Equal(fetchedAt, snapshot.CapturedAt);
        Assert.InRange(snapshot.AgeSeconds!.Value, 0, 1);
        Assert.Equal("codex-cli 0.149.0", snapshot.CliVersion);

        var path = Path.Combine(_repoDir, ".runtime", "cli-quota-last-good.json");
        Assert.True(File.Exists(path));
        var persisted = await File.ReadAllTextAsync(path);
        Assert.Contains("\"codex\"", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"weekly\"", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("codex-cli 0.149.0", persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshAsync_ClaudeSuccess_PersistsItsOwnVersionedReading()
    {
        var service = NewService(new ScriptedProbe(_ => new QuotaSnapshot
        {
            CliType = "claude",
            CliVersion = "2.1.202",
            Plan = "Max",
            Source = "/usage",
            Windows =
            [
                new QuotaWindow { Label = "Current session (5h)", UsedPct = 18 },
                new QuotaWindow { Label = "Weekly (all models)", UsedPct = 42 }
            ]
        }, "claude"));

        await service.RefreshAsync("claude");

        var persisted = new QuotaCacheStore(
            _configuration,
            NullLogger<QuotaCacheStore>.Instance).Read();
        var snapshot = Assert.Single(persisted);
        Assert.Equal("claude", snapshot.CliType);
        Assert.Equal("2.1.202", snapshot.CliVersion);
        Assert.Equal(2, snapshot.Windows.Count);
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

        var response = QuotaReportResponse.From(service.GetCached(), DateTime.UtcNow);
        var served = Assert.Single(response.Snapshots);
        Assert.True(served.Stale);
        Assert.True(served.ProbeFailed);
        Assert.Equal(fetchedAt, served.CapturedAt);
        Assert.Equal(61, Assert.Single(served.Windows).UsedPct);
        Assert.DoesNotContain("task was canceled", served.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task ColdStart_WithPersistedLastGood_ServesImmediatelyWhileProbeRunsInBackground()
    {
        var capturedAt = DateTime.UtcNow.AddMinutes(-20);
        var writer = NewService(new ScriptedProbe(_ => new QuotaSnapshot
        {
            CliType = "codex",
            CliVersion = "codex-cli 0.149.0",
            FetchedAt = capturedAt,
            Plan = "Pro",
            Windows = [new QuotaWindow { Label = "Weekly", UsedPct = 61 }]
        }));
        await writer.RefreshAsync("codex");

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var restarted = NewService(new BlockingProbe(entered, release));
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var report = restarted.GetWithBackgroundRefresh();
            stopwatch.Stop();
            var response = QuotaReportResponse.From(report, DateTime.UtcNow);
            var snapshot = Assert.Single(response.Snapshots);

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
            Assert.True(snapshot.Stale);
            Assert.Equal(capturedAt, snapshot.CapturedAt);
            Assert.Equal(61, Assert.Single(snapshot.Windows).UsedPct);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(1)), "Background probe never started.");
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

    private sealed class ScriptedProbe(
        Func<int, QuotaSnapshot> script,
        string cliType = "codex") : IQuotaProbe
    {
        private int _calls;
        public string CliType => cliType;
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
