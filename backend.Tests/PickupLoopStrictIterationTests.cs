using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Bus;
using OrchestratorApi.Services.Cli;
using OrchestratorApi.Services.Tasks;
using OrchestratorApi.Services.Clients;
using OrchestratorApi.Services.Registry;
using OrchestratorApi.Services.Pty;
using OrchestratorApi.Services.Quota;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the strict-iteration progress-first pickup contract (routing per
/// ADR-0051 failed-pickup elimination, supersedes ADR-0028/0029):
///
/// <list type="number">
///   <item>The pickup tick prefers ANY 3-progress folder over 2-ready -
///   even one with no <c>cli-output.log</c> and no captured session id.
///   The "no log" case is the most-restartable case (CLI never streamed
///   anything), not the most-skippable.</item>
///   <item>Iteration order is deterministic: oldest-first by mtime
///   (cli-output.log when present, else job.json, else folder).</item>
///   <item>A 3-progress folder past the retry budget is no longer
///   dead-lettered. It routes by cause: a spawn failure (CLI never started)
///   returns the task to <c>2-ready</c> and pauses the runner; a task-shaped
///   silence (CLI ran but stayed quiet) or a session-less zombie escalates to
///   <c>5-human-review</c> and the picker continues. A no-<c>job.json</c>
///   orphan with no downstream twin is archived to <c>7-archive</c> as debris.
///   Every routing appends a row to
///   <c>&lt;workspace&gt;/logs/pickup-failures.jsonl</c>.</item>
/// </list>
/// </summary>
public sealed class PickupLoopStrictIterationTests : IDisposable
{
    private readonly string _watchPath;
    private readonly string _workspaceRoot;
    private const string ProjectName = "demo";

    public PickupLoopStrictIterationTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "atp-pickup-strict-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspaceRoot, "projects", ProjectName);
        Directory.CreateDirectory(_workspaceRoot);
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceRoot, recursive: true); } catch { /* best-effort */ }
    }

    // ===== Pure helpers =====

    [Fact]
    public void OrderProgressByMtime_OldestFirst_TieBreaksOnSlug()
    {
        var t = DateTime.UtcNow;
        var a = new ProgressPickupCandidate("p/a", "alpha", null, t.AddMinutes(-30));
        var b = new ProgressPickupCandidate("p/b", "bravo", null, t.AddMinutes(-10));
        var c = new ProgressPickupCandidate("p/c", "charlie", null, t.AddMinutes(-30));

        var ordered = ProjectRunner.OrderProgressByMtime(new[] { b, a, c });

        // alpha and charlie share the same mtime; tie-broken by slug ascending.
        Assert.Equal(new[] { "alpha", "charlie", "bravo" }, ordered.Select(o => o.Slug).ToArray());
    }

    [Fact]
    public void MeasureProgressFolderMtime_PrefersCliLog_FallsBackToJobJson_FallsBackToFolder()
    {
        var folder = Path.Combine(_watchPath, TaskStates.Progress, "demo-task");
        Directory.CreateDirectory(folder);

        // Just folder: returns folder mtime (well after epoch).
        var folderOnly = ProjectRunner.MeasureProgressFolderMtime(folder);
        Assert.True(folderOnly > DateTime.UtcNow.AddDays(-1));

        // Add job.json with stamped mtime: returns that.
        File.WriteAllText(Path.Combine(folder, "job.json"), "{}");
        var jobStamp = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(folder, "job.json"), jobStamp);
        Assert.Equal(jobStamp, ProjectRunner.MeasureProgressFolderMtime(folder));

        // Add cli-output.log with newer mtime: takes precedence.
        Directory.CreateDirectory(Path.Combine(folder, "logs"));
        File.WriteAllText(Path.Combine(folder, "logs", "cli-output.log"), "stream");
        var logStamp = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(folder, "logs", "cli-output.log"), logStamp);
        Assert.Equal(logStamp, ProjectRunner.MeasureProgressFolderMtime(folder));
    }

    [Fact]
    public void MeasureProgressFolderMtime_EmptyFolderWithoutFiles_ReturnsFolderMtime()
    {
        var folder = Path.Combine(_watchPath, TaskStates.Progress, "empty");
        Directory.CreateDirectory(folder);
        var stamp = new DateTime(2024, 12, 1, 8, 0, 0, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(folder, stamp);

        Assert.Equal(stamp, ProjectRunner.MeasureProgressFolderMtime(folder));
    }

    [Fact]
    public void BuildArchiveSlug_DisambiguatesOnCollision()
    {
        var d = new DateTime(2026, 5, 6, 12, 0, 0, DateTimeKind.Utc);
        // No collisions.
        Assert.Equal("foo-pickup-failed-2026-05-06",
            PickupFailureLog.BuildArchiveSlug("foo", d, _ => false));

        // First two collide.
        var taken = new HashSet<string> { "foo-pickup-failed-2026-05-06", "foo-pickup-failed-2026-05-06-2" };
        Assert.Equal("foo-pickup-failed-2026-05-06-3",
            PickupFailureLog.BuildArchiveSlug("foo", d, taken.Contains));
    }

    // ===== Scenario 1: progress-first wins over ready (even with no log) =====

    /// <summary>
    /// Production observation that drove this work: a 3-progress folder
    /// without a cli-output.log was previously skipped because the older
    /// "GetNextResumableProgressJob" filter required a captured session id.
    /// The pickup then started a fresh 2-ready job. Strict-iteration says:
    /// take the 3-progress folder anyway. The "no log" case means the CLI
    /// never streamed anything, which is the most-restartable case.
    /// </summary>
    [Fact]
    public void ListProgressFoldersOldestFirst_WithOneNoLogProgressAndOneReady_PicksProgress()
    {
        WriteJob(TaskStates.Progress, "silent-progress");
        // Deliberately no cli-output.log and no session id.
        WriteJob(TaskStates.Ready, "fresh-ready");

        var runner = BuildRunner();

        var folders = runner.ListProgressFoldersOldestFirst();
        var only = Assert.Single(folders);
        Assert.Equal("silent-progress", only.Slug);

        // The picker would route this to RunCliAsync; we verify it returns the
        // progress slug without taking the ready job by exercising the same
        // method shape via the public iteration helper above. The 2-ready
        // folder is on disk and would be picked only after 3-progress drains.
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "fresh-ready")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, "silent-progress")));
    }

    // ===== Scenario 2: oldest 3-progress runs first =====

    [Fact]
    public void ListProgressFoldersOldestFirst_WithThreeProgressFolders_OrdersByMtime()
    {
        // Three progress folders in non-mtime order, plus several ready jobs.
        WriteJob(TaskStates.Progress, "newest");
        SetMtime(Path.Combine(_watchPath, TaskStates.Progress, "newest", "job.json"), TimeSpan.FromMinutes(-5));

        WriteJob(TaskStates.Progress, "oldest");
        SetMtime(Path.Combine(_watchPath, TaskStates.Progress, "oldest", "job.json"), TimeSpan.FromMinutes(-90));

        WriteJob(TaskStates.Progress, "middle");
        SetMtime(Path.Combine(_watchPath, TaskStates.Progress, "middle", "job.json"), TimeSpan.FromMinutes(-30));

        WriteJob(TaskStates.Ready, "ready-1");
        WriteJob(TaskStates.Ready, "ready-2");

        var runner = BuildRunner();

        var ordered = runner.ListProgressFoldersOldestFirst();
        Assert.Equal(new[] { "oldest", "middle", "newest" }, ordered.Select(c => c.Slug).ToArray());
    }

    // ===== Scenario 3: over-budget routing (no dead-letter lane) =====

    /// <summary>
    /// Three 3-progress folders, all with the per-slug attempt counter primed
    /// at the failure threshold and the attempt history task-shaped (the CLI
    /// did spawn but produced no output). ADR-0051: a task-shaped over-budget
    /// folder is escalated to 5-human-review, not dead-lettered. The picker
    /// keeps going (no pause) and returns null so TickAsync falls through to
    /// 2-ready. Nothing lands in 3a-failed-pickup.
    /// </summary>
    [Fact]
    public void StrictIteration_AllProgressFoldersExhausted_EscalateToHumanReviewAndFallThrough()
    {
        // Three folders that ran the CLI but never produced a CLI output line.
        WriteJob(TaskStates.Progress, "stuck-a");
        WriteJob(TaskStates.Progress, "stuck-b");
        WriteJob(TaskStates.Progress, "stuck-c");

        // The "next pickup" semantics: a fresh ready job stays untouched until
        // 3-progress drains.
        WriteJob(TaskStates.Ready, "ready-after-drain");

        var runner = BuildRunner();
        runner.SetMode("auto-continuous");
        // No executionStatus override -> task-shaped (CLI spawned, stayed silent).
        runner.SetPickupAttemptsForTest("stuck-a", ProjectRunner.PickupFailureThreshold);
        runner.SetPickupAttemptsForTest("stuck-b", ProjectRunner.PickupFailureThreshold);
        runner.SetPickupAttemptsForTest("stuck-c", ProjectRunner.PickupFailureThreshold);

        var picked = InvokePickerLoop(runner);

        // Task-shaped escalation does not pause the runner: the folder leaves
        // 3-progress so there is no spin, and pausing would stall the queue.
        Assert.Null(picked);
        Assert.Equal("auto-continuous", runner.GetStatus().Mode);

        // Each folder is now under 5-human-review under its ORIGINAL slug; the
        // 3a-failed-pickup lane is never touched.
        foreach (var slug in new[] { "stuck-a", "stuck-b", "stuck-c" })
        {
            Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, slug)),
                $"{slug} must have been moved out of 3-progress");
            Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, slug)),
                $"{slug} must have been escalated to 5-human-review under its original slug");
        }
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.FailedPickup))
            && Directory.EnumerateDirectories(Path.Combine(_watchPath, TaskStates.FailedPickup)).Any(),
            "failed-pickup elimination: nothing may land in 3a-failed-pickup");

        // pickup-failures.jsonl carries one row per escalation.
        var jsonlPath = Path.Combine(_workspaceRoot, "logs", "pickup-failures.jsonl");
        Assert.True(File.Exists(jsonlPath));
        var rows = File.ReadAllLines(jsonlPath).Where(l => l.Length > 0).ToList();
        Assert.Equal(3, rows.Count);
        foreach (var row in rows)
        {
            Assert.Contains("\"kind\":\"escalated-human-review\"", row);
            Assert.Contains("\"projectName\":\"demo\"", row);
            Assert.Contains("\"threshold\":3", row);
            Assert.Contains("\"outputDeadlineSeconds\":60", row);
            // The folder keeps its original slug as it moves to 5-human-review.
            Assert.DoesNotContain("-pickup-failed-", row);
        }

        // 2-ready folder is untouched - the runner reaches it only on the
        // next pickup tick now that 3-progress has drained.
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "ready-after-drain")));
    }

    /// <summary>
    /// One folder past the threshold (task-shaped), two below. The over-budget
    /// folder is escalated to 5-human-review, and the next-iteration call
    /// returns one of the remaining folders to resume.
    /// </summary>
    [Fact]
    public void StrictIteration_OneExhaustedTwoFresh_EscalatesExhaustedAndPicksNext()
    {
        WriteJob(TaskStates.Progress, "exhausted");
        SetMtime(Path.Combine(_watchPath, TaskStates.Progress, "exhausted", "job.json"), TimeSpan.FromMinutes(-90));
        WriteJob(TaskStates.Progress, "second-oldest");
        SetMtime(Path.Combine(_watchPath, TaskStates.Progress, "second-oldest", "job.json"), TimeSpan.FromMinutes(-30));
        WriteJob(TaskStates.Progress, "newest");
        SetMtime(Path.Combine(_watchPath, TaskStates.Progress, "newest", "job.json"), TimeSpan.FromMinutes(-5));

        var runner = BuildRunner();
        runner.SetMode("auto-continuous");
        runner.SetPickupAttemptsForTest("exhausted", ProjectRunner.PickupFailureThreshold);

        InvokePickerLoop(runner);

        // 'exhausted' escalated to 5-human-review; 'second-oldest' and 'newest'
        // remain in 3-progress because a single tick starts ONE job (ADR-0001).
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, "exhausted")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, "exhausted")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, "second-oldest")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, "newest")));

        var jsonlPath = Path.Combine(_workspaceRoot, "logs", "pickup-failures.jsonl");
        Assert.True(File.Exists(jsonlPath));
        var rows = File.ReadAllLines(jsonlPath).Where(l => l.Length > 0).ToList();
        Assert.Single(rows);
        Assert.Contains("\"slug\":\"exhausted\"", rows[0]);
        Assert.Contains("\"kind\":\"escalated-human-review\"", rows[0]);
    }

    /// <summary>
    /// A spawn failure (every recorded attempt shows the CLI never started)
    /// is infrastructure, not a task fault. ADR-0051 cause #6: the task is
    /// returned to 2-ready unchanged and the runner pauses so it does not spin
    /// against an unavailable CLI. Nothing lands in 3a-failed-pickup; the row
    /// is kind 'requeued-ready'.
    /// </summary>
    [Fact]
    public void StrictIteration_SpawnFailureOverBudget_RequeuesToReadyAndPausesRunner()
    {
        WriteJob(TaskStates.Progress, "cli-down");
        WriteJob(TaskStates.Ready, "waiting-behind");

        var runner = BuildRunner();
        runner.SetMode("auto-continuous");
        // Every attempt shows the CLI process never spawned.
        runner.SetPickupAttemptsForTest("cli-down", ProjectRunner.PickupFailureThreshold,
            executionStatus: ProjectRunner.SpawnFailedExecutionStatus);

        var picked = InvokePickerLoop(runner);

        // Spawn failure pauses the runner so it does not loop against a dead CLI.
        Assert.Null(picked);
        Assert.Equal("manual", runner.GetStatus().Mode);

        // The task is returned to 2-ready UNCHANGED (original slug), not
        // dead-lettered and not escalated.
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, "cli-down")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "cli-down")),
            "spawn-failure task must wait in 2-ready under its original slug");
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, "cli-down")));

        var jsonlPath = Path.Combine(_workspaceRoot, "logs", "pickup-failures.jsonl");
        var row = File.ReadAllLines(jsonlPath).Single(l => l.Length > 0);
        Assert.Contains("\"kind\":\"requeued-ready\"", row);
        Assert.Contains("\"slug\":\"cli-down\"", row);
        Assert.Contains("\"destinationSlug\":\"cli-down\"", row);
    }

    [Fact]
    public void StrictIteration_PostMoveSkeleton_TwinInHumanReview_IsSilentlyDeleted()
    {
        // Setup mirrors the Windows file-handle race that produces the
        // skeleton in production: a job runs in 3-progress, the move to
        // 5-human-review succeeds for most of the tree, but a logs/* file
        // stays locked by an in-process writer and leaves an empty shell
        // behind in 3-progress while the canonical folder (with job.json)
        // lives in the downstream lane.
        WriteOrphanProgressFolder("duplicate-later-lane");
        WriteJob(TaskStates.HumanReview, "duplicate-later-lane");
        WriteJob(TaskStates.Ready, "ready-after-orphan");

        var runner = BuildRunner();
        runner.SetMode("auto-continuous");

        var picked = InvokePickerLoop(runner);

        // Returns null so TickAsync falls through to GetNextReadyJob on the
        // same tick (the 2026-05-11 root cause was the picker handing the
        // orphan back as if it were runnable). Mode stays auto-continuous.
        Assert.Null(picked);
        Assert.Equal("auto-continuous", runner.GetStatus().Mode);

        // The shell folder is gone, the downstream twin is untouched, and
        // the ready job is still in line.
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, "duplicate-later-lane")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, "duplicate-later-lane")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "ready-after-orphan")));

        // No orphan entry is written: this was cleanup debris, not a
        // pickup failure. 3a-failed-pickup stays clean.
        var failedPickupRoot = Path.Combine(_watchPath, TaskStates.FailedPickup);
        var entries = Directory.Exists(failedPickupRoot)
            ? Directory.EnumerateDirectories(failedPickupRoot)
                .Where(d => Path.GetFileName(d).Contains("duplicate-later-lane", StringComparison.Ordinal))
                .ToList()
            : new List<string>();
        Assert.Empty(entries);

        Assert.False(File.Exists(Path.Combine(_workspaceRoot, "logs", "infra-halts.jsonl")),
            "post-move skeletons are not infra failures");
    }

    /// <summary>
    /// Same shape as the human-review variant but with the downstream twin
    /// in 4-auto-review. The picker must treat every post-progress lane
    /// identically: a slug match anywhere downstream is the signal that the
    /// 3-progress remnant is cleanup debris and should be deleted silently.
    /// </summary>
    [Fact]
    public void StrictIteration_PostMoveSkeleton_TwinInAutoReview_IsSilentlyDeleted()
    {
        WriteOrphanProgressFolder("canonical-in-auto-review");
        WriteJob(TaskStates.AutoReview, "canonical-in-auto-review");
        WriteJob(TaskStates.Ready, "ready-after-orphan");

        var runner = BuildRunner();
        runner.SetMode("auto-continuous");

        var picked = InvokePickerLoop(runner);

        Assert.Null(picked);
        Assert.Equal("auto-continuous", runner.GetStatus().Mode);
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, "canonical-in-auto-review")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, "canonical-in-auto-review")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "ready-after-orphan")));

        var failedPickupRoot = Path.Combine(_watchPath, TaskStates.FailedPickup);
        var entries = Directory.Exists(failedPickupRoot)
            ? Directory.EnumerateDirectories(failedPickupRoot)
                .Where(d => Path.GetFileName(d).Contains("canonical-in-auto-review", StringComparison.Ordinal))
                .ToList()
            : new List<string>();
        Assert.Empty(entries);

        Assert.False(File.Exists(Path.Combine(_workspaceRoot, "logs", "infra-halts.jsonl")),
            "post-move skeletons are not infra failures");
    }

    /// <summary>
    /// Genuine orphan: a 3-progress folder with no job.json AND no twin in
    /// any post-progress lane. A manual filesystem intervention or a hard
    /// backend crash before the move leaves this behind. ADR-0051 cause #5:
    /// a folder with no job.json is not a runnable task, it is debris. It is
    /// archived to 7-archive with its evidence (logs, status.md) intact,
    /// never parked in a dead-end failure lane the operator must triage.
    /// </summary>
    [Fact]
    public void StrictIteration_GenuineOrphan_NoTwin_IsArchivedAsDebris()
    {
        WriteOrphanProgressFolder("genuine-orphan-no-twin");
        WriteJob(TaskStates.Ready, "ready-after-orphan");

        var runner = BuildRunner();
        runner.SetMode("auto-continuous");

        var picked = InvokePickerLoop(runner);

        Assert.Null(picked);
        Assert.Equal("auto-continuous", runner.GetStatus().Mode);
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, "genuine-orphan-no-twin")));

        // Debris lands in 7-archive with its evidence intact, not in failed-pickup.
        var archiveRoot = Path.Combine(_watchPath, TaskStates.Archive);
        var archived = Directory.EnumerateDirectories(archiveRoot)
            .Where(d => Path.GetFileName(d).StartsWith("orphan-genuine-orphan-no-twin-", StringComparison.Ordinal))
            .ToList();
        var only = Assert.Single(archived);
        Assert.True(File.Exists(Path.Combine(only, "logs", "cli-output.log")), "evidence must travel to the archive");
        Assert.True(File.Exists(Path.Combine(only, "status.md")), "evidence must travel to the archive");

        var failedPickupRoot = Path.Combine(_watchPath, TaskStates.FailedPickup);
        var inFailedPickup = Directory.Exists(failedPickupRoot)
            ? Directory.EnumerateDirectories(failedPickupRoot)
                .Where(d => Path.GetFileName(d).Contains("genuine-orphan-no-twin", StringComparison.Ordinal))
                .ToList()
            : new List<string>();
        Assert.Empty(inFailedPickup);

        Assert.False(File.Exists(Path.Combine(_workspaceRoot, "logs", "infra-halts.jsonl")),
            "stale metadata orphans are queue-hygiene issues, not CLI infra failures");
    }

    [Fact]
    public void OverBudgetRow_IncludesAttemptHistoryWhenAvailable()
    {
        WriteJob(TaskStates.Progress, "history-task");
        var runner = BuildRunner();
        runner.SetMode("auto-continuous");
        // Task-shaped (no spawn-failed status) so it escalates to 5-human-review.
        runner.SetPickupAttemptsForTest("history-task", ProjectRunner.PickupFailureThreshold);

        InvokePickerLoop(runner);

        var jsonlPath = Path.Combine(_workspaceRoot, "logs", "pickup-failures.jsonl");
        var line = File.ReadAllLines(jsonlPath).Single(l => l.Length > 0);

        // Assert the row shape against the schema's required fields. ADR-0051:
        // the task keeps its original slug as it moves to 5-human-review.
        Assert.Contains("\"at\":\"", line);
        Assert.Contains("\"kind\":\"escalated-human-review\"", line);
        Assert.Contains("\"projectName\":\"demo\"", line);
        Assert.Contains("\"slug\":\"history-task\"", line);
        Assert.Contains("\"jobId\":\"history-task\"", line);
        Assert.Contains("\"destinationSlug\":\"history-task\"", line);
        Assert.Contains("\"attempts\":3", line);
        Assert.Contains("\"threshold\":3", line);
        Assert.Contains("\"outputDeadlineSeconds\":60", line);
        Assert.Contains("\"attemptHistory\":[", line);
        Assert.Contains("\"reason\":\"", line);

        // Surface a sample JSONL row to the task job folder so the task report
        // can quote a real wire-format line. Best-effort: never fails the test.
        var sampleSink = Environment.GetEnvironmentVariable("PICKUP_FAILURE_SAMPLE_PATH");
        if (!string.IsNullOrWhiteSpace(sampleSink))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(sampleSink)!);
                File.WriteAllText(sampleSink, line + Environment.NewLine);
            }
            catch { /* sample capture is informational only */ }
        }
    }

    // ===== Cross-slug infra circuit breaker (loop-inventory:
    // pickup.cross-slug-infra-circuit-breaker) =====

    /// <summary>
    /// Integration scenario for the cross-slug infra breaker under ADR-0051.
    /// A spawn failure pauses the runner on the FIRST over-budget folder
    /// (per-task pause), so a single tick can never reach a second slug; the
    /// breaker's distinct-slug cascade therefore unfolds across ticks. Tick 1:
    /// stuck-a spawn-fails, requeues to 2-ready, mode flips to manual, the
    /// breaker records its 1st distinct slug (no audit row yet). The operator
    /// resumes (mode back to auto). Tick 2: stuck-b spawn-fails, the breaker
    /// records its 2nd distinct slug and trips, writing one
    /// <c>cross-slug-spawn-failed-cascade</c> row to infra-halts.jsonl. stuck-c
    /// is never touched.
    /// </summary>
    [Fact]
    public void CrossSlug_SpawnFailuresAcrossTicks_TripBreakerOnSecondDistinctSlug()
    {
        WriteJob(TaskStates.Progress, "stuck-a");
        SetMtime(Path.Combine(_watchPath, TaskStates.Progress, "stuck-a", "job.json"), TimeSpan.FromMinutes(-90));
        WriteJob(TaskStates.Progress, "stuck-b");
        SetMtime(Path.Combine(_watchPath, TaskStates.Progress, "stuck-b", "job.json"), TimeSpan.FromMinutes(-60));
        WriteJob(TaskStates.Progress, "stuck-c");
        SetMtime(Path.Combine(_watchPath, TaskStates.Progress, "stuck-c", "job.json"), TimeSpan.FromMinutes(-30));

        var runner = BuildRunner();
        runner.SetMode("auto-continuous");
        runner.SetPickupAttemptsForTest("stuck-a", ProjectRunner.PickupFailureThreshold,
            executionStatus: ProjectRunner.SpawnFailedExecutionStatus);
        runner.SetPickupAttemptsForTest("stuck-b", ProjectRunner.PickupFailureThreshold,
            executionStatus: ProjectRunner.SpawnFailedExecutionStatus);
        runner.SetPickupAttemptsForTest("stuck-c", ProjectRunner.PickupFailureThreshold,
            executionStatus: ProjectRunner.SpawnFailedExecutionStatus);

        // Tick 1: oldest (stuck-a) spawn-fails, requeues to 2-ready, pauses.
        InvokePickerLoop(runner);
        Assert.Equal("manual", runner.GetStatus().Mode);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "stuck-a")));
        Assert.False(File.Exists(Path.Combine(_workspaceRoot, "logs", "infra-halts.jsonl")),
            "the first distinct slug does not trip the breaker");

        // Operator fixes the CLI and resumes.
        runner.SetMode("auto-continuous");

        // Tick 2: stuck-b spawn-fails, the breaker's 2nd distinct slug trips it.
        InvokePickerLoop(runner);
        Assert.Equal("manual", runner.GetStatus().Mode);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "stuck-b")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Progress, "stuck-c")),
            "third folder must NOT have been touched after the cross-slug breaker tripped");

        // Both spawn failures requeued to 2-ready; nothing dead-lettered.
        var pickupJsonl = Path.Combine(_workspaceRoot, "logs", "pickup-failures.jsonl");
        Assert.True(File.Exists(pickupJsonl));
        var pickupRows = File.ReadAllLines(pickupJsonl).Where(l => l.Length > 0).ToList();
        Assert.Equal(2, pickupRows.Count);
        Assert.All(pickupRows, r => Assert.Contains("\"kind\":\"requeued-ready\"", r));

        // infra-halts.jsonl carries exactly one cross-slug cascade row.
        var infraJsonl = Path.Combine(_workspaceRoot, "logs", "infra-halts.jsonl");
        Assert.True(File.Exists(infraJsonl));
        var infraRows = File.ReadAllLines(infraJsonl).Where(l => l.Length > 0).ToList();
        Assert.Single(infraRows);
        Assert.Contains("\"kind\":\"cross-slug-spawn-failed-cascade\"", infraRows[0]);
        Assert.Contains("\"projectName\":\"demo\"", infraRows[0]);
        Assert.Contains("\"cliType\":\"copilot\"", infraRows[0]);
        Assert.Contains("\"slugs\":[\"stuck-a\",\"stuck-b\"]", infraRows[0]);
    }

    // ===== Helpers =====

    /// <summary>
    /// Drives the picker by reflecting into the private
    /// <c>TryPickProgressJobOrDeadLetter</c> method. Picker is the unit-of-
    /// behavior we want to test; we don't want to spin up a real CLI to
    /// observe its decisions. Fragile against rename, but the rename will
    /// fail this test at the same time as the production change.
    ///
    /// Returns the picker's verdict (the <see cref="TaskInfo"/> it would
    /// hand to <c>RunCliAsync</c>, or <c>null</c> if 3-progress drained
    /// and <c>TickAsync</c> will fall through to <see cref="TaskInfo"/> from
    /// 2-ready next).
    /// </summary>
    private static TaskInfo? InvokePickerLoop(ProjectRunner runner)
    {
        var method = typeof(ProjectRunner).GetMethod("TryPickProgressJobOrDeadLetter",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        // One call: the picker walks the full 3-progress lane, dead-letters
        // every folder past the threshold, and stops at the first folder
        // still under the threshold (returning it) or returns null when
        // all folders were exhausted. That single-call shape matches what
        // TickAsync invokes; tests that need multiple ticks can call this
        // helper repeatedly.
        return method!.Invoke(runner, null) as TaskInfo;
    }

    private void WriteJob(string state, string slug)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        // Pre-stamp ownerClientId so the scanner's owner-id migration sweep
        // does not rewrite job.json on first scan (which would clobber the
        // mtime values the ordering tests rely on).
        File.WriteAllText(Path.Combine(dir, "job.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"copilot\",\"cliType\":\"copilot\",\"ownerClientId\":\"local-default\"}}");
    }

    private void WriteOrphanProgressFolder(string slug)
    {
        var dir = Path.Combine(_watchPath, TaskStates.Progress, slug);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        File.WriteAllText(Path.Combine(dir, "logs", "cli-output.log"), "orphan log");
        File.WriteAllText(Path.Combine(dir, "status.md"), "orphan status");
    }

    private static void SetMtime(string path, TimeSpan offset)
    {
        var stamp = DateTime.UtcNow + offset;
        File.SetLastWriteTimeUtc(path, stamp);
    }

    private ProjectRunner BuildRunner()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
                ["WatchPaths:0:RepositoryPath"] = _watchPath,
                ["TaskRepository"] = _workspaceRoot
            })
            .Build();

        var entry = new WatchPathEntry
        {
            Name = ProjectName,
            Path = _watchPath,
            RootPath = _watchPath,
            RepositoryPath = _watchPath
        };

        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var mutations = new TaskMutationService(scanner, new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance), new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
        var sessions = new TaskSessionLog(scanner, NullLogger<TaskSessionLog>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config, prompts);
        var transitions = new TaskTransitionService(scanner, states, mutations, git, settings, NullLogger<TaskTransitionService>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var orchestratorLog = new OrchestratorLog(NullLogger<OrchestratorLog>.Instance);
        var indexCache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(indexCache);
        var taskAccess = new OrchestratorApi.Services.TaskAccess.TaskAccessService(
            scanner, mutations, states, transitions, indexCache,
            NullLogger<OrchestratorApi.Services.TaskAccess.TaskAccessService>.Instance);

        var cliEnv = new CopilotCliEnvironment(NullLogger<CopilotCliEnvironment>.Instance);
        var copilot = new CopilotCliService(
            NullLogger<CopilotCliService>.Instance, config,
            new CopilotModelDiscovery(NullLogger<CopilotModelDiscovery>.Instance, cliEnv, config),
            cliEnv);
        var claude = new ClaudeCliService(NullLogger<ClaudeCliService>.Instance, config);
        var codexDiscovery = new CodexModelDiscovery(NullLogger<CodexModelDiscovery>.Instance, config);
        var codex = new CodexCliService(NullLogger<CodexCliService>.Instance, config, codexDiscovery,
            new CliUsageParserRegistry(new ICliUsageParser[] { new CodexUsageParser() }),
            new CliModelRegistry());
        var gemini = new AntigravityCliService(NullLogger<AntigravityCliService>.Instance, config);
        var router = new CliRouter(copilot, claude, codex, gemini);

        var orchestratorRunner = new OrchestratorRunner(claude, NullLogger<OrchestratorRunner>.Instance);
        var orchestratorSessions = new OrchestratorSessionStore(NullLogger<OrchestratorSessionStore>.Instance);

        var quotaCacheStore = new QuotaCacheStore(config, NullLogger<QuotaCacheStore>.Instance);
        var quotaService = new QuotaService(NullLogger<QuotaService>.Instance, Array.Empty<IQuotaProbe>(), config, quotaCacheStore);
        var quotaCaps = new CliQuotaCapsService(NullLogger<CliQuotaCapsService>.Instance, config);
        var pickupFailures = new PickupFailureLog(config, NullLogger<PickupFailureLog>.Instance);
        var infraHaltLog = new InfraHaltLog(config, NullLogger<InfraHaltLog>.Instance);
        var infraBreaker = new CrossSlugInfraCircuitBreaker(config, NullLogger<CrossSlugInfraCircuitBreaker>.Instance, infraHaltLog);

        return new ProjectRunner(
            ProjectName, entry,
            NullLogger<ProjectRunner>.Instance,
            scanner, states, sessions, router,
            summary, prompts, transitions, chatLog, mutations,
            orchestratorLog, orchestratorRunner, orchestratorSessions,
            settings, quotaService, quotaCaps, git, pickupFailures, infraBreaker, taskAccess, bus: null);
    }
}
