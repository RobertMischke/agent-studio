using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class QuotaServiceGracefulDegradationTests : IDisposable
{
    private readonly string _repoDir;
    private readonly IConfiguration _configuration;

    public QuotaServiceGracefulDegradationTests()
    {
        _repoDir = Path.Combine(Path.GetTempPath(), "atp-quota-degrade-" + Guid.NewGuid().ToString("N"));
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
    public async Task FailedProbe_PreservesLastGoodValuesAndRecordsFriendlyFailureMetadata()
    {
        var goodAt = new DateTime(2026, 8, 23, 20, 55, 0, DateTimeKind.Utc);
        var failedAt = new DateTime(2026, 8, 23, 21, 7, 0, DateTimeKind.Utc);
        var probe = new ScriptedProbe(call => call == 1
            ? new QuotaSnapshot
            {
                CliType = "codex",
                CliVersion = "codex-cli 0.148.0",
                FetchedAt = goodAt,
                LastProbeAt = goodAt,
                Plan = "Pro",
                Windows = [new QuotaWindow { Label = "Weekly", UsedPct = 56, Unit = "%" }]
            }
            : new QuotaSnapshot
            {
                CliType = "codex",
                CliVersion = "codex-cli 0.149.0",
                FetchedAt = failedAt,
                LastProbeAt = failedAt,
                ProbeFailedAt = failedAt,
                Error = "A task was canceled."
            });
        var service = NewService(probe);

        await service.RefreshAsync("codex");
        var result = await service.RefreshAsync("codex");

        Assert.NotNull(result);
        Assert.Equal(goodAt, result.FetchedAt);
        Assert.Equal(failedAt, result.ProbeFailedAt);
        Assert.Equal("codex-cli 0.149.0", result.CliVersion);
        Assert.Equal("Pro", result.Plan);
        Assert.Contains(result.Windows, w => w.Label == "Weekly" && w.UsedPct == 56);
        Assert.Equal("codex quota probe timed out before the CLI panel became ready.", result.Error);
        Assert.DoesNotContain("canceled", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CachedGet_ReturnsStaleValuesBeforeBackgroundProbeCompletes()
    {
        var store = NewStore();
        store.Write([
            new QuotaSnapshot
            {
                CliType = "codex",
                CliVersion = "codex-cli 0.148.0",
                FetchedAt = DateTime.UtcNow.AddHours(-1),
                Plan = "Pro",
                Windows = [new QuotaWindow { Label = "5-hour", UsedPct = 41, Unit = "%" }]
            }
        ]);
        var probe = new GatedProbe();
        var service = new QuotaService(
            NullLogger<QuotaService>.Instance,
            [probe],
            _configuration,
            store);

        var report = service.GetWithBackgroundRefresh();

        var cached = Assert.Single(report.Snapshots);
        Assert.Equal(41, Assert.Single(cached.Windows).UsedPct);
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(probe.Completion.Task.IsCompleted);

        probe.Completion.SetResult(new QuotaSnapshot
        {
            CliType = "codex",
            CliVersion = "codex-cli 0.149.0",
            LastProbeAt = DateTime.UtcNow,
            ProbeFailedAt = DateTime.UtcNow,
            Error = "Could not parse Codex /status output."
        });

        await WaitUntilAsync(() => service.GetCachedFor("codex")?.Error != null);
        Assert.Equal(41, Assert.Single(service.GetCachedFor("codex")!.Windows).UsedPct);
    }

    [Fact]
    public void Hydration_RewritesLegacyCancellationErrorBeforeItCanReachTheUi()
    {
        var failedAt = new DateTime(2026, 8, 23, 21, 7, 0, DateTimeKind.Utc);
        var store = NewStore();
        store.Write([
            new QuotaSnapshot
            {
                CliType = "codex",
                CliVersion = "codex-cli 0.149.0",
                FetchedAt = failedAt,
                Error = "A task was canceled."
            }
        ]);

        var service = new QuotaService(
            NullLogger<QuotaService>.Instance,
            [new ScriptedProbe(_ => new QuotaSnapshot { CliType = "codex" })],
            _configuration,
            store);

        var cached = service.GetCachedFor("codex");

        Assert.NotNull(cached);
        Assert.Equal(failedAt, cached.ProbeFailedAt);
        Assert.Equal("codex quota probe timed out before the CLI panel became ready.", cached.Error);
        Assert.DoesNotContain("canceled", cached.Error, StringComparison.OrdinalIgnoreCase);
    }

    private QuotaService NewService(IQuotaProbe probe)
        => new(
            NullLogger<QuotaService>.Instance,
            [probe],
            _configuration,
            NewStore());

    private QuotaCacheStore NewStore()
        => new(_configuration, NullLogger<QuotaCacheStore>.Instance);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!predicate() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(predicate());
    }

    private sealed class ScriptedProbe(Func<int, QuotaSnapshot> script) : IQuotaProbe
    {
        private int _calls;
        public string CliType => "codex";
        public Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
            => Task.FromResult(script(Interlocked.Increment(ref _calls)));
    }

    private sealed class GatedProbe : IQuotaProbe
    {
        public string CliType => "codex";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<QuotaSnapshot> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
        {
            Started.TrySetResult();
            return await Completion.Task.WaitAsync(ct);
        }
    }
}
