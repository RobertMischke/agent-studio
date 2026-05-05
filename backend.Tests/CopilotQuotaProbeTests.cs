using OrchestratorApi.Services.Quota;
using Xunit;

namespace OrchestratorApi.Tests;

public class CopilotQuotaProbeTests
{
    private static readonly DateTime AnyReset = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

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
