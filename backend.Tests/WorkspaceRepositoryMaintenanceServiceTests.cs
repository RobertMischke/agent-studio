using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class WorkspaceRepositoryMaintenancePolicyTests
{
    [Theory]
    [InlineData("git version 2.36.9.windows.1", false)]
    [InlineData("git version 2.37.0.windows.1", true)]
    [InlineData("git version 3.0.0", true)]
    [InlineData("unknown", false)]
    public void SupportsBuiltInFsMonitor_UsesDocumentedVersionFloor(string output, bool expected) =>
        Assert.Equal(expected, WorkspaceRepositoryMaintenancePolicy.SupportsBuiltInFsMonitor(output));
}

[Trait("Category", "MachineBound")]
public sealed class WorkspaceRepositoryMaintenanceServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "workspace-maintenance-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void RunOnce_ConsolidatesSyntheticLooseObjectsAndConfiguresRepository()
    {
        Directory.CreateDirectory(_root);
        RunGit("init", "-q", "-b", "main");
        RunGit("config", "user.name", "test");
        RunGit("config", "user.email", "test@example.com");
        for (var i = 0; i < 130; i++)
            File.WriteAllText(Path.Combine(_root, $"object-{i:000}.txt"), Guid.NewGuid().ToString("N"));
        RunGit("add", ".");
        RunGit("commit", "-q", "-m", "synthetic loose objects");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["WorkspaceRepository:GcAutoLooseObjectLimit"] = "100",
                ["WorkspaceRepository:LooseObjectBatchSize"] = "100",
                ["WorkspaceRepository:MaxLooseObjectPasses"] = "5",
            })
            .Build();
        var service = new WorkspaceRepositoryMaintenanceService(
            config,
            NullLogger<WorkspaceRepositoryMaintenanceService>.Instance);

        var result = service.RunOnce();

        Assert.True(result.Success, $"{result.Phase}: {result.Error}");
        Assert.True(result.DidRun);
        Assert.True(result.LooseObjectsBefore > 100);
        Assert.True(result.LooseObjectsAfter <= 100);
        Assert.Equal("100", GitOutput("config", "--local", "--get", "gc.auto").Trim());
        Assert.Equal("incremental", GitOutput("config", "--local", "--get", "maintenance.strategy").Trim());
        var excludePath = GitOutput("rev-parse", "--git-path", "info/exclude").Trim();
        if (!Path.IsPathRooted(excludePath)) excludePath = Path.Combine(_root, excludePath);
        Assert.Contains(".metadata/attempt-authority*", File.ReadAllText(excludePath));
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(_root, ".git", "objects", "pack"), "*.pack"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of an isolated temporary repository.
        }
    }

    private void RunGit(params string[] arguments)
    {
        var result = Git(arguments);
        Assert.True(result.Code == 0, result.Error);
    }

    private string GitOutput(params string[] arguments)
    {
        var result = Git(arguments);
        Assert.True(result.Code == 0, result.Error);
        return result.Output;
    }

    private (int Code, string Output, string Error) Git(IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);
        return (process.ExitCode, output, error);
    }
}
