using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2679, service side. Two defects put "A task was canceled." on the
/// operator's quota display:
/// <list type="number">
///   <item>a failed probe replaced the cached snapshot with a bare error record,
///   throwing away numbers that were still the best available;</item>
///   <item>the background refresh kicked off by <c>GET /api/cli/quota</c> observed
///   the HTTP request's cancellation token, so finishing the request cancelled
///   the probe it had just started.</item>
/// </list>
/// </summary>
public sealed class QuotaServiceProbeFailureTests : IDisposable
{
    private readonly string _repoDir;
    private readonly IConfiguration _config;

    public QuotaServiceProbeFailureTests()
    {
        _repoDir = Path.Combine(Path.GetTempPath(), "atp-quota-fail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repoDir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _repoDir })
            .Build();
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoDir, recursive: true); } catch { }
    }

    private QuotaService NewService(IQuotaProbe probe)
    {
        var store = new QuotaCacheStore(_config, NullLogger<QuotaCacheStore>.Instance);
        return new QuotaService(NullLogger<QuotaService>.Instance, new[] { probe }, _config, store);
    }

    private static QuotaSnapshot GoodSnap() => new()
    {
        CliType = "codex",
        Plan = "Pro",
        CliVersion = "codex-cli 0.149.0",
        Windows = [new QuotaWindow { Label = "5-hour", UsedPct = 42, Unit = "%" }]
    };

    [Fact]
    public async Task RefreshAsync_ProbeThrowsCancellation_KeepsLastGoodWindowsAndHidesTheStockMessage()
    {
        var probe = new SequencedProbe(call => call == 1
            ? GoodSnap()
            : throw new TaskCanceledException());
        var svc = NewService(probe);

        await svc.RefreshAsync("codex");                    // seed a good reading
        var degraded = await svc.RefreshAsync("codex");     // probe blows up

        Assert.NotNull(degraded);
        Assert.True(degraded!.Stale);
        Assert.Equal(42, Assert.Single(degraded.Windows).UsedPct);
        Assert.Equal("Pro", degraded.Plan);
        Assert.NotNull(degraded.LastGoodAt);
        Assert.DoesNotContain("A task was canceled", degraded.Error ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshAsync_ProbeReturnsEmptyErrorSnapshot_AlsoDegradesInsteadOfBlanking()
    {
        // The probe caught its own failure and returned an error snapshot rather
        // than throwing. The operator must not lose the numbers either way.
        var probe = new SequencedProbe(call => call == 1
            ? GoodSnap()
            : new QuotaSnapshot { CliType = "codex", Error = "Could not parse the Codex /status panel." });
        var svc = NewService(probe);

        await svc.RefreshAsync("codex");
        var degraded = await svc.RefreshAsync("codex");

        Assert.True(degraded!.Stale);
        Assert.Equal(42, Assert.Single(degraded.Windows).UsedPct);
        Assert.Equal("Could not parse the Codex /status panel.", degraded.Error);
    }

    [Fact]
    public async Task RefreshAsync_FirstEverProbeFails_ReportsTheFailureWithoutClaimingStaleData()
    {
        var svc = NewService(new SequencedProbe(_ => throw new TaskCanceledException()));

        var snap = await svc.RefreshAsync("codex");

        Assert.False(snap!.Stale);
        Assert.Empty(snap.Windows);
        Assert.NotNull(snap.Error);
    }

    /// <summary>
    /// The request-token leak, reproduced. The caller hands in a token and then
    /// cancels it - exactly what ASP.NET does to <c>RequestAborted</c> once the
    /// response is written. The background probe must survive that and still land
    /// its result in the cache.
    /// </summary>
    [Fact]
    public async Task GetWithBackgroundRefresh_CallerCancelsImmediately_ProbeStillCompletes()
    {
        var probe = new SequencedProbe(_ => GoodSnap(), delayMs: 150);
        var svc = NewService(probe);

        using var requestScope = new CancellationTokenSource();
        var report = svc.GetWithBackgroundRefresh(requestScope.Token);
        await requestScope.CancelAsync();   // the "request finished" moment

        // The GET itself served from cache without waiting on the probe.
        Assert.Single(report.Snapshots);

        // ...and the probe it started was not collateral damage.
        var cached = await WaitForWindowsAsync(svc, "codex");
        Assert.NotNull(cached);
        Assert.Equal(42, Assert.Single(cached!.Windows).UsedPct);
        Assert.Null(cached.Error);
    }

    [Fact]
    public void GetWithBackgroundRefresh_ReturnsWithoutAwaitingTheProbe()
    {
        // A slow probe must not hold the GET open; the endpoint serves cache.
        var probe = new SequencedProbe(_ => GoodSnap(), delayMs: 3000);
        var svc = NewService(probe);

        var started = DateTime.UtcNow;
        svc.GetWithBackgroundRefresh(CancellationToken.None);
        var elapsed = DateTime.UtcNow - started;

        Assert.True(elapsed < TimeSpan.FromSeconds(1),
            $"GET blocked for {elapsed.TotalMilliseconds:0} ms; it must serve cached data and probe in the background");
    }

    private static async Task<QuotaSnapshot?> WaitForWindowsAsync(QuotaService svc, string cliType)
    {
        for (var i = 0; i < 100; i++)
        {
            var snap = svc.GetCachedFor(cliType);
            if (snap is { Windows.Count: > 0 }) return snap;
            await Task.Delay(50);
        }
        return svc.GetCachedFor(cliType);
    }

    /// <summary>Probe whose behaviour is scripted per call index.</summary>
    private sealed class SequencedProbe(Func<int, QuotaSnapshot> script, int delayMs = 0) : IQuotaProbe
    {
        private int _calls;

        public string CliType => "codex";
        public int Calls => _calls;

        public async Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
        {
            var call = Interlocked.Increment(ref _calls);
            if (delayMs > 0) await Task.Delay(delayMs, ct);
            return script(call);
        }
    }
}
