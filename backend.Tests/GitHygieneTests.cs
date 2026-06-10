using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Hygiene-detection tests for <see cref="GitService.GetProjectHygiene"/>
/// and <see cref="GitService.GetJobHygiene"/>. The fixture builds a real
/// repo on disk (no mocks) so the asserts pin actual git output, not the
/// service's interpretation of a fake. The five required cases from the
/// repository-hygiene-accepted-task-commits task contract are covered:
/// clean, unstaged, untracked, ahead-of-upstream, job-with-recorded-commit.
/// </summary>
public class GitHygieneTests : IDisposable
{
    private readonly string _tempDir;

    public GitHygieneTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "git-hygiene-tests-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void Hygiene_CleanRepo_NotDirty()
    {
        var repo = SeedRepo("clean");
        var git = BuildGitService(("Clean", repo));

        var hygiene = git.GetProjectHygiene("Clean");

        Assert.True(hygiene.IsRepo);
        Assert.False(hygiene.IsDirty);
        Assert.Equal(0, hygiene.StagedCount);
        Assert.Equal(0, hygiene.UnstagedCount);
        Assert.Equal(0, hygiene.UntrackedCount);
        Assert.Equal("main", hygiene.Branch);
        Assert.False(string.IsNullOrEmpty(hygiene.LastCommitSha));
        Assert.NotNull(hygiene.LastCommitAtUtc);
    }

    [Fact]
    public void Hygiene_UnstagedChange_IsDirty()
    {
        var repo = SeedRepo("unstaged");
        File.WriteAllText(Path.Combine(repo, "README.md"), "seed\nmodified");
        var git = BuildGitService(("Un", repo));

        var hygiene = git.GetProjectHygiene("Un");

        Assert.True(hygiene.IsDirty);
        Assert.Equal(0, hygiene.StagedCount);
        Assert.Equal(1, hygiene.UnstagedCount);
        Assert.Equal(0, hygiene.UntrackedCount);
    }

    [Fact]
    public void Hygiene_UntrackedFile_IsDirty()
    {
        var repo = SeedRepo("untracked");
        File.WriteAllText(Path.Combine(repo, "new.txt"), "untracked");
        var git = BuildGitService(("Un", repo));

        var hygiene = git.GetProjectHygiene("Un");

        Assert.True(hygiene.IsDirty);
        Assert.Equal(0, hygiene.StagedCount);
        Assert.Equal(0, hygiene.UnstagedCount);
        Assert.Equal(1, hygiene.UntrackedCount);
    }

    [Fact]
    public void Hygiene_AheadOfUpstream_ReportsAheadCount()
    {
        // Bare "remote" + clone with one local commit beyond origin/main.
        var bare = Path.Combine(_tempDir, "remote.git");
        Directory.CreateDirectory(bare);
        RunGit(bare, "init -q --bare -b main");

        var repo = Path.Combine(_tempDir, "ahead");
        Directory.CreateDirectory(repo);
        RunGit(repo, "init -q -b main");
        RunGit(repo, "config user.email test@example.com");
        RunGit(repo, "config user.name test");
        File.WriteAllText(Path.Combine(repo, "seed.txt"), "1");
        RunGit(repo, "add -A");
        RunGit(repo, "commit -q -m seed");
        RunGit(repo, $"remote add origin \"{bare}\"");
        RunGit(repo, "push -q -u origin main");
        // Local extra commit, not pushed.
        File.WriteAllText(Path.Combine(repo, "extra.txt"), "2");
        RunGit(repo, "add -A");
        RunGit(repo, "commit -q -m extra");

        var git = BuildGitService(("Ahead", repo));
        var hygiene = git.GetProjectHygiene("Ahead");

        Assert.True(hygiene.HasUpstream);
        Assert.Equal(1, hygiene.Ahead);
        Assert.Equal(0, hygiene.Behind);
        Assert.False(hygiene.IsDirty);
    }

    [Fact]
    public void TaskHygiene_WithRecordedCommit_ReportsCommitPresent()
    {
        var repo = SeedRepo("with-job");
        // Seed a watched-target-style job folder under .orchestrator/jobs/4-auto-review.
        var jobsRoot = Path.Combine(repo, ".orchestrator", "jobs", TaskStates.AutoReview);
        var jobFolder = Path.Combine(jobsRoot, "demo-task");
        Directory.CreateDirectory(jobFolder);
        var jobJson = $$"""
            {
              "id": "demo-task",
              "title": "Demo task",
              "state": "{{TaskStates.AutoReview}}",
              "agent": "claude",
              "createdAt": "2026-05-06T10:00:00Z",
              "commit": {
                "sha": "0123456789abcdef0123456789abcdef01234567",
                "shortSha": "0123456",
                "message": "feat: demo",
                "filesChanged": 1,
                "files": ["README.md"],
                "at": "2026-05-06T10:00:01Z"
              }
            }
            """;
        File.WriteAllText(Path.Combine(jobFolder, "task.json"), jobJson);
        File.WriteAllText(Path.Combine(jobFolder, "prompt.md"), "demo");

        // Keep the orchestrator workspace out of the working tree so the
        // job folder seeding doesn't leave the repo dirty.
        File.WriteAllText(Path.Combine(repo, ".gitignore"), ".orchestrator/\n");
        RunGit(repo, "add -A");
        RunGit(repo, "commit -q -m gitignore");

        var git = BuildGitService(("WithJob", repo));
        var hygiene = git.GetJobHygiene("demo-task", null);

        Assert.NotNull(hygiene.Job);
        Assert.True(hygiene.Job!.TaskInfoCommitPresent);
        Assert.Equal(TaskStates.AutoReview, hygiene.Job.State);
        // Repo is clean, so accepted-uncommitted is false even though the
        // job sits in a post-progress lane.
        Assert.False(hygiene.Job.AcceptedTaskUncommitted);
    }

    [Fact]
    public void TaskHygiene_AcceptedLaneWithDirtyTree_FlagsAcceptedUncommitted()
    {
        var repo = SeedRepo("dirty-after-accept");
        var jobsRoot = Path.Combine(repo, ".orchestrator", "jobs", TaskStates.HumanReview);
        var jobFolder = Path.Combine(jobsRoot, "leaky-task");
        Directory.CreateDirectory(jobFolder);
        File.WriteAllText(Path.Combine(jobFolder, "task.json"), $$"""
            { "id": "leaky-task", "title": "Leaky", "state": "{{TaskStates.HumanReview}}",
              "agent": "claude", "createdAt": "2026-05-06T10:00:00Z" }
            """);
        File.WriteAllText(Path.Combine(jobFolder, "prompt.md"), "leaky");
        // Dirty the working tree after acceptance.
        File.WriteAllText(Path.Combine(repo, "leaked.txt"), "evidence");

        var git = BuildGitService(("Leaky", repo));
        // Worktree-isolation rule: AcceptedTaskUncommitted only fires
        // when the task is the runner's currently-active job. The legitimate
        // "accepted task left dirty" warning is the active-task case here;
        // the non-active case is covered separately by WorktreeIsolationTests.
        var hygiene = git.GetJobHygiene("leaky-task", null, isActiveJob: true);

        Assert.NotNull(hygiene.Job);
        Assert.False(hygiene.Job!.TaskInfoCommitPresent);
        Assert.True(hygiene.Job.AcceptedTaskUncommitted);
        Assert.True(hygiene.IsDirty);
        Assert.True(hygiene.UntrackedCount >= 1);
    }

    [Fact]
    public void Hygiene_CachedAcrossCalls_RereadsAfterInvalidation()
    {
        var repo = SeedRepo("cache");
        var git = BuildGitService(("Cache", repo));

        var first = git.GetProjectHygiene("Cache");
        Assert.False(first.IsDirty);

        // Mutate the tree; cache should still report clean (3 s TTL).
        File.WriteAllText(Path.Combine(repo, "drop.txt"), "1");
        var second = git.GetProjectHygiene("Cache");
        Assert.False(second.IsDirty);

        // Invalidate explicitly and re-observe.
        git.InvalidateHygieneCache();
        var third = git.GetProjectHygiene("Cache");
        Assert.True(third.IsDirty);
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
