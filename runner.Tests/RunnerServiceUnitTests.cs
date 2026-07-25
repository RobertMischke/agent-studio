using System.Diagnostics;
using System.Runtime.CompilerServices;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RunnerServiceUnitTests
{
    [Theory]
    [InlineData("deploy/systemd/agent-runner.service")]
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
    public void Onboarding_places_restart_state_on_persistent_runner_storage()
    {
        var content = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "remote-runner-onboard.sh"));

        Assert.Contains("service_root=\"/var/lib/agent-runner\"", content);
        Assert.Contains("RUNNER_STATE_DIR=%s/state", content);
    }

    [Theory]
    [InlineData("coding", "12", "CPUQuota=1200%\nCPUWeight=100\nIOWeight=100\n")]
    [InlineData("review", "12", "CPUQuota=400%\nCPUWeight=30\nIOWeight=30\n")]
    [InlineData("review", "2", "CPUQuota=100%\nCPUWeight=30\nIOWeight=30\n")]
    public void Agent_host_generates_role_quotas_from_host_cpu_count(
        string role,
        string cpuCount,
        string expected)
    {
        var profile = Path.Combine(Path.GetTempPath(), $"missing-agent-host-profile-{Guid.NewGuid():N}");
        var result = RunResourceGovernance(
            "--role", role,
            "--cpu-count", cpuCount,
            "--profile", profile);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StandardOutput.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Agent_host_profile_is_the_only_explicit_resource_override()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var profile = Path.Combine(root, "profile.conf");
            File.WriteAllText(
                profile,
                """
                CODING_CPU_QUOTA=600%
                CODING_CPU_WEIGHT=200
                CODING_IO_WEIGHT=150
                CODING_MEMORY_MAX=16G
                """);

            var result = RunResourceGovernance(
                "--role", "coding",
                "--cpu-count", "12",
                "--profile", profile);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(
                "CPUQuota=600%\nCPUWeight=200\nIOWeight=150\nMemoryMax=16G\n",
                result.StandardOutput.ReplaceLineEndings("\n"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Agent_host_adopts_and_replaces_legacy_resource_drop_in()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var profile = Path.Combine(root, "agent-host", "profile.conf");
            var dropInDirectory = Path.Combine(root, "agent-runner-review.service.d");
            Directory.CreateDirectory(dropInDirectory);
            var limits = Path.Combine(dropInDirectory, "10-limits.conf");
            File.WriteAllText(
                limits,
                """
                [Service]
                CPUQuota=200%
                CPUWeight=20
                IOWeight=20
                MemoryMax=8G
                """);
            var overrideLimits = Path.Combine(dropInDirectory, "90-local.conf");
            File.WriteAllText(
                overrideLimits,
                """
                [Service]
                CPUQuota=400%
                CPUWeight=30
                IOWeight=30
                RestartSec=20s
                """);

            var result = RunResourceGovernance(
                "--role", "review",
                "--cpu-count", "24",
                "--profile", profile,
                "--drop-in-dir", dropInDirectory,
                "--migrate-drop-ins");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(
                "CPUQuota=400%\nCPUWeight=30\nIOWeight=30\nMemoryMax=8G\n",
                result.StandardOutput.ReplaceLineEndings("\n"));
            Assert.Contains("REVIEW_CPU_QUOTA=400%", File.ReadAllText(profile));
            Assert.Contains("REVIEW_MEMORY_MAX=8G", File.ReadAllText(profile));
            Assert.False(File.Exists(limits));
            Assert.DoesNotContain("CPUQuota", File.ReadAllText(overrideLimits));
            Assert.Contains("RestartSec=20s", File.ReadAllText(overrideLimits));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Onboarding_embeds_generated_policy_in_the_managed_main_unit()
    {
        var content = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "remote-runner-onboard.sh"));

        Assert.Contains("--migrate-drop-ins", content);
        Assert.Contains("resource_policy=", content);
        Assert.Contains("$resource_policy", content);
        Assert.Contains("/etc/systemd/system/${service_name}.service", content);
    }

    private static (int ExitCode, string StandardOutput, string StandardError) RunResourceGovernance(
        params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "bash",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(RepoRoot(), "scripts", "agent-host-resource-governance.sh"));
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)!;
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, standardOutput, standardError);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-host-resource-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
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
