

namespace AgentStudio.Pipeline;

/// <summary>
/// Runs the deferred, operator-triggered "Merge into Develop" post-step
/// (<see cref="PipelineCatalogue.MergeIntoDevelopStepId"/>). Unlike the automatic
/// in-run integration (<c>ProjectRunner.IntegrateWorktreeRunAsync</c>, ADR-0052),
/// this step does NOT run on its own: the catalogue marks it
/// <see cref="PipelineStep.Deferred"/> so it sits "pending" in the pipeline view
/// until the operator accepts a done-green task via the "Merge into Develop"
/// action. Acceptance keeps the task in Human Review with phase
/// <c>integrating</c> and enqueues this runner for
/// <see cref="AcceptedIntegrationWorker"/>.
///
/// <para>
/// It performs the real, scoped git merge <c>task/&lt;id&gt; -&gt; develop</c> via
/// <see cref="GitService.MergeBranchIntoIntegration"/> and records the outcome
/// into the job's <c>pipeline-execution.json</c> so the deferred step flips from
/// pending to passed / failed / skipped in place. A merge conflict is recorded
/// <see cref="PipelineStepStatus.Failed"/> with the conflicted files in the
/// verdict summary - made visible, never silently resolved - while the working
/// tree is left clean (the merge is aborted). Only a successful result lets the
/// acceptance worker move the task to Completed.
/// </para>
/// </summary>
public sealed class MergeIntoDevelopRunner
{
    private readonly GitService _git;
    private readonly PipelineExecutionLog _pipelineLog;
    private readonly ILogger<MergeIntoDevelopRunner> _logger;
    private readonly IntegrationPushQueue? _pushQueue;
    private readonly ProjectSettingsService? _projectSettings;
    private readonly PreMainTestGate? _preMainTestGate;
    private readonly PreDevelopBuildGate? _preDevelopBuildGate;
    private readonly TimeSpan _preMainTimeout;
    private readonly TimeSpan _preDevelopTimeout;
    private readonly Func<int, TimeSpan> _environmentalBackoff;
    private readonly SemaphoreSlim _mergeGate = new(1, 1);
    private readonly SemaphoreSlim _pushGate = new(1, 1);
    private int _mergeGateUsers;

    public MergeIntoDevelopRunner(
        GitService git,
        PipelineExecutionLog pipelineLog,
        ILogger<MergeIntoDevelopRunner> logger,
        IntegrationPushQueue? pushQueue = null,
        ProjectSettingsService? projectSettings = null,
        PreMainTestGate? preMainTestGate = null,
        TimeSpan? preMainTimeout = null,
        Func<int, TimeSpan>? environmentalBackoff = null,
        PreDevelopBuildGate? preDevelopBuildGate = null,
        TimeSpan? preDevelopTimeout = null)
    {
        _git = git;
        _pipelineLog = pipelineLog;
        _logger = logger;
        _pushQueue = pushQueue;
        _projectSettings = projectSettings;
        _preMainTestGate = preMainTestGate;
        _preDevelopBuildGate = preDevelopBuildGate;
        _preMainTimeout = preMainTimeout is { } configured && configured > TimeSpan.Zero
            ? configured
            : TimeSpan.FromHours(1);
        _preDevelopTimeout = preDevelopTimeout is { } configuredDevelop && configuredDevelop > TimeSpan.Zero
            ? configuredDevelop
            : TimeSpan.FromMinutes(30);
        // Default to the AGT-1944 environmental backoff (30s, 120s, cap 5min); a
        // test injects a zero backoff so it does not sleep between retries.
        _environmentalBackoff = environmentalBackoff ?? PostProcessingOutcomeTaxonomy.RetryBackoff;
    }

    /// <summary>
    /// Triggers the merge of the task branch into <paramref name="integrationBranch"/>
    /// and records the post-step outcome. Returns the underlying
    /// <see cref="MergeIntoIntegrationResult"/> for callers / tests; the lane
    /// transition does not depend on it. Never throws.
    /// </summary>
    public MergeIntoIntegrationResult Run(
        string project,
        string jobId,
        string jobFolderPath,
        string? watchPath,
        string integrationBranch,
        string integrationStrategy = IntegrationStrategies.DirectMerge,
        string pipelineType = PipelineTypes.Task)
        => RunAsync(
            project,
            jobId,
            jobFolderPath,
            watchPath,
            integrationBranch,
            CancellationToken.None,
            integrationStrategy,
            pipelineType).GetAwaiter().GetResult();

    /// <summary>
    /// Async merge entry point used by the accepted-integration worker and
    /// durable backstop. A configured
    /// <c>main</c> target is a release mutation, so it is fail-closed behind the
    /// mandatory full-suite gate and advances only to the exact tested SHA.
    /// </summary>
    public async Task<MergeIntoIntegrationResult> RunAsync(
        string project,
        string jobId,
        string jobFolderPath,
        string? watchPath,
        string integrationBranch,
        CancellationToken ct,
        string integrationStrategy = IntegrationStrategies.DirectMerge,
        string pipelineType = PipelineTypes.Task)
    {
        // Count both the active operation and serialized waiters. The external
        // stable watchdog uses this drain signal to avoid cutting the process
        // between merge and gate/rollback. The accepted lane and pipeline facts
        // still recover queued work that has not entered this boundary.
        Interlocked.Increment(ref _mergeGateUsers);
        try
        {
            // Deliberately NOT the caller's token. Merge + gate + rollback form
            // one consistency boundary after acceptance is already durable.
            await _mergeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                return await RunSerializedAsync(
                    project, jobId, jobFolderPath, watchPath,
                    integrationBranch, integrationStrategy, pipelineType, ct).ConfigureAwait(false);
            }
            finally
            {
                _mergeGate.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _mergeGateUsers);
        }
    }

    /// <summary>
    /// True while an accepted integration is inside, or waiting to enter, the
    /// serialized merge + build-gate + rollback consistency boundary.
    /// </summary>
    public bool IsMergeGateBusy => Volatile.Read(ref _mergeGateUsers) > 0;

    private async Task<MergeIntoIntegrationResult> RunSerializedAsync(
        string project,
        string jobId,
        string jobFolderPath,
        string? watchPath,
        string integrationBranch,
        string integrationStrategy,
        string pipelineType,
        CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        try
        {
            var repoRoot = _git.ResolveRepoRootForWatchPath(watchPath)
                ?? (string.IsNullOrWhiteSpace(watchPath) ? null : watchPath);
            if (string.IsNullOrWhiteSpace(repoRoot))
            {
                var unresolved = MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.Error, error: "Could not resolve repository root for the project.");
                Record(
                    jobFolderPath,
                    project,
                    jobId,
                    integrationBranch,
                    unresolved,
                    preMainResult: null,
                    preDevelopResult: null,
                    startedAt);
                return unresolved;
            }

            var reviewSubject = ReviewSubjectStore.Read(jobFolderPath);
            var delivery = DeliveryRefResolver.Resolve(jobId, jobFolderPath);
            var branch = reviewSubject is not null
                ? TaskIntegrationBranch.Name(
                    reviewSubject.IntegrationBranch,
                    TaskIntegrationBranch.Name(integrationBranch))
                : _git.ResolveIntegrationBranch(repoRoot, integrationBranch);
            var taskBranch = delivery.Ref;
            var strategy = IntegrationStrategies.Normalize(integrationStrategy);
            BuildTestGateResult? preMainResult = null;
            BuildTestGateResult? preDevelopResult = null;
            MergeIntoIntegrationResult result;
            var synchronized = _git.SynchronizeIntegrationBranch(repoRoot, branch, ct);
            if (!synchronized.Success)
            {
                result = MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.Error,
                    error: synchronized.Error);
            }
            else if (string.Equals(strategy, IntegrationStrategies.PullRequest, StringComparison.Ordinal))
            {
                result = MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.PushedForReview,
                    error: $"Delivery '{taskBranch}' remains outside {branch} because the project uses the pull-request integration strategy.");
            }
            else if (IsReleaseBranch(branch))
            {
                (result, preMainResult) = await MergeIntoMainAsync(
                    project,
                    jobId,
                    jobFolderPath,
                    repoRoot,
                    delivery,
                    branch,
                    ct).ConfigureAwait(false);
            }
            else if (delivery.IsRemote)
            {
                (result, preDevelopResult) = await MergeIntoIntegrationGatedAsync(
                    project,
                    jobId,
                    jobFolderPath,
                    repoRoot,
                    branch,
                    () => _git.MergeRemoteDeliveryIntoIntegration(
                        repoRoot,
                        delivery.Ref,
                        delivery.ExpectedResultSha ?? string.Empty,
                        branch,
                        ct)).ConfigureAwait(false);
            }
            else
            {
                (result, preDevelopResult) = await MergeIntoIntegrationGatedAsync(
                    project,
                    jobId,
                    jobFolderPath,
                    repoRoot,
                    branch,
                    () => _git.MergeBranchIntoIntegration(repoRoot, taskBranch, branch, ct)).ConfigureAwait(false);
            }
            _logger.LogInformation(
                "merge-into-develop project={Project} job={JobId} delivery={Delivery} integration={Integration} strategy={Strategy} outcome={Outcome}",
                project, jobId, taskBranch, branch, strategy, result.Outcome);
            Record(jobFolderPath, project, jobId, branch, result, preMainResult, preDevelopResult, startedAt);

            // AGT-1999: once the accepted task is folded into the integration
            // branch, push that branch to origin so integration is never only
            // local. Offloaded to the background worker (the same "not on the
            // request path" strategy as the completed-job workspace push), so the
            // accept transition never awaits the network round-trip.
            if (result.Outcome is MergeIntoIntegrationOutcome.Merged or MergeIntoIntegrationOutcome.AlreadyMerged)
            {
                // Pin the object the push may publish: the merge result this card's
                // gate released, or - for AlreadyMerged, where this run produced no
                // commit - the branch tip as it stands at release time. Reading it
                // here and not in the worker is what closes the gate window: by the
                // time the queued push runs, the tip may already carry a merge no
                // gate has approved yet.
                var approvedSha = result.Outcome == MergeIntoIntegrationOutcome.Merged
                    ? result.MergedSha
                    : _git.GetBranchTip(repoRoot, branch);
                MaybeEnqueueIntegrationPush(
                    project, jobId, jobFolderPath, watchPath, integrationBranch, approvedSha, pipelineType);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "merge-into-develop post-step failed for {JobId}", jobId);
            var errored = MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Error, error: ex.Message);
            try
            {
                Record(
                    jobFolderPath,
                    project,
                    jobId,
                    integrationBranch,
                    errored,
                    preMainResult: null,
                    preDevelopResult: null,
                    startedAt);
            }
            catch (Exception __ex)
            {
                SilentCatch.Note(__ex, "MergeIntoDevelopRunner: recording is best-effort");
            }
            return errored;
        }
    }

    /// <summary>
    /// Performs a non-release integration merge behind the pre-develop build gate
    /// (<see cref="PreDevelopBuildGate"/>). The gated subject is the MERGE RESULT,
    /// not the delivery: only after the merge commit exists is there something to
    /// build that nobody built before.
    ///
    /// <list type="number">
    /// <item>capture the exact pre-merge tip of the integration branch;</item>
    /// <item>merge exactly as before (<paramref name="merge"/>);</item>
    /// <item>build the merged SHA in an isolated worktree - the live checkout is
    ///   only an object store, never a command workspace;</item>
    /// <item>red gate -&gt; hard-reset the integration branch back to the captured
    ///   tip and report <see cref="MergeIntoIntegrationOutcome.GateFailed"/>, so
    ///   nothing is pushed and the card is honestly not integrated;</item>
    /// <item>green gate -&gt; the merge stands and the push is enqueued as before.</item>
    /// </list>
    ///
    /// <para>The whole sequence runs inside the caller's <c>_mergeGate</c>, so the
    /// merge, the gate, and the rollback are one atomic step against the shared
    /// integration checkout.</para>
    /// </summary>
    private async Task<(MergeIntoIntegrationResult Merge, BuildTestGateResult? Gate)> MergeIntoIntegrationGatedAsync(
        string project,
        string jobId,
        string jobFolderPath,
        string repoRoot,
        string integrationBranch,
        Func<MergeIntoIntegrationResult> merge)
    {
        var profile = BuildProfileFor(project);
        var skipReason = _preDevelopBuildGate is null
            ? "no build gate is wired"
            : PreDevelopBuildGate.AppliesTo(profile)
                ? null
                : "the project declares no build-profile build commands";
        var result = merge();
        if (skipReason is not null)
        {
            _logger.LogInformation(
                "merge-into-develop build gate skipped for project={Project} job={JobId} integration={Integration}: {Reason}",
                project, jobId, integrationBranch, skipReason);
            return (result, null);
        }
        if (result.Outcome != MergeIntoIntegrationOutcome.Merged) return (result, null);

        // The remote-delivery merge may create the configured local integration
        // branch from origin. In that case there was no local tip to capture
        // before the merge. The first parent of the new --no-ff merge commit is
        // the exact synchronized integration tip and therefore the authoritative
        // rollback anchor.
        var preMergeTip = string.IsNullOrWhiteSpace(result.MergedSha)
            ? null
            : _git.GetFirstParent(repoRoot, result.MergedSha);
        if (string.IsNullOrWhiteSpace(preMergeTip))
        {
            var missingAnchorReason =
                $"The build gate could not determine the pre-merge tip of {integrationBranch}; " +
                "the merged branch requires manual verification and repair.";
            var missingAnchor = new BuildTestGateResult(
                BuildTestGateVerdict.Fail,
                null,
                0,
                string.Empty,
                missingAnchorReason,
                false,
                false)
            {
                ExpectedSha = result.MergedSha,
                FailureKind = BuildTestGateFailureKind.MissingSource,
            };
            RecordGateEvidence(jobFolderPath, "pre-develop-build-gate", missingAnchor);
            return (
                MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.Error,
                    mergedSha: result.MergedSha,
                    error: missingAnchor.Reason),
                missingAnchor);
        }

        var gate = await _preDevelopBuildGate!.RunAsync(
            new BuildTestGateRequest(repoRoot, result.MergedSha, "merge-into-develop-build-gate")
            {
                Project = project,
                JobId = jobId,
                Lane = TaskStates.Completed,
                TestExecution = TestExecutionFor(project),
                JobFolderPath = jobFolderPath,
                SubjectRef = integrationBranch,
            },
            profile,
            _preDevelopTimeout,
            // Deliberately NOT the caller's token: once the background worker
            // starts a merge, its gate and possible rollback must reach a
            // consistent terminal state. The gate stays bounded by its timeout.
            CancellationToken.None).ConfigureAwait(false);
        RecordGateEvidence(jobFolderPath, "pre-develop-build-gate", gate);

        if (PreDevelopBuildGate.IsGreen(gate))
        {
            _logger.LogInformation(
                "merge-into-develop build gate passed for project={Project} job={JobId} integration={Integration} merged={MergedSha} verdict={Verdict}",
                project, jobId, integrationBranch, result.MergedSha, gate.Verdict);
            return (result, gate);
        }

        var reset = _git.ResetHard(repoRoot, preMergeTip!);
        _logger.LogWarning(
            "merge-into-develop build gate FAILED for project={Project} job={JobId} integration={Integration} merged={MergedSha} verdict={Verdict} reason={Reason} rollback={Rollback}",
            project, jobId, integrationBranch, result.MergedSha, gate.Verdict, gate.Reason,
            reset.Success ? "reset-to-pre-merge-tip" : "FAILED: " + (reset.Error ?? "unknown"));

        var error = reset.Success
            ? $"The build gate blocked the merge into {integrationBranch}: {gate.Reason}. " +
              $"{integrationBranch} was rolled back to {Short(preMergeTip!)} and nothing was pushed; " +
              "start a steer round so the delivery builds on top of the current integration branch."
            : $"The build gate blocked the merge into {integrationBranch}: {gate.Reason}. " +
              $"Rolling {integrationBranch} back to {Short(preMergeTip!)} FAILED ({reset.Error ?? "unknown error"}); " +
              "the unverified merge is still on the local integration branch and needs manual repair.";
        return (MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.GateFailed, error: error), gate);
    }

    /// <summary>
    /// The project's declared build profile, or null when no settings service is
    /// wired (legacy fixtures) or the read fails. A null profile means "no gate".
    /// </summary>
    private BuildProfile? BuildProfileFor(string project)
    {
        if (_projectSettings == null) return null;
        try { return _projectSettings.Get(project).BuildProfile; }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "MergeIntoDevelopRunner: build-profile read is best-effort");
            return null;
        }
    }

    private TestExecutionPolicy? TestExecutionFor(string project)
    {
        if (_projectSettings == null) return null;
        try { return _projectSettings.Get(project).TestExecution; }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "MergeIntoDevelopRunner: test-execution read is best-effort");
            return null;
        }
    }

    private async Task<(MergeIntoIntegrationResult Merge, BuildTestGateResult? Gate)> MergeIntoMainAsync(
        string project,
        string jobId,
        string jobFolderPath,
        string repoRoot,
        DeliveryRefResolution delivery,
        string releaseBranch,
        CancellationToken ct)
    {
        var taskBranch = delivery.Ref;
        if (delivery.IsRemote)
        {
            if (!ReviewSubjectStore.IsValidResultSha(delivery.ExpectedResultSha))
            {
                return (
                    MergeIntoIntegrationResult.Of(
                        MergeIntoIntegrationOutcome.Error,
                        error: $"Remote delivery '{taskBranch}' has no valid fenced result SHA."),
                    null);
            }
            var inspected = _git.InspectRemoteDeliveryCommitRange(
                repoRoot,
                taskBranch,
                delivery.ExpectedResultSha!,
                releaseBranch,
                ct);
            if (!inspected.Success)
            {
                return (
                    MergeIntoIntegrationResult.Of(
                        MergeIntoIntegrationOutcome.NoTaskBranch,
                        error: inspected.Warning),
                    null);
            }
            taskBranch = "origin/" + taskBranch;
        }
        else if (!_git.BranchExists(repoRoot, taskBranch))
        {
            return (
                MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.NoTaskBranch,
                    error: $"Task branch '{taskBranch}' does not exist."),
                null);
        }
        if (!_git.BranchExists(repoRoot, releaseBranch))
        {
            return (
                MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.Error,
                    error: $"Release branch '{releaseBranch}' does not exist."),
                null);
        }
        if (_git.IsAncestor(repoRoot, taskBranch, releaseBranch))
        {
            return (
                MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.AlreadyMerged),
                null);
        }
        if (_preMainTestGate is null || _projectSettings is null)
        {
            return (
                MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.Error,
                    error: "Pre-main test gate is unavailable; refusing to advance main."),
                null);
        }

        var sourceSha = _git.GetBranchTip(repoRoot, taskBranch);
        var targetSha = _git.GetBranchTip(repoRoot, releaseBranch);
        if (string.IsNullOrWhiteSpace(sourceSha) || string.IsNullOrWhiteSpace(targetSha))
        {
            return (
                MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.Error,
                    error: "Could not resolve the exact source and main SHAs for the pre-main gate."),
                null);
        }

        // AGT-2417 docs rule: a delivery whose whole diff (against the merge
        // base) is documentation / task evidence cannot change any build or
        // test signal. It skips the full-suite release gate AND the
        // rebased-onto-main requirement and integrates through the same
        // conflict-checked merge the non-release path uses; a real conflict
        // still surfaces honestly. Unknown diffs stay on the strict path.
        var changedPaths = _git.ChangedPathsAgainstMergeBase(repoRoot, releaseBranch, taskBranch);
        if (DocsOnlyDeliveryPolicy.IsDocsOnly(changedPaths))
        {
            var lightGate = new BuildTestGateResult(
                BuildTestGateVerdict.Skipped,
                null,
                0,
                string.Empty,
                $"Docs-only delivery ({changedPaths!.Count} changed path(s), all documentation/evidence); " +
                "the full-suite release gate is not applicable and a conflict-checked merge probe integrates directly.",
                false,
                false)
            {
                ExpectedSha = sourceSha,
            };
            RecordGateEvidence(jobFolderPath, "pre-main-test-gate", lightGate);
            _logger.LogInformation(
                "merge-into-main docs-only light gate for project={Project} job={JobId} changedPaths={Count}",
                project, jobId, changedPaths.Count);
            var docsMerge = delivery.IsRemote
                ? _git.MergeRemoteDeliveryIntoIntegration(
                    repoRoot,
                    delivery.Ref,
                    delivery.ExpectedResultSha ?? string.Empty,
                    releaseBranch,
                    ct)
                : _git.MergeBranchIntoIntegration(repoRoot, taskBranch, releaseBranch, ct);
            return (docsMerge, lightGate);
        }

        if (!_git.IsAncestor(repoRoot, releaseBranch, taskBranch))
        {
            return (
                MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.Error,
                    error: $"Release source '{taskBranch}' must be rebased onto '{releaseBranch}' before the full-suite gate."),
                null);
        }

        var settings = _projectSettings.Get(project);
        var gate = await _preMainTestGate.RunAsync(
            new BuildTestGateRequest(repoRoot, sourceSha, "merge-into-main")
            {
                Project = project,
                JobId = jobId,
                Lane = TaskStates.Completed,
                TestExecution = settings.TestExecution,
                JobFolderPath = jobFolderPath,
                SubjectRef = taskBranch,
            },
            settings.BuildProfile,
            _preMainTimeout,
            ct).ConfigureAwait(false);
        RecordGateEvidence(jobFolderPath, "pre-main-test-gate", gate);

        if (gate.Verdict != BuildTestGateVerdict.Ok)
        {
            return (
                MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.Error,
                    error: $"Pre-main full suite blocked the merge: {gate.Reason}"),
                gate);
        }

        var merge = _git.MergeBranchFastForward(
            repoRoot,
            taskBranch,
            releaseBranch,
            sourceSha,
            targetSha);
        return (merge, gate);
    }

    private static bool IsReleaseBranch(string branch)
        => string.Equals(branch, "main", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Enqueues the integration-branch push onto the background
    /// <see cref="IntegrationPushQueue"/> when one is wired (production) and the
    /// push step is enabled for the project. No queue means no offload wiring
    /// (the unit-test fixtures) - <see cref="Run"/> then stays merge-only and a
    /// test drives <see cref="PushIntegrationBranchAsync"/> directly. Never
    /// throws: the merge has already landed and the push is best-effort.
    /// </summary>
    private void MaybeEnqueueIntegrationPush(
        string project, string jobId, string jobFolderPath, string? watchPath, string integrationBranch,
        string? approvedSha,
        string pipelineType)
    {
        if (_pushQueue == null) return;
        if (!IntegrationPushEnabled(project, pipelineType))
        {
            _logger.LogInformation(
                "merge-into-develop push disabled for project={Project} job={JobId}; leaving origin unchanged",
                project, jobId);
            return;
        }

        var enqueued = _pushQueue.Enqueue(new IntegrationPushRequest(
            project, jobId, jobFolderPath, watchPath, integrationBranch, approvedSha));
        if (enqueued)
            _logger.LogInformation(
                "merge-into-develop push enqueued for project={Project} job={JobId} branch={Branch} approved={ApprovedSha}",
                project, jobId, integrationBranch, approvedSha ?? "branch-tip");
        else
            _logger.LogWarning(
                "merge-into-develop push enqueue failed (queue closed) for project={Project} job={JobId}",
                project, jobId);
    }

    /// <summary>
    /// True unless the operator disabled the deferred push step for this project
    /// (<see cref="PipelineCatalogue.MergeIntoDevelopPushStepId"/>, default on).
    /// A missing settings service (legacy fixtures) defaults to on.
    /// </summary>
    private bool IntegrationPushEnabled(string project, string pipelineType)
    {
        if (_projectSettings == null) return true;
        try
        {
            var settings = PipelineTypeSettings.ForType(_projectSettings.Get(project), pipelineType);
            return PipelineStepConfigResolver.IsEnabled(settings, PipelineCatalogue.MergeIntoDevelopPushStepId);
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "MergeIntoDevelopRunner: push-enabled read is best-effort; default on");
            return true;
        }
    }

    /// <summary>
    /// Pushes the integration branch to <c>origin</c> and records the deferred
    /// <see cref="PipelineCatalogue.MergeIntoDevelopPushStepId"/> step outcome.
    /// This is the offloaded body the <see cref="IntegrationPushWorker"/> runs
    /// (and tests drive directly). A transient failure is classified environmental
    /// and retried with backoff per the AGT-1944 taxonomy; once the retry budget
    /// is spent the step is recorded <see cref="PipelineStepStatus.Failed"/>
    /// flagged <c>environmental</c> so a reviewer does not read an infra blip as a
    /// failed change. Never throws (except on cooperative cancellation).
    /// <para>
    /// <paramref name="approvedSha"/> is the merge result the gate released. It is
    /// what gets pushed, so nothing that landed on the integration branch after
    /// the approval rides along. Omitting it keeps the historical branch-tip push
    /// and is reserved for callers that have no approval record (the durable
    /// restart backstop).
    /// </para>
    /// </summary>
    public async Task<GitPushResult> PushIntegrationBranchAsync(
        string project,
        string jobId,
        string jobFolderPath,
        string? watchPath,
        string integrationBranch,
        CancellationToken ct = default,
        string? approvedSha = null)
    {
        await _pushGate.WaitAsync(ct);
        try
        {
            return await PushIntegrationBranchSerializedAsync(
                project,
                jobId,
                jobFolderPath,
                watchPath,
                integrationBranch,
                approvedSha,
                ct);
        }
        finally
        {
            _pushGate.Release();
        }
    }

    private async Task<GitPushResult> PushIntegrationBranchSerializedAsync(
        string project,
        string jobId,
        string jobFolderPath,
        string? watchPath,
        string integrationBranch,
        string? approvedSha,
        CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        GitPushResult result;
        var environmentalRetries = 0;
        try
        {
            var repoRoot = _git.ResolveRepoRootForWatchPath(watchPath)
                ?? (string.IsNullOrWhiteSpace(watchPath) ? null : watchPath);
            if (string.IsNullOrWhiteSpace(repoRoot))
            {
                result = new GitPushResult(false, string.Empty, "repo-missing", "Could not resolve repository root for the integration push.");
            }
            else
            {
                var branch = _git.ResolveIntegrationBranch(repoRoot, integrationBranch);
                while (true)
                {
                    result = await _git.PushIntegrationBranchAsync(repoRoot, branch, ct, approvedSha);
                    if (result.Success) break;

                    var issue = ClassifyPushFailure(result.Status);
                    if (!PostProcessingOutcomeTaxonomy.IsRetryableEnvironmental(issue)) break;

                    var decision = PostProcessingOutcomeTaxonomy.DecideEnvironmentalRetry(issue, environmentalRetries);
                    if (decision.Action != EnvironmentalRetryAction.RetryWithBackoff) break;

                    environmentalRetries = decision.Attempt;
                    var backoff = _environmentalBackoff(environmentalRetries);
                    _logger.LogWarning(
                        "merge-into-develop push failed (environmental {Status}) project={Project} job={JobId} branch={Branch}; {Reason} (backoff {Backoff})",
                        result.Status, project, jobId, branch, decision.Reason, backoff);
                    if (backoff > TimeSpan.Zero)
                    {
                        try { await Task.Delay(backoff, ct); }
                        catch (OperationCanceledException) { break; }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "merge-into-develop push threw for project={Project} job={JobId}", project, jobId);
            result = new GitPushResult(false, string.Empty, "error", ex.Message);
        }

        _logger.LogInformation(
            "merge-into-develop-push project={Project} job={JobId} branch={Branch} status={Status} retries={Retries}",
            project, jobId, integrationBranch, result.Status, environmentalRetries);
        try { RecordPushStep(jobFolderPath, project, jobId, result, startedAt, environmentalRetries); }
        catch (Exception ex) { SilentCatch.Note(ex, "MergeIntoDevelopRunner: push-step recording is best-effort"); }
        return result;
    }

    /// <summary>
    /// Maps a failed push status to the AGT-1944 issue kind that drives the
    /// retry / escalation decision. A generic push failure is treated as a
    /// transient environmental fault (network / remote availability) that retries;
    /// a non-fast-forward rejection is a diverged remote - an environmental
    /// blocker a blind retry cannot clear, so it escalates immediately (visible).
    /// Anything else is a non-environmental hard error.
    /// </summary>
    private static RunIssueKind ClassifyPushFailure(string status) => status switch
    {
        "failed" => RunIssueKind.EnvironmentalTransient,
        "remote-rejected" => RunIssueKind.EnvironmentBlocker,
        _ => RunIssueKind.None,
    };

    private void RecordPushStep(
        string jobFolderPath,
        string project,
        string jobId,
        GitPushResult result,
        DateTime startedAt,
        int environmentalRetries)
    {
        var pipelineRecord = _pipelineLog.Read(jobFolderPath)
            ?? _pipelineLog.EnsureRun(jobFolderPath, PipelineCatalogue.Standard, project, jobId);
        using var pipelineAttempt = _pipelineLog.EnterAttempt(jobFolderPath, pipelineRecord.Attempt);

        var completedAt = DateTime.UtcNow;
        var (status, verdict, reason, summary) = ProjectPush(result, environmentalRetries);

        _pipelineLog.RecordStep(jobFolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopPushStepId,
            Kind = StepKind.Tool,
            Status = status,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            DurationMs = (long)(completedAt - startedAt).TotalMilliseconds,
            Verdict = verdict,
            VerdictSummary = summary,
            Reason = reason,
        });
    }

    private static (PipelineStepStatus Status, string? Verdict, string? Reason, string? Summary) ProjectPush(
        GitPushResult result, int environmentalRetries)
    {
        if (result.Success)
        {
            return result.Status switch
            {
                "pushed" => (PipelineStepStatus.Passed, "pushed", $"Pushed integration branch to origin{ShaSuffix(result.Sha)}.", null),
                "already-remote" => (PipelineStepStatus.Passed, "already-remote", "Integration branch already up to date on origin; nothing to push.", null),
                "no-remote" => (PipelineStepStatus.Skipped, "no-remote", "No origin remote configured; nothing to push.", null),
                _ => (PipelineStepStatus.Passed, result.Status, "Integration branch push completed.", null),
            };
        }

        var issue = ClassifyPushFailure(result.Status);
        if (PostProcessingOutcomeTaxonomy.IsEnvironmental(issue))
        {
            // AGT-1944: flag the failure environmental so a reviewer does not read
            // an infra blip / diverged remote as a failed change. Retryable
            // transients reach here only after their retry budget is spent.
            var retried = environmentalRetries > 0
                ? $" after {environmentalRetries} retr{(environmentalRetries == 1 ? "y" : "ies")}"
                : string.Empty;
            return (
                PipelineStepStatus.Failed,
                "environmental",
                $"Push of the integration branch to origin failed{retried} ({result.Status}); flagged environmental (AGT-1944).",
                result.Error);
        }

        return (PipelineStepStatus.Failed, "error", $"Push of the integration branch to origin failed ({result.Status}).", result.Error);
    }

    private static string ShaSuffix(string? sha) =>
        string.IsNullOrWhiteSpace(sha) ? string.Empty : $" ({Short(sha!)})";

    private void Record(
        string jobFolderPath,
        string project,
        string jobId,
        string integrationBranch,
        MergeIntoIntegrationResult result,
        BuildTestGateResult? preMainResult,
        BuildTestGateResult? preDevelopResult,
        DateTime startedAt)
    {
        // Record into the existing run when one is present (the deferred merge
        // step already sits in it as "pending"); only begin a fresh baseline when
        // none exists yet, so RecordStep is never a silent no-op. The merge step
        // only lives in the standard pipeline (the read-only variant drops every
        // git step), so the baseline uses the standard catalogue.
        var pipelineRecord = _pipelineLog.Read(jobFolderPath)
            ?? _pipelineLog.EnsureRun(jobFolderPath, PipelineCatalogue.Standard, project, jobId);
        using var pipelineAttempt = _pipelineLog.EnterAttempt(jobFolderPath, pipelineRecord.Attempt);

        var completedAt = DateTime.UtcNow;
        var (status, verdict, reason, summary) = Project(
            result, integrationBranch, preMainResult, preDevelopResult);

        _pipelineLog.RecordStep(jobFolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopStepId,
            Kind = StepKind.Tool,
            Status = status,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            DurationMs = (long)(completedAt - startedAt).TotalMilliseconds,
            Verdict = verdict,
            VerdictSummary = summary,
            Reason = reason,
        });
    }

    /// <summary>
    /// Writes one numbered gate-evidence log into the job's <c>post-steps</c>
    /// folder (<c>&lt;prefix&gt;-N.log</c>): verdict, exact expected / tested SHA,
    /// the test-selection audit, and the tail of the command output. Same shape
    /// for both merge gates, so the evidence reads identically whether main or
    /// develop was the target.
    /// </summary>
    private static void RecordGateEvidence(
        string jobFolderPath,
        string prefix,
        BuildTestGateResult result)
    {
        var dir = Path.Combine(jobFolderPath, "post-steps");
        Directory.CreateDirectory(dir);
        var index = Directory.GetFiles(dir, $"{prefix}-*.log").Length + 1;
        var selection = System.Text.Json.JsonSerializer.Serialize(
            result.TestSelection,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var body =
            $"verdict={result.Verdict} exit={result.ExitCode?.ToString() ?? "n/a"} durationMs={result.DurationMs}\n" +
            $"expectedSha={result.ExpectedSha ?? "n/a"} testedSha={result.TestedSha ?? "n/a"}\n" +
            $"reason={result.Reason}\n" +
            "--- test-selection.json ---\n" +
            selection + "\n" +
            "--- last-300-lines ---\n" +
            result.Output;
        File.WriteAllText(
            Path.Combine(dir, $"{prefix}-{index}.log"),
            body);
    }

    private static (PipelineStepStatus Status, string? Verdict, string? Reason, string? Summary) Project(
        MergeIntoIntegrationResult result,
        string integrationBranch,
        BuildTestGateResult? preMainResult,
        BuildTestGateResult? preDevelopResult)
    {
        switch (result.Outcome)
        {
            case MergeIntoIntegrationOutcome.Merged:
                var sha = string.IsNullOrWhiteSpace(result.MergedSha)
                    ? string.Empty
                    : $" ({Short(result.MergedSha!)})";
                var fullSuite = preMainResult is null
                    ? string.Empty
                    : " after the mandatory full suite passed";
                var buildGate = preDevelopResult is null
                    ? string.Empty
                    : " after the build gate passed on the merge result";
                return (
                    PipelineStepStatus.Passed,
                    "merged",
                    $"Merged into {integrationBranch}{sha}{fullSuite}{buildGate}.",
                    preMainResult?.Reason ?? preDevelopResult?.Reason);
            case MergeIntoIntegrationOutcome.GateFailed:
                return (
                    PipelineStepStatus.Failed,
                    "gate-failed",
                    result.Error ?? $"The build gate blocked the merge into {integrationBranch}.",
                    preDevelopResult?.Reason);
            case MergeIntoIntegrationOutcome.AlreadyMerged:
                return (
                    PipelineStepStatus.Passed,
                    "already-merged",
                    $"Task branch already contained in {integrationBranch}; no merge needed.",
                    null);
            case MergeIntoIntegrationOutcome.NoTaskBranch:
                return (PipelineStepStatus.Skipped, "no-branch", result.Error ?? "No task branch to merge.", null);
            case MergeIntoIntegrationOutcome.PushedForReview:
                return (
                    PipelineStepStatus.Skipped,
                    "pushed-for-review",
                    result.Error ?? "The configured pull-request strategy did not merge the delivery.",
                    null);
            case MergeIntoIntegrationOutcome.Conflict:
                var files = result.ConflictedFiles is { Count: > 0 }
                    ? string.Join(", ", result.ConflictedFiles)
                    : "unknown files";
                return (
                    PipelineStepStatus.Failed,
                    "conflict",
                    $"Merge conflict in {result.ConflictedFiles?.Count ?? 0} file(s); merge aborted, working tree left clean. Start a steer round to rebase the delivery onto the current integration branch.",
                    $"Conflicted: {files}. Recovery: rebase the delivery onto the current integration branch, resolve the conflicts, and accept again.");
            default:
                return (PipelineStepStatus.Failed, "error", result.Error ?? "Merge failed.", null);
        }
    }

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
