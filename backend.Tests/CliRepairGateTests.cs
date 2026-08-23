using System.Text.Json;

using AgentStudio.Cli;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Covers the stateful coordination <see cref="CliRepairGate"/> adds on top
/// of the pure <see cref="CliRepairCooldownPolicy"/> decision: the
/// concurrency-safe check-then-act (bounding repair to one attempt per
/// window even under concurrent callers), and that every real attempt -
/// success or failure - lands one row in <c>logs/cli-repairs.jsonl</c>
/// while a cooldown-suppressed call does not invoke the heal delegate at all.
/// </summary>
[Collection(CliRepairGateCollection.Name)]
public sealed class CliRepairGateTests : IDisposable
{
    private readonly string _workspaceRoot;
    private readonly IConfiguration _config;

    public CliRepairGateTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "atp-cli-repair-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspaceRoot);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _workspaceRoot })
            .Build();
        CliRepairGate.ResetForTests();
    }

    public void Dispose()
    {
        CliRepairGate.ResetForTests();
        try { Directory.Delete(_workspaceRoot, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task ConcurrentCallers_OnlyOneRunsHeal()
    {
        var now = DateTime.UtcNow;
        var healCount = 0;

        async Task<HealOutcome> Heal(CancellationToken ct)
        {
            Interlocked.Increment(ref healCount);
            // Widen the race window: without the lock in CliRepairGate, N
            // concurrent callers would all observe "no prior attempt" before
            // any of them records one.
            await Task.Delay(50, ct);
            return new HealOutcome(true, new[] { "did the thing" }, null, true, "1.0.0", "1.0.0");
        }

        var calls = Enumerable.Range(0, 20)
            .Select(_ => CliRepairGate.TryHealWithCooldownAsync(
                "claude", Heal, _config, NullLogger.Instance, now, CancellationToken.None))
            .ToArray();

        await Task.WhenAll(calls);

        Assert.Equal(1, healCount);
        Assert.Equal(19, calls.Count(r => r.Result.Error == "repair suppressed by cooldown window (an attempt already ran within the last hour)"));
        Assert.Single(ReadJournalLines());
    }

    [Fact]
    public async Task SecondCallWithinWindow_SuppressedWithoutRunningHeal()
    {
        var now = DateTime.UtcNow;
        var healCount = 0;
        Task<HealOutcome> Heal(CancellationToken ct)
        {
            healCount++;
            return Task.FromResult(new HealOutcome(true, Array.Empty<string>(), null, true, "1.0.0", "1.0.0"));
        }

        var first = await CliRepairGate.TryHealWithCooldownAsync("claude", Heal, _config, NullLogger.Instance, now, CancellationToken.None);
        var second = await CliRepairGate.TryHealWithCooldownAsync("claude", Heal, _config, NullLogger.Instance, now.AddMinutes(1), CancellationToken.None);

        Assert.True(first.Available);
        // A suppressed call echoes the LAST REAL outcome, not a hardcoded
        // failure: the CLI really was fixed a minute ago, so callers must
        // keep seeing it as available instead of being told it's still
        // broken for the rest of the cooldown window.
        Assert.True(second.Available);
        Assert.Equal("1.0.0", second.VersionAfter);
        Assert.Contains("cooldown", second.Error);
        Assert.Equal(1, healCount);
        Assert.Single(ReadJournalLines());
    }

    [Fact]
    public async Task SuppressedCall_PreservesLastRealDiagnostic_InsteadOfGenericMessage()
    {
        var now = DateTime.UtcNow;
        Task<HealOutcome> Heal(CancellationToken ct) => Task.FromResult(
            new HealOutcome(false, new[] { "renamed orphan shim" }, "smoke-test probe exited 1", true, "2.1.231", "2.1.231"));

        var first = await CliRepairGate.TryHealWithCooldownAsync("claude", Heal, _config, NullLogger.Instance, now, CancellationToken.None);
        var second = await CliRepairGate.TryHealWithCooldownAsync("claude", Heal, _config, NullLogger.Instance, now.AddMinutes(1), CancellationToken.None);

        Assert.False(first.Available);
        Assert.False(second.Available);
        // The suppressed call's Error must still name the REAL underlying
        // reason (what NpmShimHealer actually found broken), not just "on
        // cooldown" - an operator reading this 40 minutes into an outage
        // needs to know WHY, not just that a repair was skipped.
        Assert.Contains("smoke-test probe exited 1", second.Error);
        Assert.True(second.PackagePresent);
        Assert.Equal("2.1.231", second.VersionBefore);
    }

    [Fact]
    public async Task CallAfterWindowElapses_RunsHealAgain()
    {
        var now = DateTime.UtcNow;
        var healCount = 0;
        Task<HealOutcome> Heal(CancellationToken ct)
        {
            healCount++;
            return Task.FromResult(new HealOutcome(true, Array.Empty<string>(), null, true, "1.0.0", "1.0.0"));
        }

        await CliRepairGate.TryHealWithCooldownAsync("claude", Heal, _config, NullLogger.Instance, now, CancellationToken.None);
        await CliRepairGate.TryHealWithCooldownAsync(
            "claude", Heal, _config, NullLogger.Instance, now.Add(CliRepairCooldownPolicy.DefaultWindow).AddSeconds(1), CancellationToken.None);

        Assert.Equal(2, healCount);
        Assert.Equal(2, ReadJournalLines().Count);
    }

    [Fact]
    public async Task FailedRepair_JournalsBeforeAndAfterVersionAndError()
    {
        var now = DateTime.UtcNow;
        Task<HealOutcome> Heal(CancellationToken ct) => Task.FromResult(
            new HealOutcome(false, new[] { "renamed orphan shim" }, "smoke-test probe exited 1", true, "2.1.231", "2.1.234"));

        var outcome = await CliRepairGate.TryHealWithCooldownAsync("claude", Heal, _config, NullLogger.Instance, now, CancellationToken.None);

        Assert.False(outcome.Available);
        var row = Assert.Single(ReadJournalLines());
        using var doc = JsonDocument.Parse(row);
        Assert.Equal("claude", doc.RootElement.GetProperty("cli").GetString());
        Assert.False(doc.RootElement.GetProperty("available").GetBoolean());
        Assert.Equal("2.1.231", doc.RootElement.GetProperty("versionBefore").GetString());
        Assert.Equal("2.1.234", doc.RootElement.GetProperty("versionAfter").GetString());
        Assert.Equal("smoke-test probe exited 1", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task HealDelegateThrows_ReturnsFailedOutcome_DoesNotPropagateAndStillJournals()
    {
        var now = DateTime.UtcNow;
        Task<HealOutcome> Heal(CancellationToken ct) => throw new InvalidOperationException("boom");

        // Must not throw: a pre-spawn health check that faults instead of
        // returning (false, reason) would surface as an unhandled exception
        // in the caller instead of the expected failure tuple.
        var outcome = await CliRepairGate.TryHealWithCooldownAsync("claude", Heal, _config, NullLogger.Instance, now, CancellationToken.None);

        Assert.False(outcome.Available);
        Assert.Contains("boom", outcome.Error);
        var row = Assert.Single(ReadJournalLines());
        using var doc = JsonDocument.Parse(row);
        Assert.False(doc.RootElement.GetProperty("available").GetBoolean());
    }

    private List<string> ReadJournalLines()
    {
        var path = Path.Combine(_workspaceRoot, "logs", "cli-repairs.jsonl");
        if (!File.Exists(path)) return new List<string>();
        return File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
    }
}
