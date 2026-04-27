using OrchestratorApi.Services.Quota;
using Xunit;

namespace OrchestratorApi.Tests;

public class ClaudeQuotaProbeTests
{
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
}
