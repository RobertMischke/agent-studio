using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Clients;
using OrchestratorApi.Services.Registry;
using OrchestratorApi.Services.Runner;
using OrchestratorApi.Services.Tasks;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// End-to-end regression for THE promise: a run produces a committed,
/// attributed change AND the task advances. This is the seam the
/// broken-commit-pipeline bug (2026-06-08) slipped through - there was deep
/// unit coverage of each piece (classifier, transition, attribution) but no
/// single test that walked outcome -> commit-landing -> lane-advance together.
///
/// <para>
/// Positive: a completed run whose agent edited a file is classified
/// <see cref="AgentOutcomeKind.Done"/>, the progress->auto-review transition
/// lands a real commit (repository HEAD moves), the commit is stamped and
/// attributed to the task, and the task is now in auto-review (advanced, not
/// stuck). Negative: an empty/failed-start run is NOT classified Done and,
/// with a clean working tree, the transition stamps no commit and HEAD does
/// not move - no false commit on a Fehlstart.
/// </para>
///
/// <para>
/// The harness is a lightweight, seedable git repo + watch-path store (no real
/// workspace, no CLI subprocess), matching the linked TaskAccess store goal so
/// the pipeline runs without heavy setup.
/// </para>
/// </summary>
public sealed class RunPipelineCommitAdvancedRegressionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _watchPath;
    private readonly string _repoRoot;
    private const string ProjectName = "demo";

    public RunPipelineCommitAdvancedRegressionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "atp-pipeline-e2e-" + Guid.NewGuid().ToString("N"));
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

    // The promise must hold for BOTH supported CLIs (task scope: "pro CLI:
    // codex UND claude"). The commit-landing/attribution/advance seam is
    // agent-agnostic, so the same walk is pinned for each agent string.
    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public async Task CompletedRunWithAgentEdit_CommitLands_IsAttributed_AndTaskAdvances(string agent)
    {
        var slug = "task-a-" + agent;

        // 1) Outcome stage: the run ended with a done sign-off.
        //    The pipeline only drives a run to auto-review when it is reviewable,
        //    so pin the classifier verdict that gates the advance.
        var runOutput = Lines(
            "I added the missing guard and a regression test.",
            "All set.",
            "[[TASK_DONE]]");
        var outcome = AgentOutcomeAnalyzer.Analyze(runOutput, status: "completed", durationSeconds: 120.0);
        Assert.Equal(AgentOutcomeKind.Done, outcome.Kind);
        Assert.True(outcome.MatchedSentinel);

        // 2) The task is in progress and its run started cleanly.
        WriteJob(TaskStates.Progress, slug, agent);
        var firstActivity = DateTime.UtcNow;
        AppendSessionEvent(slug, firstActivity, agent);

        // 3) The agent edited a file DURING the run (mtime > first activity).
        var edited = Path.Combine(_repoRoot, "work.txt");
        File.WriteAllText(edited, "agent change\n");
        File.SetLastWriteTimeUtc(edited, firstActivity.AddSeconds(30));

        var headBefore = HeadSha();

        // 4) Pipeline transition: progress -> auto-review.
        var deps = BuildDeps();
        var move = await deps.Transitions.MoveAsync(slug, TaskStates.AutoReview, _watchPath);
        Assert.Equal(MoveJobStatus.Success, move.Status);

        // Commit LANDED: repository HEAD moved.
        var headAfter = HeadSha();
        Assert.NotEqual(headBefore, headAfter);

        // Commit is stamped on the task and attributed (records the edited file).
        var moved = ReadJob(TaskStates.AutoReview, slug);
        Assert.NotNull(moved);
        Assert.NotNull(moved!.Commit);
        Assert.False(string.IsNullOrWhiteSpace(moved.Commit!.Sha));
        Assert.Equal(1, moved.Commit.FilesChanged);

        // Task ADVANCED: it now lives in auto-review, not stuck in progress.
        Assert.Equal(TaskStates.AutoReview, moved.State);
        Assert.Null(ReadJob(TaskStates.Progress, slug));
    }

    [Fact]
    public async Task EmptyFastExitRun_IsNotDone_AndStampsNoFalseCommit()
    {
        // 1) Outcome stage: an empty / failed-start run. It must NOT be Done -
        //    it is the EmptyFastExit shape, so the pipeline would never treat it
        //    as a clean completion.
        var outcome = AgentOutcomeAnalyzer.Analyze(new List<CliOutputLine>(), status: "completed", durationSeconds: 3.0, exitCode: 0);
        Assert.NotEqual(AgentOutcomeKind.Done, outcome.Kind);
        Assert.Equal(RunIssueKind.EmptyFastExit, outcome.IssueKind);

        // 2) The task is in progress; the run produced no working-tree change.
        WriteJob(TaskStates.Progress, "task-empty");
        AppendSessionEvent("task-empty", DateTime.UtcNow);

        var headBefore = HeadSha();

        // 3) Even if the transition runs, a clean tree must not fabricate a
        //    commit. This is the negative half of the promise: no false commit.
        var deps = BuildDeps();
        var move = await deps.Transitions.MoveAsync("task-empty", TaskStates.AutoReview, _watchPath);
        Assert.Equal(MoveJobStatus.Success, move.Status);

        // No commit stamped and HEAD did not move.
        var moved = ReadJob(TaskStates.AutoReview, "task-empty");
        Assert.NotNull(moved);
        Assert.Null(moved!.Commit);
        Assert.Empty(moved.Commits ?? new List<TaskCommitInfo>());
        Assert.Equal(headBefore, HeadSha());
    }

    // ---- harness ----------------------------------------------------------

    private static List<CliOutputLine> Lines(params string[] texts)
    {
        var ts = new DateTime(2026, 6, 8, 10, 0, 0, DateTimeKind.Utc);
        return texts.Select((t, i) => new CliOutputLine
        {
            Timestamp = ts.AddSeconds(i),
            Stream = "stdout",
            Text = t
        }).ToList();
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
        return new Deps(scanner, transitions, git, settings);
    }

    private void WriteJob(string state, string slug, string agent = "claude")
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"{agent}\"}}");
    }

    private void AppendSessionEvent(string slug, DateTime ts, string agent = "claude")
    {
        var logsDir = Path.Combine(_watchPath, TaskStates.Progress, slug, "logs");
        Directory.CreateDirectory(logsDir);
        var line = JsonSerializer.Serialize(new SessionEvent
        {
            Ts = ts,
            Kind = "start",
            Cli = agent,
            HeadShaBefore = null,
            HeadShaAfter = null
        }) + Environment.NewLine;
        File.AppendAllText(Path.Combine(logsDir, "session-events.jsonl"), line, Encoding.UTF8);
    }

    private TaskInfo? ReadJob(string state, string slug)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        if (!Directory.Exists(dir)) return null;
        var deps = BuildDeps();
        return deps.Scanner.FindJob(slug, _watchPath);
    }

    private string HeadSha() => RunGitCapture(_repoRoot, "rev-parse", "HEAD").Trim();

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

    private static string RunGitCapture(string cwd, params string[] args)
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
        var so = p.StandardOutput.ReadToEnd();
        p.WaitForExit(15_000);
        return so;
    }

    private sealed record Deps(
        TaskScannerService Scanner,
        TaskTransitionService Transitions,
        GitService Git,
        ProjectSettingsService Settings);
}
