using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Cli;
using OrchestratorApi.Services.Jobs;
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
    }

    // ===== Helpers =====

    /// <summary>
    /// Drives the picker by reflecting into the private
    /// <c>TryPickProgressJobOrDeadLetter</c> method. Picker is the unit-of-
    /// behavior we want to test; we don't want to spin up a real CLI to
    /// observe its decisions. Fragile against rename, but the rename will
    /// fail this test at the same time as the production change.
    /// </summary>
    private static void InvokePickerLoop(ProjectRunner runner)
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
        method!.Invoke(runner, null);
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
        var mutations = new JobMutationService(scanner, NullLogger<JobMutationService>.Instance);
        var sessions = new JobSessionLog(scanner, NullLogger<JobSessionLog>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config, prompts);
        var transitions = new JobTransitionService(scanner, states, mutations, git, settings, NullLogger<JobTransitionService>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var orchestratorLog = new OrchestratorLog(NullLogger<OrchestratorLog>.Instance);

        var cliEnv = new CopilotCliEnvironment(NullLogger<CopilotCliEnvironment>.Instance);
        var copilot = new CopilotCliService(
            NullLogger<CopilotCliService>.Instance, config,
            new CopilotModelDiscovery(NullLogger<CopilotModelDiscovery>.Instance, cliEnv, config),
            cliEnv);
        var claude = new ClaudeCliService(NullLogger<ClaudeCliService>.Instance, config);
        var codexDiscovery = new CodexModelDiscovery(NullLogger<CodexModelDiscovery>.Instance, config);
        var codex = new CodexCliService(NullLogger<CodexCliService>.Instance, config, codexDiscovery);
        var gemini = new GeminiCliService(NullLogger<GeminiCliService>.Instance, config);
        var router = new CliRouter(copilot, claude, codex, gemini);

        var orchestratorRunner = new OrchestratorRunner(claude, NullLogger<OrchestratorRunner>.Instance);
        var orchestratorSessions = new OrchestratorSessionStore(NullLogger<OrchestratorSessionStore>.Instance);

        var quotaCacheStore = new QuotaCacheStore(config, NullLogger<QuotaCacheStore>.Instance);
        var quotaService = new QuotaService(NullLogger<QuotaService>.Instance, Array.Empty<IQuotaProbe>(), config, quotaCacheStore);
        var quotaCaps = new CliQuotaCapsService(NullLogger<CliQuotaCapsService>.Instance, config);
        var pickupFailures = new PickupFailureLog(config, NullLogger<PickupFailureLog>.Instance);

        return new ProjectRunner(
            ProjectName, entry,
            NullLogger<ProjectRunner>.Instance,
            scanner, states, sessions, router,
            summary, prompts, transitions, chatLog, mutations,
            orchestratorLog, orchestratorRunner, orchestratorSessions,
            settings, quotaService, quotaCaps, git, pickupFailures, bus: null);
    }
}
