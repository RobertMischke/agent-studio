using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using AgentStudio.Pipeline;
using AgentStudio.Shared;
using AgentStudio.Tasks;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The delivery is merged into the LOCAL integration branch but the publish to
/// origin was refused. Acceptance reads the published line, so the work is not
/// where the next claim, the next merge, and every other checkout will look for
/// it.
///
/// <para>Before AGT-2688 this state had no name. The ancestor set unioned the
/// local branch with origin, so an unpublished merge read as
/// <c>integrated</c> and was accepted while the commits existed on exactly one
/// machine; where it did read as <c>pending</c> it stayed pending forever,
/// because nothing in the system would ever publish it. Both readings are
/// dishonest and neither alarms. These tests pin the distinct
/// <c>integration-push-blocked</c> verdict and prove the acceptance rail takes
/// the card off the loop.</para>
/// </summary>
public sealed class IntegrationPushBlockedStatusTests : IDisposable
{
    private readonly string _tempDir;

    public IntegrationPushBlockedStatusTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "integration-push-blocked-" + Guid.NewGuid().ToString("N"));
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
    public void MergedLocallyWithARefusedPush_ReportsPushBlockedRatherThanIntegrated()
    {
        var repo = SeedPublishedDualLineRepo();
        var svc = BuildService(repo, out var project, out var log);
        var sha = MergeDeliveryLocally(repo, "task/blocked", "blocked.txt");
        var job = Job("blocked", project, repo, commits: [Commit(sha)]);

        RecordMergeStep(log, job, project, PipelineStepStatus.Passed, "merged");
        RecordPushStep(log, job, project, "remote-rejected", "! [rejected] develop -> develop (non-fast-forward)");

        var status = svc.BuildLookup([job])[job.TaskKey];

        Assert.Equal(IntegrationStatuses.PushBlocked, status.Status);
        Assert.NotEqual(IntegrationStatuses.Pending, status.Status);
        Assert.NotEqual(IntegrationStatuses.Integrated, status.Status);
        Assert.Equal(
            AcceptedIntegrationFailureCodes.IntegrationPushBlocked,
            status.Failure?.Code);
        // The state is terminal: no rebase round can publish a branch whose push
        // origin refused, so the card must not be offered as recoverable.
        Assert.False(status.Failure?.RebaseRecoveryAvailable);
        Assert.Contains("origin/develop", status.Detail);
    }

    [Fact]
    public void MergedLocallyWithThePushStillQueued_StaysPendingAndDoesNotFalselyAlarm()
    {
        var repo = SeedPublishedDualLineRepo();
        var svc = BuildService(repo, out var project, out var log);
        var sha = MergeDeliveryLocally(repo, "task/inflight", "inflight.txt");
        var job = Job("inflight", project, repo, commits: [Commit(sha)]);

        // Merge recorded, push not yet attempted: the publish is in flight.
        RecordMergeStep(log, job, project, PipelineStepStatus.Passed, "merged");

        var status = svc.BuildLookup([job])[job.TaskKey];

        Assert.Equal(IntegrationStatuses.Pending, status.Status);
        Assert.Null(status.Failure);
    }

    [Fact]
    public void PublishedMerge_IsIntegrated()
    {
        var repo = SeedPublishedDualLineRepo();
        var svc = BuildService(repo, out var project, out var log);
        var sha = MergeDeliveryLocally(repo, "task/published", "published.txt");
        RunGit(repo, "push -q origin develop");
        RunGit(repo, "fetch -q origin develop");
        var job = Job("published", project, repo, commits: [Commit(sha)]);

        RecordMergeStep(log, job, project, PipelineStepStatus.Passed, "merged");
        RecordPushStep(log, job, project, "pushed", null, PipelineStepStatus.Passed);

        var status = svc.BuildLookup([job])[job.TaskKey];

        Assert.Equal(IntegrationStatuses.Integrated, status.Status);
    }

    [Fact]
    public void AcceptanceRail_EscalatesPushBlockedOnceInsteadOfRecheckingItForever()
    {
        var options = new AcceptanceRailOptions(
            Enabled: true,
            Interval: TimeSpan.FromMinutes(1),
            MaxRequeues: 3,
            HoldList: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var review = RailTask(TaskStates.HumanReview);
        var blocked = new TaskIntegrationStatus
        {
            Status = IntegrationStatuses.PushBlocked,
            IntegrationBranch = "develop",
            Failure = new TaskIntegrationFailure
            {
                Code = AcceptedIntegrationFailureCodes.IntegrationPushBlocked,
                Label = "Integration push blocked",
                Reason = "origin refused the integration branch.",
                RebaseRecoveryAvailable = false,
            },
        };

        var first = AcceptanceRailPolicy.Decide(review, blocked, conflictRequeues: 0, options);

        Assert.Equal(AcceptanceRailAction.Escalate, first.Action);
        Assert.Equal("integration-push-blocked", first.Reason);

        // It must never be accepted, and never re-queued for another delivery
        // round: re-delivering repeats the same refused push.
        Assert.NotEqual(AcceptanceRailAction.Accept, first.Action);
        Assert.NotEqual(AcceptanceRailAction.Requeue, first.Action);

        // Once escalated the rail leaves it alone, so it alarms exactly once
        // instead of producing an escalation on every pass.
        var afterEscalation = AcceptanceRailPolicy.Decide(
            RailTask(TaskStates.Escalated), blocked, conflictRequeues: 0, options);
        Assert.Equal(AcceptanceRailAction.Ignore, afterEscalation.Action);
    }

    // --- helpers -----------------------------------------------------------

    private static TaskInfo RailTask(string state) => new()
    {
        Id = "rail-card",
        Key = "AGT-1",
        State = state,
        ProjectName = "Fixture",
    };

    private static void RecordMergeStep(
        PipelineExecutionLog log,
        TaskInfo job,
        string project,
        PipelineStepStatus status,
        string verdict)
    {
        log.EnsureRun(job.FolderPath, PipelineCatalogue.Standard, project, job.Id);
        log.RecordStep(job.FolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopStepId,
            Kind = StepKind.Tool,
            Status = status,
            Verdict = verdict,
        });
    }

    private static void RecordPushStep(
        PipelineExecutionLog log,
        TaskInfo job,
        string project,
        string pushStatus,
        string? error,
        PipelineStepStatus status = PipelineStepStatus.Failed)
    {
        log.EnsureRun(job.FolderPath, PipelineCatalogue.Standard, project, job.Id);
        log.RecordStep(job.FolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopPushStepId,
            Kind = StepKind.Tool,
            Status = status,
            Verdict = status == PipelineStepStatus.Passed ? pushStatus : "environmental",
            VerdictSummary = $"Push of the integration branch to origin failed ({pushStatus}).",
            Reason = error,
            FailureCode = status == PipelineStepStatus.Failed
                          && MergeIntoDevelopRunner.IsPushBlocked(pushStatus)
                ? AcceptedIntegrationFailureCodes.IntegrationPushBlocked
                : null,
        });
    }

    private TaskIntegrationStatusService BuildService(
        string repo, out string projectName, out PipelineExecutionLog log)
    {
        projectName = "Fixture";
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = projectName,
            ["WatchPaths:0:RootPath"] = repo,
            ["WatchPaths:0:RepositoryPath"] = repo,
            ["WatchPaths:0:Path"] = Path.Combine(repo, ".orchestrator", "jobs"),
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        settings.SetIntegrationBranch(projectName, "develop");
        log = new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance);
        return new TaskIntegrationStatusService(
            git, settings, log, NullLogger<TaskIntegrationStatusService>.Instance);
    }

    /// <summary>A checkout whose develop line exists on origin as well as locally.</summary>
    private string SeedPublishedDualLineRepo()
    {
        var name = Guid.NewGuid().ToString("N")[..8];
        var origin = Path.Combine(_tempDir, name + "-origin.git");
        var repo = Path.Combine(_tempDir, name + "-repo");

        RunGit(_tempDir, $"init --bare -q --initial-branch=main \"{origin}\"");
        Directory.CreateDirectory(repo);
        RunGit(repo, "init -q -b main");
        RunGit(repo, "config user.email test@example.com");
        RunGit(repo, "config user.name test");
        File.WriteAllText(Path.Combine(repo, "README.md"), "seed\n");
        RunGit(repo, "add -A");
        RunGit(repo, "commit -q -m seed");
        RunGit(repo, $"remote add origin \"{origin}\"");
        RunGit(repo, "push -q origin main");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "push -q origin develop");
        RunGit(repo, "fetch -q origin develop");
        return repo;
    }

    private static string MergeDeliveryLocally(string repo, string branch, string file)
    {
        RunGit(repo, "checkout -q develop");
        RunGit(repo, $"checkout -q -b {branch}");
        File.WriteAllText(Path.Combine(repo, file), "delivered\n");
        RunGit(repo, "add -A");
        RunGit(repo, $"commit -q -m \"feat: {file}\"");
        var sha = RunGit(repo, "rev-parse HEAD").Out.Trim();
        RunGit(repo, "checkout -q develop");
        RunGit(repo, $"merge --no-ff --no-edit -m \"merge {branch}\" {branch}");
        return sha;
    }

    private static TaskCommitInfo Commit(string sha)
        => new()
        {
            Sha = sha,
            ShortSha = sha[..7],
            Message = "feat: delivered",
            FilesChanged = 1,
            Files = ["delivered.txt"],
        };

    private TaskInfo Job(string id, string project, string repo, TaskCommitInfo[] commits)
    {
        var folder = Path.Combine(_tempDir, "jobs", id);
        Directory.CreateDirectory(folder);
        return new TaskInfo
        {
            Id = id,
            Key = "AGT-" + id,
            TaskKey = repo + "::" + id,
            State = TaskStates.Completed,
            ProjectName = project,
            WatchPath = repo,
            FolderPath = folder,
            Commits = commits.ToList(),
        };
    }

    private static (string Out, string Err, int Code) RunGit(string cwd, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = "-c safe.bareRepository=all " + args,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEnd();
        var se = p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        return (so, se, p.ExitCode);
    }
}
