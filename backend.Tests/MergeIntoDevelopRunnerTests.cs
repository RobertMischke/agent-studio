using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Pipeline;
using OrchestratorApi.Services.Tasks;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// ASS-1721: the deferred, operator-triggered "Merge into Develop" post-step.
/// Drives <see cref="MergeIntoDevelopRunner"/> against a throwaway temp repo and
/// asserts it performs the real <c>task/&lt;id&gt; -&gt; develop</c> merge and
/// flips the deferred step in <c>pipeline-execution.json</c> from pending to its
/// outcome (passed / failed / skipped). A conflict is recorded as a visible
/// failure, not swallowed.
/// </summary>
public sealed class MergeIntoDevelopRunnerTests : IDisposable
{
    private readonly string _tempDir;

    public MergeIntoDevelopRunnerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "merge-into-develop-" + Guid.NewGuid().ToString("N"));
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
    public void Run_MergesTaskBranch_AndRecordsStepPassed()
    {
        var repo = SeedRepo("runner-merge");
        // develop + task/20 with a commit the merge should fold in.
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b task/20");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "task work");
        Commit(repo, "feat: task work");
        RunGit(repo, "checkout -q develop");

        var (git, log) = Build(repo);
        var jobFolder = BeginRun(log, repo, jobId: "20");

        var runner = new MergeIntoDevelopRunner(git, log, NullLogger<MergeIntoDevelopRunner>.Instance);
        var outcome = runner.Run("Fixture", "20", jobFolder, repo, "develop");

        Assert.Equal(MergeIntoIntegrationOutcome.Merged, outcome.Outcome);
        Assert.Equal(0, RunGit(repo, "rev-parse --verify develop^2").Code); // merge commit

        var step = ReadMergeStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Passed, step!.Status);
        Assert.Equal("merged", step.Verdict);
    }

    [Fact]
    public void Run_NoTaskBranch_RecordsStepSkipped()
    {
        var repo = SeedRepo("runner-skip");
        RunGit(repo, "checkout -q -b develop");

        var (git, log) = Build(repo);
        var jobFolder = BeginRun(log, repo, jobId: "21");

        var runner = new MergeIntoDevelopRunner(git, log, NullLogger<MergeIntoDevelopRunner>.Instance);
        var outcome = runner.Run("Fixture", "21", jobFolder, repo, "develop");

        Assert.Equal(MergeIntoIntegrationOutcome.NoTaskBranch, outcome.Outcome);
        var step = ReadMergeStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Skipped, step!.Status);
    }

    [Fact]
    public void Run_Conflict_RecordsStepFailed_WithConflictedFilesVisible()
    {
        var repo = SeedRepo("runner-conflict");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b task/22");
        File.WriteAllText(Path.Combine(repo, "shared.txt"), "task version");
        Commit(repo, "feat: task edits shared");
        RunGit(repo, "checkout -q develop");
        File.WriteAllText(Path.Combine(repo, "shared.txt"), "develop version");
        Commit(repo, "chore: develop edits shared");

        var (git, log) = Build(repo);
        var jobFolder = BeginRun(log, repo, jobId: "22");

        var runner = new MergeIntoDevelopRunner(git, log, NullLogger<MergeIntoDevelopRunner>.Instance);
        var outcome = runner.Run("Fixture", "22", jobFolder, repo, "develop");

        Assert.Equal(MergeIntoIntegrationOutcome.Conflict, outcome.Outcome);
        var step = ReadMergeStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Failed, step!.Status);
        Assert.Equal("conflict", step.Verdict);
        // The conflicted file is surfaced in the verdict summary tooltip.
        Assert.Contains("shared.txt", step.VerdictSummary);
    }

    private string BeginRun(PipelineExecutionLog log, string repo, string jobId)
    {
        // The job folder lives OUTSIDE the repo working tree (as in production,
        // where the tasks workspace is separate from the code checkout); writing
        // pipeline-execution.json inside the repo would otherwise make the tree
        // look dirty and the merge precondition would refuse.
        var jobFolder = Path.Combine(_tempDir, "jobs", jobId);
        Directory.CreateDirectory(jobFolder);
        // Pre-populate the run so the deferred merge step sits in it as pending,
        // exactly as it would after a real run recorded the pipeline.
        log.Begin(jobFolder, PipelineCatalogue.Standard, "Fixture", jobId);
        return jobFolder;
    }

    private static PipelineStepExecution? ReadMergeStep(PipelineExecutionLog log, string jobFolder)
    {
        var record = log.Read(jobFolder);
        return record?.Steps.FirstOrDefault(s => s.StepId == PipelineCatalogue.MergeIntoDevelopStepId);
    }

    private (GitService Git, PipelineExecutionLog Log) Build(string repo)
    {
        var dict = new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Fixture",
            ["WatchPaths:0:RootPath"] = repo,
            ["WatchPaths:0:RepositoryPath"] = repo,
            ["WatchPaths:0:Path"] = Path.Combine(repo, ".orchestrator", "jobs"),
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var log = new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance);
        return (git, log);
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

    private static void Commit(string cwd, string message)
    {
        RunGit(cwd, "add -A");
        RunGit(cwd, $"commit -q -m \"{message}\"");
    }

    private static (string Out, string Err, int Code) RunGit(string cwd, string args)
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
        var so = p.StandardOutput.ReadToEnd();
        var se = p.StandardError.ReadToEnd();
        p.WaitForExit(15_000);
        return (so, se, p.ExitCode);
    }
}
