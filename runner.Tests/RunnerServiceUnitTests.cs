using System.Diagnostics;
using System.Runtime.CompilerServices;
using AgentStudio.TestSupport;
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
    public void Agent_host_unit_uses_the_atomic_current_release_path()
    {
        var content = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "systemd", "agent-host.service"));

        Assert.Contains("ExecStart=/opt/agent-host/current/agent-host --poll", content);
        Assert.DoesNotContain("ExecStart=/opt/agent-host/agent-host --poll", content);
        Assert.Contains("Alias=agent-runner.service", content);
    }

    [Theory]
    [InlineData("deploy/systemd/agent-host.service")]
    [InlineData("scripts/remote-runner-onboard.sh")]
    public void Agent_host_units_load_the_shared_provider_auth_file_after_the_role_environment(
        string relativePath)
    {
        var content = File.ReadAllText(Path.Combine(RepoRoot(), relativePath));
        var roleEnvironment = content.IndexOf("EnvironmentFile=/etc/agent-runner/runner.env", StringComparison.Ordinal);
        if (relativePath.EndsWith("remote-runner-onboard.sh", StringComparison.Ordinal))
            roleEnvironment = content.IndexOf("EnvironmentFile=$env_file", StringComparison.Ordinal);
        var providerEnvironment = content.IndexOf(
            "EnvironmentFile=/etc/agent-runner/provider-auth.env",
            StringComparison.Ordinal);
        if (relativePath.EndsWith("remote-runner-onboard.sh", StringComparison.Ordinal))
            providerEnvironment = content.IndexOf("EnvironmentFile=$provider_auth_file", StringComparison.Ordinal);

        Assert.True(roleEnvironment >= 0);
        Assert.True(providerEnvironment > roleEnvironment);
        if (relativePath.EndsWith("remote-runner-onboard.sh", StringComparison.Ordinal))
            Assert.Contains("/proc/${main_pid}/environ", content);
    }

    [Fact]
    public void Static_and_managed_units_load_the_shared_provider_auth_file_last()
    {
        var staticUnit = File.ReadAllText(
            Path.Combine(RepoRoot(), "deploy", "systemd", "agent-host.service"));
        var onboarding = File.ReadAllText(
            Path.Combine(RepoRoot(), "scripts", "remote-runner-onboard.sh"));

        Assert.Contains(
            "EnvironmentFile=/etc/agent-runner/runner.env\n" +
            "# Shared, root-owned provider credentials.",
            staticUnit.ReplaceLineEndings("\n"));
        Assert.Contains(
            "EnvironmentFile=/etc/agent-runner/provider-auth.env",
            staticUnit);
        Assert.True(
            staticUnit.IndexOf(
                "EnvironmentFile=/etc/agent-runner/provider-auth.env",
                StringComparison.Ordinal) >
            staticUnit.IndexOf(
                "EnvironmentFile=/etc/agent-runner/runner.env",
                StringComparison.Ordinal));
        Assert.Contains(
            "EnvironmentFile=$env_file\n" +
            "# One shared provider credential file",
            onboarding.ReplaceLineEndings("\n"));
        Assert.Contains(
            "provider_auth_metadata=\"$(sudo stat -c '%U:%G:%a' \"$provider_auth_file\")\"",
            onboarding);
        Assert.True(
            onboarding.IndexOf(
                "EnvironmentFile=$provider_auth_file",
                StringComparison.Ordinal) >
            onboarding.IndexOf("EnvironmentFile=$env_file", StringComparison.Ordinal));
        Assert.Contains("root:agent:640", onboarding);
        Assert.DoesNotContain("provider_auth_tmp", onboarding);
        Assert.DoesNotContain("claude auth login", onboarding);
    }

    [Fact]
    public void Onboarding_installs_immutable_tool_releases_and_switches_current_atomically()
    {
        var content = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "remote-runner-onboard.sh"));

        Assert.Contains("releases_root=\"$tool_root/releases\"", content);
        Assert.Contains("dotnet tool install --tool-path \"$stage_root\"", content);
        Assert.Contains("ln -sfnT \"$release_root\" \"$tool_root/current\"", content);
        Assert.Contains("ExecStart=$agent_host_root/current/$runner_command --poll", content);
        Assert.DoesNotContain("dotnet tool update --global", content);
    }

    [Fact]
    public void Manual_runner_publish_stages_an_immutable_release_before_switching_current()
    {
        var content = File.ReadAllText(
            Path.Combine(RepoRoot(), "docs", "operations", "setup", "linux-runner-host.md"));

        Assert.Contains("release_root=\"/opt/agent-host/releases/$release_id\"", content);
        Assert.Contains("dotnet publish runner/AgentRunner.csproj -c Release -o \"$staging_root\"", content);
        Assert.Contains("ln -sfnT \"$release_root\" /opt/agent-host/current", content);
        Assert.DoesNotContain(
            "dotnet publish runner/AgentRunner.csproj -c Release -o /opt/agent-host",
            content);
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

        Assert.Contains("service_root=\"/var/lib/agent-runner\"", content);
        Assert.Contains("RUNNER_STATE_DIR=%s/state", content);
    }

    [Fact]
    public void Agent_runner_sudoers_enumerates_the_complete_bounded_config_allowlist()
    {
        var content = File.ReadAllText(
            Path.Combine(RepoRoot(), "deploy", "agent-host", "sudoers.d", "agent-runner"));

        Assert.DoesNotContain("NOPASSWD:ALL", content);
        Assert.DoesNotContain("NOPASSWD: ALL", content);
        Assert.DoesNotContain("*", content);
        Assert.Contains("/usr/local/sbin/agent-runner-deploy \"\"", content);
        Assert.Equal(
            12,
            CountOccurrences(
                content,
                "/usr/local/sbin/agent-runner-deploy config "));
        Assert.DoesNotContain("RUNNER_MAX_PARALLELISM 0", content);
        Assert.DoesNotContain("RUNNER_MAX_PARALLELISM 7", content);
    }

    [Fact]
    public void Agent_runner_config_helper_owns_atomic_update_restart_audit_and_process_proof()
    {
        var helper = File.ReadAllText(
            Path.Combine(RepoRoot(), "deploy", "agent-host", "agent-runner-deploy"));
        var migration = File.ReadAllText(
            Path.Combine(RepoRoot(), "scripts", "harden-agent-runner-host.sh"));

        Assert.Contains("source \"$selected_config_policy\"", helper);
        Assert.Contains("mv -fT -- \"$candidate_file\" \"$config_env_file\"", helper);
        Assert.Contains("systemctl restart \"$AGENT_RUNNER_CONFIG_UNIT\"", helper);
        Assert.Contains("wait_for_new_main_pid", helper);
        Assert.Contains("unit_environment_file_is_authoritative", helper);
        Assert.Contains("/proc/$main_pid/environ", helper);
        Assert.Contains("result=$result", helper);
        Assert.Contains("rollback_config", helper);
        Assert.Contains("EnvironmentFiles", helper);
        Assert.Contains("installed_policy", migration);
        Assert.Contains("visudo -c", migration);
        Assert.Contains("for privileged_group in sudo docker", migration);
    }

    [SkippableTheory]
    [InlineData("6", "5")]
    [InlineData("5", "6")]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    [Trait("Category", "MachineBound")]
    [Trait("Category", "ReviewFlaky")]
    public void Agent_runner_config_change_waits_for_new_main_pid_while_detached_worker_survives(
        string oldValue,
        string requestedValue)
    {
        PlatformGate.LinuxOnly("the helper proves the replacement daemon through /proc/<MainPID>/environ");
        PlatformGate.RequiresPosixShell();

        var root = CreateTemporaryDirectory();
        try
        {
            var policy = Path.Combine(root, "config-policy");
            var sharedEnvironment = Path.Combine(root, "runner.env");
            var roleEnvironment = Path.Combine(root, "runner-review.env");
            var laterEnvironment = Path.Combine(root, "later.env");
            var stateFile = Path.Combine(root, "main-pid-observation-count");
            var restartFile = Path.Combine(root, "restart-count");
            var auditLog = Path.Combine(root, "audit.log");
            var pgrepMarker = Path.Combine(root, "pgrep-was-called");

            File.WriteAllText(
                policy,
                """
                agent_runner_resolve_config() {
                  AGENT_RUNNER_CONFIG_UNIT="agent-runner-review.service"
                  AGENT_RUNNER_CONFIG_PRIMARY_ENV="$TEST_ROLE_ENV"
                  AGENT_RUNNER_CONFIG_FALLBACK_ENV="$TEST_MISSING_ENV"
                  AGENT_RUNNER_CONFIG_VARIABLE="$2"
                  AGENT_RUNNER_CONFIG_VALUE="$3"
                }
                """);
            File.WriteAllText(sharedEnvironment, "RUNNER_MAX_PARALLELISM=2\n");
            File.WriteAllText(roleEnvironment, $"RUNNER_MAX_PARALLELISM={oldValue}\n");
            File.WriteAllText(laterEnvironment, "RUNNER_MAX_PARALLELISM=4\n");
            File.WriteAllText(stateFile, "0\n");
            File.WriteAllText(restartFile, "0\n");

            var result = RunPosixShell(
                """
                set -euo pipefail
                source "$1"
                policy="$2"
                shared_env="$3"
                role_env="$4"
                later_env="$5"
                state_file="$6"
                restart_file="$7"
                audit_log="$8"
                pgrep_marker="$9"
                old_value="${10}"
                requested_value="${11}"

                env "RUNNER_MAX_PARALLELISM=$old_value" sleep 30 &
                old_main_pid=$!
                env "RUNNER_MAX_PARALLELISM=$old_value" sleep 30 &
                detached_worker_pid=$!
                env "RUNNER_MAX_PARALLELISM=$requested_value" sleep 30 &
                new_main_pid=$!
                cleanup() {
                  kill "$old_main_pid" "$detached_worker_pid" "$new_main_pid" 2>/dev/null || true
                  wait "$old_main_pid" "$detached_worker_pid" "$new_main_pid" 2>/dev/null || true
                }
                trap cleanup EXIT

                stat() {
                  if [[ "$1" == "-c" && "$2" == "%U:%G:%a" ]]; then
                    case "$3" in
                      "$policy") printf 'root:root:755\n'; return 0 ;;
                      "$role_env") printf 'root:agent:640\n'; return 0 ;;
                    esac
                  fi
                  command stat "$@"
                }
                chown() { :; }
                logger() { printf '%s\n' "$*" >>"$audit_log"; }
                pgrep() {
                  printf 'called\n' >"$pgrep_marker"
                  printf '%s\n' "$detached_worker_pid"
                }
                systemctl() {
                  case "$1:${2:-}" in
                    cat:*)
                      printf '%s\n' \
                        '[Service]' \
                        'Environment=RUNNER_MAX_PARALLELISM=2' \
                        "EnvironmentFile=$role_env" \
                        'KillMode=process'
                      ;;
                    restart:*)
                      local restart_count
                      restart_count="$(<"$restart_file")"
                      printf '%s\n' "$((restart_count + 1))" >"$restart_file"
                      kill "$old_main_pid" 2>/dev/null || true
                      ;;
                    show:--property=EnvironmentFiles)
                      printf '%s (ignore_errors=no)\n' "$shared_env" "$role_env"
                      ;;
                    show:--property=MainPID)
                      printf '%s\n' "$old_main_pid"
                      ;;
                    show:--property=KillMode)
                      printf 'process\n'
                      ;;
                    show:--property=ActiveState)
                      local observation_count
                      observation_count="$(<"$state_file")"
                      observation_count=$((observation_count + 1))
                      printf '%s\n' "$observation_count" >"$state_file"
                      case "$observation_count" in
                        1) printf 'ActiveState=active\nMainPID=%s\n' "$old_main_pid" ;;
                        2) printf 'ActiveState=activating\nMainPID=0\n' ;;
                        *) printf 'ActiveState=active\nMainPID=%s\n' "$new_main_pid" ;;
                      esac
                      ;;
                    *)
                      printf 'unexpected systemctl invocation: %s\n' "$*" >&2
                      return 90
                      ;;
                  esac
                }

                [[ "$(systemctl show --property=KillMode --value agent-runner-review.service)" == "process" ]]
                ! unit_environment_file_is_authoritative \
                  "$(printf '%s (ignore_errors=no)\n' "$role_env" "$later_env")" \
                  "$role_env" \
                  RUNNER_MAX_PARALLELISM

                export TEST_ROLE_ENV="$role_env"
                export TEST_MISSING_ENV="$role_env.missing"
                configure_role review RUNNER_MAX_PARALLELISM "$requested_value" "$policy"
                # configure_role owns the executable helper's EXIT trap. Restore
                # the harness cleanup after its successful sourced invocation.
                trap cleanup EXIT

                grep -Fxq "RUNNER_MAX_PARALLELISM=$requested_value" "$role_env"
                grep -Fq 'result=applied' "$audit_log"
                grep -Eq 'previous-pid=[0-9]+,new-pid=[0-9]+' "$audit_log"
                [[ "$(<"$restart_file")" == "1" ]]
                [[ "$(<"$state_file")" -ge 3 ]]
                [[ ! -e "$pgrep_marker" ]]
                kill -0 "$detached_worker_pid"
                tr '\0' '\n' <"/proc/$new_main_pid/environ" |
                  grep -Fxq "RUNNER_MAX_PARALLELISM=$requested_value"
                """,
                Path.Combine(RepoRoot(), "deploy", "agent-host", "agent-runner-deploy"),
                policy,
                sharedEnvironment,
                roleEnvironment,
                laterEnvironment,
                stateFile,
                restartFile,
                auditLog,
                pgrepMarker,
                oldValue,
                requestedValue);

            Assert.True(
                result.ExitCode == 0,
                $"config helper regression exited {result.ExitCode}: {result.StandardError.Trim()}");
            Assert.Contains("configured role=review", result.StandardOutput);
            Assert.Contains($"value={requestedValue}", result.StandardOutput);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [SkippableTheory]
    [InlineData("coding", "1", "agent-runner.service", "/etc/agent-runner/runner-coding.env")]
    [InlineData("coding", "6", "agent-runner.service", "/etc/agent-runner/runner-coding.env")]
    [InlineData("review", "1", "agent-runner-review.service", "/etc/agent-runner/runner-review.env")]
    [InlineData("review", "6", "agent-runner-review.service", "/etc/agent-runner/runner-review.env")]
    public void Agent_runner_config_policy_accepts_only_each_role_boundary(
        string role,
        string value,
        string expectedUnit,
        string expectedPrimaryEnvironment)
    {
        PlatformGate.RequiresPosixShell();

        var result = RunConfigPolicy(role, "RUNNER_MAX_PARALLELISM", value);

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.Contains($"unit={expectedUnit}", result.StandardOutput);
        Assert.Contains($"primary_env={expectedPrimaryEnvironment}", result.StandardOutput);
        Assert.Contains($"value={value}", result.StandardOutput);
    }

    [SkippableTheory]
    [InlineData("review", "RUNNER_MAX_PARALLELISM", "0", "must be an integer from 1 through 6")]
    [InlineData("review", "RUNNER_MAX_PARALLELISM", "7", "must be an integer from 1 through 6")]
    [InlineData("review", "RUNNER_MAX_PARALLELISM", "4x", "must be an integer from 1 through 6")]
    [InlineData("review", "RUNNER_POLL_SECONDS", "4", "variable is not allowlisted")]
    [InlineData("gate", "RUNNER_MAX_PARALLELISM", "4", "role must be")]
    public void Agent_runner_config_policy_rejects_outside_the_allowlist(
        string role,
        string variable,
        string value,
        string expectedError)
    {
        PlatformGate.RequiresPosixShell();

        var result = RunConfigPolicy(role, variable, value);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expectedError, result.StandardError);
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
    public void Onboarding_embeds_generated_policy_in_the_managed_main_unit()
    {
        var content = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "remote-runner-onboard.sh"));

        Assert.Contains("--migrate-drop-ins", content);
        Assert.Contains("resource_policy=", content);
        Assert.Contains("$resource_policy", content);
        Assert.Contains("/etc/systemd/system/${service_name}.service", content);
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

    private static (int ExitCode, string StandardOutput, string StandardError) RunConfigPolicy(
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
            PosixShell.ToShellPath(
                Path.Combine(RepoRoot(), "deploy", "agent-host", "agent-runner-config-policy")));
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)!;
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, standardOutput, standardError);
    }

    private static (int ExitCode, string StandardOutput, string StandardError) RunPosixShell(
        string script,
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
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("agent-runner-config-regression");
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

    private static int CountOccurrences(string content, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = content.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
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
