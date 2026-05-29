using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Clients;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Registry;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Regression cover for the 2026-05-26 bug where the Task-commit panel
/// surfaced an unrelated SHA on a task. The agent's CLI run produced
/// nothing, but the project's working tree was carrying leftover dirty
/// changes from an earlier context. The progress->auto-review auto-commit
/// swept those changes into a brand-new commit and stamped its SHA onto
/// the job, producing a commit whose subject described work that was
/// nothing to do with that task.
///
/// <para>
/// The fix - the per-file mtime guard in
/// <see cref="JobTransitionService.IsWorkingTreeAttributableToTask"/> -
/// refuses to bundle dirty paths whose last-write times all predate the
/// task's first session event. This file pins both branches: the unrelated
/// dirty change is skipped, the agent-authored dirty change is committed
/// and stamped on the job.
/// </para>
/// </summary>
public sealed class JobTransitionAutoCommitAttributionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _watchPath;
    private readonly string _repoRoot;
    private const string ProjectName = "demo";

    public JobTransitionAutoCommitAttributionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "atp-attribution-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_tempDir, "jobs");
        _repoRoot = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(_tempDir);
        foreach (var state in JobStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));
        Directory.CreateDirectory(_repoRoot);

        RunGit(_repoRoot, "init", "-q", "-b", "main");
        RunGit(_repoRoot, "config", "user.email", "test@example.com");
        RunGit(_repoRoot, "config", "user.name", "test");
        File.WriteAllText(Path.Combine(_repoRoot, "AGENTS.md"), "seed line\n");
        RunGit(_repoRoot, "add", "-A");
        RunGit(_repoRoot, "commit", "-q", "-m", "seed");
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
    public async Task MoveProgressToAutoReview_PreExistingDirtyChange_DoesNotStampCommit()
    {
        // 1) An external party (operator, earlier task) leaves AGENTS.md dirty
        //    BEFORE this task ever starts. mtime is anchored well in the past.
        var preExisting = Path.Combine(_repoRoot, "AGENTS.md");
        File.WriteAllText(preExisting, "seed line\nexternal edit\n");
        File.SetLastWriteTimeUtc(preExisting, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        // 2) Two adjacent progress jobs A and B; A is about to transition.
        WriteJob(JobStates.Progress, "task-a");
        WriteJob(JobStates.Progress, "task-b");

        // 3) Task A's CLI run starts AFTER the dirty change already exists.
        AppendSessionEvent("task-a", DateTime.UtcNow);

        var deps = BuildDeps();
        var outcome = await deps.Transitions.MoveAsync("task-a", JobStates.AutoReview, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);

        // Working tree changes predate the run -> guard refuses the auto-commit.
        // No SHA stamped on A. No SHA stamped on B (it never transitioned).
        var movedA = ReadJob(JobStates.AutoReview, "task-a");
        var movedB = ReadJob(JobStates.Progress, "task-b");
        Assert.Null(movedA?.Commit);
        Assert.Empty(movedA?.Commits ?? new List<JobCommitInfo>());
        Assert.Null(movedB?.Commit);
        Assert.Empty(movedB?.Commits ?? new List<JobCommitInfo>());
    }

    [Fact]
    public async Task MoveProgressToAutoReview_AgentEditDuringRun_StampsCommit()
    {
        // 1) Task A's CLI starts cleanly (no prior dirty state).
        WriteJob(JobStates.Progress, "task-a");
        WriteJob(JobStates.Progress, "task-b");
        var firstActivity = DateTime.UtcNow;
        AppendSessionEvent("task-a", firstActivity);

        // 2) Agent dirties a file DURING the run (mtime > first activity).
        var edited = Path.Combine(_repoRoot, "work.txt");
        File.WriteAllText(edited, "agent change\n");
        File.SetLastWriteTimeUtc(edited, firstActivity.AddSeconds(30));

        // 3) Transition to auto-review.
        var deps = BuildDeps();
        var outcome = await deps.Transitions.MoveAsync("task-a", JobStates.AutoReview, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);

        // The agent edit DOES qualify for attribution -> auto-commit fires
        // and stamps a real SHA on A. B is still untouched.
        var movedA = ReadJob(JobStates.AutoReview, "task-a");
        var movedB = ReadJob(JobStates.Progress, "task-b");
        Assert.NotNull(movedA?.Commit);
        Assert.False(string.IsNullOrWhiteSpace(movedA!.Commit!.Sha));
        Assert.Null(movedB?.Commit);
    }

    [Fact]
    public async Task MoveProgressToAutoReview_NoSessionEvents_LegacyBehaviorAutoCommits()
    {
        // Legacy job folders that pre-date session-events still need the
        // auto-commit to fire. The guard only kicks in when we have a first-
        // activity timestamp to compare mtimes against; without one we defer
        // to AutoCommitAsync's own clean-tree short-circuit.
        WriteJob(JobStates.Progress, "legacy-task");
        var dirty = Path.Combine(_repoRoot, "legacy-change.txt");
        File.WriteAllText(dirty, "legacy edit\n");

        var deps = BuildDeps();
        var outcome = await deps.Transitions.MoveAsync("legacy-task", JobStates.AutoReview, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var moved = ReadJob(JobStates.AutoReview, "legacy-task");
        Assert.NotNull(moved?.Commit);
    }

    private Deps BuildDeps()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _repoRoot,
                ["WatchPaths:0:RepositoryPath"] = _repoRoot,
                ["TaskRepository"] = _watchPath
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new JobScannerService(config, NullLogger<JobScannerService>.Instance, summary);
        var states = new JobStateMachine(scanner, NullLogger<JobStateMachine>.Instance);
        var mutations = new JobMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new JobChangeNotifier(NullLogger<JobChangeNotifier>.Instance),
            NullLogger<JobMutationService>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config, prompts);
        var sessions = new JobSessionLog(scanner, NullLogger<JobSessionLog>.Instance);
        var transitions = new JobTransitionService(
            scanner, states, mutations, git, settings,
            NullLogger<JobTransitionService>.Instance,
            sessions);
        return new Deps(scanner, transitions);
    }

    private void WriteJob(string state, string slug)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "job.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"copilot\"}}");
    }

    private void AppendSessionEvent(string slug, DateTime ts)
    {
        var logsDir = Path.Combine(_watchPath, JobStates.Progress, slug, "logs");
        Directory.CreateDirectory(logsDir);
        var line = JsonSerializer.Serialize(new SessionEvent
        {
            Ts = ts,
            Kind = "start",
            Cli = "copilot",
            HeadShaBefore = null,
            HeadShaAfter = null
        }) + Environment.NewLine;
        File.AppendAllText(Path.Combine(logsDir, "session-events.jsonl"), line, Encoding.UTF8);
    }

    private JobInfo? ReadJob(string state, string slug)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        if (!Directory.Exists(dir)) return null;
        // Force a fresh scan to pick up the post-transition stamp.
        var deps = BuildDeps();
        return deps.Scanner.FindJob(slug, _watchPath);
    }

    private static void RunGit(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        p.WaitForExit(15_000);
    }

    private sealed record Deps(JobScannerService Scanner, JobTransitionService Transitions);
}
