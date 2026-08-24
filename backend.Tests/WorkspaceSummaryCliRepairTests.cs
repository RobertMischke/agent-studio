using AgentStudio.Cli;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks in the visibility half of the local CLI self-heal design: a FAILED
/// npm-shim repair (logs/cli-repairs.jsonl, written by
/// <see cref="CliRepairGate"/>) surfaces in the executive summary's crash
/// list - the alarm surface. A SUCCESSFUL repair is intentionally absent
/// from crashes ("alarm only if repair fails"); it is still visible via the
/// structured log line and the journal row itself, just not counted toward
/// the workspace's crash-record headline.
/// </summary>
[Collection(CliRepairGateCollection.Name)]
public sealed class WorkspaceSummaryCliRepairTests : IDisposable
{
    private readonly string _root;

    public WorkspaceSummaryCliRepairTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agent-taskboard-cli-repair-summary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        CliRepairGate.ResetForTests();
    }

    public void Dispose()
    {
        CliRepairGate.ResetForTests();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private WorkspaceSummaryService Build()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _root })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return new WorkspaceSummaryService(scanner, new SupervisorAdvisoryStore(), config, NullLogger<WorkspaceSummaryService>.Instance);
    }

    private async Task RunRepair(HealOutcome outcome, DateTime atUtc)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _root })
            .Build();
        await CliRepairGate.TryHealWithCooldownAsync(
            "claude", _ => Task.FromResult(outcome), config, NullLogger.Instance, atUtc, CancellationToken.None);
    }

    [Fact]
    public async Task FailedRepair_AppearsInCrashList()
    {
        var now = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        await RunRepair(new HealOutcome(false, new[] { "renamed orphan shim" }, "smoke-test probe exited 1", true, "2.1.231", "2.1.234"), now);

        var summary = Build().Build(24, now.AddMinutes(5));

        var crash = Assert.Single(summary.Crashes);
        Assert.Equal("cli-repair-failed", crash.Kind);
        Assert.Contains("2.1.231", crash.Summary);
        Assert.Contains("2.1.234", crash.Summary);
    }

    [Fact]
    public async Task SuccessfulRepair_DoesNotAppearInCrashList()
    {
        var now = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        await RunRepair(new HealOutcome(true, new[] { "renamed orphan shim" }, null, true, "2.1.231", "2.1.231"), now);

        var summary = Build().Build(24, now.AddMinutes(5));

        Assert.Empty(summary.Crashes);
    }

    [Fact]
    public async Task PackageNotPresent_UsesNotInstalledKind_NotRepairFailedKind()
    {
        // HealOutcome.PackagePresent's own contract: "callers should not
        // treat a false-with-no-actions outcome the same as a failed repair
        // attempt". A host that never had claude-code installed at all
        // must not read as "we tried to fix it and couldn't".
        var now = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        await RunRepair(new HealOutcome(false, Array.Empty<string>(), "npm global bin not found at 'X'", false, null, null), now);

        var summary = Build().Build(24, now.AddMinutes(5));

        var crash = Assert.Single(summary.Crashes);
        Assert.Equal("cli-not-installed", crash.Kind);
        Assert.DoesNotContain("repair failed", crash.Summary);
    }

    [Fact]
    public async Task FailedRepair_OutsideWindow_Excluded()
    {
        var now = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        await RunRepair(new HealOutcome(false, Array.Empty<string>(), "still broken", true, "2.1.231", "2.1.231"), now.AddHours(-48));

        var summary = Build().Build(24, now);

        Assert.Empty(summary.Crashes);
    }
}
