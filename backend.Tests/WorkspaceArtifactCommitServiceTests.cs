using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

public sealed class WorkspaceArtifactCommitServiceTests : IDisposable
{
    private readonly string _root;
    private readonly WorkspaceArtifactCommitService _service;

    public WorkspaceArtifactCommitServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atp-workspace-artifacts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        RunGit(_root, "init", "-q", "-b", "main");
        RunGit(_root, "config", "user.name", "test");
        RunGit(_root, "config", "user.email", "test@example.com");
        File.WriteAllText(Path.Combine(_root, "README.md"), "seed\n");
        RunGit(_root, "add", "README.md");
        RunGit(_root, "commit", "-q", "-m", "seed");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _root })
            .Build();
        _service = new WorkspaceArtifactCommitService(
            config,
            NullLogger<WorkspaceArtifactCommitService>.Instance);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(_root, recursive: true);
        }
        catch { /* best-effort */ }
    }

    [Fact]
    public void RunBoundaryCommit_StagesOnlyTheJobFolder()
    {
        var job = JobFolder("ASS-1");
        Directory.CreateDirectory(Path.Combine(job, "logs"));
        File.WriteAllText(Path.Combine(job, "code-review.md"), "review\n");
        File.WriteAllText(Path.Combine(job, "pipeline-execution.json"), PipelineJson("aspect-code-quality", "Warn"));
        File.WriteAllText(Path.Combine(job, "logs", "session-events.jsonl"), "{}\n");

        var foreign = JobFolder("ASS-2");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "status.md"), "foreign\n");
        RunGit(_root, "add", Relative(Path.Combine(foreign, "status.md")));

        var result = _service.TryCommitRunBoundary(
            _root,
            "ASS-1",
            beforeMoveFolderPath: null,
            afterMoveFolderPath: job,
            ReviewDecisionKind.Reissue);

        Assert.True(result.Success, result.Error);
        Assert.True(result.DidCommit);
        Assert.Equal(1, result.RunIndex);
        Assert.Equal("aspect-code-quality=warn", result.Steps);

        var committed = RunGitCapture(_root, "show", "--name-only", "--pretty=format:", "HEAD")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        Assert.Contains(Relative(Path.Combine(job, "code-review.md")), committed);
        Assert.Contains(Relative(Path.Combine(job, "pipeline-execution.json")), committed);
        Assert.DoesNotContain(Relative(Path.Combine(foreign, "status.md")), committed);

        var status = RunGitCapture(_root, "status", "--porcelain=v1");
        Assert.Contains("ASS-2/status.md", status.Replace('\\', '/'));

        var message = RunGitCapture(_root, "log", "-1", "--format=%B");
        Assert.Contains("Run-Index: 1", message);
        Assert.Contains("Verdict: reissue", message);
        Assert.Contains("Steps: aspect-code-quality=warn", message);
    }

    [Fact]
    public void RunBoundaryCommit_TwoRunsProduceTwoFollowHistoryEntries()
    {
        var job = JobFolder("ASS-7");
        Directory.CreateDirectory(Path.Combine(job, "logs"));
        var codeReview = Path.Combine(job, "code-review.md");
        var sessions = Path.Combine(job, "logs", "session-events.jsonl");

        File.WriteAllText(codeReview, "run 1 review\n");
        File.WriteAllText(sessions, "{}\n");
        File.WriteAllText(Path.Combine(job, "pipeline-execution.json"), PipelineJson("aspect-code-quality", "Warn"));
        var first = _service.TryCommitRunBoundary(_root, "ASS-7", null, job, ReviewDecisionKind.Reissue);
        Assert.True(first.Success, first.Error);
        Assert.True(first.DidCommit);

        File.WriteAllText(codeReview, "run 2 review\n");
        File.AppendAllText(sessions, "{}\n");
        File.WriteAllText(Path.Combine(job, "pipeline-execution.json"), PipelineJson("aspect-code-quality", "Pass"));
        var second = _service.TryCommitRunBoundary(_root, "ASS-7", job, job, ReviewDecisionKind.AcceptAsDone);
        Assert.True(second.Success, second.Error);
        Assert.True(second.DidCommit);

        var history = RunGitCapture(_root, "log", "--follow", "--format=%B%x00", "--", Relative(codeReview))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(m => m.Contains("Run-Index:", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, history.Count);
        Assert.Contains(history, m => m.Contains("Run-Index: 1") && m.Contains("Verdict: reissue"));
        Assert.Contains(history, m => m.Contains("Run-Index: 2") && m.Contains("Verdict: accept"));
        Assert.Contains(history, m => m.Contains("Steps: aspect-code-quality=pass"));
    }

    private string JobFolder(string id) =>
        Path.Combine(_root, "projects", "agent-taskboard", "tasks", "001", id);

    private string Relative(string path) =>
        Path.GetRelativePath(_root, path).Replace('\\', '/');

    private static string PipelineJson(string stepId, string verdict) =>
        $$"""
        {
          "steps": [
            { "stepId": "{{stepId}}", "status": "Passed", "verdict": "{{verdict}}" }
          ]
        }
        """;

    private static void RunGit(string cwd, params string[] args)
    {
        var result = RunGitResult(cwd, args);
        Assert.Equal(0, result.Code);
    }

    private static string RunGitCapture(string cwd, params string[] args)
    {
        var result = RunGitResult(cwd, args);
        Assert.Equal(0, result.Code);
        return result.Out;
    }

    private static (string Out, string Err, int Code) RunGitResult(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(15_000);
        return (stdout, stderr, p.ExitCode);
    }
}
