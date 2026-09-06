using AgentStudio.Pipeline;
using Xunit;

namespace OrchestratorApi.Tests;

public sealed class RetentionCommitGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "retention-guard-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Commit_guard_refuses_class_c_above_50_mib()
    {
        var task = Path.Combine(_root, "projects", "P", "tasks", "7-archive", "T", "results");
        Directory.CreateDirectory(task);
        using (var file = File.Create(Path.Combine(task, "trace.zip"))) file.SetLength(50L * 1024 * 1024 + 1);
        Assert.Single(WorkspaceArtifactCommitService.FindCommitRefusals(_root, ["projects/P"]));
    }

    [Fact]
    public void Commit_guard_allows_limit_and_large_authority()
    {
        var task = Path.Combine(_root, "projects", "P", "tasks", "7-archive", "T");
        Directory.CreateDirectory(Path.Combine(task, "results"));
        using (var file = File.Create(Path.Combine(task, "results", "trace.zip"))) file.SetLength(50L * 1024 * 1024);
        using (var file = File.Create(Path.Combine(task, "task.json"))) file.SetLength(51L * 1024 * 1024);
        Assert.Empty(WorkspaceArtifactCommitService.FindCommitRefusals(_root, ["projects/P"]));
    }

    [Fact]
    public void Evidence_batcher_uses_shared_classifier_for_heavy_data()
    {
        Assert.True(WorkspaceEvidenceBatcher.IsHeavyArtifact("projects/P/tasks/3-progress/T/logs/cli-output.log"));
        Assert.False(WorkspaceEvidenceBatcher.IsHeavyArtifact("projects/P/tasks/3-progress/T/status.md"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
