
using Xunit;

namespace AgentStudio.Tests;

public class CodexQuotaProbeTests
{
    private static string ReadFixture(string name)
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "quota",
            "codex",
            name));

    [Theory]
    [InlineData("codex-status-v0.144.1.txt", "v0.144.1")]
    [InlineData("codex-status-v0.149.0.txt", "v0.149.0")]
    public void ParseStatusWindows_ReadsVersionedRealPtyFixtures(string fixture, string versionMarker)
    {
        var snapshot = ReadFixture(fixture);

        var windows = CodexQuotaProbe.ParseStatusWindows(snapshot);

        Assert.Contains(versionMarker, snapshot, StringComparison.Ordinal);
        Assert.Equal("Pro", CodexQuotaProbe.ParseStatusPlan(snapshot));
        Assert.Equal(3, windows.Count);
        Assert.DoesNotContain(windows, window => window.Label == "5-hour");
        Assert.Contains(windows, window => window.Label == "Weekly" && window.UsedPct == 29);
        Assert.Contains(windows, window => window.Label == "Spark 5-hour" && window.UsedPct == 0);
        Assert.Contains(windows, window => window.Label == "Spark Weekly" && window.UsedPct == 0);
    }

    [Fact]
    public void BuildProbeSteps_Codex0149UpdatePromptIsDeclinedWithoutBlindInput()
    {
        var steps = CodexQuotaProbe.BuildProbeSteps();
        var updatePrompt = ReadFixture("codex-startup-v0.149.0.txt");

        var update = Assert.Single(steps, step => step.Name == "dismiss-update");
        Assert.Matches(update.WaitForPattern!, updatePrompt);
        Assert.Equal("<Down><Enter>", update.SendKeys);
        Assert.True(update.SendKeysOnlyIfMatched);

        var trust = Assert.Single(steps, step => step.Name == "await-trust");
        Assert.True(trust.SendKeysOnlyIfMatched);

        var ready = Assert.Single(steps, step => step.Name == "await-ready");
        Assert.True(ready.RequirePattern);
        Assert.True(ready.SendKeysOnlyIfMatched);
        Assert.Equal("/status", ready.SendKeys);
    }

    [Fact]
    public void ParseStatusWindows_ReadsStandardAndSparkLimitBlocks()
    {
        const string snapshot =
            "Account:        robertmischke@gmail.com (Plus)\n" +
            "5h limit:       [###################.] 97% left (resets 20:09)\n" +
            "Weekly limit:   [#################...] 86% left (resets 23:43 on 11 Jun)\n" +
            "GPT-5.3-Codex-Spark limit:\n" +
            "5h limit:       [####################] 100% left (resets 21:25)\n" +
            "Weekly limit:   [####################] 100% left (resets 16:25 on 14 Jun)\n";

        var windows = CodexQuotaProbe.ParseStatusWindows(snapshot);

        Assert.Equal(4, windows.Count);

        Assert.Equal("5-hour", windows[0].Label);
        Assert.Equal(3, windows[0].UsedPct);
        Assert.Equal("20:09", windows[0].ResetLabel);

        Assert.Equal("Weekly", windows[1].Label);
        Assert.Equal(14, windows[1].UsedPct);
        Assert.Equal("23:43 on 11 Jun", windows[1].ResetLabel);

        Assert.Equal("Spark 5-hour", windows[2].Label);
        Assert.Equal(0, windows[2].UsedPct);
        Assert.Equal("21:25", windows[2].ResetLabel);

        Assert.Equal("Spark Weekly", windows[3].Label);
        Assert.Equal(0, windows[3].UsedPct);
        Assert.Equal("16:25 on 14 Jun", windows[3].ResetLabel);
    }

    [Fact]
    public void ParseStatusWindows_StillReadsStandardLimitsWithoutSparkBlock()
    {
        const string snapshot =
            "Account:        robertmischke@gmail.com (Plus)\n" +
            "5h limit:       [############........] 60% left (resets 20:09)\n" +
            "Weekly limit:   [###############.....] 75% left (resets 23:43 on 11 Jun)\n";

        var windows = CodexQuotaProbe.ParseStatusWindows(snapshot);

        Assert.Equal(2, windows.Count);
        Assert.Equal("5-hour", windows[0].Label);
        Assert.Equal(40, windows[0].UsedPct);
        Assert.Equal("Weekly", windows[1].Label);
        Assert.Equal(25, windows[1].UsedPct);
    }

    // AGT-2064: the Spark sub-block header must be recognised regardless of the
    // Spark model version. The old regex pinned it to "GPT-5.3-Codex-Spark";
    // once the CLI advertised "GPT-5.6-Codex-Spark" the split collapsed. This
    // proves the standard/spark split still holds after a model bump.
    [Fact]
    public void ParseStatusWindows_BumpedSparkModel_StillSplitsStandardFromSpark()
    {
        const string snapshot =
            "Account:        robertmischke@gmail.com (Plus)\n" +
            "5h limit:       [############........] 60% left (resets 20:09)\n" +
            "Weekly limit:   [###############.....] 75% left (resets 23:43 on 11 Jun)\n" +
            "GPT-5.6-Codex-Spark limit:\n" +
            "5h limit:       [####################] 100% left (resets 21:25)\n" +
            "Weekly limit:   [####################] 100% left (resets 16:25 on 14 Jun)\n";

        var windows = CodexQuotaProbe.ParseStatusWindows(snapshot);

        Assert.Equal(4, windows.Count);
        Assert.Equal("5-hour", windows[0].Label);
        Assert.Equal(40, windows[0].UsedPct);
        Assert.Equal("Weekly", windows[1].Label);
        Assert.Equal(25, windows[1].UsedPct);
        Assert.Equal("Spark 5-hour", windows[2].Label);
        Assert.Equal(0, windows[2].UsedPct);
        Assert.Equal("Spark Weekly", windows[3].Label);
        Assert.Equal(0, windows[3].UsedPct);
    }

    // AGT-2064 root cause, reproduced. Near a reset the standard 5h/Weekly lines
    // can be momentarily absent while the near-empty Spark block still renders,
    // under a bumped ("GPT-5.6") Spark header. With the old version-pinned
    // header regex the standard-window parser latched onto the Spark line's high
    // "% left" and reported an exhausted account as MAIN "5-hour: 4% |
    // Weekly: 1%" - the exact false snapshot the operator acted on. The fix must
    // NEVER attribute Spark values to the main windows.
    [Fact]
    public void ParseStatusWindows_SparkOnlyWithBumpedHeader_DoesNotMasqueradeAsMainWindows()
    {
        const string snapshot =
            "Account:        robertmischke@gmail.com (Plus)\n" +
            "GPT-5.6-Codex-Spark limit:\n" +
            "5h limit:       [###################.] 96% left (resets 21:25)\n" +
            "Weekly limit:   [####################] 99% left (resets 16:25 on 14 Jun)\n";

        var windows = CodexQuotaProbe.ParseStatusWindows(snapshot);

        // The dangerous mislabel: the near-empty Spark values MUST NOT appear as
        // the main 5-hour / Weekly windows the admission gate reads.
        Assert.DoesNotContain(windows, w => w.Label == "5-hour");
        Assert.DoesNotContain(windows, w => w.Label == "Weekly");

        // They are correctly attributed to the Spark sub-windows instead.
        Assert.Contains(windows, w => w.Label == "Spark 5-hour" && w.UsedPct == 4);
        Assert.Contains(windows, w => w.Label == "Spark Weekly" && w.UsedPct == 1);
    }
}
