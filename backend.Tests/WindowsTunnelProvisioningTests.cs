using AgentStudio.Management;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AgentStudio.Tests;

public sealed class WindowsTunnelProvisioningTests
{
    private static WindowsTunnelRegisterRequest ValidRequest() => new(
        "agent-runner", 15031, 5031, 5, 60, 2);

    [Fact]
    public void Policy_AcceptsTheDocumentedDefaults()
    {
        Assert.Null(WindowsTunnelProvisioningPolicy.Validate(ValidRequest()));
    }

    [Theory]
    [InlineData("runner;touch /tmp/x", 15031, 5031, 5, 60, 2)]
    [InlineData("agent-runner", 0, 5031, 5, 60, 2)]
    [InlineData("agent-runner", 70000, 5031, 5, 60, 2)]
    [InlineData("agent-runner", 15031, 0, 5, 60, 2)]
    [InlineData("agent-runner", 15031, 5031, 0, 60, 2)]
    [InlineData("agent-runner", 15031, 5031, 61, 60, 2)]
    [InlineData("agent-runner", 15031, 5031, 5, 5, 2)]
    [InlineData("agent-runner", 15031, 5031, 5, 60, 0)]
    [InlineData("agent-runner", 15031, 5031, 5, 60, 21)]
    public void Policy_RejectsOutOfBoundsOrUnsafeInput(
        string sshTarget, int remotePort, int taskServerPort, int intervalMinutes,
        int probeIntervalSeconds, int failureThreshold)
    {
        var request = new WindowsTunnelRegisterRequest(
            sshTarget, remotePort, taskServerPort, intervalMinutes, probeIntervalSeconds, failureThreshold);

        Assert.NotNull(WindowsTunnelProvisioningPolicy.Validate(request));
    }

    [Fact]
    public void ResolveScriptPath_PointsAtTheRepositoryOwnedDeployFolder()
    {
        var contentRoot = "/home/operator/agent-studio/backend";

        var path = PowerShellWindowsTunnelProvisioner.ResolveScriptPath(contentRoot, "tunnel-status.ps1");

        Assert.EndsWith(
            "/agent-studio/deploy/windows/agent-runner-tunnel/tunnel-status.ps1",
            path.Replace('\\', '/'));
    }

    [Fact]
    public void ParseStatus_ReadsTaskAndHealthFieldsFromTheScriptJson()
    {
        const string json = """
        {
          "observedAt": "2026-08-18T09:00:00Z",
          "keeper": {
            "task": {
              "taskName": "AgentRunner-TunnelKeeper",
              "registered": true,
              "state": "Ready",
              "lastRunTime": "2026-08-18T08:55:00Z",
              "lastTaskResult": 0,
              "nextRunTime": "2026-08-18T09:00:00Z"
            },
            "health": {
              "status": "healthy",
              "message": "Replacement forward passed the remote functional probe.",
              "observedAt": "2026-08-18T08:55:03Z",
              "repairAttempts": 1
            }
          },
          "watchdog": {
            "task": {
              "taskName": "AgentRunner-TunnelWatchdog",
              "registered": true,
              "state": "Running",
              "lastRunTime": "2026-08-18T08:59:00Z",
              "lastTaskResult": null,
              "nextRunTime": null
            },
            "health": {
              "lastHealSucceededAt": "2026-08-18T08:40:00Z",
              "lastHealFailedAt": null,
              "lastProbeFailedAt": "2026-08-18T08:39:00Z",
              "lastEvent": "heal_succeeded",
              "lastEventAt": "2026-08-18T08:40:00Z"
            },
            "alarmActive": false
          }
        }
        """;

        var status = PowerShellWindowsTunnelProvisioner.ParseStatus(json);

        Assert.Equal("windows", status.Platform);
        Assert.NotNull(status.KeeperTask);
        Assert.True(status.KeeperTask!.Registered);
        Assert.Equal("Ready", status.KeeperTask.State);
        Assert.Equal("healthy", status.KeeperHealth!.Status);
        Assert.Equal(1, status.KeeperHealth.RepairAttempts);
        Assert.True(status.WatchdogTask!.Registered);
        Assert.Equal("heal_succeeded", status.WatchdogHealth!.LastEvent);
        Assert.False(status.AlarmActive);
    }

    [Fact]
    public void ParseStatus_ReadsAnActiveAlarm()
    {
        const string json = """
        {
          "observedAt": "2026-08-18T09:00:00Z",
          "keeper": { "task": { "taskName": "AgentRunner-TunnelKeeper", "registered": true }, "health": {} },
          "watchdog": { "task": { "taskName": "AgentRunner-TunnelWatchdog", "registered": true }, "health": {}, "alarmActive": true }
        }
        """;

        var status = PowerShellWindowsTunnelProvisioner.ParseStatus(json);

        Assert.True(status.AlarmActive);
    }

    [Fact]
    public void TryParseRegistration_ReadsTheLastJsonLineAndSucceedsOnOk()
    {
        var requestedAt = DateTime.UtcNow;
        const string stdout = """
        Windows tunnel keeper and watchdog setup needs administrator rights once, to register two AtStartup Scheduled Tasks.
        A Windows "User Account Control" consent prompt is about to open. Approve it to continue; the two scheduled tasks it creates run under your own account with limited rights, not as administrator.
        {"elevated":true,"completedAt":"2026-08-18T09:00:00Z","keeper":{"label":"keeper","taskName":"AgentRunner-TunnelKeeper","registered":true,"error":null},"watchdog":{"label":"watchdog","taskName":"AgentRunner-TunnelWatchdog","registered":true,"error":null},"ok":true}
        """;

        var response = PowerShellWindowsTunnelProvisioner.TryParseRegistration(stdout, requestedAt);

        Assert.NotNull(response);
        Assert.True(response!.Ok);
        Assert.True(response.Elevated);
        Assert.Equal("windows", response.Platform);
        Assert.Contains("keeper registered", response.Detail);
        Assert.Contains("watchdog registered", response.Detail);
    }

    [Fact]
    public void TryParseRegistration_SurfacesADeclinedElevationAsFailure()
    {
        const string stdout = """
        {"elevated":false,"ok":false,"error":"Elevation was declined at the Windows consent prompt. No scheduled task was registered."}
        """;

        var response = PowerShellWindowsTunnelProvisioner.TryParseRegistration(stdout, DateTime.UtcNow);

        Assert.NotNull(response);
        Assert.False(response!.Ok);
        Assert.False(response.Elevated);
        Assert.Equal("Elevation was declined at the Windows consent prompt. No scheduled task was registered.", response.Detail);
    }

    [Fact]
    public void TryParseRegistration_ReturnsNullWhenNoJsonLineIsPresent()
    {
        Assert.Null(PowerShellWindowsTunnelProvisioner.TryParseRegistration("no json here", DateTime.UtcNow));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"keeper": {}}""")]
    public void ParseStatus_ThrowsOnUnrecognizedShape(string malformed)
    {
        // GetStatusAsync wraps this call and degrades to a "could not parse" response;
        // this pins the exact contract that wrapper depends on.
        Assert.ThrowsAny<Exception>(() => PowerShellWindowsTunnelProvisioner.ParseStatus(malformed));
    }

    [Fact]
    public async Task GetStatusAsync_DegradesToAStateInsteadOfThrowing()
    {
        // The UI polls this every 30 seconds. On a non-Windows host it must report
        // "unsupported" quietly; on Windows every probe failure (missing script,
        // failed launch, hung Task Scheduler, unparseable output) has to come back
        // as a response carrying Detail rather than escaping to a 500 on each tick.
        var provisioner = new PowerShellWindowsTunnelProvisioner(
            new FakeHostEnvironment("/nonexistent/agent-studio/backend"));

        var status = await provisioner.GetStatusAsync(CancellationToken.None);

        Assert.Equal(OperatingSystem.IsWindows() ? "windows" : "unsupported", status.Platform);
        Assert.False(status.AlarmActive);
        Assert.NotNull(status.Detail);
    }

    private sealed class FakeHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "AgentStudio.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = "Development";
    }
}
