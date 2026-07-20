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
}
