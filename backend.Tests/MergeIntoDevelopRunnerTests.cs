using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

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
    public async Task RunAsync_MainTarget_RunsFullSuiteOnExactSourceBeforeFastForward()
    {
        var repo = SeedRepo("runner-main-full-suite");
        RunGit(repo, "checkout -q -b task/50");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "release work");
        Commit(repo, "feat: release work");
        var taskSha = RunGit(repo, "rev-parse task/50").Out.Trim();
        var mainBefore = RunGit(repo, "rev-parse main").Out.Trim();
        RunGit(repo, "checkout -q main");

        var (git, log, settings) = BuildWithSettings(repo);
        settings.SetBuildProfile("Fixture", new BuildProfile
        {
            TestCmds = [TagMarker("pre-main-suite-ran")],
        });
        var gateRunner = new BuildTestGateRunner(
            NullLogger<BuildTestGateRunner>.Instance);
        var runner = new MergeIntoDevelopRunner(
            git,
            log,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: settings,
            preMainTestGate: new PreMainTestGate(gateRunner),
            preMainTimeout: TimeSpan.FromSeconds(30));
        var jobFolder = BeginRun(log, repo, jobId: "50");

        var outcome = await runner.RunAsync(
            "Fixture",
            "50",
            jobFolder,
            repo,
            "main",
            CancellationToken.None);

        Assert.Equal(MergeIntoIntegrationOutcome.Merged, outcome.Outcome);
        Assert.Equal(taskSha, outcome.MergedSha);
        Assert.NotEqual(mainBefore, taskSha);
        Assert.Equal(taskSha, RunGit(repo, "rev-parse main").Out.Trim());
        Assert.True(HasTag(repo, "pre-main-suite-ran"), "the declared full-suite test command must run before main advances");

        var evidencePath = Assert.Single(
            Directory.GetFiles(Path.Combine(jobFolder, "post-steps"), "pre-main-test-gate-*.log"));
        var evidence = File.ReadAllText(evidencePath);
        Assert.Contains($"expectedSha={taskSha}", evidence);
        Assert.Contains($"testedSha={taskSha}", evidence);
        Assert.Contains("\"Level\": \"full\"", evidence);
        Assert.Contains("\"FullSuiteRequired\": true", evidence);
        Assert.Contains("\"FullSuiteRan\": true", evidence);

        var step = ReadMergeStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Passed, step!.Status);
        Assert.Contains("mandatory full suite passed", step.Reason);
        Assert.Contains("full-suite=required-and-run", step.VerdictSummary);
    }

    [Fact]
    public async Task RunAsync_MainTarget_RedFullSuiteLeavesMainUnchanged()
    {
        var repo = SeedRepo("runner-main-red");
        RunGit(repo, "checkout -q -b task/51");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "blocked release work");
        Commit(repo, "feat: blocked release work");
        var mainBefore = RunGit(repo, "rev-parse main").Out.Trim();
        RunGit(repo, "checkout -q main");

        var (git, log, settings) = BuildWithSettings(repo);
        var gateRunner = new CapturingBuildTestGateRunner(new BuildTestGateResult(
            BuildTestGateVerdict.Fail,
            1,
            20,
            "release regression",
            "full-suite test failed",
            true,
            false)
        {
            TestSelection = new TestSelectionAudit
            {
                Level = TestExecutionLevels.Full,
                FullSuiteRequired = true,
                FullSuiteRan = true,
            },
        });
        var runner = new MergeIntoDevelopRunner(
            git,
            log,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: settings,
            preMainTestGate: new PreMainTestGate(gateRunner));
        var jobFolder = BeginRun(log, repo, jobId: "51");

        var outcome = await runner.RunAsync(
            "Fixture",
            "51",
            jobFolder,
            repo,
            "main",
            CancellationToken.None);

        Assert.Equal(MergeIntoIntegrationOutcome.Error, outcome.Outcome);
        Assert.Contains("Pre-main full suite blocked", outcome.Error);
        Assert.Equal(mainBefore, RunGit(repo, "rev-parse main").Out.Trim());
        Assert.Equal(1, gateRunner.Invocations);
        Assert.Equal(TestExecutionLevels.Full, gateRunner.Request!.RequiredTestLevel);
        Assert.True(gateRunner.Request.RequireExactSubject);

        var step = ReadMergeStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Failed, step!.Status);
        Assert.Contains("full-suite test failed", step.Reason);
    }

    [Fact]
    public async Task RunAsync_MainTarget_SourceMovesDuringSuiteLeavesMainUnchanged()
    {
        var repo = SeedRepo("runner-main-source-moved");
        RunGit(repo, "checkout -q -b task/52");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "tested release work");
        Commit(repo, "feat: tested release work");
        var testedSha = RunGit(repo, "rev-parse task/52").Out.Trim();
        File.WriteAllText(Path.Combine(repo, "late.txt"), "late untested work");
        Commit(repo, "feat: late untested work");
        RunGit(repo, "branch task/52-next");
        RunGit(repo, "checkout -q main");
        RunGit(repo, $"branch -f task/52 {testedSha}");
        var mainBefore = RunGit(repo, "rev-parse main").Out.Trim();

        var (git, log, settings) = BuildWithSettings(repo);
        var gateRunner = new CapturingBuildTestGateRunner(
            new BuildTestGateResult(
                BuildTestGateVerdict.Ok,
                0,
                20,
                "",
                "full suite passed",
                true,
                false)
            {
                TestSelection = new TestSelectionAudit
                {
                    Level = TestExecutionLevels.Full,
                    FullSuiteRequired = true,
                    FullSuiteRan = true,
                },
            },
            () => RunGit(repo, "branch -f task/52 task/52-next"));
        var runner = new MergeIntoDevelopRunner(
            git,
            log,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: settings,
            preMainTestGate: new PreMainTestGate(gateRunner));
        var jobFolder = BeginRun(log, repo, jobId: "52");

        var outcome = await runner.RunAsync(
            "Fixture",
            "52",
            jobFolder,
            repo,
            "main",
            CancellationToken.None);

        Assert.Equal(MergeIntoIntegrationOutcome.Error, outcome.Outcome);
        Assert.Contains("moved after the pre-main test run", outcome.Error);
        Assert.Equal(mainBefore, RunGit(repo, "rev-parse main").Out.Trim());
        Assert.Equal(testedSha, gateRunner.Request!.ExpectedSha);
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

    // ---- AGT-1999: integration-branch push to origin -----------------------

    [Fact]
    public async Task PushIntegrationBranch_PushesDevelopToOrigin_AndRecordsStepPassed()
    {
        var (repo, remote) = SeedRepoWithOrigin("push-develop");
        // develop is ahead of origin (origin has only main).
        RunGit(repo, "checkout -q -b develop");
        File.WriteAllText(Path.Combine(repo, "dev.txt"), "dev work");
        Commit(repo, "feat: dev work");
        var localHead = RunGit(repo, "rev-parse refs/heads/develop").Out.Trim();
        Assert.NotEqual(localHead, RemoteSha(remote, "develop")); // not on origin yet

        var (git, log) = Build(repo);
        var jobFolder = BeginRun(log, repo, jobId: "30");
        var runner = new MergeIntoDevelopRunner(git, log, NullLogger<MergeIntoDevelopRunner>.Instance);

        var result = await runner.PushIntegrationBranchAsync("Fixture", "30", jobFolder, repo, "develop");

        Assert.True(result.Success);
        Assert.Equal("pushed", result.Status);
        Assert.Equal(localHead, RemoteSha(remote, "develop"));

        var step = ReadPushStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Passed, step!.Status);
        Assert.Equal("pushed", step.Verdict);
    }

    [Fact]
    public async Task PushIntegrationBranch_AlreadyOnOrigin_RecordsAlreadyRemote()
    {
        var (repo, remote) = SeedRepoWithOrigin("push-noop");
        RunGit(repo, "checkout -q -b develop");
        File.WriteAllText(Path.Combine(repo, "dev.txt"), "dev work");
        Commit(repo, "feat: dev work");
        // Already push develop so a second push is a no-op ancestor short-circuit.
        RunGit(repo, "push -q origin develop");

        var (git, log) = Build(repo);
        var jobFolder = BeginRun(log, repo, jobId: "31");
        var runner = new MergeIntoDevelopRunner(git, log, NullLogger<MergeIntoDevelopRunner>.Instance);

        var result = await runner.PushIntegrationBranchAsync("Fixture", "31", jobFolder, repo, "develop");

        Assert.True(result.Success);
        Assert.Equal("already-remote", result.Status);
        var step = ReadPushStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Passed, step!.Status);
        Assert.Equal("already-remote", step.Verdict);
    }

    [Fact]
    public async Task PushIntegrationBranch_NoOriginRemote_RecordsSkipped()
    {
        // A local-only checkout (no origin) is a benign skip, not a failure.
        var repo = SeedRepo("push-no-remote");
        RunGit(repo, "checkout -q -b develop");
        File.WriteAllText(Path.Combine(repo, "dev.txt"), "dev work");
        Commit(repo, "feat: dev work");

        var (git, log) = Build(repo);
        var jobFolder = BeginRun(log, repo, jobId: "32");
        var runner = new MergeIntoDevelopRunner(git, log, NullLogger<MergeIntoDevelopRunner>.Instance);

        var result = await runner.PushIntegrationBranchAsync("Fixture", "32", jobFolder, repo, "develop");

        Assert.True(result.Success);
        Assert.Equal("no-remote", result.Status);
        var step = ReadPushStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Skipped, step!.Status);
        Assert.Equal("no-remote", step.Verdict);
    }

    [Fact]
    public async Task PushIntegrationBranch_TransientFailure_RetriesThenRecordsEnvironmental()
    {
        // Point origin at a path that is not a repository so every push fails with
        // a generic (non-fast-forward-free) error -> classified environmental ->
        // retried per the AGT-1944 taxonomy (zero backoff here) -> recorded as a
        // visible Failed step flagged environmental, never silently dropped.
        var repo = SeedRepo("push-transient");
        RunGit(repo, $"remote add origin \"{Path.Combine(_tempDir, "does-not-exist.git")}\"");
        RunGit(repo, "checkout -q -b develop");
        File.WriteAllText(Path.Combine(repo, "dev.txt"), "dev work");
        Commit(repo, "feat: dev work");

        var (git, log) = Build(repo);
        var jobFolder = BeginRun(log, repo, jobId: "33");
        var runner = new MergeIntoDevelopRunner(
            git, log, NullLogger<MergeIntoDevelopRunner>.Instance, environmentalBackoff: _ => TimeSpan.Zero);

        var result = await runner.PushIntegrationBranchAsync("Fixture", "33", jobFolder, repo, "develop");

        Assert.False(result.Success);
        Assert.Equal("failed", result.Status);
        var step = ReadPushStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Failed, step!.Status);
        Assert.Equal("environmental", step.Verdict);
    }

    [Fact]
    public void Run_MergedAndPushEnabled_EnqueuesDevelopPush_OffTheRequestPath()
    {
        // The merge runs synchronously on the accept trigger; the origin push is
        // handed off to the queue (the same offload shape as the completed-job
        // workspace push) so the transition never awaits the network. This is the
        // request-path guard: Run returns after an instant channel write.
        var repo = SeedRepo("run-enqueue");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b task/40");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "task work");
        Commit(repo, "feat: task work");
        RunGit(repo, "checkout -q develop");

        var (git, log, settings) = BuildWithSettings(repo);
        var queue = new IntegrationPushQueue();
        var jobFolder = BeginRun(log, repo, jobId: "40");
        var runner = new MergeIntoDevelopRunner(
            git, log, NullLogger<MergeIntoDevelopRunner>.Instance, pushQueue: queue, projectSettings: settings);

        var outcome = runner.Run("Fixture", "40", jobFolder, repo, "develop");

        Assert.Equal(MergeIntoIntegrationOutcome.Merged, outcome.Outcome);
        Assert.True(queue.Reader.TryRead(out var queued), "a develop push must be enqueued after a successful merge");
        Assert.Equal("40", queued!.JobId);
        Assert.Equal("develop", queued.IntegrationBranch);
    }

    [Fact]
    public void Run_MergedButPushDisabled_DoesNotEnqueue()
    {
        var repo = SeedRepo("run-disabled");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b task/41");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "task work");
        Commit(repo, "feat: task work");
        RunGit(repo, "checkout -q develop");

        var (git, log, settings) = BuildWithSettings(repo);
        settings.SetPipelineStep("Fixture", PipelineCatalogue.MergeIntoDevelopPushStepId, new PipelineStepSetting { Enabled = false });
        var queue = new IntegrationPushQueue();
        var jobFolder = BeginRun(log, repo, jobId: "41");
        var runner = new MergeIntoDevelopRunner(
            git, log, NullLogger<MergeIntoDevelopRunner>.Instance, pushQueue: queue, projectSettings: settings);

        var outcome = runner.Run("Fixture", "41", jobFolder, repo, "develop");

        Assert.Equal(MergeIntoIntegrationOutcome.Merged, outcome.Outcome);
        Assert.False(queue.Reader.TryRead(out _), "no push must be enqueued when the push step is disabled");
    }

    [Fact]
    public async Task IntegrationPushWorker_DrainsQueue_AndPushesDevelop()
    {
        var (repo, remote) = SeedRepoWithOrigin("worker-push");
        RunGit(repo, "checkout -q -b develop");
        File.WriteAllText(Path.Combine(repo, "dev.txt"), "dev work");
        Commit(repo, "feat: dev work");
        var localHead = RunGit(repo, "rev-parse refs/heads/develop").Out.Trim();

        var (git, log) = Build(repo);
        var jobFolder = BeginRun(log, repo, jobId: "42");
        var runner = new MergeIntoDevelopRunner(git, log, NullLogger<MergeIntoDevelopRunner>.Instance);
        var queue = new IntegrationPushQueue();
        var worker = new IntegrationPushWorker(queue, runner, NullLogger<IntegrationPushWorker>.Instance);

        queue.Enqueue(new IntegrationPushRequest("Fixture", "42", jobFolder, repo, "develop"));
        Assert.True(queue.Reader.TryRead(out var request));
        await worker.ProcessAsync(request!, CancellationToken.None);

        Assert.Equal(localHead, RemoteSha(remote, "develop"));
        var step = ReadPushStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Passed, step!.Status);
    }

    // ---- Pre-develop build gate: the merge RESULT must compile ---------------

    [Fact]
    public async Task RunAsync_DevelopTarget_RedBuildGate_RollsBackAndBlocksThePush()
    {
        var repo = SeedRepo("develop-gate-red");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b task/60");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "task work");
        Commit(repo, "feat: task work");
        RunGit(repo, "checkout -q develop");
        var developBefore = RunGit(repo, "rev-parse develop").Out.Trim();

        var (git, log, settings) = BuildWithSettings(repo);
        settings.SetBuildProfile("Fixture", new BuildProfile { BuildCmds = ["cd ."] });
        var gateRunner = new CapturingBuildTestGateRunner(new BuildTestGateResult(
            BuildTestGateVerdict.Fail, 1, 20, "CS0103: the merge does not compile",
            "backend build exit 1", true, false));
        var queue = new IntegrationPushQueue();
        var jobFolder = BeginRun(log, repo, jobId: "60");
        var runner = new MergeIntoDevelopRunner(
            git, log, NullLogger<MergeIntoDevelopRunner>.Instance,
            pushQueue: queue,
            projectSettings: settings,
            preDevelopBuildGate: new PreDevelopBuildGate(gateRunner),
            preDevelopTimeout: TimeSpan.FromSeconds(30));

        var outcome = await runner.RunAsync(
            "Fixture", "60", jobFolder, repo, "develop", CancellationToken.None);

        // The gate saw the exact merge commit, not the delivery and not a branch name.
        Assert.Equal(1, gateRunner.Invocations);
        Assert.Equal(TestExecutionLevels.BuildOnly, gateRunner.Request!.RequiredTestLevel);
        Assert.True(gateRunner.Request.RequireExactSubject);
        Assert.NotEqual(developBefore, gateRunner.Request.ExpectedSha);

        // Red gate: develop is back on its exact pre-merge tip, nothing pushed.
        Assert.Equal(MergeIntoIntegrationOutcome.GateFailed, outcome.Outcome);
        Assert.Equal(developBefore, RunGit(repo, "rev-parse develop").Out.Trim());
        Assert.Equal(string.Empty, RunGit(repo, "status --porcelain").Out.Trim());
        Assert.False(queue.Reader.TryRead(out _), "a gate-blocked merge must never enqueue a push");

        var step = ReadMergeStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Failed, step!.Status);
        Assert.Equal("gate-failed", step.Verdict);
        Assert.Contains("backend build exit 1", step.Reason);
        Assert.Contains("rolled back", step.Reason);
    }

    [Fact]
    public async Task RunAsync_DevelopTarget_GreenBuildGate_KeepsTheMergeAndEnqueuesThePush()
    {
        var repo = SeedRepo("develop-gate-green");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b task/61");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "task work");
        Commit(repo, "feat: task work");
        RunGit(repo, "checkout -q develop");
        var developBefore = RunGit(repo, "rev-parse develop").Out.Trim();

        var (git, log, settings) = BuildWithSettings(repo);
        settings.SetBuildProfile("Fixture", new BuildProfile { BuildCmds = ["cd ."] });
        var gateRunner = new CapturingBuildTestGateRunner(new BuildTestGateResult(
            BuildTestGateVerdict.Ok, 0, 20, "", "verify gate passed (build-profile)", true, false));
        var queue = new IntegrationPushQueue();
        var jobFolder = BeginRun(log, repo, jobId: "61");
        var runner = new MergeIntoDevelopRunner(
            git, log, NullLogger<MergeIntoDevelopRunner>.Instance,
            pushQueue: queue,
            projectSettings: settings,
            preDevelopBuildGate: new PreDevelopBuildGate(gateRunner),
            preDevelopTimeout: TimeSpan.FromSeconds(30));

        var outcome = await runner.RunAsync(
            "Fixture", "61", jobFolder, repo, "develop", CancellationToken.None);

        Assert.Equal(MergeIntoIntegrationOutcome.Merged, outcome.Outcome);
        var developAfter = RunGit(repo, "rev-parse develop").Out.Trim();
        Assert.NotEqual(developBefore, developAfter);
        Assert.Equal(developAfter, outcome.MergedSha);
        Assert.Equal(developAfter, gateRunner.Request!.ExpectedSha);
        Assert.Equal(0, RunGit(repo, "rev-parse --verify develop^2").Code); // merge commit stands
        Assert.True(queue.Reader.TryRead(out var queued), "a green gate merges and pushes as before");
        Assert.Equal("develop", queued!.IntegrationBranch);

        var step = ReadMergeStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Passed, step!.Status);
        Assert.Equal("merged", step.Verdict);
        Assert.Contains("build gate passed", step.Reason);
    }

    [Fact]
    public async Task RunAsync_DevelopTarget_BuildGateRunsBuildCommandsWithoutTheSuite()
    {
        // Build-only stage against the REAL gate runner: the declared build
        // command runs on the merge result, the declared test command does not.
        var repo = SeedRepo("develop-gate-build-only");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b task/62");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "task work");
        Commit(repo, "feat: task work");
        RunGit(repo, "checkout -q develop");

        var (git, log, settings) = BuildWithSettings(repo);
        settings.SetBuildProfile("Fixture", new BuildProfile
        {
            BuildCmds = [TagMarker("gate-build-ran")],
            TestCmds = [TagMarker("gate-test-ran")],
        });
        var jobFolder = BeginRun(log, repo, jobId: "62");
        var runner = new MergeIntoDevelopRunner(
            git, log, NullLogger<MergeIntoDevelopRunner>.Instance,
            projectSettings: settings,
            preDevelopBuildGate: new PreDevelopBuildGate(
                new BuildTestGateRunner(NullLogger<BuildTestGateRunner>.Instance)),
            preDevelopTimeout: TimeSpan.FromSeconds(60));

        var outcome = await runner.RunAsync(
            "Fixture", "62", jobFolder, repo, "develop", CancellationToken.None);

        Assert.True(
            outcome.Outcome == MergeIntoIntegrationOutcome.Merged,
            outcome.Error ?? outcome.Outcome.ToString());
        Assert.True(HasTag(repo, "gate-build-ran"), "the declared build command must run on the merge result");
        Assert.False(HasTag(repo, "gate-test-ran"), "the build-only stage must not run the test suite again");

        var evidencePath = Assert.Single(
            Directory.GetFiles(Path.Combine(jobFolder, "post-steps"), "pre-develop-build-gate-*.log"));
        var evidence = File.ReadAllText(evidencePath);
        Assert.Contains($"expectedSha={outcome.MergedSha}", evidence);
        Assert.Contains($"testedSha={outcome.MergedSha}", evidence);
        Assert.Contains("\"Level\": \"build-only\"", evidence);
    }

    [Fact]
    public async Task RunAsync_DevelopTarget_WithoutBuildProfile_MergesUngated()
    {
        var repo = SeedRepo("develop-gate-absent");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b task/63");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "task work");
        Commit(repo, "feat: task work");
        RunGit(repo, "checkout -q develop");

        var (git, log, settings) = BuildWithSettings(repo);
        var gateRunner = new CapturingBuildTestGateRunner(new BuildTestGateResult(
            BuildTestGateVerdict.Fail, 1, 20, "", "must never be consulted", true, false));
        var queue = new IntegrationPushQueue();
        var jobFolder = BeginRun(log, repo, jobId: "63");
        var runner = new MergeIntoDevelopRunner(
            git, log, NullLogger<MergeIntoDevelopRunner>.Instance,
            pushQueue: queue,
            projectSettings: settings,
            preDevelopBuildGate: new PreDevelopBuildGate(gateRunner));

        var outcome = await runner.RunAsync(
            "Fixture", "63", jobFolder, repo, "develop", CancellationToken.None);

        // Convention instead of a settings switch: no build profile, no gate.
        Assert.Equal(0, gateRunner.Invocations);
        Assert.Equal(MergeIntoIntegrationOutcome.Merged, outcome.Outcome);
        Assert.Equal(0, RunGit(repo, "rev-parse --verify develop^2").Code);
        Assert.True(queue.Reader.TryRead(out _));

        var step = ReadMergeStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Passed, step!.Status);
        Assert.Equal("merged", step.Verdict);
        Assert.DoesNotContain("build gate", step.Reason ?? string.Empty);
    }

    [Fact]
    public void Run_LocalDelivery_DivergedIntegrationBranch_ReportsHealingErrorInsteadOfMergingStale()
    {
        // Local develop and origin/develop both moved on from main: a real
        // divergence. The local task-branch path used to merge onto the stale
        // local tip and report success; it must now say so and merge nothing.
        var (repo, _) = SeedRepoWithOrigin("develop-diverged");
        RunGit(repo, "checkout -q -b develop");
        File.WriteAllText(Path.Combine(repo, "local.txt"), "local develop work");
        Commit(repo, "chore: local develop work");
        RunGit(repo, "push -q -u origin develop");

        // Rewrite origin/develop onto an unrelated commit -> histories diverge.
        RunGit(repo, "checkout -q -b origin-side main");
        File.WriteAllText(Path.Combine(repo, "remote.txt"), "remote develop work");
        Commit(repo, "chore: remote develop work");
        RunGit(repo, "push -q -f origin origin-side:develop");

        RunGit(repo, "checkout -q develop");
        RunGit(repo, "checkout -q -b task/64");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "task work");
        Commit(repo, "feat: task work");
        RunGit(repo, "checkout -q develop");
        var developBefore = RunGit(repo, "rev-parse develop").Out.Trim();

        var (git, log) = Build(repo);
        var jobFolder = BeginRun(log, repo, jobId: "64");
        var runner = new MergeIntoDevelopRunner(git, log, NullLogger<MergeIntoDevelopRunner>.Instance);

        var outcome = runner.Run("Fixture", "64", jobFolder, repo, "develop");

        Assert.Equal(MergeIntoIntegrationOutcome.Error, outcome.Outcome);
        Assert.Contains(
            "Integration branch 'develop' diverged from origin - heal or recreate it via project settings before accepting deliveries.",
            outcome.Error);
        Assert.Equal(developBefore, RunGit(repo, "rev-parse develop").Out.Trim());

        var step = ReadMergeStep(log, jobFolder);
        Assert.NotNull(step);
        Assert.Equal(PipelineStepStatus.Failed, step!.Status);
        Assert.Equal("error", step.Verdict);
    }

    /// <summary>
    /// A verify command that leaves a durable, checkable trace and exits zero on
    /// every platform: it tags the commit the gate actually checked out. Git tags
    /// are written to the shared repository, so the test can read them from the
    /// main checkout after the gate's isolated worktree is gone. Deliberately
    /// quote- and path-free: the gate launches commands through
    /// <c>cmd.exe /c</c> with an argument list, and cmd does not understand the
    /// backslash-escaped quotes .NET produces for embedded quotes.
    /// </summary>
    private static string TagMarker(string tag) => $"git tag {tag}";

    private static bool HasTag(string repo, string tag)
        => RunGit(repo, "tag --list " + tag).Out.Trim().Length > 0;

    private static PipelineStepExecution? ReadPushStep(PipelineExecutionLog log, string jobFolder)
    {
        var record = log.Read(jobFolder);
        return record?.Steps.FirstOrDefault(s => s.StepId == PipelineCatalogue.MergeIntoDevelopPushStepId);
    }

    private (GitService Git, PipelineExecutionLog Log, ProjectSettingsService Settings) BuildWithSettings(string repo)
    {
        var (git, log) = Build(repo);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "Fixture",
                ["WatchPaths:0:RootPath"] = repo,
                ["WatchPaths:0:RepositoryPath"] = repo,
                // Keep project-settings.json inside the throwaway temp dir.
                ["TaskRepository"] = Path.Combine(_tempDir, "settings-store"),
            })
            .Build();
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        return (git, log, settings);
    }

    private (string Repo, string Remote) SeedRepoWithOrigin(string name)
    {
        var repo = Path.Combine(_tempDir, name);
        var remote = Path.Combine(_tempDir, name + "-origin.git");
        Directory.CreateDirectory(repo);
        RunGit(_tempDir, $"init --bare -q --initial-branch=main \"{remote}\"");
        RunGit(repo, "init -q -b main");
        RunGit(repo, "config user.email test@example.com");
        RunGit(repo, "config user.name test");
        File.WriteAllText(Path.Combine(repo, "README.md"), "seed");
        RunGit(repo, "add -A");
        RunGit(repo, "commit -q -m seed");
        RunGit(repo, $"remote add origin \"{remote}\"");
        RunGit(repo, "push -q -u origin main");
        return (repo, remote);
    }

    private static string RemoteSha(string remote, string branch)
    {
        // The remote is a bare repo; relax safe.bareRepository per-invocation so
        // the ref read works on hosts where it is set to "explicit".
        var (o, _, code) = RunGit(remote, $"-c safe.bareRepository=all rev-parse refs/heads/{branch}");
        return code == 0 ? o.Trim() : string.Empty;
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

    private sealed class CapturingBuildTestGateRunner : IBuildTestGateRunner
    {
        private readonly BuildTestGateResult _result;
        private readonly Action? _duringRun;

        public CapturingBuildTestGateRunner(
            BuildTestGateResult result,
            Action? duringRun = null)
        {
            _result = result;
            _duringRun = duringRun;
        }

        public int Invocations { get; private set; }
        public BuildTestGateRequest? Request { get; private set; }

        public Task<BuildTestGateResult> RunAsync(
            BuildTestGateRequest request,
            IReadOnlyList<string>? changedFiles,
            BuildProfile? profile,
            PostStepMode mode,
            TimeSpan timeout,
            CancellationToken ct)
        {
            Invocations++;
            Request = request;
            _duringRun?.Invoke();
            return Task.FromResult(_result with
            {
                ExpectedSha = request.ExpectedSha,
                TestedSha = request.ExpectedSha,
            });
        }
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
