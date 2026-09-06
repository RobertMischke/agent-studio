using System.Diagnostics;
using System.Text.Json;
using AgentStudio.TaskServer;
using Xunit;

namespace AgentStudio.TaskServer.Tests;

public sealed class RetentionCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "task-server-retention-" + Guid.NewGuid().ToString("N"));
    private string ArchiveRoot => _root + "-archive";

    public RetentionCommandTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        Directory.Delete(_root, true);
        if (Directory.Exists(ArchiveRoot))
            Directory.Delete(ArchiveRoot, true);
    }

    [Fact]
    public void ParserAcceptsRetentionSurface()
    {
        var command = TaskServerCommandLine.Parse(
            ["retention", "plan", "--workspace", _root, "--policy", "default", "--project", "Demo", "--json"]);
        Assert.Equal(TaskServerCommandKind.Retention, command.Kind);
        Assert.Equal("plan", command.Retention!.Operation);
        Assert.Equal("Demo", command.Retention.Project);
        Assert.True(command.Retention.Json);
    }

    [Fact]
    public async Task PlanDryRunWritesJsonReportWithoutMovingArtifacts()
    {
        var task = SeedTask("3-progress", DateTimeOffset.UtcNow.AddDays(-1));
        var result = await RunAsync("retention", "plan", "--workspace", _root, "--policy", "default", "--json");
        Assert.Equal(0, result.Code);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal(0, document.RootElement.GetProperty("actionCount").GetInt32());
        var reportPath = document.RootElement.GetProperty("reportPath").GetString()!;
        Assert.True(File.Exists(reportPath));
        Assert.True(File.Exists(Path.Combine(task, "logs", "cli-output.log")));
    }

    [Fact]
    public async Task Agt2739ScenarioPlanIsEmptyThenAgeingProducesOneArchiveAction()
    {
        var task = SeedTask("3-progress", DateTimeOffset.UtcNow);
        var initial = await RunAsync("retention", "plan", "--workspace", _root, "--json");
        using var initialJson = JsonDocument.Parse(initial.Output);
        Assert.Equal(0, initialJson.RootElement.GetProperty("actionCount").GetInt32());

        File.WriteAllText(Path.Combine(task, "task.json"), $$"""
            {"id":"id-1","key":"DEM-1","state":"7-archive","enteredLaneAt":"{{DateTimeOffset.UtcNow.AddDays(-31):O}}"}
            """);
        var aged = await RunAsync("retention", "plan", "--workspace", _root, "--json");
        using var agedJson = JsonDocument.Parse(aged.Output);
        Assert.Equal(1, agedJson.RootElement.GetProperty("actionCount").GetInt32());
        Assert.Equal(1, agedJson.RootElement.GetProperty("byRule")[0].GetProperty("actions").GetInt32());
    }

    [Fact]
    public async Task ApplyArchivesAndCommitsEachChangedProject()
    {
        var task = SeedTask("7-archive", DateTimeOffset.UtcNow.AddDays(-31));
        File.WriteAllLines(Path.Combine(task, "logs", "cli-output.log"),
            Enumerable.Range(1, 10_000).Select(index => $"output line {index}"));
        RunGit("init", "-q", "-b", "main");
        RunGit("config", "user.name", "test");
        RunGit("config", "user.email", "test@example.com");
        RunGit("add", ".");
        RunGit("commit", "-q", "-m", "seed");

        var result = await RunAsync("retention", "apply", "--workspace", _root, "--json");

        Assert.Equal(0, result.Code);
        using var report = JsonDocument.Parse(result.Output);
        Assert.Equal(1, report.RootElement.GetProperty("execution").GetProperty("archivedTasks").GetInt32());
        Assert.False(File.Exists(Path.Combine(task, "logs", "cli-output.log")));
        Assert.True(File.Exists(Path.Combine(task, "archive-manifest.json")));
        Assert.True(Directory.EnumerateFiles(ArchiveRoot, "payload.zip", SearchOption.AllDirectories).Any());
        Assert.Contains("retention: archived 1 tasks", Git("log", "--format=%s"));
        Assert.True(report.RootElement.GetProperty("before").GetProperty("hotTaskBytes").GetInt64()
                    > report.RootElement.GetProperty("after").GetProperty("hotTaskBytes").GetInt64());
    }

    private string SeedTask(string lane, DateTimeOffset enteredAt)
    {
        var task = Path.Combine(_root, "projects", "Demo", "tasks", lane, "demo-one");
        Directory.CreateDirectory(Path.Combine(task, "logs"));
        File.WriteAllText(Path.Combine(task, "task.json"), $$"""
            {"id":"id-1","key":"DEM-1","state":"{{lane}}","enteredLaneAt":"{{enteredAt:O}}"}
            """);
        File.WriteAllText(Path.Combine(task, "logs", "cli-output.log"), "output");
        return task;
    }

    private static async Task<(int Code, string Output, string Error)> RunAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["ARCHIVE_PATH"] = arguments.Length > 0
            ? Path.GetFullPath(arguments.SkipWhile(value => value != "--workspace").Skip(1).FirstOrDefault() ?? Path.GetTempPath()) + "-archive"
            : Path.Combine(Path.GetTempPath(), "agent-taskboard-archive-test");
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, output, error);
    }

    private void RunGit(params string[] arguments)
    {
        var result = Git(arguments);
        Assert.DoesNotContain("fatal:", result, StringComparison.OrdinalIgnoreCase);
    }

    private string Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return output + error;
    }
}
