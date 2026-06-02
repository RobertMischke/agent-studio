using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Clients;
using OrchestratorApi.Services.Tasks;
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
/// <see cref="TaskTransitionService.IsWorkingTreeAttributableToTask"/> -
/// refuses to bundle dirty paths whose last-write times all predate the
/// task's first session event. This file pins both branches: the unrelated
/// dirty change is skipped, the agent-authored dirty change is committed
/// and stamped on the job.
/// </para>
/// </summary>
public sealed class TaskTransitionAutoCommitAttributionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _watchPath;
    private readonly string _repoRoot;
    private const string ProjectName = "demo";

    public TaskTransitionAutoCommitAttributionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "atp-attribution-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_tempDir, "jobs");
        _repoRoot = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(_tempDir);
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));
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
        WriteJob(TaskStates.Progress, "task-a");
        WriteJob(TaskStates.Progress, "task-b");

        // 3) Task A's CLI run starts AFTER the dirty change already exists.
        AppendSessionEvent("task-a", DateTime.UtcNow);

        var deps = BuildDeps();
        var outcome = await deps.Transitions.MoveAsync("task-a", TaskStates.AutoReview, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);

        // Working tree changes predate the run -> guard refuses the auto-commit.
        // No SHA stamped on A. No SHA stamped on B (it never transitioned).
        var movedA = ReadJob(TaskStates.AutoReview, "task-a");
        var movedB = ReadJob(TaskStates.Progress, "task-b");
        Assert.Null(movedA?.Commit);
        Assert.Empty(movedA?.Commits ?? new List<TaskCommitInfo>());
        Assert.Null(movedB?.Commit);
        Assert.Empty(movedB?.Commits ?? new List<TaskCommitInfo>());
    }

    [Fact]
    public async Task MoveProgressToAutoReview_AgentEditDuringRun_StampsCommit()
    {
        // 1) Task A's CLI starts cleanly (no prior dirty state).
        WriteJob(TaskStates.Progress, "task-a");
        WriteJob(TaskStates.Progress, "task-b");
        var firstActivity = DateTime.UtcNow;
        AppendSessionEvent("task-a", firstActivity);

        // 2) Agent dirties a file DURING the run (mtime > first activity).
        var edited = Path.Combine(_repoRoot, "work.txt");
        File.WriteAllText(edited, "agent change\n");
        File.SetLastWriteTimeUtc(edited, firstActivity.AddSeconds(30));

        // 3) Transition to auto-review.
        var deps = BuildDeps();
        var outcome = await deps.Transitions.MoveAsync("task-a", TaskStates.AutoReview, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);

        // The agent edit DOES qualify for attribution -> auto-commit fires
        // and stamps a real SHA on A. B is still untouched.
        var movedA = ReadJob(TaskStates.AutoReview, "task-a");
        var movedB = ReadJob(TaskStates.Progress, "task-b");
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
        WriteJob(TaskStates.Progress, "legacy-task");
        var dirty = Path.Combine(_repoRoot, "legacy-change.txt");
        File.WriteAllText(dirty, "legacy edit\n");

        var deps = BuildDeps();
        var outcome = await deps.Transitions.MoveAsync("legacy-task", TaskStates.AutoReview, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var moved = ReadJob(TaskStates.AutoReview, "legacy-task");
        Assert.NotNull(moved?.Commit);
    }

    [Fact]
    public async Task MoveProgressToAutoReview_ReadOnlyMode_SkipsAutoCommit_LeavesTreeDirty()
    {
        // Read-only-Pipeline fuer planning/research: a planning / research run
        // skips every git side effect on the transition. Even an agent edit that
        // WOULD qualify for attribution (mtime > first activity) must NOT be
        // auto-committed - the runner reports the dirty tree as a containment
        // violation instead. Contrast with
        // MoveProgressToAutoReview_AgentEditDuringRun_StampsCommit (coding mode).
        WriteJob(TaskStates.Progress, "plan-task", mode: TaskModes.Planning);
        var firstActivity = DateTime.UtcNow;
        AppendSessionEvent("plan-task", firstActivity);

        var edited = Path.Combine(_repoRoot, "stray.txt");
        File.WriteAllText(edited, "agent wrote this in a read-only run\n");
        File.SetLastWriteTimeUtc(edited, firstActivity.AddSeconds(30));

        var deps = BuildDeps();
        var outcome = await deps.Transitions.MoveAsync("plan-task", TaskStates.AutoReview, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);

        // No commit was stamped (auto-commit + attribution were both skipped).
        var moved = ReadJob(TaskStates.AutoReview, "plan-task");
        Assert.Null(moved?.Commit);
        Assert.Empty(moved?.Commits ?? new List<TaskCommitInfo>());

        // And the stray edit is still uncommitted in the working tree, so the
        // runner's containment check can see and report it.
        var status = deps.Git.GetStatus("plan-task", _watchPath);
        Assert.True(status.IsRepo);
        Assert.Contains(status.Files, f => f.Path.EndsWith("stray.txt", StringComparison.Ordinal));
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
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config, prompts);
        var sessions = new TaskSessionLog(scanner, NullLogger<TaskSessionLog>.Instance);
        var transitions = new TaskTransitionService(
            scanner, states, mutations, git, settings,
            NullLogger<TaskTransitionService>.Instance,
            sessions);
        return new Deps(scanner, transitions, git);
    }

    private void WriteJob(string state, string slug, string? mode = null)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        var modeField = mode == null ? "" : $",\"mode\":\"{mode}\"";
        File.WriteAllText(Path.Combine(dir, "job.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"copilot\"{modeField}}}");
    }

    private void AppendSessionEvent(string slug, DateTime ts)
    {
        var logsDir = Path.Combine(_watchPath, TaskStates.Progress, slug, "logs");
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

    private TaskInfo? ReadJob(string state, string slug)
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

    private sealed record Deps(TaskScannerService Scanner, TaskTransitionService Transitions, GitService Git);
}
