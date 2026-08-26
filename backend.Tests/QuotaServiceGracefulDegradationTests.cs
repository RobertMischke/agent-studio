using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class QuotaServiceGracefulDegradationTests : IDisposable
{
    private readonly string _repoDir = Path.Combine(
        Path.GetTempPath(),
        "quota-graceful-degradation-" + Guid.NewGuid().ToString("N"));
    private readonly IConfiguration _configuration;

    public QuotaServiceGracefulDegradationTests()
    {
        Directory.CreateDirectory(_repoDir);
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _repoDir,
                ["Quota:ProbeTimeoutSeconds"] = "2"
            })
            .Build();
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task FailedProbe_KeepsLastGoodValuesAndTimestamp()
    {
        var goodAt = new DateTime(2026, 8, 23, 18, 55, 0, DateTimeKind.Utc);
        var good = new QuotaSnapshot
        {
            CliType = "codex",
            FetchedAt = goodAt,
            Plan = "Pro",
            Source = "/status",
            Windows = [new QuotaWindow { Label = "Weekly", UsedPct = 73, Unit = "%" }]
        };
        var failed = new QuotaSnapshot
        {
            CliType = "codex",
            CliVersion = "codex-cli 0.149.0",
            Error = "Codex /status probe timed out before the quota panel was ready."
        };
        var service = NewService(new ScriptedProbe(good, failed));

        await service.RefreshAsync("codex");
        var result = await service.RefreshAsync("codex");

        Assert.NotNull(result);
        Assert.Equal(goodAt, result.FetchedAt);
        Assert.Equal("Pro", result.Plan);
        Assert.Equal(73, Assert.Single(result.Windows).UsedPct);
        Assert.Equal(failed.Error, result.Error);
        Assert.Equal("codex-cli 0.149.0", result.CliVersion);
        Assert.NotNull(result.ProbeFailedAt);
    }

    [Fact]
    public async Task CanceledProbe_DoesNotExposeFrameworkCancellationMessage()
    {
        var service = NewService(new ThrowingProbe(new TaskCanceledException("A task was canceled.")));

        var result = await service.RefreshAsync("codex");

        Assert.NotNull(result);
        Assert.DoesNotContain("A task was canceled", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("timed out", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CachedGet_DoesNotRunLiveProbeOnRequestPath()
    {
        using var probe = new SynchronouslyBlockingProbe();
        var service = NewService(probe);
        var request = Task.Run(service.GetWithBackgroundRefresh);

        try
        {
            var completed = await Task.WhenAny(request, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(request, completed);
            Assert.True(probe.Entered.Wait(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            probe.Release.Set();
        }

        var report = await request;
        Assert.Contains(report.Snapshots, snapshot => snapshot.CliType == "codex");
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

    private sealed class ScriptedProbe(params QuotaSnapshot[] snapshots) : IQuotaProbe
    {
        private int _index;
        public string CliType => "codex";

        public Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
        {
            var index = Math.Min(Interlocked.Increment(ref _index) - 1, snapshots.Length - 1);
            return Task.FromResult(snapshots[index]);
        }
    }

    private sealed class ThrowingProbe(Exception exception) : IQuotaProbe
    {
        public string CliType => "codex";
        public Task<QuotaSnapshot> ProbeAsync(CancellationToken ct) => Task.FromException<QuotaSnapshot>(exception);
    }

    private sealed class SynchronouslyBlockingProbe : IQuotaProbe, IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public string CliType => "codex";

        public Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
        {
            Entered.Set();
            Release.Wait(ct);
            return Task.FromResult(new QuotaSnapshot { CliType = CliType });
        }

        public void Dispose()
        {
            Entered.Dispose();
            Release.Dispose();
        }
    }
}
