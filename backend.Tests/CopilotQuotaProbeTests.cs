using OrchestratorApi.Services.Cli;
using OrchestratorApi.Services.Quota;
using Xunit;

namespace OrchestratorApi.Tests;

public class CopilotQuotaProbeTests
{
    private static readonly DateTime AnyReset = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ProbeNow = new(2026, 6, 3, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Verbatim ANSI-stripped home-screen snapshot captured from the installed
    /// Copilot CLI 1.0.59 (see this task's results/copilot-home-stripped.txt).
    /// The quota figure is glued to the working-dir + "+0 -0" change counter
    /// exactly as the real footer renders it — the parser must still find it.
    /// </summary>
    private const string RealHomeScreen =
        "\n\n\n" +
        "  ╭─╮╭─╮\n" +
        "  ╰─╯╰─╯  Copilot v1.0.59 uses AI.\n" +
        "  █ ▘▝ █  Check for mistakes.\n" +
        "   ▔▔▔▔● No copilot-instructions.md found. Run /init to generate.● Tip: ctrl+x → o\n" +
        "  └ open most recent link~\\AppData\\Local\\Temp\\agent-taskboard-quota\\copilot +0 -0Remaining reqs.: 71.1%\n" +
        "────────────────────────────────────────────────────────────────────────────\n" +
        "❯\n";

    // ---- ParseSnapshot (real-world footer + unavailable fallback) ----
    //
    // Root cause of the empty-windows bug was NOT this regex: the live probe
    // spawned the `copilot` npm shim, whose cmd.exe → node wrapper chain
    // swallows the TUI render under ConPTY, so the snapshot arrived empty and
    // the inline parse returned a hard error. These tests pin the extracted,
    // PTY-free parser against the real captured footer (populated) and against
    // a banner-only render (clean unavailable, no error pill).

    [Fact]
    public void ParseSnapshot_RealCapturedFooter_ReturnsPopulatedPremiumWindow()
    {
        var snapshot = CopilotQuotaProbe.ParseSnapshot(RealHomeScreen, "Pro", ProbeNow);

        Assert.Null(snapshot.Error);
        var window = Assert.Single(snapshot.Windows);
        Assert.Equal("Premium requests (monthly)", window.Label);
        // Remaining 71.1% -> used 28.9%.
        Assert.Equal(28.9, window.UsedPct);
        Assert.Equal("requests", window.Unit);
        // Pro = 300/mo -> round(300 * 28.9 / 100) = 87.
        Assert.Equal(300, window.Limit);
        Assert.Equal(87, window.Used);
        // Premium-request counter rolls over on the 1st of next month.
        Assert.Equal(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), window.ResetAt);
        Assert.Equal("Pro", snapshot.Plan);
    }

    [Theory]
    [InlineData("Pro+", 1500)]
    [InlineData("Business", 300)]
    [InlineData("Enterprise", 1000)]
    public void ParseSnapshot_PlanDrivesLimit(string plan, double expectedLimit)
    {
        var snapshot = CopilotQuotaProbe.ParseSnapshot(RealHomeScreen, plan, ProbeNow);

        var window = Assert.Single(snapshot.Windows);
        Assert.Equal(expectedLimit, window.Limit);
    }

    [Theory]
    [InlineData("")]                                   // empty snapshot (shim chain swallowed the render)
    [InlineData("  Copilot v1.0.59 uses AI.\n❯\n")]    // banner only, footer not yet painted
    public void ParseSnapshot_NoRemainingReqsLine_ReturnsCleanUnavailable(string snap)
    {
        var snapshot = CopilotQuotaProbe.ParseSnapshot(snap, "Pro", ProbeNow);

        // Clean "unavailable": no windows AND no error string, so the status bar
        // renders "—" rather than a red error pill.
        Assert.Empty(snapshot.Windows);
        Assert.Null(snapshot.Error);
        Assert.Equal("Pro", snapshot.Plan);
    }

    // ---- ResolveCopilotNativeExe (shim → native exe) ----

    [Fact]
    public void ResolveCopilotNativeExe_WhenNativeExeMissing_ReturnsInputUnchanged()
    {
        // No node_modules/@github/copilot/.../copilot.exe sibling exists under a
        // bogus dir, so the resolver must fall back to the input path rather than
        // invent one (the probe then degrades to "unavailable" instead of throwing).
        var bogusShim = Path.Combine(Path.GetTempPath(), "atp-copilot-resolve-test", "copilot.cmd");
        var resolved = CopilotQuotaProbe.ResolveCopilotNativeExe(bogusShim);

        Assert.Equal(bogusShim, resolved);
    }

    [SkippableFact]
    public void ResolveCopilotNativeExe_AgainstRealInstall_ResolvesNativeExe()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Native-exe resolution is Windows-only.");
        var shim = CliExecutionServiceBase.ResolveExecutable("copilot");
        Skip.If(string.Equals(shim, "copilot", StringComparison.OrdinalIgnoreCase),
            "Copilot CLI not found on PATH.");

        var resolved = CopilotQuotaProbe.ResolveCopilotNativeExe(shim);
        Skip.If(string.Equals(resolved, shim, StringComparison.OrdinalIgnoreCase),
            "Bundled native copilot.exe not present next to the shim (portable/non-standard install).");

        Assert.EndsWith("copilot.exe", resolved, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(resolved), $"Resolved native exe should exist: {resolved}");
    }

    // ---- TryParseSonnetMaximum ----

    [Theory]
    [InlineData("Remaining reqs.: 75.0%\nSonnet Maximum Usage: 45%",              45.0)]
    [InlineData("Remaining reqs.: 75.0%\nSonnet Maximum: 45.0%",                  45.0)]
    [InlineData("Remaining reqs.: 75.0%\nSonnet Max: 45%",                        45.0)]
    [InlineData("Remaining reqs.: 75.0%\nClaude Sonnet Maximum Usage: 30.5%",     30.5)]
    [InlineData("Remaining reqs.: 75.0%\nClaude Sonnet Max: 10%",                 10.0)]
    [InlineData("Remaining reqs.: 75.0%\nMaximum Sonnet: 60%",                    60.0)]
    [InlineData("Remaining reqs.: 75.0%\nMaximum Claude Sonnet: 60%",             60.0)]
    public void TryParseSonnetMaximum_UsedPct_ReturnsExpected(string snap, double expectedUsedPct)
    {
        var window = CopilotQuotaProbe.TryParseSonnetMaximum(snap, AnyReset);

        Assert.NotNull(window);
        Assert.Equal("Sonnet Maximum (monthly)", window.Label);
        Assert.Equal(expectedUsedPct, window.UsedPct);
        Assert.Equal("%", window.Unit);
        Assert.Equal(AnyReset, window.ResetAt);
    }

    [Theory]
    [InlineData("Remaining reqs.: 75.0%\nSonnet Maximum: remaining 55.0%",        45.0)]
    [InlineData("Remaining reqs.: 75.0%\nSonnet Max: remaining 90%",              10.0)]
    [InlineData("Remaining reqs.: 75.0%\nClaude Sonnet Max: remaining 100%",       0.0)]
    public void TryParseSonnetMaximum_RemainingPct_ComputesUsedCorrectly(string snap, double expectedUsedPct)
    {
        var window = CopilotQuotaProbe.TryParseSonnetMaximum(snap, AnyReset);

        Assert.NotNull(window);
        Assert.Equal(expectedUsedPct, window.UsedPct);
    }

    [Theory]
    [InlineData("Remaining reqs.: 75.0%")]
    [InlineData("Remaining reqs.: 75.0%\nSome other line: 45%")]
    [InlineData("No quota data here")]
    [InlineData("")]
    public void TryParseSonnetMaximum_AbsentLine_ReturnsNull(string snap)
    {
        var window = CopilotQuotaProbe.TryParseSonnetMaximum(snap, AnyReset);

        Assert.Null(window);
    }
}
