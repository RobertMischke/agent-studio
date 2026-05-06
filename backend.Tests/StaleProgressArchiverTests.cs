using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Boot-time stale-progress sweep (pairs with ADR-0020 crash recovery, ADR-0028
/// loud-not-archived). Five cases plus the active-job defensive guard:
///
/// <list type="number">
///   <item>Sentinel + stale -> finished missed transition into 4-auto-review
///   with a <c>recovered-from-stuck-progress</c> supervisor chat note.</item>
///   <item>No sentinel + stale -> moved to <c>3a-failed-pickup</c> as
///   <c>-orphan-&lt;date&gt;</c> with a <c>failed-pickup-reason.md</c>
///   placard (ADR-0028: never silently archived).</item>
///   <item>Empty + stale -> moved to <c>3a-failed-pickup</c> as
///   <c>-empty-&lt;date&gt;</c> with a placard and a synthesized
///   <c>job.json</c>.</item>
///   <item>Fresh -> untouched (progress-first pickup will resume).</item>
///   <item>Re-run on the same lane -> no further changes (idempotency).</item>
///   <item>Active job -> never touched even when stale (defensive guard).</item>
/// </list>
/// </summary>
public sealed class StaleProgressArchiverTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _watchPath;
    private readonly string _workspaceRoot;
    private const string ProjectName = "demo";

    public StaleProgressArchiverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "atp-stale-progress-" + Guid.NewGuid().ToString("N"));
        _workspaceRoot = Path.Combine(_tempDir, "workspace");
        _watchPath = Path.Combine(_workspaceRoot, "projects", ProjectName);
        Directory.CreateDirectory(_workspaceRoot);
        foreach (var state in JobStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));
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
    public async Task Sweep_StaleFolderWithDoneSentinel_RecoversToReviewAndAppendsChatNote()
    {
        WriteJob(JobStates.Progress, "demo-task");
        var folder = Path.Combine(_watchPath, JobStates.Progress, "demo-task");
        WriteCliLogWithSentinel(folder, "[[TASK_DONE]]");
        SetMtimeOldEnough(Path.Combine(folder, "logs", "cli-output.log"));
        SetMtimeOldEnough(Path.Combine(folder, "job.json"));

        var (archiver, _) = Build();
        var decisions = await archiver.SweepAsync();

        var moved = Path.Combine(_watchPath, JobStates.AutoReview, "demo-task");
        Assert.False(Directory.Exists(folder), "source 3-progress folder must be moved");
        Assert.True(Directory.Exists(moved), "job folder must land in 4-review");

        var d = Assert.Single(decisions);
        Assert.Equal(StaleProgressDecisionKinds.RecoveredToReview, d.Kind);
        Assert.Equal("DONE", d.SentinelKeyword);
        Assert.Equal(JobStates.AutoReview, d.TargetState);

        // Chat-log note lands on the moved folder so the protocol pane sees it.
        var log = File.ReadAllText(Path.Combine(moved, "logs", "cli-output.log"));
        Assert.Contains("[recovered-from-stuck-progress]", log);
        Assert.Contains("[supervisor]", log);

        // Decision lands in <workspace>/logs/orphan-recoveries.jsonl.
        var jsonl = File.ReadAllText(Path.Combine(_workspaceRoot, "logs", "orphan-recoveries.jsonl"));
        Assert.Contains("recovered-to-review", jsonl);
        Assert.Contains("\"slug\":\"demo-task\"", jsonl);
    }

    [Fact]
    public async Task Sweep_StaleFolderWithoutSentinel_IsMovedToFailedPickupNotSilentlyArchived()
    {
        // ADR-0028: orphan folders surface in 3a-failed-pickup, not 7-archive.
        WriteJob(JobStates.Progress, "no-sentinel");
        var folder = Path.Combine(_watchPath, JobStates.Progress, "no-sentinel");
        WriteCliLog(folder, "agent talked but never finished");
        SetMtimeOldEnough(Path.Combine(folder, "logs", "cli-output.log"));
        SetMtimeOldEnough(Path.Combine(folder, "job.json"));

        var (archiver, _) = Build();
        var decisions = await archiver.SweepAsync();

        Assert.False(Directory.Exists(folder));
        var d = Assert.Single(decisions);
        Assert.Equal(StaleProgressDecisionKinds.MovedToFailedPickup, d.Kind);
        Assert.Equal("orphan", d.FailureKind);
        Assert.NotNull(d.FailedPickupSlug);
        Assert.StartsWith("no-sentinel-orphan-", d.FailedPickupSlug);
        Assert.Equal(JobStates.FailedPickup, d.TargetState);

        var moved = Path.Combine(_watchPath, JobStates.FailedPickup, d.FailedPickupSlug!);
        Assert.True(Directory.Exists(moved), "folder must land in 3a-failed-pickup, not 7-archive");
        Assert.False(Directory.Exists(Path.Combine(_watchPath, JobStates.Archive, d.FailedPickupSlug!)),
            "loud-not-archived: nothing may land in 7-archive on this path");

        // Placard captures kind + timestamps so the operator sees what the
        // sweep saw without re-parsing logs.
        var placard = File.ReadAllText(Path.Combine(moved, "failed-pickup-reason.md"));
        Assert.Contains("**Kind**: orphan", placard);
        Assert.Contains("Pickup failure", placard);

        var jsonl = File.ReadAllText(Path.Combine(_workspaceRoot, "logs", "orphan-recoveries.jsonl"));
        Assert.Contains("moved-to-failed-pickup", jsonl);
        Assert.DoesNotContain("archived-orphan", jsonl);
    }

    [Fact]
    public async Task Sweep_EmptyStaleFolder_IsMovedToFailedPickupNotSilentlyArchived()
    {
        // ADR-0028: even empty stale folders surface in 3a-failed-pickup so
        // the operator sees that the runner could not resume them.
        var folder = Path.Combine(_watchPath, JobStates.Progress, "empty-shell");
        Directory.CreateDirectory(folder);
        // No job.json, no logs. MeasureFolder treats this as epoch 0 so it
        // always crosses the threshold.

        var (archiver, _) = Build();
        var decisions = await archiver.SweepAsync();

        Assert.False(Directory.Exists(folder));
        var d = Assert.Single(decisions);
        Assert.Equal(StaleProgressDecisionKinds.MovedToFailedPickup, d.Kind);
        Assert.Equal("empty", d.FailureKind);
        Assert.NotNull(d.FailedPickupSlug);
        Assert.StartsWith("empty-shell-empty-", d.FailedPickupSlug);

        var moved = Path.Combine(_watchPath, JobStates.FailedPickup, d.FailedPickupSlug!);
        Assert.True(Directory.Exists(moved));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, JobStates.Archive, d.FailedPickupSlug!)),
            "loud-not-archived: nothing may land in 7-archive on this path");

        // Empty folders gain a synthetic job.json so the kanban can render the
        // card and the state-field invariant holds.
        var jobJson = Path.Combine(moved, "job.json");
        Assert.True(File.Exists(jobJson));
        Assert.Contains(JobStates.FailedPickup, File.ReadAllText(jobJson));
    }

    [Fact]
    public async Task Sweep_FreshFolder_IsLeftAlone()
    {
        WriteJob(JobStates.Progress, "fresh");
        var folder = Path.Combine(_watchPath, JobStates.Progress, "fresh");
        WriteCliLog(folder, "still working");
        // mtime stays "now" so the folder is well within the resume window.

        var (archiver, _) = Build();
        var decisions = await archiver.SweepAsync();

        Assert.True(Directory.Exists(folder), "fresh folder must not be moved");
        var d = Assert.Single(decisions);
        Assert.Equal(StaleProgressDecisionKinds.Fresh, d.Kind);

        // Fresh verdicts are not persisted in orphan-recoveries.jsonl.
        Assert.False(File.Exists(Path.Combine(_workspaceRoot, "logs", "orphan-recoveries.jsonl")));
    }

    [Fact]
    public async Task Sweep_IsIdempotentAcrossRuns()
    {
        WriteJob(JobStates.Progress, "first-orphan");
        var f1 = Path.Combine(_watchPath, JobStates.Progress, "first-orphan");
        WriteCliLog(f1, "no sentinel here");
        SetMtimeOldEnough(Path.Combine(f1, "logs", "cli-output.log"));
        SetMtimeOldEnough(Path.Combine(f1, "job.json"));

        WriteJob(JobStates.Progress, "second-recovered");
        var f2 = Path.Combine(_watchPath, JobStates.Progress, "second-recovered");
        WriteCliLogWithSentinel(f2, "[[TASK_NEEDS_INPUT:waiting]]");
        SetMtimeOldEnough(Path.Combine(f2, "logs", "cli-output.log"));
        SetMtimeOldEnough(Path.Combine(f2, "job.json"));

        var (archiver, _) = Build();
        var first = await archiver.SweepAsync();
        Assert.Equal(2, first.Count);

        var jsonlLen1 = new FileInfo(Path.Combine(_workspaceRoot, "logs", "orphan-recoveries.jsonl")).Length;

        var second = await archiver.SweepAsync();
        Assert.Empty(second); // no candidates remain in 3-progress

        var jsonlLen2 = new FileInfo(Path.Combine(_workspaceRoot, "logs", "orphan-recoveries.jsonl")).Length;
        Assert.Equal(jsonlLen1, jsonlLen2); // no new lines on the rerun
    }

    [Fact]
    public async Task Sweep_ActiveJobIsNeverTouchedEvenWhenStale()
    {
        WriteJob(JobStates.Progress, "running-now");
        var folder = Path.Combine(_watchPath, JobStates.Progress, "running-now");
        WriteCliLog(folder, "agent mid-stream");
        SetMtimeOldEnough(Path.Combine(folder, "logs", "cli-output.log"));
        SetMtimeOldEnough(Path.Combine(folder, "job.json"));

        var (archiver, _) = Build();
        archiver.StatusProviderOverride = () => new RunnerStatus
        {
            Projects = new Dictionary<string, ProjectRunnerStatus>
            {
                [ProjectName] = new ProjectRunnerStatus
                {
                    ProjectName = ProjectName,
                    Mode = "auto-continuous",
                    ActiveJobId = "running-now"
                }
            }
        };

        var decisions = await archiver.SweepAsync();

        Assert.True(Directory.Exists(folder), "active job folder must never be moved by the sweep");
        var d = Assert.Single(decisions);
        Assert.Equal(StaleProgressDecisionKinds.Skipped, d.Kind);
    }

    [Fact]
    public async Task Sweep_ZeroWindow_DisablesPass()
    {
        WriteJob(JobStates.Progress, "would-be-orphan");
        var folder = Path.Combine(_watchPath, JobStates.Progress, "would-be-orphan");
        WriteCliLog(folder, "no sentinel");
        SetMtimeOldEnough(Path.Combine(folder, "logs", "cli-output.log"));
        SetMtimeOldEnough(Path.Combine(folder, "job.json"));

        var (archiver, _) = Build(stuckResumeWindowMinutes: 0);
        var decisions = await archiver.SweepAsync();

        Assert.True(Directory.Exists(folder));
        Assert.Empty(decisions);
    }

    private (StaleProgressArchiver Archiver, JobScannerService Scanner) Build(int stuckResumeWindowMinutes = 60)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = ProjectName,
            ["WatchPaths:0:Path"] = _watchPath,
            ["WatchPaths:0:RootPath"] = _workspaceRoot,
            ["WatchPaths:0:RepositoryPath"] = _workspaceRoot,
            ["TaskRepository"] = _workspaceRoot,
            ["Supervisor:StuckResumeWindowMinutes"] = stuckResumeWindowMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }).Build();

        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new JobScannerService(config, NullLogger<JobScannerService>.Instance, summary);
        var states = new JobStateMachine(scanner, NullLogger<JobStateMachine>.Instance);
        var mutations = new JobMutationService(scanner, NullLogger<JobMutationService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config, prompts);
        var transitions = new JobTransitionService(scanner, states, mutations, git, settings, NullLogger<JobTransitionService>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);

        // Empty service provider: tests use StatusProviderOverride to drive the
        // active-job guard, so the runner doesn't need to be instantiated.
        var sp = new ServiceCollection().BuildServiceProvider();

        var archiver = new StaleProgressArchiver(
            scanner, states, transitions, chatLog, sp, config,
            NullLogger<StaleProgressArchiver>.Instance);
        return (archiver, scanner);
    }

    private void WriteJob(string state, string slug)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "job.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"copilot\"}}");
    }

    private static void WriteCliLog(string folder, string body)
    {
        var dir = Path.Combine(folder, "logs");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "cli-output.log"),
            $"[12:00:00.000] [stdout] {body}{Environment.NewLine}");
    }

    private static void WriteCliLogWithSentinel(string folder, string sentinel)
    {
        var dir = Path.Combine(folder, "logs");
        Directory.CreateDirectory(dir);
        var lines = new List<string>();
        for (int i = 0; i < 10; i++) lines.Add($"[12:0{i}:00.000] [stdout] working line {i}");
        lines.Add($"[12:30:00.000] [stdout] {sentinel}");
        File.WriteAllText(Path.Combine(dir, "cli-output.log"), string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }

    private static void SetMtimeOldEnough(string path)
    {
        // Three hours back keeps us well past the 60-minute default window.
        var stale = DateTime.UtcNow - TimeSpan.FromHours(3);
        File.SetLastWriteTimeUtc(path, stale);
    }
}
