

using Xunit;

namespace AgentStudio.Tests;

public class ClaudeQuotaProbeTests
{
    private static string ReadFixture(string name)
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "quota",
            "claude",
            name));

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

    /// <summary>
    /// Live capture from claude 2.1.201 on 2026-07-07: with hasCompletedOnboarding
    /// missing from ~/.claude.json the CLI opens the first-run THEME picker instead
    /// of the ready REPL. The old probe matched the welcome banner and fired /usage
    /// into this wizard, so windows came back empty for a reason that is NOT a
    /// /usage format drift.
    /// </summary>
    private const string ThemeWizardSnapshotV2_1_201 =
        "WelcometoClaudeCodev2.1.201\r\n" +
        "Let'sgetstarted.ChoosethetextstylethatlooksbestwithyourterminalTochangethislater,run/theme" +
        "1.Auto(matchterminal)❯2.Darkmode✔3.Lightmode4.Darkmode(colorblind-friendly)\r\n";

    /// <summary>
    /// Live capture from claude 2.1.201 on 2026-07-07: after onboarding was seeded the
    /// CLI still interposed a feature upsell ("Try the new fullscreen renderer?") in
    /// front of the ready prompt, again swallowing /usage. plan parsed ("Max") but
    /// windows stayed empty.
    /// </summary>
    private const string FullscreenUpsellSnapshotV2_1_201 =
        "Trythenewfullscreenrenderer?·Flicker-freeoutput—fixestheflashingyouseeduringlongresponses" +
        "·Mousesupport—clicktomoveyourcursor❯1.Yes,tryit2.NotnowEntertoconfirm·Esctocancel\r\n";

    [Fact]
    public void LooksLikeOnboardingWizard_FlagsThemePicker()
    {
        Assert.True(ClaudeQuotaProbe.LooksLikeOnboardingWizard(ThemeWizardSnapshotV2_1_201),
            "the 2.1.201 first-run theme picker should be detected as an onboarding wizard");
    }

    [Fact]
    public void LooksLikeOnboardingWizard_FlagsFullscreenUpsell()
    {
        Assert.True(ClaudeQuotaProbe.LooksLikeOnboardingWizard(FullscreenUpsellSnapshotV2_1_201),
            "the 'Try the new fullscreen renderer?' upsell should be detected as an onboarding wizard");
    }

    [Fact]
    public void LooksLikeOnboardingWizard_DoesNotFlag_ParserDriftOrUsageOrEmpty()
    {
        // The genuine v2.1.140 parser-drift snapshot is a real ready-REPL screen, NOT a
        // wizard — it must NOT be misclassified, otherwise the two self-diagnosis paths blur.
        Assert.False(ClaudeQuotaProbe.LooksLikeOnboardingWizard(BrokenSnapshotV2_1_140));
        Assert.False(ClaudeQuotaProbe.LooksLikeOnboardingWizard("Currentsession██100%usedResets3:40am(Europe/Berlin)"));
        Assert.False(ClaudeQuotaProbe.LooksLikeOnboardingWizard(""));
        Assert.False(ClaudeQuotaProbe.LooksLikeOnboardingWizard("   \r\n  "));
    }

    [Fact]
    public void OnboardingWizard_And_ParserDrift_AreDistinct()
    {
        // A wizard snapshot carries the welcome banner too, so LooksLikeParserDrift alone
        // would also fire — which is exactly why ProbeAsync checks the wizard FIRST. Pin the
        // precedence contract: the wizard predicate is true here, so the drift branch is dead.
        Assert.True(ClaudeQuotaProbe.LooksLikeOnboardingWizard(ThemeWizardSnapshotV2_1_201));
        // ...while the real drift snapshot is the mirror image: drift true, wizard false.
        var driftWindows = ClaudeQuotaProbe.ParseUsageWindows(BrokenSnapshotV2_1_140);
        Assert.True(ClaudeQuotaProbe.LooksLikeParserDrift(BrokenSnapshotV2_1_140, driftWindows));
        Assert.False(ClaudeQuotaProbe.LooksLikeOnboardingWizard(BrokenSnapshotV2_1_140));
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

    [Fact]
    public void ParseUsageWindows_RecognizesRealV2_1_202TabbedApiBillingPanelAsUnknown()
    {
        var snapshot = ReadFixture("claude-usage-v2.1.202-api-billing.txt");

        var windows = ClaudeQuotaProbe.ParseUsageWindows(snapshot);

        var quota = Assert.Single(windows);
        Assert.Equal("Quota", quota.Label);
        Assert.Null(quota.UsedPct);
        Assert.Equal("%", quota.Unit);
        Assert.False(ClaudeQuotaProbe.LooksLikeParserDrift(snapshot, windows));
    }

    [Fact]
    public void ParseUsageWindows_V2_1_202IgnoresUnknownPanelLines()
    {
        var snapshot = ReadFixture("claude-usage-v2.1.202-api-billing.txt")
            .Replace("Session", "Session\nExperimental provider field: ignored", StringComparison.Ordinal)
            .Replace("Usage:", "Unrecognized line with arbitrary values\nUsage:", StringComparison.Ordinal);

        var quota = Assert.Single(ClaudeQuotaProbe.ParseUsageWindows(snapshot));

        Assert.Equal("Quota", quota.Label);
        Assert.Null(quota.UsedPct);
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
        Assert.Equal(expected, BuiltInCliBehaviors.NormalizeModelId(input));
    }
}
