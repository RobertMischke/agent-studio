using OrchestratorApi.Models;
using OrchestratorApi.Services.Cli;
using OrchestratorApi.Services.Quota;
using Xunit;

namespace OrchestratorApi.Tests;

public class ClaudeQuotaProbeTests
{
    /// <summary>
    /// Live capture from claude --version 2.1.140 on 2026-05-13. The probe
    /// sent "1&lt;Enter&gt;" to a trust prompt that never appeared (the scratch
    /// folder was already trusted via ~/.claude.json), the "1" was treated as
    /// chat input, "/usage" never executed, and the snapshot has no Current
    /// session / Current week lines. ParseUsageWindows must return empty AND
    /// LooksLikeParserDrift must flag this as drift so it shows up in logs.
    /// Source: backend.Tests/Fixtures/claude-usage-v2.1.140-broken-snapshot.txt
    /// </summary>
    private const string BrokenSnapshotV2_1_140 =
        "▐▛███▜▌ClaudeCodev2.1.140\r\n" +
        "▝▜█████▛▘Opus4.7withxhigheffort·ClaudePro▘▘▝▝~\\AppData\\Local\\Temp\\agent-taskboard-quota\\claude\r\n" +
        "❯ Try\"refactor<filepath>\"\r\n" +
        "? for shortcuts ◉ x high · /effort❯ 1\r\n" +
        "\r\n✻ Cultivating…\r\n" +
        "esc to interrupt\r\n" +
        "● It looks like you sent just \"1\"—did you mean to send a question or task? Let me know what you'd like help with.✶ Cultivating… (2s · ↓ 9 tokens)\r\n";

    [Fact]
    public void ParseUsageWindows_ReturnsEmpty_OnBrokenV2_1_140_Snapshot()
    {
        // The snapshot contains the banner but no /usage panel — the parser is
        // correct to return empty. The point of the test is to pin the regression:
        // this exact rawSample shipped on 2026-05-13 with windows: [] and the
        // detail modal showed no 5h / 7d windows. The fix lives in the probe
        // step, not the parser.
        var windows = ClaudeQuotaProbe.ParseUsageWindows(BrokenSnapshotV2_1_140);

        Assert.Empty(windows);
    }

    [Fact]
    public void LooksLikeParserDrift_FlagsBannerWithoutWindows()
    {
        // Drift signature: banner is visible (we got a session) but Windows is
        // empty. This is what the runtime warn-log triggers on.
        var windows = ClaudeQuotaProbe.ParseUsageWindows(BrokenSnapshotV2_1_140);

        Assert.True(ClaudeQuotaProbe.LooksLikeParserDrift(BrokenSnapshotV2_1_140, windows),
            "v2.1.140 broken snapshot should be flagged as parser drift");
    }

    [Fact]
    public void LooksLikeParserDrift_DoesNotFlag_WhenWindowsParsed()
    {
        var windows = new List<QuotaWindow> { new() { Label = "Current session (5h)", UsedPct = 12 } };
        Assert.False(ClaudeQuotaProbe.LooksLikeParserDrift(BrokenSnapshotV2_1_140, windows));
    }

    [Fact]
    public void LooksLikeParserDrift_DoesNotFlag_OnEmptySnapshot()
    {
        // Empty snapshot means the CLI never even spawned; that is "probe broke"
        // (caught by the Error field), not "format drifted".
        Assert.False(ClaudeQuotaProbe.LooksLikeParserDrift("", Array.Empty<QuotaWindow>()));
        Assert.False(ClaudeQuotaProbe.LooksLikeParserDrift("   \r\n  ", Array.Empty<QuotaWindow>()));
    }

    [Fact]
    public void ParseUsageWindows_ReadsCompactCurrentWeekWithoutDate()
    {
        const string snapshot =
            "Currentsession▌1%usedResets11:40pm(Europe/Berlin)" +
            "Currentweek(allmodels)████████████████32%usedResets2pm(Europe/Berlin)";

        var windows = ClaudeQuotaProbe.ParseUsageWindows(snapshot);

        Assert.Equal(2, windows.Count);
        Assert.Equal("Current session (5h)", windows[0].Label);
        Assert.Equal(1, windows[0].UsedPct);
        Assert.Equal("11:40pm (Europe/Berlin)", windows[0].ResetLabel);
        Assert.Equal("Weekly (all models)", windows[1].Label);
        Assert.Equal(32, windows[1].UsedPct);
        Assert.Equal("2pm (Europe/Berlin)", windows[1].ResetLabel);
        Assert.NotNull(windows[1].ResetAt);
    }

    [Fact]
    public void ParseUsageWindows_ReadsWeeklyResetWithDate()
    {
        const string snapshot =
            "Current week (all models) ████████████ 28% used Resets Apr 28, 2pm (Europe/Berlin)";

        var windows = ClaudeQuotaProbe.ParseUsageWindows(snapshot);

        Assert.Single(windows);
        Assert.Equal("Weekly (all models)", windows[0].Label);
        Assert.Equal(28, windows[0].UsedPct);
        Assert.Equal("Apr 28, 2pm (Europe/Berlin)", windows[0].ResetLabel);
    }

    [Theory]
    [InlineData("claude-opus-4.7",        "claude-opus-4-7")]
    [InlineData("claude-sonnet-4.6",      "claude-sonnet-4-6")]
    [InlineData("claude-haiku-4.5",       "claude-haiku-4-5")]
    [InlineData("claude-opus-4-7",        "claude-opus-4-7")]   // already correct
    [InlineData("claude-3.5-sonnet-20240620", "claude-3-5-sonnet-20240620")]
    [InlineData("",                       "")]
    [InlineData(null,                     null)]
    [InlineData("gpt-5.5",                "gpt-5.5")]            // non-Claude unchanged
    public void NormalizeModelId_FixesDottedClaudeIdsLeavesOthers(string? input, string? expected)
    {
        Assert.Equal(expected, ClaudeCliService.NormalizeModelId(input));
    }
}
