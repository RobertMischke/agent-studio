using System.Diagnostics;
using System.Runtime.CompilerServices;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RunnerServiceUnitTests
{
    [Theory]
    [InlineData("deploy/systemd/agent-host.service")]
    [InlineData("deploy/agent-host/agent-host-admin")]
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
    public void Agent_host_unit_uses_the_atomic_current_release_path()
    {
        var content = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "systemd", "agent-host.service"));

        Assert.Contains("ExecStart=/opt/agent-host/current/agent-host --poll", content);
        Assert.DoesNotContain("ExecStart=/opt/agent-host/agent-host --poll", content);
        Assert.Contains("Alias=agent-runner.service", content);
    }

    [Fact]
    public void Onboarding_installs_immutable_tool_releases_and_switches_current_atomically()
    {
        var content = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "remote-runner-onboard.sh"));

        Assert.Contains("releases_root=\"$tool_root/releases\"", content);
        Assert.Contains("dotnet tool install --tool-path \"$stage_root\"", content);
        Assert.Contains("ln -sfnT \"$release_root\" \"$tool_root/current\"", content);
        Assert.Contains("agent-host-admin activate \"$installed_version\"", content);
        Assert.Contains("runner_bin=\"$agent_host_root/current/agent-host\"", content);
        Assert.DoesNotContain("dotnet tool update --global", content);
    }

    [Fact]
    public void Manual_runner_publish_stages_an_immutable_release_before_switching_current()
    {
        var runbook = File.ReadAllText(
            Path.Combine(RepoRoot(), "docs", "operations", "setup", "linux-runner-host.md"));
        var deployScript = File.ReadAllText(
            Path.Combine(RepoRoot(), "scripts", "remote-agent-host-deploy.sh"));

        Assert.Contains("dotnet publish runner/AgentRunner.csproj -c Release -o \"$staging_root\"", runbook);
        Assert.Contains("scripts/remote-agent-host-deploy.sh", runbook);
        Assert.Contains("scp_base=(scp", deployScript);
        Assert.Contains("agent-host-admin activate \"$release_id\"", deployScript);
        Assert.DoesNotContain("sudo cp", runbook);
        Assert.DoesNotContain("sudo ln", runbook);
    }

    [Fact]
    public void Scoped_admin_creates_the_legacy_publish_path_symlink()
    {
        var content = File.ReadAllText(
            Path.Combine(RepoRoot(), "deploy", "agent-host", "agent-host-admin"));

        Assert.Contains("readonly release_root=\"/opt/agent-host/releases\"", content);
        Assert.Contains("ln -sfnT -- /opt/agent-host /opt/agent-runner", content);
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

        Assert.Contains("service_root=\"/var/lib/agent-runner\"", content);
        Assert.Contains("RUNNER_STATE_DIR=%s/state", content);
    }

    [SkippableTheory]
    [InlineData("coding", "12", "CPUQuota=1200%\nCPUWeight=100\nIOWeight=100\n")]
    [InlineData("review", "12", "CPUQuota=400%\nCPUWeight=30\nIOWeight=30\n")]
    [InlineData("review", "2", "CPUQuota=100%\nCPUWeight=30\nIOWeight=30\n")]
    public void Agent_host_generates_role_quotas_from_host_cpu_count(
        string role,
        string cpuCount,
        string expected)
    {
        PlatformGate.RequiresPosixShell();

        var profile = Path.Combine(Path.GetTempPath(), $"missing-agent-host-profile-{Guid.NewGuid():N}");
        var result = RunResourceGovernance(
            "--role", role,
            "--cpu-count", cpuCount,
            "--profile", profile);

        AssertScriptSucceeded(result);
        Assert.Equal(expected, result.StandardOutput.ReplaceLineEndings("\n"));
    }

    [SkippableFact]
    public void Agent_host_profile_is_the_only_explicit_resource_override()
    {
        PlatformGate.RequiresPosixShell();

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

            AssertScriptSucceeded(result);
            Assert.Equal(
                "CPUQuota=600%\nCPUWeight=200\nIOWeight=150\nMemoryMax=16G\n",
                result.StandardOutput.ReplaceLineEndings("\n"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [SkippableFact]
    public void Agent_host_adopts_and_replaces_legacy_resource_drop_in()
    {
        PlatformGate.RequiresPosixShell();

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

            AssertScriptSucceeded(result);
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
    public void Scoped_admin_embeds_generated_resource_policy_in_the_managed_main_unit()
    {
        var content = File.ReadAllText(
            Path.Combine(RepoRoot(), "deploy", "agent-host", "agent-host-admin"));

        Assert.Contains("--migrate-drop-ins", content);
        Assert.Contains("local resource_policy", content);
        Assert.Contains("$resource_policy", content);
        Assert.Contains("/etc/systemd/system/${service_name}.d", content);
    }

    [Fact]
    public void Runner_host_sudoers_is_limited_to_the_admin_boundary_and_two_units()
    {
        var content = File.ReadAllText(
            Path.Combine(RepoRoot(), "deploy", "agent-host", "sudoers.agent-host"));

        Assert.Contains("/usr/local/sbin/agent-host-admin", content);
        Assert.Contains("/usr/bin/systemctl restart agent-host.service", content);
        Assert.Contains("/usr/bin/systemctl status --no-pager agent-host.service", content);
        Assert.Contains("/usr/bin/systemctl restart agent-runner-review.service", content);
        Assert.Contains("/usr/bin/systemctl status --no-pager agent-runner-review.service", content);
        Assert.DoesNotContain("NOPASSWD: ALL", content);
        Assert.DoesNotContain("/usr/bin/docker", content);
        Assert.DoesNotContain("/usr/bin/journalctl", content);
    }

    [Fact]
    public void Scoped_admin_accepts_only_fixed_deploy_roots_and_unit_roles()
    {
        var content = File.ReadAllText(
            Path.Combine(RepoRoot(), "deploy", "agent-host", "agent-host-admin"));

        Assert.Contains("readonly incoming_root=\"$deploy_root/incoming\"", content);
        Assert.Contains("readonly current_link=\"/opt/agent-host/current\"", content);
        Assert.Contains("service_name=\"agent-host.service\"", content);
        Assert.Contains("service_name=\"agent-runner-review.service\"", content);
        Assert.Contains("release contains a link or special file", content);
        Assert.Contains("environment key is not admitted by the root boundary", content);
        Assert.DoesNotContain("eval ", content);
    }

    [Fact]
    public void Review_daemon_logs_a_bounded_success_line_after_capability_advertisement()
    {
        var daemon = File.ReadAllText(
            Path.Combine(RepoRoot(), "runner", "RemoteReviewDaemon.cs"));
        var admin = File.ReadAllText(
            Path.Combine(RepoRoot(), "deploy", "agent-host", "agent-host-admin"));

        Assert.Contains("review-capability-advertisement status=ready generation=", daemon);
        Assert.Contains("review-capability-advertisement status=ready generation=[0-9]+", admin);
    }

    [Fact]
    public void Onboarding_no_longer_depends_on_generic_passwordless_sudo()
    {
        var content = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "remote-runner-onboard.sh"));

        Assert.DoesNotContain("sudo -n true", content);
        Assert.DoesNotContain("sudo -n npm", content);
        Assert.DoesNotContain("sudo install", content);
        Assert.DoesNotContain("sudo chown", content);
        Assert.DoesNotContain("sudo chmod", content);
        Assert.DoesNotContain("sudo journalctl", content);
        Assert.Contains("sudo -n /usr/local/sbin/agent-host-admin configure \"$role\"", content);
    }

    /// <summary>
    /// Runs the agent-host resource policy script through a real POSIX shell.
    ///
    /// Two Windows-specific details, both handled centrally in
    /// <see cref="PosixShell"/> rather than here: the interpreter must be a full
    /// path (Git's bash is usually not on the PATH of the test host process),
    /// and every path argument must be MSYS-style, because the script enforces
    /// <c>[[ "$profile" == /* ]]</c> and a literal <c>C:\...</c> fails that guard
    /// with exit 2. The Windows form stays in the caller so its own file
    /// assertions keep working - both spellings address the same file.
    /// </summary>
    private static (int ExitCode, string StandardOutput, string StandardError) RunResourceGovernance(
        params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = PosixShell.RequirePath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(
            PosixShell.ToShellPath(Path.Combine(RepoRoot(), "scripts", "agent-host-resource-governance.sh")));
        foreach (var argument in arguments)
            start.ArgumentList.Add(Path.IsPathRooted(argument) ? PosixShell.ToShellPath(argument) : argument);

        using var process = Process.Start(start)!;
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, standardOutput, standardError);
    }

    /// <summary>
    /// A bare exit-code assertion hides the script's own diagnosis; the script
    /// reports every rejection on stderr, so surface it in the failure message.
    /// </summary>
    private static void AssertScriptSucceeded(
        (int ExitCode, string StandardOutput, string StandardError) result)
        => Assert.True(
            result.ExitCode == 0,
            $"agent-host-resource-governance.sh exited {result.ExitCode}: {result.StandardError.Trim()}");

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
