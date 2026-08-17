using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Direct matrix over the pure halves of the Windows tunnel supervision
/// status (AGT-2664): mapping a raw <c>schtasks /Query</c> transcript to a
/// task's presence, and reading the watchdog journal tail for the last heal.
/// </summary>
public sealed class WindowsTunnelSupervisionPolicyTests
{
    // -- ParseScheduledTaskStatus -------------------------------------------

    [Fact]
    public void ParseScheduledTaskStatus_NonZeroExitCode_IsNotRegistered()
    {
        var status = WindowsTunnelSupervisionPolicy.ParseScheduledTaskStatus(
            "AgentRunner-TunnelKeeper", exitCode: 1, stdout: "ERROR: The system cannot find the file specified.");

        Assert.Equal(ScheduledTaskPresence.NotRegistered, status.Presence);
        Assert.Null(status.LastRunAt);
        Assert.Null(status.LastRunResult);
    }

    [Theory]
    [InlineData("Status:                               Ready", ScheduledTaskPresence.Registered)]
    [InlineData("Status:                               Running", ScheduledTaskPresence.Running)]
    [InlineData("Status:                               Disabled", ScheduledTaskPresence.Disabled)]
    [InlineData("Status:                               Queued", ScheduledTaskPresence.Unknown)]
    public void ParseScheduledTaskStatus_ReadsStatusLine(string statusLine, ScheduledTaskPresence expected)
    {
        var stdout = string.Join('\n', [
            "Folder: \\",
            "HostName:                             STUDIO-PC",
            "TaskName:                             \\AgentRunner-TunnelKeeper",
            statusLine,
            "Last Run Time:                        8/18/2026 3:00:01 AM",
            "Last Result:                          0",
        ]);

        var status = WindowsTunnelSupervisionPolicy.ParseScheduledTaskStatus(
            "AgentRunner-TunnelKeeper", exitCode: 0, stdout);

        Assert.Equal(expected, status.Presence);
        Assert.Equal("8/18/2026 3:00:01 AM", status.LastRunAt);
        Assert.Equal("0", status.LastRunResult);
    }

    [Fact]
    public void ParseScheduledTaskStatus_MissingFields_AreNull()
    {
        var status = WindowsTunnelSupervisionPolicy.ParseScheduledTaskStatus(
            "AgentRunner-TunnelWatchdog", exitCode: 0, stdout: "Status:                               Ready");

        Assert.Equal(ScheduledTaskPresence.Registered, status.Presence);
        Assert.Null(status.LastRunAt);
        Assert.Null(status.LastRunResult);
    }

    // -- ParseHealHistory -----------------------------------------------------

    [Fact]
    public void ParseHealHistory_EmptyOrNull_ReturnsNoHistory()
    {
        Assert.Equal((null, null, 0), WindowsTunnelSupervisionPolicy.ParseHealHistory(null));
        Assert.Equal((null, null, 0), WindowsTunnelSupervisionPolicy.ParseHealHistory(""));
        Assert.Equal((null, null, 0), WindowsTunnelSupervisionPolicy.ParseHealHistory("   \n  "));
    }

    [Fact]
    public void ParseHealHistory_FindsLastSuccessfulHeal()
    {
        var log = string.Join('\n', [
            "2026-08-18T02:00:00Z event=probe_failed consecutive=1 threshold=2",
            "2026-08-18T02:00:05Z event=heal_started consecutive_probe_failures=2",
            "2026-08-18T02:00:15Z event=heal_succeeded health_url=http://127.0.0.1:15031/healthz",
            "2026-08-18T03:00:00Z event=probe_failed consecutive=1 threshold=2",
        ]);

        var (at, detail, failures) = WindowsTunnelSupervisionPolicy.ParseHealHistory(log);

        Assert.Equal("2026-08-18T02:00:15Z", at);
        Assert.NotNull(detail);
        Assert.Equal(0, failures);
    }

    [Fact]
    public void ParseHealHistory_CountsFailedHealsSinceLastSuccess()
    {
        var log = string.Join('\n', [
            "2026-08-18T02:00:15Z event=heal_succeeded health_url=http://127.0.0.1:15031/healthz",
            "2026-08-18T04:00:00Z event=heal_failure_count consecutive=1 alarm_threshold=2",
            "2026-08-18T05:00:00Z event=heal_failure_count consecutive=2 alarm_threshold=2",
        ]);

        var (at, _, failures) = WindowsTunnelSupervisionPolicy.ParseHealHistory(log);

        Assert.Equal("2026-08-18T02:00:15Z", at);
        Assert.Equal(2, failures);
    }

    [Fact]
    public void ParseHealHistory_NoSuccessfulHealYet_ReturnsNullTimestampWithFailureCount()
    {
        var log = "2026-08-18T05:00:00Z event=heal_failure_count consecutive=1 alarm_threshold=2";

        var (at, detail, failures) = WindowsTunnelSupervisionPolicy.ParseHealHistory(log);

        Assert.Null(at);
        Assert.Null(detail);
        Assert.Equal(1, failures);
    }
}
