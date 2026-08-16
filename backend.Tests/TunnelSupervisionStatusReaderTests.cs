using AgentStudio.Management;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace OrchestratorApi.Tests;

public sealed class TunnelSupervisionStatusReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "agent-studio-tunnel-status-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Read_ProjectsRegisteredRunningAndLastHealFromProductState()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "registration.json"), """
            {
              "sshTarget": "agent-runner",
              "remotePort": 15031,
              "keeperRegistered": true,
              "watchdogRegistered": true
            }
            """);
        File.WriteAllText(Path.Combine(_root, "keeper.json"), $$"""
            { "status": "healthy", "observedAt": "{{DateTime.UtcNow.AddMinutes(-5):O}}" }
            """);
        File.WriteAllText(Path.Combine(_root, "watchdog.json"), $$"""
            {
              "status": "running",
              "observedAt": "{{DateTime.UtcNow:O}}",
              "keeperTaskState": "Running",
              "lastHealAt": "{{DateTime.UtcNow.AddMinutes(-2):O}}",
              "lastHealResult": "succeeded"
            }
            """);

        var status = Reader().Read();

        Assert.NotNull(status);
        Assert.Equal("agent-runner", status.SshTarget);
        Assert.Equal(15031, status.RemotePort);
        Assert.True(status.Keeper.Registered);
        Assert.Equal("running", status.Keeper.State);
        Assert.True(status.Watchdog.Registered);
        Assert.Equal("running", status.Watchdog.State);
        Assert.Equal("succeeded", status.LastHealResult);
    }

    [Fact]
    public void Read_MarksTaskStateStaleWhenWatchdogHeartbeatExpired()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "registration.json"), """
            {
              "sshTarget": "agent-runner",
              "remotePort": 15031,
              "keeperRegistered": true,
              "watchdogRegistered": true
            }
            """);
        File.WriteAllText(Path.Combine(_root, "watchdog.json"), $$"""
            {
              "status": "running",
              "observedAt": "{{DateTime.UtcNow.AddMinutes(-4):O}}",
              "keeperTaskState": "Running"
            }
            """);

        var status = Reader().Read();

        Assert.NotNull(status);
        Assert.Equal("stale", status.Keeper.State);
        Assert.Equal("stale", status.Watchdog.State);
    }

    [Fact]
    public void Attach_OnlyTargetsTheRunnerUsingTheRegisteredTunnelPort()
    {
        var matching = Snapshot("matching", "127.0.0.1:15031");
        var other = Snapshot("other", "https://tasks.example.test");
        var status = new TunnelSupervisionStatusDto(
            "agent-runner",
            15031,
            new TunnelScheduledTaskStatusDto(true, "running", DateTime.UtcNow),
            new TunnelScheduledTaskStatusDto(true, "running", DateTime.UtcNow),
            null,
            null);

        var result = TunnelSupervisionProjection.Attach([matching, other], status);

        Assert.Same(status, result[0].TunnelSupervision);
        Assert.Null(result[1].TunnelSupervision);
    }

    private TunnelSupervisionStatusReader Reader()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TunnelSupervision:StateDirectory"] = _root,
            })
            .Build();
        return new TunnelSupervisionStatusReader(
            configuration,
            NullLogger<TunnelSupervisionStatusReader>.Instance);
    }

    private static RunnerCapabilitySnapshotDto Snapshot(string id, string connectivityIdentity)
        => new(
            id,
            id,
            id,
            "instance",
            "1.0.0",
            1,
            "active",
            DateTime.UtcNow,
            DateTime.UtcNow,
            new RemoteHostAdmissionDto(id, "open", null, null, null, null),
            [
                new CapabilityHealthDto(
                    CapabilityProtocol.TaskServerConnectivity,
                    "foundation",
                    "ready",
                    CapabilityHealthStates.Healthy,
                    null,
                    DateTime.UtcNow,
                    DateTime.UtcNow.AddMinutes(3),
                    true,
                    null,
                    null,
                    null,
                    null,
                    0,
                    null,
                    connectivityIdentity,
                    null,
                    [],
                    []),
            ],
            null);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
