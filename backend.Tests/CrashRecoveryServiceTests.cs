using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Diagnostics;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Crash-recovery doctrine (ADR-0020). Two simulations:
///
/// <list type="number">
///   <item><b>Surviving completion marker.</b> A job sits in <c>3-progress</c>
///   with a <c>completion-marker.json</c> next to it - the proxy for a runner
///   that crashed between deciding to transition and actually moving the
///   folder. After <see cref="CrashRecoveryService.RecoverAsync"/>, the job
///   must be in <c>4-review</c> and the marker gone.</item>
///   <item><b>Orphan working-tree changes.</b> The repo has uncommitted files
///   and a recently-active job in <c>3-progress</c>. After recovery, the
///   working tree is clean and the resulting commit carries the fixed
///   <c>crash-recovery</c> author tag so it's findable in <c>git log</c>.</item>
/// </list>
/// </summary>
public sealed class CrashRecoveryServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _watchPath;
    private readonly string _repoRoot;
    private readonly string _logDir;
    private const string ProjectName = "demo";

    public CrashRecoveryServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "atp-crash-recovery-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_tempDir, "jobs");
        _repoRoot = Path.Combine(_tempDir, "repo");
        _logDir = Path.Combine(_tempDir, "logs");
        Directory.CreateDirectory(_tempDir);
        foreach (var state in JobStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));
        Directory.CreateDirectory(_repoRoot);
        Directory.CreateDirectory(_logDir);

        RunGit(_repoRoot, "init -q -b main");
        RunGit(_repoRoot, "config user.email test@example.com");
        RunGit(_repoRoot, "config user.name test");
        File.WriteAllText(Path.Combine(_repoRoot, "README.md"), "seed");
        RunGit(_repoRoot, "add -A");
        RunGit(_repoRoot, "commit -q -m seed");
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
    public async Task RecoverAsync_SurvivingCompletionMarker_FinishesProgressToReviewTransition()
    {
        WriteJob(JobStates.Progress, "demo-task");
        var jobFolder = Path.Combine(_watchPath, JobStates.Progress, "demo-task");

        // Drop a marker as if the runner had matched [[TASK_DONE]] but
        // crashed before MoveAsync ran.
        CompletionMarker.Write(jobFolder, new CompletionMarker
        {
            TargetState = JobStates.AutoReview,
            ExecutionStatus = "completed",
            AgentOutcome = "Done"
        });

        var (recovery, _) = BuildRecovery();
        var decisions = await recovery.RecoverAsync();

        Assert.False(Directory.Exists(jobFolder), "job folder must move out of 3-progress");
        var newFolder = Path.Combine(_watchPath, JobStates.AutoReview, "demo-task");
        Assert.True(Directory.Exists(newFolder), "job folder must land in 4-review");
        Assert.False(File.Exists(CompletionMarker.PathFor(newFolder)),
            "completion-marker.json must be cleared after a successful recovery move");

        var transition = Assert.Single(decisions);
        Assert.Equal(RecoveryDecisionKinds.TransitionCompleted, transition.Kind);
        Assert.Equal("demo-task", transition.JobId);
        Assert.Equal(JobStates.AutoReview, transition.TargetState);

        // recovery.jsonl: one structured row per decision so an external
        // operator (or Layer 3 review) can parse it without tailing logs.
        var jsonl = File.ReadAllText(Path.Combine(_logDir, "recovery.jsonl"));
        Assert.Contains("transition-completed", jsonl);
        Assert.Contains("\"jobId\":\"demo-task\"", jsonl);
    }

    [Fact]
    public async Task RecoverAsync_OrphanWorkingTreeChanges_AreCommittedWithCrashRecoveryAuthor()
    {
        WriteJob(JobStates.Progress, "active-task");
        var jobFolder = Path.Combine(_watchPath, JobStates.Progress, "active-task");
        // lastProgressAt makes "active-task" the attribution target.
        StampLastProgressAt(jobFolder, DateTime.UtcNow);

        // Simulate orphan changes: a tracked file modification + a brand
        // new untracked file. Both must end up in the recovery commit.
        File.WriteAllText(Path.Combine(_repoRoot, "README.md"), "modified post-crash");
        File.WriteAllText(Path.Combine(_repoRoot, "new-file.txt"), "agent left this behind");

        var (recovery, _) = BuildRecovery();
        var decisions = await recovery.RecoverAsync();

        // Working tree must be clean now.
        var status = RunGitCapture(_repoRoot, "status --porcelain=v1");
        Assert.True(string.IsNullOrWhiteSpace(status),
            $"working tree must be clean after orphan recovery; got: {status}");

        // The new commit must carry the crash-recovery author tag.
        var lastAuthor = RunGitCapture(_repoRoot, "log -1 --format=%ae");
        Assert.Contains("crash-recovery", lastAuthor, StringComparison.OrdinalIgnoreCase);

        var commitDecision = Assert.Single(decisions, d => d.Kind == RecoveryDecisionKinds.OrphanCommitted);
        Assert.Equal("active-task", commitDecision.JobId);
        Assert.False(string.IsNullOrWhiteSpace(commitDecision.CommitSha));

        // Decision is mirrored both in recovery.jsonl and in the daily backend log.
        var jsonl = File.ReadAllText(Path.Combine(_logDir, "recovery.jsonl"));
        Assert.Contains("orphan-committed", jsonl);

        var dailyLog = Path.Combine(_logDir, $"{DateTime.UtcNow:yyyy-MM-dd}.log");
        Assert.True(File.Exists(dailyLog), "daily backend log must exist after recovery sweep");
        Assert.Contains("Backend.CrashRecovery", File.ReadAllText(dailyLog));
    }

    [Fact]
    public async Task RecoverAsync_OrphanChangesWithNoActiveJob_AreSkipped()
    {
        // C1 (2026-05-22): uncommitted changes WITHOUT an active 3-progress
        // job are usually a human editor session, not a crashed agent run.
        // The recovery sweep now logs an OrphanSkipped decision instead of
        // committing those changes blindly. Re-enable the old behaviour
        // via ATP_CRASH_RECOVERY_AGGRESSIVE=1.
        File.WriteAllText(Path.Combine(_repoRoot, "README.md"), "edited by a human");
        File.WriteAllText(Path.Combine(_repoRoot, "new-file.txt"), "from a Claude Code session");

        var (recovery, _) = BuildRecovery();
        var decisions = await recovery.RecoverAsync();

        // Working tree must stay dirty — the operator's edits are preserved.
        var status = RunGitCapture(_repoRoot, "status --porcelain=v1");
        Assert.False(string.IsNullOrWhiteSpace(status),
            "uncommitted edits must be preserved when there is no active job to attribute them to");

        // The decision must be OrphanSkipped, not OrphanCommitted.
        var skipped = Assert.Single(decisions, d => d.Kind == RecoveryDecisionKinds.OrphanSkipped);
        Assert.Null(skipped.JobId);
        Assert.Contains("no 3-progress job", skipped.Reason);
        Assert.DoesNotContain(decisions, d => d.Kind == RecoveryDecisionKinds.OrphanCommitted);

        var jsonl = File.ReadAllText(Path.Combine(_logDir, "recovery.jsonl"));
        Assert.Contains("orphan-skipped", jsonl);
    }

    [Fact]
    public async Task RecoverAsync_NoMarkersAndCleanTree_IsANoOp()
    {
        WriteJob(JobStates.Progress, "untouched");

        var (recovery, _) = BuildRecovery();
        var decisions = await recovery.RecoverAsync();

        Assert.Empty(decisions);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, JobStates.Progress, "untouched")));
    }

    private (CrashRecoveryService Recovery, JobScannerService Scanner) BuildRecovery()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = ProjectName,
            ["WatchPaths:0:Path"] = _watchPath,
            ["WatchPaths:0:RootPath"] = _repoRoot,
            ["WatchPaths:0:RepositoryPath"] = _repoRoot,
            ["Logging:BackendFile:LogDirectory"] = _logDir
        }).Build();

        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new JobScannerService(config, NullLogger<JobScannerService>.Instance, summary);
        var states = new JobStateMachine(scanner, NullLogger<JobStateMachine>.Instance);
        var mutations = new JobMutationService(scanner, NullLogger<JobMutationService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config, prompts);
        var transitions = new JobTransitionService(scanner, states, mutations, git, settings, NullLogger<JobTransitionService>.Instance);

        var logOptions = new BackendFileLoggerOptions { LogDirectory = _logDir, RetentionDays = 14 };
        var sink = new BackendFileLogSink(logOptions);
        var recovery = new CrashRecoveryService(
            scanner, transitions, mutations, git,
            sink, Options.Create(logOptions),
            NullLogger<CrashRecoveryService>.Instance);
        return (recovery, scanner);
    }

    private void WriteJob(string state, string slug)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "job.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"copilot\"}}");
    }

    private static void StampLastProgressAt(string jobFolder, DateTime utc)
    {
        var jsonPath = Path.Combine(jobFolder, "job.json");
        var json = File.ReadAllText(jsonPath);
        using var doc = JsonDocument.Parse(json);
        var dict = new Dictionary<string, JsonElement>();
        foreach (var p in doc.RootElement.EnumerateObject()) dict[p.Name] = p.Value.Clone();
        var newDict = new Dictionary<string, object>();
        foreach (var kv in dict) newDict[kv.Key] = kv.Value;
        newDict["lastProgressAt"] = utc.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(newDict, new JsonSerializerOptions { WriteIndented = true }));
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

    private static string RunGitCapture(string cwd, string args)
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
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(15_000);
        return output.Trim();
    }
}
