using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Jobs;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Surface-scope tests for <see cref="GitService.GetJobHygiene"/>. The
/// rule under test: per-task hygiene answers questions about THIS task
/// only. Repo-level signals (ahead of upstream, push pending, branch
/// behind, untracked files at repo root) must NOT be surfaced through
/// <see cref="JobHygieneContext"/>; they remain on the surrounding
/// <see cref="GitHygieneStatus"/> fields where the project-level
/// surface reads them. Approval and push are decoupled in the
/// workflow - "ahead of origin" is the repo's concern, not the task's.
/// </summary>
public class TaskDetailHygieneScopeTests : IDisposable
{
    private readonly string _tempDir;

    public TaskDetailHygieneScopeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "task-hygiene-scope-" + Guid.NewGuid().ToString("N"));
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
    [InlineData(TaskStates.AutoReview)]
    [InlineData(TaskStates.HumanReview)]
    [InlineData(TaskStates.Completed)]
    [InlineData(TaskStates.Archive)]
    public void JobHygiene_RepoAheadOfUpstream_TaskScopeStaysSilent(string lane)
    {
        var (repo, _) = SeedRepoAheadOfUpstream("ahead-" + lane.Replace("-", ""));
        SeedJobFolder(repo, "demo-task", lane, withCommit: true);

        var git = BuildGitService(("AheadProj", repo));
        var hygiene = git.GetJobHygiene("demo-task", null);

        // Repo-level signal IS present at the project layer of the
        // snapshot - the project-hygiene-badge reads from here.
        Assert.True(hygiene.HasUpstream);
        Assert.True(hygiene.Ahead > 0);

        // Per-task overlay must NOT echo the repo-level signal. The
        // task itself is in good shape: stamped commit, clean tree,
        // not the active job, not in any uncommitted state.
        Assert.NotNull(hygiene.Job);
        Assert.True(hygiene.Job!.JobInfoCommitPresent);
        Assert.False(hygiene.Job.AcceptedTaskUncommitted);
        // The previous "CommitUnpushed" field has been removed from
        // JobHygieneContext entirely; the property no longer exists on
        // the record so callers cannot accidentally surface push-pending
        // through the per-task surface.
        AssertNoLegacyPushFieldOnTaskOverlay(hygiene.Job);
    }

    [Fact]
    public void JobHygiene_NonActiveTaskInReview_DoesNotInheritDirtyTreeOnTaskOverlay()
    {
        // Working tree is dirty with changes that are not attributed
        // to this task. The task is in a review lane but is NOT the
        // runner's active job (no isActiveJob flag passed). The
        // task-scoped AcceptedTaskUncommitted must stay false; the
        // repo-level IsDirty stays true on the project layer for the
        // project surface to render.
        var repo = SeedRepo("dirty-no-active");
        SeedJobFolder(repo, "sleeper-task", TaskStates.HumanReview, withCommit: true);
        File.WriteAllText(Path.Combine(repo, "scratch.txt"), "wip from another task");

        var git = BuildGitService(("Dirty", repo));
        var hygiene = git.GetJobHygiene("sleeper-task", null /* isActiveJob defaults to false */);

        Assert.True(hygiene.IsDirty);
        Assert.NotNull(hygiene.Job);
        Assert.False(hygiene.Job!.AcceptedTaskUncommitted);
    }

    [Fact]
    public void RepoLevelHygieneSurface_StillCarriesAheadSignal_ForProjectBadge()
    {
        // Mirror image of the task-scope test: the project-level
        // GetProjectHygiene call must still surface the repo-wide
        // ahead/upstream signal that the project-hygiene-badge reads.
        var (repo, _) = SeedRepoAheadOfUpstream("ahead-project");
        var git = BuildGitService(("AheadProj", repo));

        var hygiene = git.GetProjectHygiene("AheadProj");

        Assert.True(hygiene.IsRepo);
        Assert.True(hygiene.HasUpstream);
        Assert.True(hygiene.Ahead > 0);
        Assert.Null(hygiene.Job); // project surface has no per-task overlay
    }

    private static void AssertNoLegacyPushFieldOnTaskOverlay(JobHygieneContext overlay)
    {
        // The legacy CommitUnpushed flag was removed when push-pending
        // migrated off the per-task surface. Reflection guard so a
        // future hand-back of that field trips this test.
        var prop = overlay.GetType().GetProperty("CommitUnpushed");
        Assert.Null(prop);
    }

    private string SeedRepo(string name)
    {
        var repo = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(repo);
        RunGit(repo, "init -q -b main");
        RunGit(repo, "config user.email test@example.com");
        RunGit(repo, "config user.name test");
        File.WriteAllText(Path.Combine(repo, ".gitignore"), ".orchestrator/\n");
        File.WriteAllText(Path.Combine(repo, "README.md"), "seed");
        RunGit(repo, "add -A");
        RunGit(repo, "commit -q -m seed");
        return repo;
    }

    private (string Repo, string Bare) SeedRepoAheadOfUpstream(string name)
    {
        var bare = Path.Combine(_tempDir, name + ".remote.git");
        Directory.CreateDirectory(bare);
        RunGit(bare, "init -q --bare -b main");

        var repo = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(repo);
        RunGit(repo, "init -q -b main");
        RunGit(repo, "config user.email test@example.com");
        RunGit(repo, "config user.name test");
        File.WriteAllText(Path.Combine(repo, ".gitignore"), ".orchestrator/\n");
        File.WriteAllText(Path.Combine(repo, "seed.txt"), "1");
        RunGit(repo, "add -A");
        RunGit(repo, "commit -q -m seed");
        RunGit(repo, $"remote add origin \"{bare}\"");
        RunGit(repo, "push -q -u origin main");
        // Two local commits beyond origin: simulates the "repo has
        // unpushed work" condition the user observed.
        File.WriteAllText(Path.Combine(repo, "extra1.txt"), "a");
        RunGit(repo, "add -A");
        RunGit(repo, "commit -q -m extra1");
        File.WriteAllText(Path.Combine(repo, "extra2.txt"), "b");
        RunGit(repo, "add -A");
        RunGit(repo, "commit -q -m extra2");
        return (repo, bare);
    }

    private static void SeedJobFolder(string repo, string slug, string lane, bool withCommit)
    {
        var jobsRoot = Path.Combine(repo, ".orchestrator", "jobs", lane);
        var folder = Path.Combine(jobsRoot, slug);
        Directory.CreateDirectory(folder);
        var commitBlock = withCommit
            ? """
              ,
              "commit": {
                "sha": "0123456789abcdef0123456789abcdef01234567",
                "shortSha": "0123456",
                "message": "feat: demo",
                "filesChanged": 1,
                "files": ["README.md"],
                "at": "2026-05-09T10:00:01Z"
              }
              """
            : "";
        var jobJson = $$"""
            {
              "id": "{{slug}}",
              "title": "Demo",
              "state": "{{lane}}",
              "agent": "claude",
              "createdAt": "2026-05-09T10:00:00Z"{{commitBlock}}
            }
            """;
        File.WriteAllText(Path.Combine(folder, "job.json"), jobJson);
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "demo");
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
