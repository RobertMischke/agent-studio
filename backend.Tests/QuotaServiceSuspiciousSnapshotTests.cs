using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2064: the QuotaService side of the plausibility gate and the
/// ground-truth launch-fail hook. A suspicious downward jump must be confirmed
/// by a second probe before it replaces the trusted value, and a live
/// usage-limit error must invalidate the cached snapshot immediately (flagging
/// it suspicious so admission stays conservative) and re-probe without waiting
/// for the TTL.
/// </summary>
public sealed class QuotaServiceSuspiciousSnapshotTests : IDisposable
{
    private readonly string _repoDir;
    private readonly IConfiguration _config;

    public QuotaServiceSuspiciousSnapshotTests()
    {
        _repoDir = Path.Combine(Path.GetTempPath(), "atp-quota-svc-" + Guid.NewGuid().ToString("N"));
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

    private static QuotaSnapshot Snap(double usedPct) => new()
    {
        CliType = "codex",
        Windows = new()
        {
            new QuotaWindow { Label = "5-hour", UsedPct = usedPct, ResetAt = new DateTime(2026, 7, 10, 17, 0, 0, DateTimeKind.Utc) }
        }
    };

    [Fact]
    public async Task RefreshAsync_SuspiciousDropConfirmedBySecondProbe_AcceptsNewValue()
    {
        // high -> then two consistent low readings: the drop is real, accept it.
        var probe = new ScriptedProbe(call => call == 1 ? Snap(100) : Snap(call == 2 ? 4 : 6));
        var svc = NewService(probe);

        await svc.RefreshAsync("codex");          // seeds the trusted high value
        var final = await svc.RefreshAsync("codex"); // sees the drop, confirms it

        Assert.Equal(3, probe.Calls);             // seed + drop + confirmation
        Assert.False(final!.Suspicious);
        Assert.Equal(6, final.Windows[0].UsedPct);
    }

    [Fact]
    public async Task RefreshAsync_SuspiciousDropContradictedByConfirmation_HoldsPriorAndFlagsSuspicious()
    {
        // high -> glitch low -> confirmation snaps back to high: keep the prior
        // (still-blocking) value and flag it suspicious so admission holds.
        var probe = new ScriptedProbe(call => call == 2 ? Snap(4) : Snap(100));
        var svc = NewService(probe);

        await svc.RefreshAsync("codex");
        var final = await svc.RefreshAsync("codex");

        Assert.Equal(3, probe.Calls);
        Assert.True(final!.Suspicious);
        Assert.Equal(100, final.Windows[0].UsedPct); // held the prior high value, not the glitch
        Assert.NotNull(final.SuspiciousReason);
    }

    [Fact]
    public async Task InvalidateForGroundTruthLimit_FlagsSuspiciousImmediately_AndReprobesToGroundTruth()
    {
        // Seed a green snapshot, then a launch dies with a usage-limit error.
        var gate = new TaskCompletionSource();
        var probe = new GatedProbe(firstCall: Snap(4), gate: gate.Task, afterGate: Snap(100));
        var svc = NewService(probe);

        await svc.RefreshAsync("codex");          // cache reads a green 4% used
        Assert.False(svc.GetCachedFor("codex")!.Suspicious);

        var reprobe = svc.InvalidateForGroundTruthLimit("codex", "launch died with a usage-limit error");

        // The block is in force the instant we invalidate - before the re-probe
        // (still parked on the gate) can return anything.
        var whileReprobing = svc.GetCachedFor("codex")!;
        Assert.True(whileReprobing.Suspicious);
        Assert.Equal("launch died with a usage-limit error", whileReprobing.SuspiciousReason);

        gate.SetResult();
        await reprobe;

        // The fresh probe (ground truth) replaces the green reading with the real
        // exhausted value and clears the transient suspicious flag.
        var final = svc.GetCachedFor("codex")!;
        Assert.False(final.Suspicious);
        Assert.Equal(100, final.Windows[0].UsedPct);
        Assert.Equal(2, probe.Calls);             // did not wait for the TTL
    }

    private sealed class ScriptedProbe : IQuotaProbe
    {
        private readonly Func<int, QuotaSnapshot> _script;
        private int _calls;
        public ScriptedProbe(Func<int, QuotaSnapshot> script) => _script = script;
        public string CliType => "codex";
        public int Calls => _calls;
        public Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
            => Task.FromResult(_script(Interlocked.Increment(ref _calls)));
    }

    private sealed class GatedProbe : IQuotaProbe
    {
        private readonly QuotaSnapshot _first;
        private readonly Task _gate;
        private readonly QuotaSnapshot _afterGate;
        private int _calls;
        public GatedProbe(QuotaSnapshot firstCall, Task gate, QuotaSnapshot afterGate)
        {
            _first = firstCall;
            _gate = gate;
            _afterGate = afterGate;
        }
        public string CliType => "codex";
        public int Calls => _calls;
        public async Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1) return _first;
            await _gate;
            return _afterGate;
        }
    }
}
