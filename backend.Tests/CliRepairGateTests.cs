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
    public async Task DifferentCli_HasIndependentCooldownAndLastOutcome()
    {
        // Regression test: cooldown/last-outcome state must be keyed per
        // `cli` name. Before this fix, a single set of static fields was
        // shared across every caller regardless of the `cli` argument, so a
        // `claude` repair could suppress (or leak its outcome into) a
        // `gemini` caller sharing the same process.
        var now = DateTime.UtcNow;
        Task<HealOutcome> HealClaude(CancellationToken ct) => Task.FromResult(
            new HealOutcome(true, Array.Empty<string>(), null, true, "1.0.0", "1.0.0"));
        Task<HealOutcome> HealGemini(CancellationToken ct) => Task.FromResult(
            new HealOutcome(false, Array.Empty<string>(), "gemini still broken", true, "0.5.0", "0.5.0"));

        var claudeResult = await CliRepairGate.TryHealWithCooldownAsync("claude", HealClaude, _config, NullLogger.Instance, now, CancellationToken.None);
        var geminiResult = await CliRepairGate.TryHealWithCooldownAsync("gemini", HealGemini, _config, NullLogger.Instance, now, CancellationToken.None);

        Assert.True(claudeResult.Available);
        Assert.False(geminiResult.Available);
        Assert.Equal("gemini still broken", geminiResult.Error);

        // A second `gemini` call within the cooldown window must be
        // suppressed independently of `claude`'s cooldown, and must echo
        // gemini's own last outcome, not claude's.
        var geminiCalls = 0;
        Task<HealOutcome> CountedHealGemini(CancellationToken ct)
        {
            geminiCalls++;
            return HealGemini(ct);
        }
        var geminiSecond = await CliRepairGate.TryHealWithCooldownAsync(
            "gemini", CountedHealGemini, _config, NullLogger.Instance, now.AddMinutes(1), CancellationToken.None);

        Assert.Equal(0, geminiCalls);
        Assert.Equal("0.5.0", geminiSecond.VersionBefore);
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

    /// <summary>
    /// A repair that only "failed" because the host is shutting down is not an
    /// operator-visible defect. NpmShimHealer folds cancellation into a failed
    /// HealOutcome rather than throwing, so the gate must recognise that shape
    /// and neither journal it (the journal's FAILED rows are the executive
    /// summary's alarm surface) nor burn the host's one attempt per hour.
    /// </summary>
    [Fact]
    public async Task CancelledHeal_IsNotJournaledAsFailure_AndDoesNotBurnCooldown()
    {
        var now = DateTime.UtcNow;
        using var shutdown = new CancellationTokenSource();
        shutdown.Cancel();

        // The shape NpmShimHealer produces on a cancelled probe: a failed
        // outcome returned normally, not an OperationCanceledException.
        Task<HealOutcome> CancelledHeal(CancellationToken ct) =>
            Task.FromResult(new HealOutcome(false, Array.Empty<string>(), "smoke-test probe timed out", true, "2.1.234", "2.1.234"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CliRepairGate.TryHealWithCooldownAsync(
                "claude", CancelledHeal, _config, NullLogger.Instance, now, shutdown.Token));

        Assert.Empty(ReadJournalLines());

        // The cooldown slot was released, so a real repair one second later
        // still runs instead of being suppressed for the rest of the hour.
        var healed = false;
        Task<HealOutcome> GoodHeal(CancellationToken ct)
        {
            healed = true;
            return Task.FromResult(new HealOutcome(true, new[] { "restored shim" }, null, true, "2.1.234", "2.1.234"));
        }

        var outcome = await CliRepairGate.TryHealWithCooldownAsync(
            "claude", GoodHeal, _config, NullLogger.Instance, now.AddSeconds(1), CancellationToken.None);

        Assert.True(healed);
        Assert.True(outcome.Available);
        Assert.Single(ReadJournalLines());
    }

    private List<string> ReadJournalLines()
    {
        var path = Path.Combine(_workspaceRoot, "logs", "cli-repairs.jsonl");
        if (!File.Exists(path)) return new List<string>();
        return File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
    }
}
