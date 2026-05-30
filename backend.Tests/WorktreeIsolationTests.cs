using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Jobs;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Worktree-isolation rule (task: task-detail-worktree-isolation-and-multi-commit-support).
/// The Git working tree is shared across the whole repository, so the
/// "Accepted task work is sitting uncommitted" hygiene warning must
/// only fire when the task in question is the runner's currently-active
/// job for its project. Otherwise the working-tree dirt belongs to
/// whichever task the agent is currently editing, not the one whose
/// detail view the operator is looking at.
///
/// <para>
/// These tests pin the data-layer gate inside
/// <see cref="GitService.GetJobHygiene"/> so the warning is suppressed
/// at the source rather than only hidden in the UI. The matrix walks
/// each canonical lane plus the active / non-active flag.
/// </para>
/// </summary>
public class WorktreeIsolationTests : IDisposable
{
    private readonly string _tempDir;

    public WorktreeIsolationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "worktree-isolation-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best-effort */ }
    }

    [Theory]
    [InlineData("4-auto-review", false)]
    [InlineData("5-human-review", false)]
    [InlineData("6-completed", false)]
    [InlineData("7-archive", false)]
    public void NonActiveTask_PostProgressLane_DirtyTree_DoesNotFlagAcceptedUncommitted(string lane, bool isActive)
    {
        var hygiene = SeedAndMeasure(lane, dirtyTree: true, isActiveJob: isActive);

        // The matrix walks each post-progress lane that historically raised
        // the false alarm. With isActive=false the gate should suppress the
        // warning even though the lane and tree state would otherwise match.
        Assert.NotNull(hygiene.Job);
        Assert.True(hygiene.IsDirty,
            "fixture should have dirtied the tree so we are testing the gate, not a clean repo");
        Assert.False(hygiene.Job!.AcceptedTaskUncommitted,
            $"non-active task in {lane} must not raise AcceptedTaskUncommitted - the dirty tree belongs to whichever task is currently active");
    }

    [Theory]
    [InlineData("3-progress")]
    public void ActiveTask_InProgressLane_DirtyTree_DoesNotFlagAcceptedUncommitted_BecauseProgressIsNotPostProgress(string lane)
    {
        // The active task lives in 3-progress. The hygiene flag is named for
        // POST-progress work that landed dirty; it never fires on 3-progress
        // even when the task is the active one. (The git-pane on the active
        // task continues to show the live working tree separately - that
        // surface is not gated by this flag.)
        var hygiene = SeedAndMeasure(lane, dirtyTree: true, isActiveJob: true);

        Assert.NotNull(hygiene.Job);
        Assert.False(hygiene.Job!.AcceptedTaskUncommitted);
    }

    [Theory]
    [InlineData("4-auto-review")]
    [InlineData("5-human-review")]
    [InlineData("6-completed")]
    [InlineData("7-archive")]
    public void ActiveTask_PostProgressLane_DirtyTree_FlagsAcceptedUncommitted(string lane)
    {
        // The legitimate case the original warning was designed for: the
        // task is the runner's active job AND it has already moved into a
        // post-progress lane AND the working tree is still dirty. Here the
        // warning is correct - the agent's edits demonstrably belong to
        // this task. This is the only configuration that should fire.
        var hygiene = SeedAndMeasure(lane, dirtyTree: true, isActiveJob: true);

        Assert.NotNull(hygiene.Job);
        Assert.True(hygiene.Job!.AcceptedTaskUncommitted);
    }

    [Theory]
    [InlineData("4-auto-review", false)]
    [InlineData("4-auto-review", true)]
    [InlineData("5-human-review", false)]
    [InlineData("5-human-review", true)]
    [InlineData("6-completed", false)]
    [InlineData("6-completed", true)]
    public void CleanTree_NeverFlagsAcceptedUncommitted_RegardlessOfActive(string lane, bool isActive)
    {
        var hygiene = SeedAndMeasure(lane, dirtyTree: false, isActiveJob: isActive);

        Assert.NotNull(hygiene.Job);
        Assert.False(hygiene.IsDirty);
        Assert.False(hygiene.Job!.AcceptedTaskUncommitted);
    }

    private GitHygieneStatus SeedAndMeasure(string lane, bool dirtyTree, bool isActiveJob)
    {
        var repo = SeedRepo($"{lane.Replace('-', '_')}-{(isActiveJob ? "active" : "noact")}-{(dirtyTree ? "dirty" : "clean")}");
        var jobsRoot = Path.Combine(repo, ".orchestrator", "jobs", lane);
        var jobFolder = Path.Combine(jobsRoot, "fixture-task");
        Directory.CreateDirectory(jobFolder);
        File.WriteAllText(Path.Combine(jobFolder, "job.json"), $$"""
            { "id": "fixture-task", "title": "Fixture", "state": "{{lane}}",
              "agent": "claude", "createdAt": "2026-05-09T10:00:00Z" }
            """);
        File.WriteAllText(Path.Combine(jobFolder, "prompt.md"), "fixture");

        // Keep the orchestrator workspace out of the working tree so the
        // job folder seeding doesn't bleed into the dirty/clean assertion.
        File.WriteAllText(Path.Combine(repo, ".gitignore"), ".orchestrator/\n");
        RunGit(repo, "add -A");
        RunGit(repo, "commit -q -m gitignore");

        if (dirtyTree)
        {
            File.WriteAllText(Path.Combine(repo, "leak.txt"), "agent edit");
        }

        var git = BuildGitService(("Fixture", repo));
        return git.GetJobHygiene("fixture-task", null, isActiveJob: isActiveJob);
    }

    private string SeedRepo(string name)
    {
        var repo = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(repo);
        RunGit(repo, "init -q -b main");
        RunGit(repo, "config user.email test@example.com");
        RunGit(repo, "config user.name test");
        File.WriteAllText(Path.Combine(repo, "README.md"), "seed");
        RunGit(repo, "add -A");
        RunGit(repo, "commit -q -m seed");
        return repo;
    }

    private GitService BuildGitService(params (string Name, string RootPath)[] entries)
    {
        var dict = new Dictionary<string, string?>();
        for (var i = 0; i < entries.Length; i++)
        {
            dict[$"WatchPaths:{i}:Name"] = entries[i].Name;
            dict[$"WatchPaths:{i}:RootPath"] = entries[i].RootPath;
            dict[$"WatchPaths:{i}:Path"] = Path.Combine(entries[i].RootPath, ".orchestrator", "jobs");
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return new GitService(NullLogger<GitService>.Instance, scanner, config);
    }

    private static void RunGit(string cwd, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit(15_000);
    }
}
