using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Bus;
using OrchestratorApi.Services.Cli;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Clients;
using OrchestratorApi.Services.Pty;
using OrchestratorApi.Services.Quota;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the strict-iteration progress-first pickup contract:
///
/// <list type="number">
///   <item>The pickup tick prefers ANY 3-progress folder over 2-ready -
///   even one with no <c>cli-output.log</c> and no captured session id.
///   The "no log" case is the most-restartable case (CLI never streamed
///   anything), not the most-skippable.</item>
///   <item>Iteration order is deterministic: oldest-first by mtime
///   (cli-output.log when present, else job.json, else folder).</item>
///   <item>A 3-progress folder past the retry budget is dead-lettered
///   into <c>3a-failed-pickup</c> via <see cref="JobStateMachine.MoveFolderToFailedPickup"/>
///   (single-state-machine authority), a row is appended to
///   <c>&lt;workspace&gt;/logs/pickup-failures.jsonl</c>, and the picker
///   moves on to the next 3-progress folder, then to 2-ready.</item>
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
        foreach (var state in JobStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));
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
        var folder = Path.Combine(_watchPath, JobStates.Progress, "demo-task");
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
        var folder = Path.Combine(_watchPath, JobStates.Progress, "empty");
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
        WriteJob(JobStates.Progress, "silent-progress");
        // Deliberately no cli-output.log and no session id.
        WriteJob(JobStates.Ready, "fresh-ready");

        var runner = BuildRunner();

        var folders = runner.ListProgressFoldersOldestFirst();
        var only = Assert.Single(folders);
        Assert.Equal("silent-progress", only.Slug);

        // The picker would route this to RunCliAsync; we verify it returns the
        // progress slug without taking the ready job by exercising the same
        // method shape via the public iteration helper above. The 2-ready
        // folder is on disk and would be picked only after 3-progress drains.
        Assert.True(Directory.Exists(Path.Combine(_watchPath, JobStates.Ready, "fresh-ready")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, JobStates.Progress, "silent-progress")));
    }

    // ===== Scenario 2: oldest 3-progress runs first =====

    [Fact]
    public void ListProgressFoldersOldestFirst_WithThreeProgressFolders_OrdersByMtime()
    {
        // Three progress folders in non-mtime order, plus several ready jobs.
        WriteJob(JobStates.Progress, "newest");
        SetMtime(Path.Combine(_watchPath, JobStates.Progress, "newest", "job.json"), TimeSpan.FromMinutes(-5));

        WriteJob(JobStates.Progress, "oldest");
        SetMtime(Path.Combine(_watchPath, JobStates.Progress, "oldest", "job.json"), TimeSpan.FromMinutes(-90));

        WriteJob(JobStates.Progress, "middle");
        SetMtime(Path.Combine(_watchPath, JobStates.Progress, "middle", "job.json"), TimeSpan.FromMinutes(-30));

        WriteJob(JobStates.Ready, "ready-1");
        WriteJob(JobStates.Ready, "ready-2");

        var runner = BuildRunner();

        var ordered = runner.ListProgressFoldersOldestFirst();
        Assert.Equal(new[] { "oldest", "middle", "newest" }, ordered.Select(c => c.Slug).ToArray());
    }

    // ===== Scenario 3: dead-letter on threshold =====

    /// <summary>
    /// Three 3-progress folders, all with the per-slug attempt counter primed
    /// at the failure threshold. The picker dead-letters each via the state
    /// machine, writes one row per dead-letter into pickup-failures.jsonl,
    /// and returns null so TickAsync falls through to 2-ready.
    /// </summary>
    [Fact]
    public void StrictIteration_AllProgressFoldersExhausted_DeadLettersAllAndFallsThrough()
    {
        // Three folders that never produced a CLI output line on prior pickups.
        WriteJob(JobStates.Progress, "stuck-a");
        WriteJob(JobStates.Progress, "stuck-b");
        WriteJob(JobStates.Progress, "stuck-c");

        // The "next pickup" semantics: a fresh ready job stays untouched until
        // 3-progress drains.
        WriteJob(JobStates.Ready, "ready-after-drain");

        var runner = BuildRunner();
        runner.SetPickupAttemptsForTest("stuck-a", ProjectRunner.PickupFailureThreshold);
        runner.SetPickupAttemptsForTest("stuck-b", ProjectRunner.PickupFailureThreshold);
        runner.SetPickupAttemptsForTest("stuck-c", ProjectRunner.PickupFailureThreshold);

        // Drive the picker. We invoke the private iteration helper via the
        // public-facing GetStatus path: instead of touching CLI infra we walk
        // the same scan that TickAsync would walk. The simpler path: invoke
        // the picker through the public test seam and assert effects.
        InvokePickerLoop(runner);

        // Each folder is now under 3a-failed-pickup with the dead-letter slug.
        foreach (var slug in new[] { "stuck-a", "stuck-b", "stuck-c" })
        {
            Assert.False(Directory.Exists(Path.Combine(_watchPath, JobStates.Progress, slug)),
                $"{slug} must have been moved out of 3-progress");

            var failedPickupRoot = Path.Combine(_watchPath, JobStates.FailedPickup);
            var matches = Directory.EnumerateDirectories(failedPickupRoot)
                .Where(d => Path.GetFileName(d).StartsWith($"{slug}-pickup-failed-", StringComparison.Ordinal))
                .ToList();
            Assert.Single(matches);
        }

        // pickup-failures.jsonl carries one row per dead-letter.
        var jsonlPath = Path.Combine(_workspaceRoot, "logs", "pickup-failures.jsonl");
        Assert.True(File.Exists(jsonlPath));
        var rows = File.ReadAllLines(jsonlPath).Where(l => l.Length > 0).ToList();
        Assert.Equal(3, rows.Count);
        foreach (var row in rows)
        {
            Assert.Contains("\"kind\":\"pickup-failed\"", row);
            Assert.Contains("\"projectName\":\"demo\"", row);
            Assert.Contains("\"threshold\":3", row);
            Assert.Contains("\"outputDeadlineSeconds\":60", row);
            Assert.Contains("\"destinationSlug\":", row);
        }

        // 2-ready folder is untouched - the runner reaches it only on the
        // next pickup tick now that 3-progress has drained.
        Assert.True(Directory.Exists(Path.Combine(_watchPath, JobStates.Ready, "ready-after-drain")));
    }

    /// <summary>
    /// One folder past the threshold, two below. The over-threshold folder
    /// is dead-lettered, and the next-iteration call returns one of the
    /// remaining folders. Three iterations exhaust 3-progress.
    /// </summary>
    [Fact]
    public void StrictIteration_OneExhaustedTwoFresh_DeadLettersExhaustedAndPicksNext()
    {
        WriteJob(JobStates.Progress, "exhausted");
        SetMtime(Path.Combine(_watchPath, JobStates.Progress, "exhausted", "job.json"), TimeSpan.FromMinutes(-90));
        WriteJob(JobStates.Progress, "second-oldest");
        SetMtime(Path.Combine(_watchPath, JobStates.Progress, "second-oldest", "job.json"), TimeSpan.FromMinutes(-30));
        WriteJob(JobStates.Progress, "newest");
        SetMtime(Path.Combine(_watchPath, JobStates.Progress, "newest", "job.json"), TimeSpan.FromMinutes(-5));

        var runner = BuildRunner();
        runner.SetPickupAttemptsForTest("exhausted", ProjectRunner.PickupFailureThreshold);

        InvokePickerLoop(runner);

        // 'exhausted' moved out; 'second-oldest' and 'newest' remain because
        // a single TickAsync only starts ONE job per project (ADR-0001).
        Assert.False(Directory.Exists(Path.Combine(_watchPath, JobStates.Progress, "exhausted")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, JobStates.Progress, "second-oldest")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, JobStates.Progress, "newest")));

        var jsonlPath = Path.Combine(_workspaceRoot, "logs", "pickup-failures.jsonl");
        Assert.True(File.Exists(jsonlPath));
        var rows = File.ReadAllLines(jsonlPath).Where(l => l.Length > 0).ToList();
        Assert.Single(rows);
        Assert.Contains("\"slug\":\"exhausted\"", rows[0]);
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
        WriteJob(JobStates.HumanReview, "duplicate-later-lane");
        WriteJob(JobStates.Ready, "ready-after-orphan");

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
        Assert.False(Directory.Exists(Path.Combine(_watchPath, JobStates.Progress, "duplicate-later-lane")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, JobStates.HumanReview, "duplicate-later-lane")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, JobStates.Ready, "ready-after-orphan")));

        // No orphan entry is written: this was cleanup debris, not a
        // pickup failure. 3a-failed-pickup stays clean.
        var failedPickupRoot = Path.Combine(_watchPath, JobStates.FailedPickup);
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
        WriteJob(JobStates.AutoReview, "canonical-in-auto-review");
        WriteJob(JobStates.Ready, "ready-after-orphan");

        var runner = BuildRunner();
        runner.SetMode("auto-continuous");

        var picked = InvokePickerLoop(runner);

        Assert.Null(picked);
        Assert.Equal("auto-continuous", runner.GetStatus().Mode);
        Assert.False(Directory.Exists(Path.Combine(_watchPath, JobStates.Progress, "canonical-in-auto-review")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, JobStates.AutoReview, "canonical-in-auto-review")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, JobStates.Ready, "ready-after-orphan")));

        var failedPickupRoot = Path.Combine(_watchPath, JobStates.FailedPickup);
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
    /// any post-progress lane. This is the case a manual filesystem
    /// intervention or a hard backend crash before the move leaves behind.
    /// It must still produce a loud failed-pickup entry with the canonical
    /// reason file, because there is no provenance trail to recover from.
    /// </summary>
    [Fact]
    public void StrictIteration_GenuineOrphan_NoTwin_IsMovedToFailedPickup()
    {
        WriteOrphanProgressFolder("genuine-orphan-no-twin");
        WriteJob(JobStates.Ready, "ready-after-orphan");

        var runner = BuildRunner();
        runner.SetMode("auto-continuous");

        var picked = InvokePickerLoop(runner);

        Assert.Null(picked);
        Assert.Equal("auto-continuous", runner.GetStatus().Mode);
        Assert.False(Directory.Exists(Path.Combine(_watchPath, JobStates.Progress, "genuine-orphan-no-twin")));

        var failedPickupRoot = Path.Combine(_watchPath, JobStates.FailedPickup);
        var moved = Directory.EnumerateDirectories(failedPickupRoot)
            .Where(d => Path.GetFileName(d).StartsWith("orphan-genuine-orphan-no-twin-", StringComparison.Ordinal))
            .ToList();
        var only = Assert.Single(moved);
        Assert.True(File.Exists(Path.Combine(only, "logs", "cli-output.log")));
        Assert.True(File.Exists(Path.Combine(only, "job.json")));
        Assert.True(File.Exists(Path.Combine(only, "failed-pickup-reason.md")));

        Assert.False(File.Exists(Path.Combine(_workspaceRoot, "logs", "infra-halts.jsonl")),
            "stale metadata orphans are queue-hygiene issues, not CLI infra failures");
    }

    [Fact]
    public void DeadLetterRow_IncludesAttemptHistoryWhenAvailable()
    {
        WriteJob(JobStates.Progress, "history-task");
        var runner = BuildRunner();
        runner.SetPickupAttemptsForTest("history-task", ProjectRunner.PickupFailureThreshold);

        InvokePickerLoop(runner);

        var jsonlPath = Path.Combine(_workspaceRoot, "logs", "pickup-failures.jsonl");
        var line = File.ReadAllLines(jsonlPath).Single(l => l.Length > 0);

        // Assert the row shape against the schema's required fields.
        Assert.Contains("\"at\":\"", line);
        Assert.Contains("\"kind\":\"pickup-failed\"", line);
        Assert.Contains("\"projectName\":\"demo\"", line);
        Assert.Contains("\"slug\":\"history-task\"", line);
        Assert.Contains("\"jobId\":\"history-task\"", line);
        Assert.Contains("\"destinationSlug\":\"history-task-pickup-failed-", line);
        Assert.Contains("\"attempts\":3", line);
        Assert.Contains("\"threshold\":3", line);
        Assert.Contains("\"outputDeadlineSeconds\":60", line);
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
    /// Integration scenario for the cross-slug infra breaker. Three
    /// 3-progress folders, all primed at the per-slug failure threshold,
    /// all carrying <c>cliType=copilot</c>. The first dead-letter is the
    /// 1st distinct slug for the (project, cliType) pair; the second is
    /// the 2nd and trips the breaker, flipping the runner to manual. The
    /// THIRD folder must NOT be dead-lettered - it stays in 3-progress
    /// for the operator to inspect after fixing the infra.
    /// </summary>
    [Fact]
    public void CrossSlug_TwoSpawnFailedDeadLetters_TripsBreakerAndHaltsThirdPickup()
    {
        WriteJob(JobStates.Progress, "stuck-a");
        SetMtime(Path.Combine(_watchPath, JobStates.Progress, "stuck-a", "job.json"), TimeSpan.FromMinutes(-90));
        WriteJob(JobStates.Progress, "stuck-b");
        SetMtime(Path.Combine(_watchPath, JobStates.Progress, "stuck-b", "job.json"), TimeSpan.FromMinutes(-60));
        WriteJob(JobStates.Progress, "stuck-c");
        SetMtime(Path.Combine(_watchPath, JobStates.Progress, "stuck-c", "job.json"), TimeSpan.FromMinutes(-30));

        var runner = BuildRunner();
        runner.SetMode("auto-continuous");
        runner.SetPickupAttemptsForTest("stuck-a", ProjectRunner.PickupFailureThreshold);
        runner.SetPickupAttemptsForTest("stuck-b", ProjectRunner.PickupFailureThreshold);
        runner.SetPickupAttemptsForTest("stuck-c", ProjectRunner.PickupFailureThreshold);

        InvokePickerLoop(runner);

        // Mode flipped to manual on the second dead-letter.
        Assert.Equal("manual", runner.GetStatus().Mode);

        // First two dead-lettered, third stays in 3-progress.
        Assert.False(Directory.Exists(Path.Combine(_watchPath, JobStates.Progress, "stuck-a")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, JobStates.Progress, "stuck-b")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, JobStates.Progress, "stuck-c")),
            "third folder must NOT have been dead-lettered after the cross-slug breaker tripped");

        // pickup-failures.jsonl carries exactly two rows (one per dead-letter).
        var pickupJsonl = Path.Combine(_workspaceRoot, "logs", "pickup-failures.jsonl");
        Assert.True(File.Exists(pickupJsonl));
        Assert.Equal(2, File.ReadAllLines(pickupJsonl).Count(l => l.Length > 0));

        // infra-halts.jsonl carries exactly one row.
        var infraJsonl = Path.Combine(_workspaceRoot, "logs", "infra-halts.jsonl");
        Assert.True(File.Exists(infraJsonl));
        var infraRows = File.ReadAllLines(infraJsonl).Where(l => l.Length > 0).ToList();
        Assert.Single(infraRows);
        Assert.Contains("\"kind\":\"cross-slug-spawn-failed-cascade\"", infraRows[0]);
        Assert.Contains("\"projectName\":\"demo\"", infraRows[0]);
        Assert.Contains("\"cliType\":\"copilot\"", infraRows[0]);
        Assert.Contains("\"slugs\":[\"stuck-a\",\"stuck-b\"]", infraRows[0]);
    }

    /// <summary>
    /// A single dead-letter does NOT trip the cross-slug breaker. The
    /// per-slug breaker still works the same way it did before this layer.
    /// </summary>
    [Fact]
    public void CrossSlug_OneSpawnFailedDeadLetter_DoesNotTripBreakerOrFlipMode()
    {
        WriteJob(JobStates.Progress, "stuck-a");
        WriteJob(JobStates.Progress, "stuck-b");

        var runner = BuildRunner();
        runner.SetMode("auto-continuous");
        runner.SetPickupAttemptsForTest("stuck-a", ProjectRunner.PickupFailureThreshold);
        // 'stuck-b' is healthy: not over threshold.

        InvokePickerLoop(runner);

        Assert.Equal("auto-continuous", runner.GetStatus().Mode);
        Assert.False(Directory.Exists(Path.Combine(_watchPath, JobStates.Progress, "stuck-a")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, JobStates.Progress, "stuck-b")));

        var infraJsonl = Path.Combine(_workspaceRoot, "logs", "infra-halts.jsonl");
        Assert.False(File.Exists(infraJsonl));
    }

    // ===== Helpers =====

    /// <summary>
    /// Drives the picker by reflecting into the private
    /// <c>TryPickProgressJobOrDeadLetter</c> method. Picker is the unit-of-
    /// behavior we want to test; we don't want to spin up a real CLI to
    /// observe its decisions. Fragile against rename, but the rename will
    /// fail this test at the same time as the production change.
    ///
    /// Returns the picker's verdict (the <see cref="JobInfo"/> it would
    /// hand to <c>RunCliAsync</c>, or <c>null</c> if 3-progress drained
    /// and <c>TickAsync</c> will fall through to <see cref="JobInfo"/> from
    /// 2-ready next).
    /// </summary>
    private static JobInfo? InvokePickerLoop(ProjectRunner runner)
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
        return method!.Invoke(runner, null) as JobInfo;
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
        var dir = Path.Combine(_watchPath, JobStates.Progress, slug);
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
        var scanner = new JobScannerService(config, NullLogger<JobScannerService>.Instance, summary);
        var states = new JobStateMachine(scanner, NullLogger<JobStateMachine>.Instance);
        var mutations = new JobMutationService(scanner, new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), NullLogger<JobMutationService>.Instance);
        var sessions = new JobSessionLog(scanner, NullLogger<JobSessionLog>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config, prompts);
        var transitions = new JobTransitionService(scanner, states, mutations, git, settings, NullLogger<JobTransitionService>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var orchestratorLog = new OrchestratorLog(NullLogger<OrchestratorLog>.Instance);
        var indexCache = new JobIndexCache(scanner, NullLogger<JobIndexCache>.Instance, config);
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
        var gemini = new GeminiCliService(NullLogger<GeminiCliService>.Instance, config);
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
