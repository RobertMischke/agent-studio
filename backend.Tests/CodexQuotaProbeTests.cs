using OrchestratorApi.Services.Quota;
using Xunit;

namespace OrchestratorApi.Tests;

public class CodexQuotaProbeTests
{
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
}
