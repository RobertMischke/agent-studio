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
        Assert.Equal("codex-cli 0.149.0", stale.FailedProbeCliVersion);
        Assert.NotNull(stale.ProbeFailedAt);
        Assert.Equal("Quota probe timed out before the CLI panel rendered.", stale.Error);

        var served = Assert.Single(service.GetCached().Snapshots);
        Assert.True(served.IsStale);
        Assert.True(served.AgeSeconds >= 119);
        Assert.Equal(fetchedAt, served.CapturedAt);
    }

    [Theory]
    [InlineData("claude", "2.1.202")]
    [InlineData("codex", "codex-cli 0.149.0")]
    public async Task RefreshAsync_SuccessPersistsAndColdStartServesFresh(
        string cliType,
        string cliVersion)
    {
        var capturedAt = DateTime.UtcNow;
        var first = NewService(new ScriptedProbe(cliType, _ => new QuotaSnapshot
        {
            CliType = cliType,
            CliVersion = cliVersion,
            FetchedAt = capturedAt,
            Plan = "Pro",
            Source = cliType == "codex" ? "/status" : "/usage",
            Windows =
            [
                new QuotaWindow { Label = "Current session (5h)", UsedPct = 27 },
                new QuotaWindow { Label = "Weekly", UsedPct = 42 }
            ]
        }));

        await first.RefreshAsync(cliType);

        var persisted = Assert.Single(
            new QuotaCacheStore(_configuration, NullLogger<QuotaCacheStore>.Instance).Read());
        Assert.Equal(cliType, persisted.CliType);
        Assert.Equal(cliVersion, persisted.CliVersion);
        Assert.Equal(capturedAt, persisted.FetchedAt);

        using var release = new ManualResetEventSlim();
        var restarted = NewService(new BlockingProbe(cliType, null, release));
        try
        {
            var report = restarted.GetWithBackgroundRefresh();
            var served = Assert.Single(report.Snapshots);

            Assert.Equal(cliType, served.CliType);
            Assert.Equal(capturedAt, served.CapturedAt);
            Assert.Equal(cliVersion, served.CliVersion);
            Assert.False(served.IsStale);
            Assert.InRange(served.AgeSeconds, 0, 1);
            Assert.Equal(27, served.Windows[0].UsedPct);
            Assert.Equal(42, served.Windows[1].UsedPct);
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

    private QuotaService NewService(IQuotaProbe probe)
    {
        var store = new QuotaCacheStore(_configuration, NullLogger<QuotaCacheStore>.Instance);
        return new QuotaService(
            NullLogger<QuotaService>.Instance,
            [probe],
            _configuration,
            store);
    }

    private sealed class ScriptedProbe : IQuotaProbe
    {
        private readonly Func<int, QuotaSnapshot> _script;
        private int _calls;

        public ScriptedProbe(Func<int, QuotaSnapshot> script)
            : this("codex", script)
        {
        }

        public ScriptedProbe(string cliType, Func<int, QuotaSnapshot> script)
        {
            CliType = cliType;
            _script = script;
        }

        public string CliType { get; }
        public Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
            => Task.FromResult(_script(Interlocked.Increment(ref _calls)));
    }

    private sealed class BlockingProbe : IQuotaProbe
    {
        private readonly ManualResetEventSlim? _entered;
        private readonly ManualResetEventSlim _release;

        public BlockingProbe(
            string cliType,
            ManualResetEventSlim? entered,
            ManualResetEventSlim release)
        {
            CliType = cliType;
            _entered = entered;
            _release = release;
        }

        public string CliType { get; }

        public Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
        {
            _entered?.Set();
            _release.Wait(ct);
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
