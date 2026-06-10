using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Integration test for the per-run commits linkage. The user surfaced
/// this as a recurring fear: "ich will halt immer die task sehen, dass
/// das task in der Zukunft die verknüpften Git commits hat" - i.e. a
/// commit made during a run must surface on that run, durably, even
/// after the wall-clock window has long passed.
///
/// The test stands up a real git repository on disk, fakes a job folder
/// with a session-events.jsonl that captures HeadShaBefore + HeadShaAfter
/// (the deterministic linkage path), commits two files between the two
/// SHAs, then exercises the same code paths the
/// <c>/api/tasks/{id}/runs/{n}/commits</c> endpoint uses
/// (<see cref="RunTimelineBuilder.Build"/> +
/// <see cref="GitService.GetCommitsInShaRange"/>) and asserts both
/// commits show up on the run. A second test pins the wall-clock
/// fallback for older runs that didn't capture the SHAs.
/// </summary>
public class RunCommitsLinkageTests : IDisposable
{
    private readonly string _tempDir;

    public RunCommitsLinkageTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "run-commits-linkage-" + Guid.NewGuid().ToString("N"));
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
    public void ShaRange_CapturesEveryCommitMadeDuringTheRun()
    {
        var (repoRoot, jobFolder, jobId, watchPath) = SetupRepoAndJob();

        // Seed commit (pre-run).
        WriteFile(repoRoot, "README.md", "seed");
        RunGit(repoRoot, "add", "-A");
        RunGit(repoRoot, "commit", "-q", "-m", "seed");
        var headBefore = RunGitCapture(repoRoot, "rev-parse", "HEAD").Trim();

        // Two commits during the run window.
        WriteFile(repoRoot, "src/foo.cs", "// foo");
        RunGit(repoRoot, "add", "-A");
        RunGit(repoRoot, "commit", "-q", "-m", "feat: add foo");

        WriteFile(repoRoot, "src/bar.cs", "// bar");
        RunGit(repoRoot, "add", "-A");
        RunGit(repoRoot, "commit", "-q", "-m", "fix: bar");
        var headAfter = RunGitCapture(repoRoot, "rev-parse", "HEAD").Trim();

        // Persist a session event the way ProjectRunner does, including
        // the HEAD SHAs that the deterministic linkage path needs.
        AppendSessionEvent(jobFolder, new SessionEvent
        {
            Ts = DateTime.UtcNow.AddMinutes(-1),
            Kind = "start",
            Cli = "claude",
            Resumed = false,
            HeadShaBefore = headBefore,
            HeadShaAfter = headAfter
        });
        // And a matching [taskboard] Started/exited marker pair so the
        // timeline picks the run up with a status, not "unknown".
        AppendCliOutput(jobFolder, [
            ($"[taskboard] Started claude CLI (PID 1234)", DateTime.UtcNow.AddMinutes(-1).AddSeconds(1)),
            ("[taskboard] claude CLI exited: status=completed, exitCode=0, duration=55.0s", DateTime.UtcNow.AddSeconds(-1))
        ]);

        var (git, sessions, jobInfo) = BuildServices(repoRoot, watchPath, jobId);
        var events = sessions.ReadSessionEvents(jobId, watchPath);
        var lines = CliOutputLogParser.ParseFile(Path.Combine(jobInfo.FolderPath, "logs", "cli-output.log"));
        var timeline = RunTimelineBuilder.Build(events, lines, DateTime.UtcNow);

        var run = Assert.Single(timeline.Runs);
        Assert.Equal("completed", run.Status);
        Assert.Equal(headBefore, run.HeadShaBefore);
        Assert.Equal(headAfter, run.HeadShaAfter);

        var commits = git.GetCommitsInShaRange(jobId, watchPath, run.HeadShaBefore, run.HeadShaAfter);
        Assert.Equal(2, commits.Count);
        // Order: most recent first (git log default).
        Assert.Equal("fix: bar", commits[0].Subject);
        Assert.Equal("feat: add foo", commits[1].Subject);
        Assert.All(commits, c => Assert.True(c.FilesChanged >= 1));
        Assert.All(commits, c => Assert.True(c.Added >= 1));
    }

    [Fact]
    public void ShaRange_NoCommitsDuringRun_ReturnsEmpty()
    {
        var (repoRoot, jobFolder, jobId, watchPath) = SetupRepoAndJob();

        WriteFile(repoRoot, "README.md", "seed");
        RunGit(repoRoot, "add", "-A");
        RunGit(repoRoot, "commit", "-q", "-m", "seed");
        var head = RunGitCapture(repoRoot, "rev-parse", "HEAD").Trim();

        // Run with the same before/after SHA = the agent didn't commit.
        AppendSessionEvent(jobFolder, new SessionEvent
        {
            Ts = DateTime.UtcNow.AddMinutes(-1),
            Kind = "start",
            Cli = "claude",
            Resumed = false,
            HeadShaBefore = head,
            HeadShaAfter = head
        });

        var (git, _, _) = BuildServices(repoRoot, watchPath, jobId);
        var commits = git.GetCommitsInShaRange(jobId, watchPath, head, head);
        Assert.Empty(commits);
    }

    [Fact]
    public void WallClockFallback_StillWorksForRunsWithoutCapturedShas()
    {
        // Older runs (and runs against a project with no repo) won't have
        // SHAs persisted. The endpoint falls back to the wall-clock
        // window so existing data keeps surfacing commits. This test
        // pins that fallback so a future refactor can't quietly drop it.
        var (repoRoot, jobFolder, jobId, watchPath) = SetupRepoAndJob();

        WriteFile(repoRoot, "README.md", "seed");
        RunGit(repoRoot, "add", "-A");
        RunGit(repoRoot, "commit", "-q", "-m", "seed");

        var runStart = DateTime.UtcNow.AddSeconds(-5);
        WriteFile(repoRoot, "during.txt", "agent wrote this");
        RunGit(repoRoot, "add", "-A");
        RunGit(repoRoot, "commit", "-q", "-m", "chore: during");

        var (git, _, _) = BuildServices(repoRoot, watchPath, jobId);
        var commits = git.GetCommitsBetween(jobId, watchPath, runStart, DateTime.UtcNow);
        Assert.Contains(commits, c => c.Subject == "chore: during");
    }

    [Fact]
    public void FilesChanged_AggregatesAcrossCommits()
    {
        var (repoRoot, _, jobId, watchPath) = SetupRepoAndJob();

        WriteFile(repoRoot, "README.md", "seed");
        RunGit(repoRoot, "add", "-A");
        RunGit(repoRoot, "commit", "-q", "-m", "seed");
        var headBefore = RunGitCapture(repoRoot, "rev-parse", "HEAD").Trim();

        WriteFile(repoRoot, "a.txt", "alpha");
        RunGit(repoRoot, "add", "-A");
        RunGit(repoRoot, "commit", "-q", "-m", "c1");

        WriteFile(repoRoot, "b.txt", "beta");
        WriteFile(repoRoot, "a.txt", "alpha v2");
        RunGit(repoRoot, "add", "-A");
        RunGit(repoRoot, "commit", "-q", "-m", "c2");
        var headAfter = RunGitCapture(repoRoot, "rev-parse", "HEAD").Trim();

        var (git, _, _) = BuildServices(repoRoot, watchPath, jobId);
        var files = git.GetFilesChangedInShaRange(jobId, watchPath, headBefore, headAfter);

        // Both files appear once in the aggregated range diff (a.txt
        // touched twice -> one row with the net diff).
        Assert.Equal(2, files.Count);
        Assert.Contains(files, f => f.Path == "a.txt");
        Assert.Contains(files, f => f.Path == "b.txt");
    }

    [Fact]
    public void WorktreeIntegrationRange_ExcludesSiblingMergedWhileTaskRan()
    {
        var (repoRoot, _, jobId, watchPath) = SetupRepoAndJob();

        WriteFile(repoRoot, "README.md", "seed");
        RunGit(repoRoot, "add", "-A");
        RunGit(repoRoot, "commit", "-q", "-m", "seed");
        var runStartedAtDevelop = RunGitCapture(repoRoot, "rev-parse", "HEAD").Trim();

        RunGit(repoRoot, "checkout", "-q", "-b", "task/ASS-1690", runStartedAtDevelop);
        WriteFile(repoRoot, "task-a.txt", "task a work");
        RunGit(repoRoot, "add", "-A");
        RunGit(repoRoot, "commit", "-q", "-m", "feat: task ASS-1690");

        RunGit(repoRoot, "checkout", "-q", "main");
        RunGit(repoRoot, "checkout", "-q", "-b", "task/ASS-1685", runStartedAtDevelop);
        WriteFile(repoRoot, "task-b.txt", "sibling task work");
        RunGit(repoRoot, "add", "-A");
        RunGit(repoRoot, "commit", "-q", "-m", "fix: sibling ASS-1685");

        RunGit(repoRoot, "checkout", "-q", "main");
        RunGit(repoRoot, "merge", "--ff-only", "task/ASS-1685");
        var integrationBaseAtMergeStart = RunGitCapture(repoRoot, "rev-parse", "HEAD").Trim();

        RunGit(repoRoot, "checkout", "-q", "task/ASS-1690");
        RunGit(repoRoot, "rebase", "main");
        RunGit(repoRoot, "checkout", "-q", "main");
        RunGit(repoRoot, "merge", "--ff-only", "task/ASS-1690");
        var integratedTaskTip = RunGitCapture(repoRoot, "rev-parse", "HEAD").Trim();

        var (git, _, _) = BuildServices(repoRoot, watchPath, jobId);
        var broadDevelopRange = git.GetCommitsInShaRange(jobId, watchPath, runStartedAtDevelop, integratedTaskTip);
        Assert.Contains(broadDevelopRange, c => c.Subject == "fix: sibling ASS-1685");
        Assert.Contains(broadDevelopRange, c => c.Subject == "feat: task ASS-1690");

        var scopedIntegrationRange = git.GetCommitsInShaRange(jobId, watchPath, integrationBaseAtMergeStart, integratedTaskTip);
        var commit = Assert.Single(scopedIntegrationRange);
        Assert.Equal("feat: task ASS-1690", commit.Subject);
    }

    private (string repoRoot, string jobFolder, string jobId, string watchPath) SetupRepoAndJob()
    {
        var repoRoot = Path.Combine(_tempDir, "repo");
        var watchPath = Path.Combine(repoRoot, ".orchestrator", "jobs");
        Directory.CreateDirectory(watchPath);

        RunGit(_tempDir, "init", "-q", "-b", "main", "repo");
        RunGit(repoRoot, "config", "user.email", "test@example.com");
        RunGit(repoRoot, "config", "user.name", "test");
        RunGit(repoRoot, "config", "commit.gpgsign", "false");

        var jobId = "demo-task";
        var jobFolder = Path.Combine(watchPath, "3-progress", jobId);
        Directory.CreateDirectory(jobFolder);
        Directory.CreateDirectory(Path.Combine(jobFolder, "logs"));
        var jobJson = new
        {
            id = jobId,
            title = "Demo task",
            state = "3-progress",
            order = 1,
            agent = "claude",
            createdAt = DateTime.UtcNow.ToString("o")
        };
        File.WriteAllText(Path.Combine(jobFolder, "task.json"),
            JsonSerializer.Serialize(jobJson, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(jobFolder, "prompt.md"), "Do the thing.");
        return (repoRoot, jobFolder, jobId, watchPath);
    }

    private static (GitService git, TaskSessionLog sessions, TaskInfo jobInfo) BuildServices(
        string repoRoot, string watchPath, string jobId)
    {
        var dict = new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Demo",
            ["WatchPaths:0:RootPath"] = repoRoot,
            ["WatchPaths:0:RepositoryPath"] = repoRoot,
            ["WatchPaths:0:Path"] = watchPath
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var sessions = new TaskSessionLog(scanner, NullLogger<TaskSessionLog>.Instance);
        var info = scanner.FindJob(jobId, watchPath)
            ?? throw new InvalidOperationException("Test setup failed: scanner did not pick up the job.");
        return (git, sessions, info);
    }

    private static void AppendSessionEvent(string jobFolder, SessionEvent evt)
    {
        var logs = Path.Combine(jobFolder, "logs");
        Directory.CreateDirectory(logs);
        var path = Path.Combine(logs, "session-events.jsonl");
        var line = JsonSerializer.Serialize(evt, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
        }) + Environment.NewLine;
        File.AppendAllText(path, line);
    }

    private static void AppendCliOutput(string jobFolder, IEnumerable<(string Text, DateTime Ts)> lines)
    {
        var logs = Path.Combine(jobFolder, "logs");
        Directory.CreateDirectory(logs);
        var path = Path.Combine(logs, "cli-output.log");
        var sb = new System.Text.StringBuilder();
        foreach (var (text, ts) in lines)
        {
            sb.Append('[').Append(ts.ToString("HH:mm:ss.fff")).Append(']').Append(' ');
            sb.Append("[system] ").Append(text).Append(Environment.NewLine);
        }
        File.AppendAllText(path, sb.ToString());
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        var full = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
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
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(15_000);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
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
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(15_000);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {p.StandardError.ReadToEnd()}");
        return output;
    }
}
