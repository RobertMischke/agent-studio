using System.Runtime.CompilerServices;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RunnerServiceUnitTests
{
    [Theory]
    [InlineData("deploy/systemd/agent-host.service")]
    [InlineData("scripts/remote-runner-onboard.sh")]
    public void Installed_units_preserve_workers_and_bound_restart_loops(string relativePath)
    {
        var content = File.ReadAllText(Path.Combine(RepoRoot(), relativePath));

        Assert.Contains("KillSignal=SIGTERM", content);
        Assert.Contains("KillMode=process", content);
        Assert.Contains("StartLimitIntervalSec=300", content);
        Assert.Contains("StartLimitBurst=5", content);
        Assert.Contains("RestartSec=10s", content);
    }

    [Fact]
    public void Agent_host_unit_keeps_the_legacy_service_alias_and_publish_path()
    {
        var content = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "systemd", "agent-host.service"));

        Assert.Contains("ExecStart=/opt/agent-host/agent-host --poll", content);
        Assert.Contains("Alias=agent-runner.service", content);
    }

    [Fact]
    public void Onboarding_creates_the_legacy_publish_path_symlink()
    {
        var content = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "remote-runner-onboard.sh"));

        Assert.Contains("agent_host_root=\"/opt/agent-host\"", content);
        Assert.Contains("legacy_root=\"/opt/agent-runner\"", content);
        Assert.Contains("ln -sfnT \"$agent_host_root\" \"$legacy_root\"", content);
    }

    [Fact]
    public void Runner_project_publishes_the_agent_host_binary()
    {
        var content = File.ReadAllText(Path.Combine(RepoRoot(), "runner", "AgentRunner.csproj"));

        Assert.Contains("<AssemblyName>agent-host</AssemblyName>", content);
    }

    [Fact]
    public void Onboarding_places_restart_state_on_persistent_runner_storage()
    {
        var content = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "remote-runner-onboard.sh"));

        Assert.Contains("RUNNER_STATE_DIR=/var/lib/agent-runner/state", content);
        Assert.Contains("/var/lib/agent-runner/state", content);
    }

    private static string RepoRoot([CallerFilePath] string sourceFile = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Repository root was not found above {sourceFile}.");
    }
}
