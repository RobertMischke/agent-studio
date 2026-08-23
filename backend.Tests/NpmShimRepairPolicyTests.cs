using AgentStudio.Cli;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2673: the pure classify/cooldown decisions behind the
/// <c>npm install -g</c> fallback in <see cref="NpmShimHealer"/> (step 6).
/// Kept OS-independent (no Windows guard, no filesystem) so it can run
/// anywhere, matching the <c>RapidCrashBreaker</c> pure-decision-library
/// pattern - the file-level repair steps 1-4 remain Windows-only and are
/// exercised on the host only (see results/AGT-2673/root-cause.md).
/// </summary>
public sealed class NpmShimRepairPolicyTests
{
    [Fact]
    public void Classify_ShimAvailable_IsHealthy()
    {
        Assert.Equal(ShimRepairCategory.Healthy, NpmShimRepairPolicy.Classify(shimAvailable: true, packagePresent: true));
        Assert.Equal(ShimRepairCategory.Healthy, NpmShimRepairPolicy.Classify(shimAvailable: true, packagePresent: false));
    }

    [Fact]
    public void Classify_ShimMissingPackagePresent_IsEligibleForFallback()
    {
        Assert.Equal(
            ShimRepairCategory.ShimMissingPackagePresent,
            NpmShimRepairPolicy.Classify(shimAvailable: false, packagePresent: true));
    }

    [Fact]
    public void Classify_ShimMissingPackageAbsent_IsTrulyUninstalled()
    {
        Assert.Equal(
            ShimRepairCategory.TrulyUninstalled,
            NpmShimRepairPolicy.Classify(shimAvailable: false, packagePresent: false));
    }

    [Fact]
    public void IsNpmInstallAllowed_NoPriorAttempt_Allows()
    {
        Assert.True(NpmShimRepairPolicy.IsNpmInstallAllowed(lastAttemptUtc: null, nowUtc: DateTime.UtcNow));
    }

    [Fact]
    public void IsNpmInstallAllowed_WithinCooldown_Denies()
    {
        var now = DateTime.UtcNow;
        var last = now - TimeSpan.FromMinutes(30);
        Assert.False(NpmShimRepairPolicy.IsNpmInstallAllowed(last, now));
    }

    [Fact]
    public void IsNpmInstallAllowed_ExactlyAtCooldown_Allows()
    {
        var now = DateTime.UtcNow;
        var last = now - NpmShimRepairPolicy.NpmInstallCooldown;
        Assert.True(NpmShimRepairPolicy.IsNpmInstallAllowed(last, now));
    }

    [Fact]
    public void IsNpmInstallAllowed_JustPastCooldown_Allows()
    {
        var now = DateTime.UtcNow;
        var last = now - NpmShimRepairPolicy.NpmInstallCooldown - TimeSpan.FromSeconds(1);
        Assert.True(NpmShimRepairPolicy.IsNpmInstallAllowed(last, now));
    }

    [Fact]
    public void IsNpmInstallAllowed_JustShortOfCooldown_Denies()
    {
        var now = DateTime.UtcNow;
        var last = now - NpmShimRepairPolicy.NpmInstallCooldown + TimeSpan.FromSeconds(1);
        Assert.False(NpmShimRepairPolicy.IsNpmInstallAllowed(last, now));
    }

    [Fact]
    public void Cooldown_IsOneHour()
    {
        // Pinned explicitly: this is the "bounded to one attempt per hour"
        // requirement from AGT-2673, not an implementation detail.
        Assert.Equal(TimeSpan.FromHours(1), NpmShimRepairPolicy.NpmInstallCooldown);
    }
}

/// <summary>
/// AGT-2673: <see cref="CliSelfHealJournal"/> writes the root-cause audit
/// trail to <c>&lt;workspace&gt;/logs/cli-self-heal.jsonl</c>, mirroring
/// <c>InfraHaltLog</c>'s contract test shape.
/// </summary>
public sealed class CliSelfHealJournalTests : IDisposable
{
    private readonly string _workspaceRoot;

    public CliSelfHealJournalTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "atp-cli-self-heal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspaceRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceRoot, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void HealthyNoOp_DoesNotWriteAJournalRow()
    {
        var outcome = new HealOutcome(true, Array.Empty<string>(), null);
        CliSelfHealJournal.RecordIfRepairAttempted(BuildConfig(), NullLogger.Instance, "claude", outcome, DateTime.UtcNow);

        Assert.False(File.Exists(Path.Combine(_workspaceRoot, "logs", "cli-self-heal.jsonl")));
    }

    [Fact]
    public void SuccessfulRepair_WritesRowWithVersionsAndActions()
    {
        var outcome = new HealOutcome(
            true,
            new[] { "renamed orphan shim .claude.cmd-x -> claude.cmd", "npm install -g completed" },
            null)
        {
            Category = ShimRepairCategory.ShimMissingPackagePresent,
            VersionBefore = "2.1.231",
            VersionAfter = "2.1.234",
            NpmInstallAttempted = true,
        };
        var t = new DateTime(2026, 8, 18, 10, 5, 0, DateTimeKind.Utc);

        CliSelfHealJournal.RecordIfRepairAttempted(BuildConfig(), NullLogger.Instance, "claude", outcome, t);

        var jsonl = Path.Combine(_workspaceRoot, "logs", "cli-self-heal.jsonl");
        Assert.True(File.Exists(jsonl));
        var row = Assert.Single(File.ReadAllLines(jsonl).Where(l => l.Length > 0));
        Assert.Contains("\"cliType\":\"claude\"", row);
        Assert.Contains("\"category\":\"ShimMissingPackagePresent\"", row);
        Assert.Contains("\"healed\":true", row);
        Assert.Contains("\"versionBefore\":\"2.1.231\"", row);
        Assert.Contains("\"versionAfter\":\"2.1.234\"", row);
        Assert.Contains("\"npmInstallAttempted\":true", row);
    }

    [Fact]
    public void FailedRepair_StillWritesRow_WithError()
    {
        var outcome = new HealOutcome(false, new[] { "npm install -g failed (smoke re-test below is verdict)" }, "smoke-test probe exited 1")
        {
            Category = ShimRepairCategory.ShimMissingPackagePresent,
            NpmInstallAttempted = true,
        };

        CliSelfHealJournal.RecordIfRepairAttempted(BuildConfig(), NullLogger.Instance, "claude", outcome, DateTime.UtcNow);

        var jsonl = Path.Combine(_workspaceRoot, "logs", "cli-self-heal.jsonl");
        Assert.True(File.Exists(jsonl));
        var row = Assert.Single(File.ReadAllLines(jsonl).Where(l => l.Length > 0));
        Assert.Contains("\"healed\":false", row);
        Assert.Contains("\"error\":\"smoke-test probe exited 1\"", row);
    }

    [Fact]
    public void RateLimitedSkip_WritesRowWithRateLimitedTrue()
    {
        var outcome = new HealOutcome(false, Array.Empty<string>(), "npm install -g skipped (rate-limited, last attempt ...)")
        {
            Category = ShimRepairCategory.ShimMissingPackagePresent,
            RateLimited = true,
        };

        CliSelfHealJournal.RecordIfRepairAttempted(BuildConfig(), NullLogger.Instance, "claude", outcome, DateTime.UtcNow);

        var jsonl = Path.Combine(_workspaceRoot, "logs", "cli-self-heal.jsonl");
        var row = Assert.Single(File.ReadAllLines(jsonl).Where(l => l.Length > 0));
        Assert.Contains("\"rateLimited\":true", row);
    }

    [Fact]
    public void TrulyUninstalled_WritesRow_WithoutAttemptingNpmInstall()
    {
        var outcome = new HealOutcome(false, new[] { "removed staging orphan .foo-bar" }, "shim 'claude.cmd' still missing after repair pass")
        {
            Category = ShimRepairCategory.TrulyUninstalled,
        };

        CliSelfHealJournal.RecordIfRepairAttempted(BuildConfig(), NullLogger.Instance, "claude", outcome, DateTime.UtcNow);

        var jsonl = Path.Combine(_workspaceRoot, "logs", "cli-self-heal.jsonl");
        var row = Assert.Single(File.ReadAllLines(jsonl).Where(l => l.Length > 0));
        Assert.Contains("\"category\":\"TrulyUninstalled\"", row);
        Assert.Contains("\"npmInstallAttempted\":false", row);
    }

    [Fact]
    public void MissingTaskRepository_DoesNotThrow()
    {
        var config = new ConfigurationBuilder().Build();
        var outcome = new HealOutcome(true, new[] { "renamed orphan shim" }, null);

        var exception = Record.Exception(() =>
            CliSelfHealJournal.RecordIfRepairAttempted(config, NullLogger.Instance, "claude", outcome, DateTime.UtcNow));

        Assert.Null(exception);
    }

    private IConfiguration BuildConfig()
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _workspaceRoot })
            .Build();
}
