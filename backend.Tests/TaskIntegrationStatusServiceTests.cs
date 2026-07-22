using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using AgentStudio.Pipeline;
using AgentStudio.Shared;
using AgentStudio.Tasks;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2202: the honest, git-derived integration verdict for an accepted card must
/// resolve the "Accept != Merge" blind spot from ground truth (the develop
/// git-log), not the ephemeral anchor-ancestry signal. Every test drives real git
/// against a throwaway repo so all four <see cref="IntegrationStatuses"/> classes
/// are exercised end to end:
///   - integrated via a curated <c>merge(&lt;KEY&gt;)</c> log commit (the signal
///     anchor-ancestry cannot see because the curated integrator rewrites commits),
///   - integrated via anchor / branch-tip ancestry (the plain --no-ff merge),
///   - pending (accepted work still only on the task branch),
///   - conflict-skipped (a recorded merge-into-develop conflict),
///   - no-branch (nothing to integrate).
/// </summary>
public sealed class TaskIntegrationStatusServiceTests : IDisposable
{
    private readonly string _tempDir;

    public TaskIntegrationStatusServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "task-integration-status-" + Guid.NewGuid().ToString("N"));
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
    public void ParseIntegrationMergeKey_MatchesCuratedMergeSubjects()
    {
        Assert.Equal("AGT-2202", GitService.ParseIntegrationMergeKey("merge(AGT-2202): integrations-sicht"));
        Assert.Equal("AGT-2202", GitService.ParseIntegrationMergeKey("merge-recut(AGT-2202): re-cut after conflict"));
        Assert.Equal("RB-42", GitService.ParseIntegrationMergeKey("merge(rb-42): lower-case key upper-cased"));
        Assert.Null(GitService.ParseIntegrationMergeKey("Merge branch 'task/foo' into develop"));
        Assert.Null(GitService.ParseIntegrationMergeKey("feat: not a merge"));
    }

    [Fact]
    public void BuildLookup_CuratedMergeCommitOnDevelop_IsIntegratedEvenWhenAnchorIsNotAnAncestor()
    {
        // The curated integrator lands the work under a merge(KEY) commit on
        // develop WITHOUT the task's own commit being an ancestor (it rewrites).
        // Simulate: the task commit lives only on the task branch; develop gets a
        // separate commit whose SUBJECT is the curated merge marker.
        var repo = SeedDevelopMainRepo();
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "checkout -q -b task/curated");
        File.WriteAllText(Path.Combine(repo, "curated.txt"), "task work");
        Commit(repo, "feat: curated work");
        var anchor = RunGit(repo, "rev-parse task/curated").Out.Trim();
        // Develop advances with a curated marker commit that does NOT contain the
        // task branch (empty-ish commit via --allow-empty, different content).
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "commit -q --allow-empty -m \"merge(AGT-2202): curated integration of curated work\"");
        var curatedSha = RunGit(repo, "rev-parse develop").Out.Trim();

        var svc = BuildService(repo, out var project, out var log);
        var job = Job("curated", "AGT-2202", project, repo, log, commits: new[] { Commit(anchor) },
            prov: Prov(branch: "task/curated"));

        var status = svc.BuildLookup(new[] { job })[job.TaskKey];

        Assert.Equal(IntegrationStatuses.Integrated, status.Status);
        Assert.Equal(curatedSha[..7], status.Sha);
        Assert.Equal("curated-merge", status.Detail);
        // Ground-truth cross-check: the anchor really is NOT an ancestor of develop.
        var svcGit = new GitService(NullLogger<GitService>.Instance,
            new TaskScannerService(EmptyConfig(), NullLogger<TaskScannerService>.Instance,
                new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, EmptyConfig())), EmptyConfig());
        Assert.False(svcGit.IsAncestor(repo, anchor, "develop"));
    }

    [Fact]
    public void BuildLookup_PlainMergeIntoDevelop_IsIntegratedViaAnchorAncestry()
    {
        var repo = SeedDevelopMainRepo();
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "checkout -q -b task/merged");
        File.WriteAllText(Path.Combine(repo, "merged.txt"), "dev work");
        Commit(repo, "feat: dev work");
        var anchor = RunGit(repo, "rev-parse task/merged").Out.Trim();
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "merge --no-ff --no-edit task/merged");

        var svc = BuildService(repo, out var project, out var log);
        var job = Job("merged", "AGT-3000", project, repo, log, commits: new[] { Commit(anchor) },
            prov: Prov(branch: "task/merged"));

        var status = svc.BuildLookup(new[] { job })[job.TaskKey];

        Assert.Equal(IntegrationStatuses.Integrated, status.Status);
        Assert.Equal(anchor[..7], status.Sha);
        Assert.Equal("anchor-ancestor", status.Detail);
    }

    [Fact]
    public void BuildLookup_AcceptedWorkOnlyOnTaskBranch_IsPending()
    {
        var repo = SeedDevelopMainRepo();
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "checkout -q -b task/pending");
        File.WriteAllText(Path.Combine(repo, "pending.txt"), "wip");
        Commit(repo, "feat: pending wip");
        var anchor = RunGit(repo, "rev-parse task/pending").Out.Trim();
        // Never merged into develop.

        var svc = BuildService(repo, out var project, out var log);
        var job = Job("pending", "AGT-3001", project, repo, log, commits: new[] { Commit(anchor) },
            prov: Prov(branch: "task/pending"));

        var status = svc.BuildLookup(new[] { job })[job.TaskKey];

        Assert.Equal(IntegrationStatuses.Pending, status.Status);
        Assert.Null(status.Sha);
    }

    [Fact]
    public void BuildLookup_RecordedMergeConflict_IsConflictSkipped()
    {
        var repo = SeedDevelopMainRepo();
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "checkout -q -b task/conflict");
        File.WriteAllText(Path.Combine(repo, "conflict.txt"), "wip");
        Commit(repo, "feat: conflict wip");
        var anchor = RunGit(repo, "rev-parse task/conflict").Out.Trim();

        var svc = BuildService(repo, out var project, out var log);
        var job = Job("conflict", "AGT-3002", project, repo, log, commits: new[] { Commit(anchor) },
            prov: Prov(branch: "task/conflict"));

        // Seed a recorded merge-into-develop conflict in the job's pipeline record.
        log.EnsureRun(job.FolderPath, PipelineCatalogue.Standard, project, job.Id);
        log.RecordStep(job.FolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopStepId,
            Kind = StepKind.Tool,
            Status = PipelineStepStatus.Failed,
            Verdict = "conflict",
            VerdictSummary = "Conflicted: conflict.txt",
            Reason = "Merge conflict in 1 file(s); merge aborted.",
        });

        var status = svc.BuildLookup(new[] { job })[job.TaskKey];

        Assert.Equal(IntegrationStatuses.ConflictSkipped, status.Status);
        Assert.Contains("conflict.txt", status.Detail);
    }

    [Fact]
    public void BuildLookup_NoCommitAndNoBranch_IsNoBranch()
    {
        var repo = SeedDevelopMainRepo();
        var svc = BuildService(repo, out var project, out var log);
        // A read-only / no-code accepted card: no anchor commit, no branch tip.
        var job = Job("nobranch", "AGT-3003", project, repo, log);

        var status = svc.BuildLookup(new[] { job })[job.TaskKey];

        Assert.Equal(IntegrationStatuses.NoBranch, status.Status);
        Assert.Null(status.Sha);
    }

    [Fact]
    public void BuildLookup_OnlyAcceptedLanes_GetAVerdict()
    {
        var repo = SeedDevelopMainRepo();
        RunGit(repo, "checkout -q develop");
        File.WriteAllText(Path.Combine(repo, "x.txt"), "x");
        Commit(repo, "feat: x");
        var sha = RunGit(repo, "rev-parse develop").Out.Trim();

        var svc = BuildService(repo, out var project, out var log);
        var inProgress = Job("wip", "AGT-3004", project, repo, log, commits: new[] { Commit(sha) }) with { State = TaskStates.Progress };
        var completed = Job("done", "AGT-3005", project, repo, log, commits: new[] { Commit(sha) }) with { State = TaskStates.Completed };

        var lookup = svc.BuildLookup(new[] { inProgress, completed });

        Assert.False(lookup.ContainsKey(inProgress.TaskKey));
        Assert.True(lookup.ContainsKey(completed.TaskKey));
        Assert.Equal(IntegrationStatuses.Integrated, lookup[completed.TaskKey].Status);
    }

    // --- helpers -----------------------------------------------------------

    private TaskIntegrationStatusService BuildService(string repo, out string projectName, out PipelineExecutionLog log)
    {
        projectName = "Fixture";
        var config = ConfigFor(repo, projectName);
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        log = new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance);
        return new TaskIntegrationStatusService(
            git, settings, log, NullLogger<TaskIntegrationStatusService>.Instance);
    }

    private static IConfiguration ConfigFor(string repo, string projectName)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = projectName,
            ["WatchPaths:0:RootPath"] = repo,
            ["WatchPaths:0:RepositoryPath"] = repo,
            ["WatchPaths:0:Path"] = Path.Combine(repo, ".orchestrator", "jobs"),
        }).Build();

    private static IConfiguration EmptyConfig()
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

    private string SeedDevelopMainRepo()
    {
        var repo = Path.Combine(_tempDir, "repo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repo);
        RunGit(repo, "init -q -b main");
        RunGit(repo, "config user.email test@example.com");
        RunGit(repo, "config user.name test");
        File.WriteAllText(Path.Combine(repo, "README.md"), "seed");
        RunGit(repo, "add -A");
        RunGit(repo, "commit -q -m seed");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q main");
        return repo;
    }

    private static TaskProvenance Prov(string branch, string? merge = null)
        => new()
        {
            Branch = branch,
            Merge = merge is null ? null : new TaskProvenanceMerge { MergeCommit = merge },
        };

    private static TaskCommitInfo Commit(string sha)
        => new() { Sha = sha, ShortSha = sha.Length > 7 ? sha[..7] : sha, Message = "commit " + sha };

    private TaskInfo Job(
        string id,
        string key,
        string project,
        string repo,
        PipelineExecutionLog log,
        TaskProvenance? prov = null,
        TaskCommitInfo[]? commits = null)
    {
        // Give the card a real on-disk folder so the pipeline-execution.json read
        // path works for the conflict-skipped classification.
        var folder = Path.Combine(_tempDir, "jobs", id);
        Directory.CreateDirectory(folder);
        return new TaskInfo
        {
            Id = id,
            Key = key,
            TaskKey = repo + "::" + id,
            State = TaskStates.Completed,
            ProjectName = project,
            WatchPath = repo,
            FolderPath = folder,
            Provenance = prov,
            Commits = (commits ?? Array.Empty<TaskCommitInfo>()).ToList(),
        };
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
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEnd();
        var se = p.StandardError.ReadToEnd();
        p.WaitForExit(15_000);
        return (so, se, p.ExitCode);
    }

    private static void Commit(string cwd, string message)
    {
        RunGit(cwd, "add -A");
        RunGit(cwd, $"commit -q -m \"{message}\"");
    }
}
