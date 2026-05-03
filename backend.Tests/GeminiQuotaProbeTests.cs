using OrchestratorApi.Services.Quota;
using Xunit;

namespace OrchestratorApi.Tests;

public class GeminiQuotaProbeTests
{
    private const string PaidPanel = """
        ───────────────────────────────────────────────────
         > /stats model
        ╭───────────────────────────────────────────────────╮
        │  Auto (Gemini 3) Stats For Nerds                  │
        │                                                   │
        │  Auth Method:    Signed in with Google (user@example.com) │
        │  Tier:           Gemini Code Assist in Google One AI Pro │
        │  47% used (Limit resets in 5h 12m)                │
        │  Usage limit: 1,000                               │
        │  Usage limits span all sessions and reset daily.  │
        ╰───────────────────────────────────────────────────╯
        """;

    private const string LimitReachedPanel = """
        ╭───────────────────────────────────────────────────╮
        │  Auto (Gemini 3) Stats For Nerds                  │
        │                                                   │
        │  Auth Method:    Signed in with Google (heavy@example.com) │
        │  Tier:           Gemini Code Assist Standard      │
        │  Limit reached, resets in 30m                     │
        │  Usage limit: 200                                 │
        │  Usage limits span all sessions and reset daily.  │
        │  Please /auth to upgrade or switch to an API key. │
        ╰───────────────────────────────────────────────────╯
        """;

    private const string FreeTierPanel = """
        ╭───────────────────────────────────────────────────╮
        │  Model Stats For Nerds                            │
        │                                                   │
        │  Auth Method:    Signed in with Google (free@example.com) │
        │  Tier:           Free                             │
        │                                                   │
        │  Metric          gemini-2.5-flash-lite            │
        │  API                                              │
        │  Requests        1                                │
        ╰───────────────────────────────────────────────────╯
        """;

    [Fact]
    public void ParseSnapshot_PaidPlan_ExtractsAllFields()
    {
        var snap = GeminiQuotaProbe.ParseSnapshot(PaidPanel);

        Assert.Equal("gemini", snap.CliType);
        Assert.Equal("/stats model", snap.Source);
        Assert.Null(snap.Error);
        Assert.Equal("Gemini Code Assist in Google One AI Pro", snap.Plan);

        var w = Assert.Single(snap.Windows);
        Assert.Equal("Daily (user@example.com)", w.Label);
        Assert.Equal(47, w.UsedPct);
        Assert.Equal(1000, w.Limit);
        Assert.Equal(470, w.Used);
        Assert.Equal("requests", w.Unit);
        Assert.Equal("5h 12m", w.ResetLabel);
        Assert.NotNull(w.ResetAt);
        // resets in 5h 12m → between 5h11m and 5h13m from now.
        var diff = w.ResetAt!.Value - DateTime.UtcNow;
        Assert.InRange(diff.TotalMinutes, 311, 313);
    }

    [Fact]
    public void ParseSnapshot_LimitReached_FlagsAt100Percent()
    {
        var snap = GeminiQuotaProbe.ParseSnapshot(LimitReachedPanel);

        Assert.Null(snap.Error);
        Assert.Equal("Gemini Code Assist Standard", snap.Plan);

        var w = Assert.Single(snap.Windows);
        Assert.Equal("Daily (heavy@example.com)", w.Label);
        Assert.Equal(100, w.UsedPct);
        Assert.Equal(200, w.Limit);
        Assert.Equal(200, w.Used);
        Assert.Equal("30m", w.ResetLabel);
        Assert.NotNull(w.ResetAt);
    }

    [Fact]
    public void ParseSnapshot_FreeTier_HasIdentityButNoQuotaWindow()
    {
        var snap = GeminiQuotaProbe.ParseSnapshot(FreeTierPanel);

        // Free tier still surfaces plan + email, but no quota numbers — so the UI
        // shows an empty card with a soft note rather than a misleading "?" donut.
        Assert.Equal("Free", snap.Plan);
        Assert.Empty(snap.Windows);
        Assert.NotNull(snap.Error);
        Assert.Contains("free tier", snap.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseSnapshot_EmptyInput_ReturnsExplanatoryError()
    {
        var snap = GeminiQuotaProbe.ParseSnapshot("");
        Assert.Empty(snap.Windows);
        Assert.NotNull(snap.Error);
    }
}
