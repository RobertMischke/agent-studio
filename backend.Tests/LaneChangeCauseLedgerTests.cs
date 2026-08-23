using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The transition sites name WHY a lane changed on the <c>lane_changed</c>
/// ledger row (<c>details.cause</c> + <c>details.causeDetail</c>), and the
/// Remote Review claim writes its own <c>review_attempt_claimed</c> row. The
/// cycle-time lane-transition analysis reads these fields exactly instead of
/// inferring the cause from neighbouring rows.
/// </summary>
public sealed class LaneChangeCauseLedgerTests : IDisposable
{
    private readonly TaskLanePipelineFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task EscalationFunnel_StampsEscalatedCauseWithTheCategory()
    {
        const string id = "funnel-cause";
        _fixture.SeedTask(TaskStates.Progress, id, LifecyclePhases.ExecutionRunning);
        var funnel = _fixture.CreateEscalation();

        var outcome = await funnel.EscalateAsync(
            id,
            _fixture.WatchPath,
            TaskLanePipelineFixture.Project,
            HumanReviewEscalationCategories.AgentBlocked,
            "The agent reported a blocking dependency.");

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var laneChange = Assert.Single(
            _fixture.Timeline.ReadAll(outcome.NewFolderPath!),
            row => row.Kind == TimelineEventKinds.LaneChanged);
        Assert.Equal(TaskStates.Escalated, laneChange.Details!["to"]);
        Assert.Equal(LaneChangeCauses.Escalated, laneChange.Details![LaneChangeCauses.DetailKey]);
        Assert.Equal(HumanReviewEscalationCategories.AgentBlocked, laneChange.Details![LaneChangeCauses.DetailQualifierKey]);
        Assert.Equal("The agent reported a blocking dependency.", laneChange.Details!["reason"]);
    }

    [Fact]
    public void EscalationFunnel_SynchronousPath_StampsTheSameCause()
    {
        const string id = "funnel-cause-sync";
        _fixture.SeedTask(TaskStates.Progress, id, LifecyclePhases.ExecutionRunning);
        var funnel = _fixture.CreateEscalation();

        var outcome = funnel.Escalate(
            id,
            _fixture.WatchPath,
            TaskLanePipelineFixture.Project,
            HumanReviewEscalationCategories.PickupZombie,
            "No CLI output within the pickup deadline.");

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var laneChange = Assert.Single(
            _fixture.Timeline.ReadAll(outcome.NewFolderPath!),
            row => row.Kind == TimelineEventKinds.LaneChanged);
        Assert.Equal(LaneChangeCauses.Escalated, laneChange.Details![LaneChangeCauses.DetailKey]);
        Assert.Equal(HumanReviewEscalationCategories.PickupZombie, laneChange.Details![LaneChangeCauses.DetailQualifierKey]);
    }

    [Fact]
    public async Task TransitionService_PassesTheTransitionCauseThroughToTheLedgerRow()
    {
        const string id = "transition-cause";
        _fixture.SeedTask(TaskStates.Progress, id, LifecyclePhases.ExecutionRunning);

        var outcome = await _fixture.Transitions.MoveAsync(
            id,
            TaskStates.AutoReview,
            _fixture.WatchPath,
            cause: "remote-runner-completion:agent-runner-01",
            suppressProductExecution: true,
            transitionCause: LaneChangeCauses.Delivered,
            transitionDetail: "done");

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var laneChange = Assert.Single(
            _fixture.Timeline.ReadAll(outcome.NewFolderPath!),
            row => row.Kind == TimelineEventKinds.LaneChanged);
        Assert.Equal("remote-runner-completion:agent-runner-01", laneChange.Actor);
        Assert.Equal(LaneChangeCauses.Delivered, laneChange.Details![LaneChangeCauses.DetailKey]);
        Assert.Equal("done", laneChange.Details![LaneChangeCauses.DetailQualifierKey]);
    }

    [Fact]
    public async Task SettledRunRecovery_RequeueBecomesADeliveryHandOff_OnTheLaneRow()
    {
        const string id = "settled-run-cause";
        const string key = "AGT-SETTLED-RUN-CAUSE";
        const string resultSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        _fixture.SeedTask(TaskStates.Progress, id, LifecyclePhases.ExecutionRunning, key: key);
        var authority = _fixture.CreateAttemptAuthority(() => DateTime.UtcNow);
        var run = authority.AcquireRun(key, "PROJ-002", null, "runner-a", "host-a", 60, "run-claim").RunAttempt!;
        var envelope = new AgentStudio.TaskServer.Contracts.ImmutableResultEnvelope(
            "PROJ-002", run.AttemptId,
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", resultSha,
            "refs/agent-studio/results/settled-run-cause", null,
            "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc");
        authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(run.AttemptId, run.LastFence, run.AuthorityEpoch, "run-complete"),
            Outcome = "done",
            ResultSha = resultSha,
            ResultEnvelope = envelope,
            ResultEnvelopeDigest = AgentStudio.TaskServer.Contracts.ResultEnvelopeDigest.Compute(envelope),
        });
        var transitions = new TaskTransitionService(
            _fixture.Scanner,
            _fixture.States,
            _fixture.Mutations,
            _fixture.Git,
            _fixture.Settings,
            NullLogger<TaskTransitionService>.Instance,
            timeline: _fixture.Timeline,
            attemptAuthority: authority);

        // The caller asks for a lease-recovery requeue; BP-09 drives the settled
        // run forward instead. The lane row names the hand-off that landed.
        var outcome = await transitions.MoveAsync(
            id,
            TaskStates.Ready,
            _fixture.WatchPath,
            cause: "run-liveness-detector",
            suppressProductExecution: true,
            transitionCause: LaneChangeCauses.LeaseRecovery,
            transitionDetail: "run-liveness-process-lost");

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var recovered = _fixture.Scanner.FindJob(id, _fixture.WatchPath)!;
        Assert.Equal(TaskStates.AutoReview, recovered.State);
        var laneChange = Assert.Single(
            _fixture.Timeline.ReadAll(recovered.FolderPath),
            row => row.Kind == TimelineEventKinds.LaneChanged);
        Assert.Equal(TaskStates.AutoReview, laneChange.Details!["to"]);
        Assert.Equal(LaneChangeCauses.Delivered, laneChange.Details![LaneChangeCauses.DetailKey]);
        Assert.Equal("settled-run-recovery", laneChange.Details![LaneChangeCauses.DetailQualifierKey]);
    }

    [Fact]
    public async Task TaskAccess_TransitionRequest_CarriesTheTransitionCause()
    {
        const string id = "task-access-cause";
        _fixture.SeedTask(TaskStates.AutoReview, id, LifecyclePhases.AwaitingReview);
        var access = new TaskAccessService(
            _fixture.Scanner,
            _fixture.Mutations,
            _fixture.States,
            _fixture.Transitions,
            new TaskIndexCache(_fixture.Scanner, NullLogger<TaskIndexCache>.Instance, _fixture.Configuration),
            NullLogger<TaskAccessService>.Instance);

        var result = await access.TransitionLaneAsync(new TaskTransitionRequest
        {
            JobId = id,
            WatchPath = _fixture.WatchPath,
            TargetLane = TaskStates.Ready,
            TransitionCause = LaneChangeCauses.GateFailure,
            TransitionDetail = "build-test-gate-fail",
        });

        Assert.Equal(TaskMutationStatus.Applied, result.Status);
        var laneChange = Assert.Single(
            _fixture.Timeline.ReadAll(result.Job!.FolderPath),
            row => row.Kind == TimelineEventKinds.LaneChanged);
        Assert.Equal(LaneChangeCauses.GateFailure, laneChange.Details![LaneChangeCauses.DetailKey]);
        Assert.Equal("build-test-gate-fail", laneChange.Details![LaneChangeCauses.DetailQualifierKey]);
    }

    [Fact]
    public void ReviewClaim_WritesTheClaimRow_AtTheLeaseAcquisition()
    {
        const string id = "review-claim-row";
        const string key = "AGT-REVIEW-CLAIM-ROW";
        _fixture.SeedTask(TaskStates.AutoReview, id, LifecyclePhases.AwaitingReview, key: key);
        const string resultSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var now = new DateTime(2026, 8, 23, 9, 30, 0, DateTimeKind.Utc);
        var authority = _fixture.CreateAttemptAuthority(() => now);
        var run = authority.AcquireRun(key, "PROJ-002", null, "runner-a", "host-a", 60, "run-claim").RunAttempt!;
        // A claimable subject needs a settled immutable Result-Envelope; without
        // one the authority withholds the attempt from every executor.
        var envelope = new AgentStudio.TaskServer.Contracts.ImmutableResultEnvelope(
            "PROJ-002", run.AttemptId,
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", resultSha,
            "refs/agent-studio/results/review-claim-row", null,
            "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc");
        authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(run.AttemptId, run.LastFence, run.AuthorityEpoch, "run-complete"),
            Outcome = "done",
            ResultSha = resultSha,
            ResultEnvelope = envelope,
            ResultEnvelopeDigest = AgentStudio.TaskServer.Contracts.ResultEnvelopeDigest.Compute(envelope),
        });
        var review = authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            key, "PROJ-002", resultSha, run.AttemptId, "requirements", "policy", [], "review-create")).ReviewAttempt!;
        var lifecycle = _fixture.CreateReviewAttemptLifecycle(authority);

        var claimed = lifecycle.ClaimNextReview("review-executor-01", "review-host", "instance-1", 120);

        Assert.Equal(AttemptWriteStatus.Accepted, claimed.Status);
        var task = _fixture.Scanner.FindJob(id, _fixture.WatchPath)!;
        var row = Assert.Single(
            _fixture.Timeline.ReadAll(task.FolderPath),
            item => item.Kind == TimelineEventKinds.ReviewAttemptClaimed);
        Assert.Equal(now, row.Ts);
        Assert.Equal(review.AttemptId, row.RunId);
        Assert.Equal("remote-review:review-executor-01", row.Actor);
        Assert.Equal(review.AttemptId, row.Details!["attemptId"]);
        Assert.Equal(claimed.ReviewAttempt!.Lease!.LeaseId, row.Details["leaseId"]);
        Assert.Equal("review-executor-01", row.Details["executorId"]);
        Assert.Equal("review-host", row.Details["hostId"]);
        Assert.Equal("remote", row.Details["executionLocation"]);
        Assert.Equal(review.Subject.SubjectId, row.Details["subjectId"]);
        Assert.Equal(run.AttemptId, row.Details["sourceRunAttemptId"]);
        Assert.Equal(resultSha, row.Details["expectedResultSha"]);
        Assert.Equal(now.ToString("O"), row.Details["acquiredAt"]);

        // A replayed claim of the same attempt by the same executor is a
        // duplicate and writes no second row.
        var replay = lifecycle.ClaimReview(
            review.AttemptId, "review-executor-01", "review-host", 120,
            $"v1-review-claim:review-executor-01:instance-1:{review.AttemptId}", "instance-1");
        Assert.True(replay.Accepted);
        Assert.Single(
            _fixture.Timeline.ReadAll(task.FolderPath),
            item => item.Kind == TimelineEventKinds.ReviewAttemptClaimed);
    }

    [Fact]
    public void ReviewClaim_WithoutAnEligibleAttempt_WritesNothing()
    {
        const string id = "review-claim-empty";
        _fixture.SeedTask(TaskStates.AutoReview, id, LifecyclePhases.AwaitingReview, key: "AGT-REVIEW-CLAIM-EMPTY");
        var authority = _fixture.CreateAttemptAuthority(() => DateTime.UtcNow);
        var lifecycle = _fixture.CreateReviewAttemptLifecycle(authority);

        var claimed = lifecycle.ClaimNextReview("review-executor-01", "review-host", "instance-1", 120);

        Assert.Equal(AttemptWriteStatus.NotFound, claimed.Status);
        var task = _fixture.Scanner.FindJob(id, _fixture.WatchPath)!;
        Assert.DoesNotContain(
            _fixture.Timeline.ReadAll(task.FolderPath),
            item => item.Kind == TimelineEventKinds.ReviewAttemptClaimed);
    }

    [Theory]
    [InlineData("build-test-gate-fail", LaneChangeCauses.GateFailure)]
    [InlineData("multi-aspect-block", LaneChangeCauses.QualityLoop)]
    [InlineData("completion-gate", LaneChangeCauses.QualityLoop)]
    [InlineData("lint-scss-fail", LaneChangeCauses.QualityLoop)]
    [InlineData("needs-input", LaneChangeCauses.QualityLoop)]
    public void Orchestrator_ReopenCause_MapsToTheLaneChangeCause(string reopenCause, string expected)
        => Assert.Equal(expected, ReviewDecisionOrchestrator.ReopenLaneChangeCause(reopenCause));

    [Theory]
    [InlineData(TaskStates.AutoReview, LaneChangeCauses.Delivered)]
    [InlineData(TaskStates.HumanReview, LaneChangeCauses.Delivered)]
    [InlineData(TaskStates.Backlog, LaneChangeCauses.RunnerRequeue)]
    public void Runner_CompletionLane_MapsToTheLaneChangeCause(string lane, string expected)
        => Assert.Equal(expected, ProjectRunner.CompletionLaneChangeCause(lane));

    [Theory]
    [InlineData(TaskStates.HumanReview, TaskStates.Ready, LaneChangeCauses.OperatorRequeue)]
    [InlineData(TaskStates.Escalated, TaskStates.Ready, LaneChangeCauses.EscalationRequeue)]
    [InlineData(TaskStates.Completed, TaskStates.Preparation, LaneChangeCauses.CompletedReopen)]
    [InlineData(TaskStates.Archive, TaskStates.Completed, LaneChangeCauses.OperatorMove)]
    [InlineData(TaskStates.Backlog, TaskStates.Ready, LaneChangeCauses.Promoted)]
    [InlineData(TaskStates.Escalated, TaskStates.HumanReview, LaneChangeCauses.OperatorDecision)]
    [InlineData(TaskStates.HumanReview, TaskStates.Completed, LaneChangeCauses.Accepted)]
    [InlineData(TaskStates.Completed, TaskStates.Archive, LaneChangeCauses.Archived)]
    [InlineData(TaskStates.Ready, TaskStates.Progress, LaneChangeCauses.OperatorMove)]
    [InlineData("unknown-lane", TaskStates.Ready, LaneChangeCauses.Promoted)]
    public void OperatorMoveCause_FollowsTheLanePair(string from, string to, string expected)
        => Assert.Equal(expected, LaneChangeCauses.ForOperatorMove(from, to));

    [Fact]
    public void StateMachine_ResolvesHumanActorToOperatorCause_AndLeavesSystemRowsInferable()
    {
        Assert.Equal(LaneChangeCauses.OperatorRequeue,
            TaskStateMachine.ResolveTransitionCause("human:alice@example.com", TaskStates.HumanReview, TaskStates.Ready, null));
        Assert.Equal(LaneChangeCauses.Promoted,
            TaskStateMachine.ResolveTransitionCause("human", TaskStates.Backlog, TaskStates.Ready, null));
        Assert.Equal(LaneChangeCauses.Claimed,
            TaskStateMachine.ResolveTransitionCause("human:alice@example.com", TaskStates.Ready, TaskStates.Progress, LaneChangeCauses.Claimed));
        Assert.Null(TaskStateMachine.ResolveTransitionCause("system", TaskStates.Progress, TaskStates.Ready, null));
        Assert.Null(TaskStateMachine.ResolveTransitionCause("remote-runner:r", TaskStates.Ready, TaskStates.Progress, null));
        Assert.Null(TaskStateMachine.ResolveTransitionCause("humanoid-service", TaskStates.Ready, TaskStates.Progress, null));
    }
}
