using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class QuotaServiceGracefulDegradationTests : IDisposable
{
    private readonly string _repoDir = Path.Combine(
        Path.GetTempPath(), "atp-quota-degrade-" + Guid.NewGuid().ToString("N"));
    private readonly IConfiguration _configuration;

    public QuotaServiceGracefulDegradationTests()
    {
        Directory.CreateDirectory(_repoDir);
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _repoDir,
                ["Quota:TtlSeconds"] = "1",
                ["Quota:ProbeTimeoutSeconds"] = "5"
            })
            .Build();
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task RefreshAsync_FailedProbeRetainsLastGoodValuesAndAddsFailureMetadata()
    {
        var fetchedAt = new DateTime(2026, 8, 23, 20, 55, 0, DateTimeKind.Utc);
        var failedAt = new DateTime(2026, 8, 23, 21, 7, 0, DateTimeKind.Utc);
        var probe = new ScriptedProbe(call => call == 1
            ? GoodSnapshot(fetchedAt)
            : new QuotaSnapshot
            {
                CliType = "codex",
                CliVersion = "codex-cli 0.149.0",
                Error = "A task was canceled.",
                ProbeFailedAt = failedAt
            });
        var service = NewService(probe);

        await service.RefreshAsync("codex");
        var stale = await service.RefreshAsync("codex");

        Assert.NotNull(stale);
        Assert.Equal(fetchedAt, stale.FetchedAt);
        Assert.Equal("Pro", stale.Plan);
        Assert.Equal(37, Assert.Single(stale.Windows).UsedPct);
        Assert.Equal("codex-cli 0.149.0", stale.CliVersion);
        Assert.Equal(failedAt, stale.ProbeFailedAt);
        Assert.Equal(
            "Quota probe timed out before the CLI status panel became ready.",
            stale.Error);
    }

    [Fact]
    public async Task GetWithBackgroundRefresh_ReturnsBeforeTheProbeCompletes()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new GatedProbe(started, gate.Task);
        var service = NewService(probe);

        var report = service.GetWithBackgroundRefresh();

        Assert.Single(report.Snapshots);
        Assert.False(gate.Task.IsCompleted);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        gate.SetResult();
    }

    private QuotaService NewService(IQuotaProbe probe)
    {
        var store = new QuotaCacheStore(
            _configuration, NullLogger<QuotaCacheStore>.Instance);
        return new QuotaService(
            NullLogger<QuotaService>.Instance, [probe], _configuration, store);
    }

    private static QuotaSnapshot GoodSnapshot(DateTime fetchedAt) => new()
    {
        CliType = "codex",
        CliVersion = "codex-cli 0.144.1",
        FetchedAt = fetchedAt,
        Plan = "Pro",
        Windows =
        [
            new QuotaWindow
            {
                Label = "Weekly",
                UsedPct = 37,
                Unit = "%"
            }
        ]
    };

    private sealed class ScriptedProbe(Func<int, QuotaSnapshot> script) : IQuotaProbe
    {
        private int _calls;
        public string CliType => "codex";
        public Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
            => Task.FromResult(script(Interlocked.Increment(ref _calls)));
    }

    private sealed class GatedProbe(
        TaskCompletionSource started, Task gate) : IQuotaProbe
    {
        public string CliType => "codex";

        public async Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
        {
            started.TrySetResult();
            await gate.WaitAsync(ct);
            return GoodSnapshot(DateTime.UtcNow);
        }
    }
}
