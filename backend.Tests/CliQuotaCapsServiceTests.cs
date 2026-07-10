using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the cap-evaluation contract for <see cref="CliQuotaCapsService"/>.
/// The runner gates auto-pickup (and manual start) on <see cref="CliQuotaCapsService.Evaluate"/>;
/// the user's intent is "leave a buffer in the 5-hour Claude session and the
/// weekly budget", so the failure mode that matters most is "Evaluate returns
/// Blocked when any window's UsedPct meets or passes the configured cap".
/// </summary>
public sealed class CliQuotaCapsServiceTests : IDisposable
{
    private readonly string _repoDir;
    private readonly IConfiguration _config;

    public CliQuotaCapsServiceTests()
    {
        _repoDir = Path.Combine(Path.GetTempPath(), "atp-quota-caps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repoDir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _repoDir })
            .Build();
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoDir, recursive: true); } catch { }
    }

    [Fact]
    public void GetCap_ReturnsDefault_WhenNothingConfigured()
    {
        var svc = NewService();
        Assert.Equal(CliQuotaCapsService.DefaultCapPct, svc.GetCap("claude", "Current 5-hour session"));
    }

    [Fact]
    public void SetCap_PersistsAndIsReadByFreshInstance()
    {
        var svc = NewService();
        svc.SetCap("claude", "Weekly", 90);

        var reloaded = NewService();
        Assert.Equal(90, reloaded.GetCap("claude", "Weekly"));
        Assert.Equal(CliQuotaCapsService.DefaultCapPct, reloaded.GetCap("claude", "Current 5-hour session"));
    }

    [Fact]
    public void SetCap_ClampsToValidRange()
    {
        var svc = NewService();
        svc.SetCap("claude", "Weekly", -5);
        Assert.Equal(1, svc.GetCap("claude", "Weekly"));

        svc.SetCap("claude", "Weekly", 250);
        Assert.Equal(100, svc.GetCap("claude", "Weekly"));
    }

    [Fact]
    public void Evaluate_NotBlocked_WhenAllWindowsUnderCap()
    {
        var svc = NewService();
        svc.SetCap("claude", "Current 5-hour session", 96);
        svc.SetCap("claude", "Weekly", 95);

        var snap = new QuotaSnapshot
        {
            CliType = "claude",
            Windows = new()
            {
                new QuotaWindow { Label = "Current 5-hour session", UsedPct = 80 },
                new QuotaWindow { Label = "Weekly", UsedPct = 70 }
            }
        };

        Assert.False(svc.Evaluate(snap).Blocked);
    }

    [Fact]
    public void Evaluate_Blocks_WhenAnyWindowAtOrAboveCap()
    {
        var svc = NewService();
        svc.SetCap("claude", "Current 5-hour session", 96);
        svc.SetCap("claude", "Weekly", 95);

        var snap = new QuotaSnapshot
        {
            CliType = "claude",
            Windows = new()
            {
                new QuotaWindow { Label = "Current 5-hour session", UsedPct = 50 },
                new QuotaWindow { Label = "Weekly", UsedPct = 95.0 }
            }
        };

        var ev = svc.Evaluate(snap);
        Assert.True(ev.Blocked);
        Assert.Equal("Weekly", ev.WindowLabel);
        Assert.Equal(95, ev.CapPct);
    }

    [Fact]
    public void Evaluate_PicksWorstOvershoot_WhenMultipleWindowsBlock()
    {
        var svc = NewService();
        svc.SetCap("claude", "Current 5-hour session", 95);
        svc.SetCap("claude", "Weekly", 95);

        var snap = new QuotaSnapshot
        {
            CliType = "claude",
            Windows = new()
            {
                new QuotaWindow { Label = "Current 5-hour session", UsedPct = 96 }, // +1
                new QuotaWindow { Label = "Weekly", UsedPct = 102 }                 // +7
            }
        };

        var ev = svc.Evaluate(snap);
        Assert.True(ev.Blocked);
        Assert.Equal("Weekly", ev.WindowLabel);
    }

    [Fact]
    public void Evaluate_NotBlocked_WhenSnapshotIsNullOrEmpty()
    {
        var svc = NewService();
        Assert.False(svc.Evaluate(null).Blocked);
        Assert.False(svc.Evaluate(new QuotaSnapshot { CliType = "claude" }).Blocked);
        Assert.False(svc.Evaluate(new QuotaSnapshot
        {
            CliType = "claude",
            Windows = new() { new QuotaWindow { Label = "Weekly", UsedPct = null } }
        }).Blocked);
    }

    [Fact]
    public void Evaluate_UsesDefaultCap_ForUnseenWindowLabel()
    {
        var svc = NewService();
        // No explicit cap set; default is 95.
        var snap = new QuotaSnapshot
        {
            CliType = "claude",
            Windows = new() { new QuotaWindow { Label = "Surprise window", UsedPct = 99 } }
        };
        var ev = svc.Evaluate(snap);
        Assert.True(ev.Blocked);
        Assert.Equal(CliQuotaCapsService.DefaultCapPct, ev.CapPct);
    }

    // AGT-2064: a snapshot flagged suspicious must block admission even when its
    // numbers look green, so a transient downward glitch (or a snapshot a live
    // usage-limit error contradicted) can never open the launch gate before a
    // re-probe confirms.
    [Fact]
    public void Evaluate_Blocks_WhenSnapshotIsSuspicious_EvenWithGreenWindows()
    {
        var svc = NewService();
        var snap = new QuotaSnapshot
        {
            CliType = "codex",
            Suspicious = true,
            SuspiciousReason = "5-hour dropped 96 points with no reset to explain it",
            Windows = new()
            {
                new QuotaWindow { Label = "5-hour", UsedPct = 4 },
                new QuotaWindow { Label = "Weekly", UsedPct = 1 }
            }
        };

        var ev = svc.Evaluate(snap);

        Assert.True(ev.Blocked);
        Assert.True(ev.Suspicious);
        Assert.Contains("unconfirmed", ev.DescribeReason());
    }

    [Fact]
    public void Evaluate_Blocks_WhenSnapshotIsSuspicious_AndHasNoWindows()
    {
        var svc = NewService();
        var snap = new QuotaSnapshot
        {
            CliType = "codex",
            Suspicious = true,
            SuspiciousReason = "launch died with a usage-limit error"
        };

        var ev = svc.Evaluate(snap);

        Assert.True(ev.Blocked);
        Assert.True(ev.Suspicious);
    }

    private CliQuotaCapsService NewService() =>
        new(NullLogger<CliQuotaCapsService>.Instance, _config);
}
