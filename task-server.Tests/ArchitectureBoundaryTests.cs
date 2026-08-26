using Xunit;

namespace TaskServer.Tests;

public sealed class ArchitectureBoundaryTests
{
    [Fact]
    public void Task_server_project_references_only_contracts_and_persistence_packages()
    {
        var root = ProtocolTests.RepositoryRoot();
        var project = File.ReadAllText(Path.Combine(root, "task-server", "TaskServer.csproj"));
        Assert.Contains("TaskServer.Contracts.csproj", project);
        Assert.DoesNotContain("frontend", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OrchestratorApi.csproj", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AgentRunner.csproj", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CodingAgentRunner", project, StringComparison.OrdinalIgnoreCase);

        var serverSources = Directory.EnumerateFiles(Path.Combine(root, "task-server"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        foreach (var source in serverSources)
        {
            var text = File.ReadAllText(source);
            Assert.DoesNotContain("System.Diagnostics.Process", text, StringComparison.Ordinal);
            Assert.DoesNotContain("AgentRunner", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Angular", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Production_profile_is_a_self_contained_single_file_with_its_own_backup_timer()
    {
        var root = ProtocolTests.RepositoryRoot();
        var profile = File.ReadAllText(Path.Combine(
            root,
            "task-server",
            "Properties",
            "PublishProfiles",
            "linux-x64.pubxml"));
        Assert.Contains("<RuntimeIdentifier>linux-x64</RuntimeIdentifier>", profile);
        Assert.Contains("<SelfContained>true</SelfContained>", profile);
        Assert.Contains("<PublishSingleFile>true</PublishSingleFile>", profile);
        Assert.Contains("<IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>", profile);

        var timerService = File.ReadAllText(Path.Combine(
            root,
            "deploy",
            "systemd",
            "agent-task-server-backup.service"));
        Assert.Contains("task-server backup --name timer", timerService);
        Assert.Contains("EnvironmentFile=/etc/agent-orchestrator/server.env", timerService);
    }

    [Fact]
    public void Windows_cutover_uses_s4u_supervision_versioned_install_and_authority_probes()
    {
        var root = ProtocolTests.RepositoryRoot();
        var windows = Path.Combine(root, "deploy", "windows", "task-server");
        var registration = File.ReadAllText(Path.Combine(windows, "register-task-server.ps1"));
        var installer = File.ReadAllText(Path.Combine(windows, "install-task-server.ps1"));
        var rehearsal = File.ReadAllText(Path.Combine(windows, "rehearse-legacy-migration.ps1"));
        var updater = File.ReadAllText(Path.Combine(root, "scripts", "update-stable.sh"));
        var probe = File.ReadAllText(Path.Combine(root, "scripts", "stable-frontend-boot-probe.mjs"));

        Assert.Contains("-LogonType S4U", registration);
        Assert.Contains("-AtStartup", registration);
        Assert.Contains("PublishProfile=win-x64", installer);
        Assert.Contains("New-Item -ItemType Junction", installer);
        Assert.Contains("TaskServer", installer);
        Assert.Contains("legacy-copy", rehearsal);
        Assert.Contains("legacyAuthority", rehearsal);
        Assert.Contains("runnerIdentities", rehearsal);
        Assert.Contains("reviewAttempts", rehearsal);
        Assert.Contains("expectedMigrationId", rehearsal);
        Assert.Contains("integritySha256", rehearsal);
        Assert.True(
            updater.IndexOf("Publishing, installing, and starting Task Server", StringComparison.Ordinal)
            < updater.IndexOf("Starting Stable", StringComparison.Ordinal));
        Assert.Contains("/readyz", probe);
        Assert.Contains("/api/v1/protocol", probe);
    }
}
