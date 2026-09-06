using System.Text.Json;
using AgentStudio.TaskServer;
using Xunit;

namespace TaskServer.Tests;

public sealed class RetentionCliTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "retention-cli-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Dry_run_fixture_is_empty_then_reports_one_action_after_ageing()
    {
        var workspace = Path.Combine(_root, "workspace");
        var task = Path.Combine(workspace, "projects", "PROJ-1", "tasks", "7-archive", "AGT-2739");
        Directory.CreateDirectory(Path.Combine(task, "logs"));
        await WriteTaskAsync(task, DateTimeOffset.UtcNow.AddDays(-1));
        await File.WriteAllTextAsync(Path.Combine(task, "logs", "cli-output.log"), "output");

        var firstOutput = new StringWriter();
        var first = await RetentionCli.RunAsync(
            Options(workspace), firstOutput, new StringWriter());
        Assert.Equal(0, first);
        using (var document = JsonDocument.Parse(firstOutput.ToString()))
            Assert.Equal(0, document.RootElement.GetProperty("plan").GetProperty("actionCount").GetInt32());

        await WriteTaskAsync(task, DateTimeOffset.UtcNow.AddDays(-31));
        var secondOutput = new StringWriter();
        var second = await RetentionCli.RunAsync(
            Options(workspace), secondOutput, new StringWriter());
        Assert.Equal(0, second);
        using var aged = JsonDocument.Parse(secondOutput.ToString());
        Assert.Equal(1, aged.RootElement.GetProperty("plan").GetProperty("actionCount").GetInt32());
        Assert.Equal("archiveHeavy", aged.RootElement.GetProperty("plan").GetProperty("tasks")[0].GetProperty("actions")[0].GetProperty("kind").GetString());
    }

    [Fact]
    public void Parses_retention_command_contract()
    {
        var command = TaskServerCommandLine.Parse([
            "retention", "apply", "--workspace", "/data/workspace", "--policy", "default",
            "--project", "PROJ-1", "--json",
        ]);
        Assert.Equal(TaskServerCommandKind.Retention, command.Kind);
        Assert.Equal("apply", command.Retention!.Operation);
        Assert.Equal("PROJ-1", command.Retention.Project);
        Assert.True(command.Retention.Json);
    }

    [Fact]
    public async Task Apply_moves_heavy_file_writes_excerpt_manifest_report_and_audit()
    {
        var workspace = Path.Combine(_root, "apply-workspace");
        var task = Path.Combine(workspace, "projects", "PROJ-1", "tasks", "7-archive", "AGT-2739");
        Directory.CreateDirectory(Path.Combine(task, "logs"));
        await WriteTaskAsync(task, DateTimeOffset.UtcNow.AddDays(-31));
        await File.WriteAllTextAsync(Path.Combine(task, "status.md"), "status");
        await File.WriteAllTextAsync(Path.Combine(task, "logs", "cli-output.log"), "ERROR failed with exit code 1");
        Directory.CreateDirectory(Path.Combine(workspace, "logs", "bus"));
        await File.WriteAllTextAsync(Path.Combine(workspace, "logs", "bus", "current.jsonl"), "event");
        Git(workspace, "init", "-q", "-b", "main");
        Git(workspace, "add", "-A");
        Git(workspace, "-c", "user.name=test", "-c", "user.email=test@local", "commit", "-q", "-m", "fixture");
        var archive = Path.Combine(_root, "apply-archive");
        var output = new StringWriter();

        var exit = await RetentionCli.RunAsync(
            new RetentionCommandOptions("apply", workspace, "default", null, null, archive, null, null, true),
            output, new StringWriter());

        Assert.Equal(0, exit);
        Assert.False(File.Exists(Path.Combine(task, "logs", "cli-output.log")));
        Assert.True(File.Exists(Path.Combine(task, ".retention-excerpts", "logs-cli-output.log.md")));
        Assert.True(File.Exists(Path.Combine(task, "archive-manifest.json")));
        Assert.True(File.Exists(Path.Combine(workspace, ".metadata", "retention-audit.jsonl")));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(workspace, ".metadata", "retention-reports"), "*-apply.json"));
        Assert.Single(Directory.EnumerateFiles(archive, "payload.zip", SearchOption.AllDirectories));
        Assert.Contains("logs/bus/", await File.ReadAllTextAsync(Path.Combine(workspace, ".gitignore")));
        Assert.Equal(string.Empty, Git(workspace, "ls-files", "logs/bus").Trim());
        Assert.Contains("retention: archived 1 tasks", Git(workspace, "log", "--format=%s"));
    }

    private static RetentionCommandOptions Options(string workspace) =>
        new("plan", workspace, "default", null, null, Path.Combine(Path.GetDirectoryName(workspace)!, "archive"), null, null, true);

    private static Task WriteTaskAsync(string task, DateTimeOffset terminalAt) =>
        File.WriteAllTextAsync(Path.Combine(task, "task.json"), JsonSerializer.Serialize(new
        {
            id = "task-id",
            key = "AGT-2739",
            state = "7-archive",
            terminalAt,
        }));

    private static string Git(string path, params string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
