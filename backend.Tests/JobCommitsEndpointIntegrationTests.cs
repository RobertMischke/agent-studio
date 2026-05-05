using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Integration test that walks the same path the
/// <c>/api/jobs/{id}/commits</c> endpoint takes against a real on-disk
/// git repo: scan job, build run timeline, ask GitService for each
/// SHA range, run the aggregator, and assert the result. The pure
/// aggregator tests in <see cref="JobCommitsAggregatorTests"/> cover
/// the dedup / ordering rules without git; this test pins the wiring
/// so a refactor of GitService can't quietly break the endpoint.
/// </summary>
public class JobCommitsEndpointIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public JobCommitsEndpointIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "job-commits-integration-" + Guid.NewGuid().ToString("N"));
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
        catch { }
    }

    [Fact]
    public void Aggregate_AcrossTwoRuns_DedupsAndOrdersNewestFirst()
    {
        var (repoRoot, jobFolder, jobId, watchPath) = SetupRepoAndJob();

        WriteFile(repoRoot, "README.md", "seed");
        RunGit(repoRoot, "add", "-A");
        RunGit(repoRoot, "commit", "-q", "-m", "seed");
        var head0 = RunGitCapture(repoRoot, "rev-parse", "HEAD").Trim();

        WriteFile(repoRoot, "src/foo.cs", "// foo");
        RunGit(repoRoot, "add", "-A");
        RunGit(repoRoot, "commit", "-q", "-m", "feat: foo");
        var head1 = RunGitCapture(repoRoot, "rev-parse", "HEAD").Trim();

        WriteFile(repoRoot, "src/bar.cs", "// bar");
        RunGit(repoRoot, "add", "-A");
        RunGit(repoRoot, "commit", "-q", "-m", "fix: bar");
        var head2 = RunGitCapture(repoRoot, "rev-parse", "HEAD").Trim();

        AppendSessionEvent(jobFolder, new SessionEvent
        {
            Ts = DateTime.UtcNow.AddMinutes(-10), Kind = "start", Cli = "claude",
            HeadShaBefore = head0, HeadShaAfter = head1
        });
        AppendCliOutput(jobFolder, [
            ("[taskboard] Started claude CLI (PID 1)", DateTime.UtcNow.AddMinutes(-10).AddSeconds(1)),
            ("[taskboard] claude CLI exited: status=completed, exitCode=0, duration=5.0s", DateTime.UtcNow.AddMinutes(-9))
        ]);
        AppendSessionEvent(jobFolder, new SessionEvent
        {
            Ts = DateTime.UtcNow.AddMinutes(-5), Kind = "continue", Cli = "claude",
            HeadShaBefore = head1, HeadShaAfter = head2
        });
        AppendCliOutput(jobFolder, [
            ("[taskboard] Started claude CLI (PID 2)", DateTime.UtcNow.AddMinutes(-5).AddSeconds(1)),
            ("[taskboard] claude CLI exited: status=completed, exitCode=0, duration=5.0s", DateTime.UtcNow.AddMinutes(-4))
        ]);

        var (git, sessions, info) = BuildServices(repoRoot, watchPath, jobId);
        var events = sessions.ReadSessionEvents(jobId, watchPath);
        var lines = CliOutputLogParser.ParseFile(Path.Combine(info.FolderPath, "logs", "cli-output.log"));
        var timeline = RunTimelineBuilder.Build(events, lines, DateTime.UtcNow);

        var aggregate = JobCommitsAggregator.Aggregate(info, timeline.Runs,
            (before, after) => git.GetCommitsInShaRange(jobId, watchPath, before, after));

        Assert.Equal(2, aggregate.Count);
        Assert.Equal("fix: bar", aggregate.Commits[0].Subject);
        Assert.Equal("feat: foo", aggregate.Commits[1].Subject);
        // Run-index attribution lines up with the SHA range that produced each commit.
        Assert.Equal(2, aggregate.Commits[0].RunIndex);
        Assert.Equal(1, aggregate.Commits[1].RunIndex);
    }

    [Fact]
    public void Aggregate_DeletionOnlyCommit_ShowsUpInChangeSet()
    {
        var (repoRoot, jobFolder, jobId, watchPath) = SetupRepoAndJob();

        WriteFile(repoRoot, "doomed.txt", "remove me");
        RunGit(repoRoot, "add", "-A");
        RunGit(repoRoot, "commit", "-q", "-m", "seed");
        var head0 = RunGitCapture(repoRoot, "rev-parse", "HEAD").Trim();

        File.Delete(Path.Combine(repoRoot, "doomed.txt"));
        RunGit(repoRoot, "add", "-A");
        RunGit(repoRoot, "commit", "-q", "-m", "remove doomed");
        var head1 = RunGitCapture(repoRoot, "rev-parse", "HEAD").Trim();

        AppendSessionEvent(jobFolder, new SessionEvent
        {
            Ts = DateTime.UtcNow.AddMinutes(-1), Kind = "start", Cli = "claude",
            HeadShaBefore = head0, HeadShaAfter = head1
        });

        var (git, sessions, info) = BuildServices(repoRoot, watchPath, jobId);
        var events = sessions.ReadSessionEvents(jobId, watchPath);
        var timeline = RunTimelineBuilder.Build(events, [], DateTime.UtcNow);
        var aggregate = JobCommitsAggregator.Aggregate(info, timeline.Runs,
            (before, after) => git.GetCommitsInShaRange(jobId, watchPath, before, after));

        Assert.Equal(1, aggregate.Count);
        Assert.Equal("remove doomed", aggregate.Commits[0].Subject);
        Assert.True(aggregate.Commits[0].Removed >= 1);
    }

    [Fact]
    public void Scanner_PopulatesCommitCountFromSessionEvents()
    {
        var (repoRoot, jobFolder, jobId, watchPath) = SetupRepoAndJob();

        // Two non-trivial ranges should yield CommitCount = 2 even
        // without ever calling git (cheap kanban path).
        AppendSessionEvent(jobFolder, new SessionEvent
        {
            Ts = DateTime.UtcNow.AddMinutes(-10), Kind = "start",
            HeadShaBefore = "aaa", HeadShaAfter = "bbb"
        });
        AppendSessionEvent(jobFolder, new SessionEvent
        {
            Ts = DateTime.UtcNow.AddMinutes(-5), Kind = "continue",
            HeadShaBefore = "bbb", HeadShaAfter = "ccc"
        });
        AppendSessionEvent(jobFolder, new SessionEvent
        {
            Ts = DateTime.UtcNow.AddMinutes(-1), Kind = "continue",
            HeadShaBefore = "ccc", HeadShaAfter = "ccc" // trivial, no commit
        });

        var (_, _, info) = BuildServices(repoRoot, watchPath, jobId);
        Assert.Equal(2, info.CommitCount);
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
        File.WriteAllText(Path.Combine(jobFolder, "job.json"),
            JsonSerializer.Serialize(jobJson, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(jobFolder, "prompt.md"), "Do the thing.");
        return (repoRoot, jobFolder, jobId, watchPath);
    }

    private static (GitService git, JobSessionLog sessions, JobInfo jobInfo) BuildServices(
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
        var scanner = new JobScannerService(config, NullLogger<JobScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var sessions = new JobSessionLog(scanner, NullLogger<JobSessionLog>.Instance);
        var info = scanner.FindJob(jobId, watchPath)
            ?? throw new InvalidOperationException("Test setup: scanner did not pick up the job.");
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
