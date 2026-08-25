using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class QuotaServiceGracefulDegradationTests : IDisposable
{
    private readonly string _repoDir = Path.Combine(
        Path.GetTempPath(),
        "agent-studio-quota-degrade-" + Guid.NewGuid().ToString("N"));

    public QuotaServiceGracefulDegradationTests() => Directory.CreateDirectory(_repoDir);

    public void Dispose()
    {
        try { Directory.Delete(_repoDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task GetWithBackgroundRefresh_ReturnsCachedBeforeProbeAndRunsProbeOffCallerThread()
    {
        var probe = new GatedProbe();
        var service = NewService(probe, ttlSeconds: 0);
        var callerThread = Environment.CurrentManagedThreadId;

        var report = service.GetWithBackgroundRefresh();

        var placeholder = Assert.Single(report.Snapshots);
        Assert.Equal("codex", placeholder.CliType);
        Assert.Empty(placeholder.Windows);
        var probeThread = await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(callerThread, probeThread);

        probe.Release.SetResult();
        await probe.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RefreshAsync_FailedProbePreservesLastGoodValuesAndAddsFailureMetadata()
    {
        var goodAt = new DateTime(2026, 8, 23, 20, 55, 0, DateTimeKind.Utc);
        var failedAt = new DateTime(2026, 8, 23, 21, 7, 0, DateTimeKind.Utc);
        var probe = new ScriptedProbe(
            new QuotaSnapshot
            {
                CliType = "codex",
                CliVersion = "0.144.1",
                FetchedAt = goodAt,
                Plan = "Pro",
                Source = "/status",
                Windows = [new QuotaWindow { Label = "Weekly", UsedPct = 41, Unit = "%" }]
            },
            new QuotaSnapshot
            {
                CliType = "codex",
                CliVersion = "0.149.0",
                FetchedAt = failedAt,
                ProbeFailedAt = failedAt,
                Source = "/status",
                Error = "codex quota probe exceeded its bounded timeout during PTY step 'await-status'."
            });
        var service = NewService(probe);

        await service.RefreshAsync("codex");
        var degraded = await service.RefreshAsync("codex");

        Assert.NotNull(degraded);
        Assert.Equal(goodAt, degraded.FetchedAt);
        Assert.Equal(failedAt, degraded.ProbeFailedAt);
        Assert.Equal("0.149.0", degraded.CliVersion);
        Assert.Equal("Pro", degraded.Plan);
        var window = Assert.Single(degraded.Windows);
        Assert.Equal(41, window.UsedPct);
        Assert.Contains("bounded timeout", degraded.Error);
    }

    [Fact]
    public async Task RefreshAsync_CancellationNeverReturnsTaskWasCanceledCopy()
    {
        var good = new QuotaSnapshot
        {
            CliType = "codex",
            CliVersion = "0.149.0",
            Windows = [new QuotaWindow { Label = "Weekly", UsedPct = 41 }]
        };
        var probe = new ThrowAfterFirstProbe(good);
        var service = NewService(probe);

        await service.RefreshAsync("codex");
        var degraded = await service.RefreshAsync("codex");

        Assert.NotNull(degraded);
        Assert.Single(degraded.Windows);
        Assert.DoesNotContain("A task was canceled", degraded.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bounded 45-second timeout", degraded.Error);
        Assert.NotNull(degraded.ProbeFailedAt);
    }

    private QuotaService NewService(IQuotaProbe probe, int ttlSeconds = 600)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _repoDir,
                ["Quota:TtlSeconds"] = ttlSeconds.ToString()
            })
            .Build();
        return new QuotaService(
            NullLogger<QuotaService>.Instance,
            [probe],
            config,
            new QuotaCacheStore(config, NullLogger<QuotaCacheStore>.Instance));
    }

    private sealed class GatedProbe : IQuotaProbe
    {
        public string CliType => "codex";
        public TaskCompletionSource<int> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
        {
            Started.SetResult(Environment.CurrentManagedThreadId);
            await Release.Task.WaitAsync(ct);
            Completed.SetResult();
            return new QuotaSnapshot { CliType = CliType };
        }
    }

    private sealed class ScriptedProbe(params QuotaSnapshot[] snapshots) : IQuotaProbe
    {
        private int _index;
        public string CliType => "codex";
        public Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
            => Task.FromResult(snapshots[Math.Min(Interlocked.Increment(ref _index) - 1, snapshots.Length - 1)]);
    }

    private sealed class ThrowAfterFirstProbe(QuotaSnapshot first) : IQuotaProbe
    {
        private int _calls;
        public string CliType => "codex";
        public Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
            => Interlocked.Increment(ref _calls) == 1
                ? Task.FromResult(first)
                : Task.FromException<QuotaSnapshot>(new TaskCanceledException("A task was canceled."));
    }
}
