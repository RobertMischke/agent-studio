using AgentStudio.Cli;
using AgentStudio.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The algorithmic pre-launch admission check (AGT-2055). Mirrors the task's
/// acceptance scenarios: quota-full -> switch to fallback + event; both empty ->
/// quiet wait with a reason; reset -> normal start on primary; plus the
/// projection cases (umschichten / drosseln before the wall).
/// </summary>
public sealed class QuotaAdmissionPlannerTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "atp-admission-" + Guid.NewGuid().ToString("N"));
    private readonly IConfiguration _config;
    private readonly CliQuotaCapsService _caps;
    private readonly Dictionary<string, QuotaSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);

    public QuotaAdmissionPlannerTests()
    {
        Directory.CreateDirectory(_root);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _root })
            .Build();
        _caps = new CliQuotaCapsService(NullLogger<CliQuotaCapsService>.Instance, _config);
    }

    // ── acceptance scenario 1: quota full -> fallback pick + documented switch ──
    [Fact]
    public void QuotaFull_SwitchesToFallback_WithDocumentedReason()
    {
        var fallback = Routing(new CliModelRouteProfile
        {
            CliType = "claude", PrimaryModel = "claude-opus",
            FallbackCliType = "codex", FallbackModel = "gpt-5.3-codex",
        });
        Snapshot("claude", ("Weekly", 100, Now.AddDays(3)));
        Snapshot("codex", ("Weekly", 10, Now.AddDays(3)));

        var plan = Plan("claude", fallback, occupiedSlots: 0);

        Assert.Equal(QuotaAdmissionOutcome.LaunchFallback, plan.Outcome);
        Assert.True(plan.IsFallback);
        Assert.Equal("codex", plan.CliType);
        Assert.Equal("gpt-5.3-codex", plan.Model);
        Assert.Contains("switched pre-launch", plan.Reason);
        Assert.Contains("Weekly", plan.Reason);
    }

    // ── acceptance scenario 2: both exhausted -> quiet wait + reason + reset ──
    [Fact]
    public void BothExhausted_Waits_WithReasonAndNextReset()
    {
        var fallback = Routing(new CliModelRouteProfile
        {
            CliType = "claude", FallbackCliType = "codex", FallbackModel = "gpt-5.3-codex",
        });
        Snapshot("claude", ("Weekly", 100, Now.AddHours(5)));
        Snapshot("codex", ("Weekly", 100, Now.AddHours(4)));

        var plan = Plan("claude", fallback, occupiedSlots: 0);

        Assert.Equal(QuotaAdmissionOutcome.Wait, plan.Outcome);
        Assert.False(plan.ShouldLaunch);
        Assert.StartsWith("waiting: all quotas exhausted", plan.Reason);
        Assert.Contains("next reset", plan.Reason);
        Assert.NotNull(plan.NextResetAt);
    }

    // ── acceptance scenario 3: after reset -> normal start on primary ──
    [Fact]
    public void AfterReset_LaunchesPrimary()
    {
        var fallback = Routing(new CliModelRouteProfile
        {
            CliType = "claude", PrimaryModel = "claude-opus",
            FallbackCliType = "codex", FallbackModel = "gpt-5.3-codex",
        });
        Snapshot("claude", ("Weekly", 12, Now.AddDays(3)));
        Snapshot("codex", ("Weekly", 10, Now.AddDays(3)));

        var plan = Plan("claude", fallback, occupiedSlots: 0);

        Assert.Equal(QuotaAdmissionOutcome.LaunchPrimary, plan.Outcome);
        Assert.False(plan.IsFallback);
        Assert.Equal("claude", plan.CliType);
        Assert.Equal("claude-opus", plan.Model);
    }

    // ── req 6: projected breach + usable fallback -> pre-emptive switch ──
    [Fact]
    public void ProjectedBreach_WithFallback_SwitchesBeforeTheWall()
    {
        var fallback = Routing(new CliModelRouteProfile
        {
            CliType = "claude", PrimaryModel = "claude-opus",
            FallbackCliType = "codex", FallbackModel = "gpt-5.3-codex",
        });
        // 60% used at the halfway point of a 5-hour window -> projects to 120%,
        // not yet over the 95% cap.
        Snapshot("claude", ("5-hour", 60, Now.AddHours(2.5)));
        Snapshot("codex", ("5-hour", 10, Now.AddHours(2.5)));

        var plan = Plan("claude", fallback, occupiedSlots: 0);

        Assert.Equal(QuotaAdmissionOutcome.LaunchFallback, plan.Outcome);
        Assert.True(plan.IsFallback);
        Assert.Equal("codex", plan.CliType);
        Assert.Contains("projected", plan.Reason);
        Assert.NotNull(plan.Projection);
        Assert.True(plan.Projection!.BreachesBeforeReset);
    }

    // ── req 6: projected breach, no fallback, a slot already busy -> throttle ──
    [Fact]
    public void ProjectedBreach_NoFallback_SlotBusy_Throttles()
    {
        var fallback = Routing(new CliModelRouteProfile { CliType = "claude", PrimaryModel = "claude-opus" });
        Snapshot("claude", ("5-hour", 60, Now.AddHours(2.5)));

        var plan = Plan("claude", fallback, occupiedSlots: 1);

        Assert.Equal(QuotaAdmissionOutcome.Throttle, plan.Outcome);
        Assert.StartsWith("throttling", plan.Reason);
    }

    // ── never throttle to zero: the first/only run always proceeds ──
    [Fact]
    public void ProjectedBreach_NoFallback_NoSlotBusy_LaunchesPrimaryFlagged()
    {
        var fallback = Routing(new CliModelRouteProfile { CliType = "claude", PrimaryModel = "claude-opus" });
        Snapshot("claude", ("5-hour", 60, Now.AddHours(2.5)));

        var plan = Plan("claude", fallback, occupiedSlots: 0);

        Assert.Equal(QuotaAdmissionOutcome.LaunchPrimary, plan.Outcome);
        Assert.False(plan.IsFallback);
        Assert.Contains("projection-flagged", plan.Reason);
    }

    // ── no routing service configured: still a correct primary/wait decision ──
    [Fact]
    public void NoRoutingService_ExhaustedPrimary_Waits()
    {
        Snapshot("claude", ("Weekly", 100, Now.AddDays(2)));
        var plan = Plan("claude", fallback: null, occupiedSlots: 0);
        Assert.Equal(QuotaAdmissionOutcome.Wait, plan.Outcome);
    }

    [Fact]
    public void NoRoutingService_HealthyPrimary_Launches()
    {
        Snapshot("claude", ("Weekly", 10, Now.AddDays(2)));
        var plan = Plan("claude", fallback: null, occupiedSlots: 0);
        Assert.Equal(QuotaAdmissionOutcome.LaunchPrimary, plan.Outcome);
    }

    // ── no cached snapshot at all -> never stall the queue, launch primary ──
    [Fact]
    public void NoSnapshot_LaunchesPrimary()
    {
        var fallback = Routing(new CliModelRouteProfile { CliType = "claude", PrimaryModel = "claude-opus" });
        var plan = Plan("claude", fallback, occupiedSlots: 0);
        Assert.Equal(QuotaAdmissionOutcome.LaunchPrimary, plan.Outcome);
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private QuotaAdmissionPlan Plan(string cli, CliQuotaFallbackService? fallback, int occupiedSlots) =>
        QuotaAdmissionPlanner.Plan(
            cli, requestedModel: null, requestedThinking: null,
            fallback, _caps,
            c => c != null && _snapshots.TryGetValue(c, out var s) ? s : null,
            Now, occupiedSlots);

    private CliQuotaFallbackService Routing(CliModelRouteProfile profile)
    {
        var svc = new CliQuotaFallbackService(_config, NullLogger<CliQuotaFallbackService>.Instance);
        svc.Set(profile);
        return svc;
    }

    private void Snapshot(string cli, params (string Label, double UsedPct, DateTime ResetAt)[] windows)
    {
        var snap = new QuotaSnapshot { CliType = cli };
        foreach (var w in windows)
            snap.Windows.Add(new QuotaWindow { Label = w.Label, UsedPct = w.UsedPct, ResetAt = w.ResetAt });
        _snapshots[cli] = snap;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}
