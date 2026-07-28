using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;

using Xunit;

namespace AgentStudio.Tests;

public sealed class AttemptAuthorityServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "attempt-authority-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Restart_preserves_attempt_lease_expiry_fence_epoch_and_review_subject()
    {
        var now = new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc);
        var first = NewService(() => now);
        var run = first.AcquireRun("AGT-1", "PROJ-1", null, "runner-a", "host-a", 120, "claim-1");
        Assert.Equal(AttemptWriteStatus.Accepted, run.Status);
        var settled = first.SettleRun(
            new AttemptWriteReference(run.RunAttempt!.AttemptId, run.RunAttempt.LastFence, run.RunAttempt.AuthorityEpoch, "completion-1"),
            "done", "589c462f", null);
        Assert.Equal(AttemptWriteStatus.Accepted, settled.Status);
        var cleanupReference = new AttemptWriteReference(
            run.RunAttempt.AttemptId, run.RunAttempt.LastFence, run.RunAttempt.AuthorityEpoch, "cleanup-1");
        Assert.Equal(AttemptWriteStatus.Accepted, first.ReleaseRun(cleanupReference, "runner-a").Status);
        Assert.Equal(AttemptWriteStatus.Duplicate, first.ReleaseRun(cleanupReference, "runner-a").Status);
        var review = first.CreateReviewAttempt(new CreateReviewAttemptRequest(
            "AGT-1", "PROJ-1", "589c462f", run.RunAttempt.AttemptId,
            "requirements-hash", "policy-hash", ["artifact:abc"], "review-create-1"));
        var reviewLease = first.ClaimReview(
            review.ReviewAttempt!.AttemptId, "reviewer", "review-host", 120, "review-claim-1").ReviewAttempt!;

        var restarted = NewService(() => now.AddSeconds(30));
        var projection = restarted.GetTaskProjection("agt-1");

        Assert.Equal(run.RunAttempt.AttemptId, projection.CurrentRunAttempt!.AttemptId);
        Assert.Equal(run.RunAttempt.LastFence, projection.CurrentRunAttempt.LastFence);
        Assert.Equal(run.RunAttempt.AuthorityEpoch, projection.AuthorityEpoch);
        Assert.Equal(AttemptLifecycleState.Completed, projection.CurrentRunAttempt.State);
        Assert.Equal("runner-a", projection.CurrentRunAttempt.Lease!.ExecutorId);
        Assert.Equal("host-a", projection.CurrentRunAttempt.Lease.HostId);
        Assert.Equal("589c462f", projection.CurrentReviewSubject!.ExpectedResultSha);
        Assert.Equal(review.ReviewAttempt!.AttemptId, projection.CurrentReviewAttempt!.AttemptId);
        Assert.Equal(reviewLease.LastFence, projection.CurrentReviewAttempt.LastFence);
        Assert.Equal(reviewLease.Lease!.ExpiresAt, projection.CurrentReviewAttempt.Lease!.ExpiresAt);
        Assert.Equal(AttemptWriteStatus.Duplicate, restarted.ClaimReview(
            review.ReviewAttempt.AttemptId, "reviewer", "review-host", 120, "review-claim-1").Status);

        var next = restarted.AcquireRun(
            "agt-1", "PROJ-1", run.RunAttempt.AttemptId, "runner-b", "host-b", 120, "claim-2").RunAttempt!;
        Assert.True(next.LastFence > reviewLease.LastFence);
    }

    [Fact]
    public void Takeover_raises_persisted_fence_and_rejects_stale_or_duplicate_writes_deterministically()
    {
        var now = new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc);
        var service = NewService(() => now);
        var a = service.AcquireRun("AGT-1", "PROJ-1", null, "runner-a", "host-a", 30, "claim-a").RunAttempt!;
        now = now.AddSeconds(31);
        var b = service.AcquireRun("AGT-1", "PROJ-1", a.AttemptId, "runner-b", "host-b", 30, "claim-b").RunAttempt!;

        var stale = service.AcceptRunWrite(new AttemptWriteReference(a.AttemptId, a.LastFence, a.AuthorityEpoch, "log-a"));
        var accepted = service.AcceptRunWrite(new AttemptWriteReference(b.AttemptId, b.LastFence, b.AuthorityEpoch, "log-b"));
        var duplicate = service.AcceptRunWrite(new AttemptWriteReference(b.AttemptId, b.LastFence, b.AuthorityEpoch, "log-b"));

        Assert.True(b.LastFence > a.LastFence);
        Assert.Equal(AttemptWriteStatus.Superseded, stale.Status);
        Assert.Equal(AttemptWriteStatus.Accepted, accepted.Status);
        Assert.Equal(AttemptWriteStatus.Duplicate, duplicate.Status);
    }

    [Fact]
    public void Replayed_acquire_after_takeover_cannot_restore_superseded_authority()
    {
        var now = new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc);
        var service = NewService(() => now);
        var first = service.AcquireRun(
            "AGT-1", "PROJ-1", null, "runner-a", "host-a", 30, "claim-a").RunAttempt!;
        now = now.AddSeconds(31);
        var replacement = service.AcquireRun(
            "AGT-1", "PROJ-1", first.AttemptId, "runner-b", "host-b", 30, "claim-b");

        var replay = service.AcquireRun(
            "AGT-1", "PROJ-1", null, "runner-a", "host-a", 30, "claim-a");

        Assert.Equal(AttemptWriteStatus.Accepted, replacement.Status);
        Assert.Equal(AttemptWriteStatus.Superseded, replay.Status);
        Assert.Equal(first.AttemptId, replay.AttemptId);
        Assert.Equal(replacement.AttemptId, service.GetTaskProjection("AGT-1").CurrentRunAttempt!.AttemptId);
    }

    [Fact]
    public void Live_run_cannot_be_renewed_by_reacquiring_with_only_the_same_executor_identity()
    {
        var now = new DateTime(2026, 7, 21, 10, 0, 0, DateTimeKind.Utc);
        var service = NewService(() => now);
        var first = service.AcquireRun(
            "AGT-1", "PROJ-1", null, "runner-a", "host-a", 30, "claim-a").RunAttempt!;
        var originalExpiry = first.Lease!.ExpiresAt;
        now = now.AddSeconds(10);

        var reacquire = service.AcquireRun(
            "AGT-1", "PROJ-1", null, "runner-a", "host-a", 120, "claim-b");

        Assert.Equal(AttemptWriteStatus.InvalidState, reacquire.Status);
        Assert.Equal(first.AttemptId, reacquire.AttemptId);
        Assert.Equal(originalExpiry, service.GetRun(first.AttemptId)!.Lease!.ExpiresAt);
    }

    [Fact]
    public void Infrastructure_retry_creates_new_review_attempt_for_same_subject_without_new_run()
    {
        var service = NewService();
        var (run, firstReview) = CompletedRunWithReview(service, "sha-a");
        var claimed = service.ClaimReview(firstReview.AttemptId, "reviewer-a", "host-a", 60, "claim-review-a").ReviewAttempt!;
        var renewed = service.RenewReview(
            new AttemptWriteReference(claimed.AttemptId, claimed.LastFence, claimed.AuthorityEpoch, "renew-review-a"),
            "reviewer-a", 120).ReviewAttempt!;
        Assert.True(renewed.Lease!.ExpiresAt >= claimed.Lease!.ExpiresAt);
        var failed = service.SettleReview(new SettleReviewAttemptRequest(
            new AttemptWriteReference(renewed.AttemptId, renewed.LastFence, renewed.AuthorityEpoch, "settle-review-a"),
            "sha-a", ReviewTerminalOutcome.InfrastructureFailure, "worker-lost", "partition"));
        Assert.Equal(AttemptWriteStatus.Accepted, failed.Status);

        var retry = service.CreateReviewAttempt(new CreateReviewAttemptRequest(
            "AGT-1", "PROJ-1", "sha-a", run.AttemptId, "req", "policy", [],
            "review-create-b", firstReview.AttemptId));
        var projection = service.GetTaskProjection("AGT-1");

        Assert.Equal(firstReview.Subject.SubjectId, retry.ReviewAttempt!.Subject.SubjectId);
        Assert.Equal(firstReview.AttemptId, retry.ReviewAttempt.SourceReviewAttemptId);
        Assert.Single(projection.RunAttempts);
        Assert.Equal(2, projection.ReviewAttempts.Count);
    }

    [Fact]
    public void Crash_restarted_executor_reclaims_with_the_same_delivery_key_after_lease_expiry()
    {
        var now = new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc);
        var service = NewService(() => now);
        var (_, review) = CompletedRunWithReview(service, "sha-a");
        var first = service.ClaimReview(
            review.AttemptId, "reviewer", "host", 30, "claim-crash").ReviewAttempt!;

        // Same executor identity, same idempotency key, lease dead: this is a
        // takeover after a daemon crash, not a replay with surviving authority.
        // It must mint a fresh lease instead of bouncing with LeaseExpired.
        now = now.AddSeconds(31);
        var reclaimed = service.ClaimReview(review.AttemptId, "reviewer", "host", 30, "claim-crash");

        Assert.Equal(AttemptWriteStatus.Accepted, reclaimed.Status);
        Assert.True(reclaimed.ReviewAttempt!.LastFence > first.LastFence);
        Assert.Equal("reviewer", reclaimed.ReviewAttempt.Lease!.ExecutorId);
    }

    [Fact]
    public void Review_takeover_on_same_attempt_rejects_old_claim_and_renewal_replays()
    {
        var now = new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc);
        var service = NewService(() => now);
        var (_, review) = CompletedRunWithReview(service, "sha-a");
        var first = service.ClaimReview(
            review.AttemptId, "reviewer", "host", 30, "claim-first").ReviewAttempt!;
        var renew = new AttemptWriteReference(
            first.AttemptId, first.LastFence, first.AuthorityEpoch, "renew-first");
        Assert.Equal(AttemptWriteStatus.Accepted, service.RenewReview(renew, "reviewer", 30).Status);
        Assert.Equal(AttemptWriteStatus.Duplicate,
            service.ClaimReview(review.AttemptId, "reviewer", "host", 30, "claim-first").Status);

        now = now.AddSeconds(31);
        var takeover = service.ClaimReview(
            review.AttemptId, "reviewer", "host", 30, "claim-second").ReviewAttempt!;
        var oldClaim = service.ClaimReview(
            review.AttemptId, "reviewer", "host", 30, "claim-first");
        var oldRenew = service.RenewReview(renew, "reviewer", 30);

        Assert.True(takeover.LastFence > first.LastFence);
        Assert.Equal(AttemptWriteStatus.StaleFence, oldClaim.Status);
        Assert.Equal(AttemptWriteStatus.StaleFence, oldRenew.Status);
    }

    [Fact]
    public void New_result_supersedes_old_review_and_late_report_is_retained_but_cannot_settle()
    {
        var service = NewService();
        var (_, oldReview) = CompletedRunWithReview(service, "sha-a");
        var oldClaim = service.ClaimReview(oldReview.AttemptId, "reviewer-a", "host-a", 60, "claim-old").ReviewAttempt!;
        var oldRenewWrite = new AttemptWriteReference(
            oldClaim.AttemptId, oldClaim.LastFence, oldClaim.AuthorityEpoch, "renew-old");
        Assert.Equal(AttemptWriteStatus.Accepted,
            service.RenewReview(oldRenewWrite, "reviewer-a", 60).Status);

        var runB = service.AcquireRun("AGT-1", "PROJ-1", oldReview.SourceRunAttemptId, "runner-b", "host-b", 60, "run-b").RunAttempt!;
        service.SettleRun(new AttemptWriteReference(runB.AttemptId, runB.LastFence, runB.AuthorityEpoch, "complete-b"), "done", "sha-b", null);

        var replayedClaim = service.ClaimReview(
            oldReview.AttemptId, "reviewer-a", "host-a", 60, "claim-old");
        var replayedRenew = service.RenewReview(oldRenewWrite, "reviewer-a", 60);

        var replayedOldCompletion = service.SettleRun(
            new AttemptWriteReference(oldReview.SourceRunAttemptId, oldClaim.LastFence - 1, oldClaim.AuthorityEpoch, "run-complete"),
            "done", "sha-a", null);

        service.CreateReviewAttempt(new CreateReviewAttemptRequest(
            "AGT-1", "PROJ-1", "sha-b", runB.AttemptId, "req", "policy", [], "review-b"));

        var late = service.SettleReview(new SettleReviewAttemptRequest(
            new AttemptWriteReference(oldClaim.AttemptId, oldClaim.LastFence, oldClaim.AuthorityEpoch, "late-a"),
            "sha-a", ReviewTerminalOutcome.Pass));
        var lateCreate = service.CreateReviewAttempt(new CreateReviewAttemptRequest(
            "AGT-1", "PROJ-1", "sha-a", oldReview.SourceRunAttemptId,
            "req", "policy", [], "late-review-a"));
        var projection = service.GetTaskProjection("AGT-1");

        Assert.Equal(AttemptWriteStatus.Superseded, late.Status);
        Assert.Equal(AttemptWriteStatus.Superseded, replayedOldCompletion.Status);
        Assert.Equal(AttemptWriteStatus.Superseded, lateCreate.Status);
        Assert.Equal(AttemptWriteStatus.Superseded, replayedClaim.Status);
        Assert.Equal(AttemptWriteStatus.Superseded, replayedRenew.Status);
        var historical = Assert.Single(projection.ReviewAttempts, x => x.AttemptId == oldReview.AttemptId);
        Assert.Equal(AttemptLifecycleState.Superseded, historical.State);
        var retainedReport = Assert.Single(historical.Reports);
        Assert.Equal("late-a", retainedReport.IdempotencyKey);
        Assert.Equal("sha-a", retainedReport.MaterializedResultSha);
        Assert.Equal(ReviewTerminalOutcome.Pass, retainedReport.Outcome);
        Assert.Equal(AttemptWriteStatus.Superseded, retainedReport.AuthorityStatus);
        Assert.Equal("sha-b", projection.CurrentReviewSubject!.ExpectedResultSha);
    }

    [Fact]
    public void Replayed_review_settlement_is_superseded_after_a_new_subject_becomes_current()
    {
        var service = NewService();
        var (_, reviewA) = CompletedRunWithReview(service, "sha-a");
        var claimA = service.ClaimReview(
            reviewA.AttemptId, "reviewer-a", "host-a", 60, "claim-a").ReviewAttempt!;
        var settlement = new SettleReviewAttemptRequest(
            new AttemptWriteReference(
                claimA.AttemptId, claimA.LastFence, claimA.AuthorityEpoch, "settle-a"),
            "sha-a",
            ReviewTerminalOutcome.Pass);
        Assert.Equal(AttemptWriteStatus.Accepted, service.SettleReview(settlement).Status);

        var runB = service.AcquireRun(
            "AGT-1", "PROJ-1", reviewA.SourceRunAttemptId,
            "runner-b", "host-b", 60, "run-b").RunAttempt!;
        service.SettleRun(
            new AttemptWriteReference(
                runB.AttemptId, runB.LastFence, runB.AuthorityEpoch, "complete-b"),
            "done", "sha-b", null);
        service.CreateReviewAttempt(new CreateReviewAttemptRequest(
            "AGT-1", "PROJ-1", "sha-b", runB.AttemptId,
            "req", "policy", [], "review-b"));

        var replay = service.SettleReview(settlement);

        Assert.Equal(AttemptWriteStatus.Superseded, replay.Status);
        Assert.Equal(reviewA.AttemptId, replay.AttemptId);
        Assert.Equal("sha-b", service.GetTaskProjection("AGT-1").CurrentReviewSubject!.ExpectedResultSha);
    }

    [Fact]
    public void Idempotency_keys_are_scoped_by_task_and_cannot_alias_another_attempt()
    {
        var service = NewService();

        var first = service.AcquireRun("AGT-1", "PROJ-1", null, "runner", "host", 60, "same-key");
        var second = service.AcquireRun("AGT-2", "PROJ-1", null, "runner", "host", 60, "same-key");

        Assert.Equal(AttemptWriteStatus.Accepted, first.Status);
        Assert.Equal(AttemptWriteStatus.Accepted, second.Status);
        Assert.NotEqual(first.AttemptId, second.AttemptId);
        Assert.Equal("AGT-2", second.RunAttempt!.TaskKey);

        var write = new AttemptWriteReference(
            first.AttemptId, first.RunAttempt!.LastFence, first.RunAttempt.AuthorityEpoch, "same-key");
        Assert.Equal(AttemptWriteStatus.Accepted, service.AcceptRunWrite(write).Status);
        Assert.Equal(AttemptWriteStatus.Duplicate, service.AcceptRunWrite(write).Status);
    }

    [Fact]
    public void Real_remote_completion_subject_fails_closed_when_materialized_sha_differs()
    {
        var service = NewService();
        var (_, review) = CompletedRunWithReview(service, "589c462f");
        var claimed = service.ClaimReview(review.AttemptId, "reviewer", "review-host", 60, "review-claim").ReviewAttempt!;

        var mismatch = service.SettleReview(new SettleReviewAttemptRequest(
            new AttemptWriteReference(claimed.AttemptId, claimed.LastFence, claimed.AuthorityEpoch, "review-result"),
            "61306343", ReviewTerminalOutcome.Pass));

        Assert.Equal(AttemptWriteStatus.SubjectMismatch, mismatch.Status);
        Assert.Equal(ReviewTerminalOutcome.InfrastructureFailure, mismatch.ReviewAttempt!.Outcome);
        Assert.Equal("immutable-result-mismatch", mismatch.ReviewAttempt.FailureClassification);
        Assert.NotEqual(AttemptLifecycleState.Completed, mismatch.ReviewAttempt.State);
    }

    [Fact]
    public void Authority_epoch_change_revokes_old_write_authority_without_resetting_fence()
    {
        var service = NewService();
        var old = service.AcquireRun("AGT-1", "PROJ-1", null, "runner-a", "host-a", 60, "run-a").RunAttempt!;
        var epoch = service.RotateAuthorityEpoch("takeover");

        var stale = service.AcceptRunWrite(new AttemptWriteReference(old.AttemptId, old.LastFence, old.AuthorityEpoch, "late"));
        var replacement = service.AcquireRun("AGT-1", "PROJ-1", old.AttemptId, "runner-b", "host-b", 60, "run-b").RunAttempt!;

        Assert.Equal(AttemptWriteStatus.AuthorityEpochMismatch, stale.Status);
        Assert.Equal(AttemptLifecycleState.Superseded, service.GetRun(old.AttemptId)!.State);
        Assert.Equal(epoch, replacement.AuthorityEpoch);
        Assert.True(replacement.LastFence > old.LastFence);
    }

    [Fact]
    public void Failed_side_effect_does_not_consume_delivery_and_attempt_cannot_write_to_another_task()
    {
        var service = NewService();
        var run = service.AcquireRun(
            "AGT-1", "PROJ-1", null, "runner-a", "host-a", 60, "run-a").RunAttempt!;
        var write = new AttemptWriteReference(
            run.AttemptId, run.LastFence, run.AuthorityEpoch, "log-batch-1");
        var calls = 0;

        Assert.Throws<IOException>(() => service.ExecuteRunWrite(
            write,
            "log",
            "AGT-1",
            () =>
            {
                calls++;
                throw new IOException("disk unavailable");
            }));

        var retried = service.ExecuteRunWrite(write, "log", "AGT-1", () => calls++);
        var duplicate = service.ExecuteRunWrite(write, "log", "AGT-1", () => calls++);
        var wrongTask = service.ExecuteRunWrite(
            write with { IdempotencyKey = "wrong-task" }, "log", "AGT-2", () => calls++);

        Assert.Equal(AttemptWriteStatus.Accepted, retried.Status);
        Assert.Equal(AttemptWriteStatus.Duplicate, duplicate.Status);
        Assert.Equal(AttemptWriteStatus.SubjectMismatch, wrongTask.Status);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Review_writes_require_complete_fenced_idempotency_identity()
    {
        var service = NewService();
        var (_, review) = CompletedRunWithReview(service, "sha-a");
        var claimed = service.ClaimReview(
            review.AttemptId, "reviewer", "review-host", 60, "review-claim").ReviewAttempt!;

        var missingKey = service.SettleReview(new SettleReviewAttemptRequest(
            new AttemptWriteReference(
                claimed.AttemptId, claimed.LastFence, claimed.AuthorityEpoch, string.Empty),
            "sha-a",
            ReviewTerminalOutcome.Pass));
        var missingFence = service.RenewReview(
            new AttemptWriteReference(
                claimed.AttemptId, 0, claimed.AuthorityEpoch, "review-renew"),
            "reviewer",
            60);

        Assert.Equal(AttemptWriteStatus.Invalid, missingKey.Status);
        Assert.Equal(AttemptWriteStatus.Invalid, missingFence.Status);
        Assert.Equal(AttemptLifecycleState.Leased, service.GetReview(claimed.AttemptId)!.State);
    }

    [Fact]
    public void Review_infrastructure_retry_budget_allows_exactly_three_linked_retries()
    {
        var service = NewService();
        var (_, initial) = CompletedRunWithReview(service, "sha-a");
        var current = initial;

        for (var retryNumber = 1;
             retryNumber <= AttemptAuthorityService.ReviewInfrastructureRetryBudget;
             retryNumber++)
        {
            var claimed = service.ClaimReview(
                current.AttemptId,
                "reviewer",
                "review-host",
                60,
                $"claim-{retryNumber}").ReviewAttempt!;
            var settled = service.SettleReview(new SettleReviewAttemptRequest(
                new AttemptWriteReference(
                    claimed.AttemptId,
                    claimed.LastFence,
                    claimed.AuthorityEpoch,
                    $"infra-{retryNumber}"),
                "sha-a",
                ReviewTerminalOutcome.InfrastructureFailure,
                "SnapshotUnavailable"));

            Assert.True(settled.Accepted);
            Assert.True(service.HasReviewInfrastructureRetryBudget(claimed.AttemptId));
            current = service.CreateReviewAttempt(new CreateReviewAttemptRequest(
                claimed.TaskKey,
                claimed.RepositoryId,
                claimed.Subject.ExpectedResultSha,
                claimed.SourceRunAttemptId,
                claimed.Subject.TaskRequirementsHash,
                claimed.Subject.ReviewPolicyHash,
                claimed.Subject.EvidenceDigestInputs,
                $"retry-{retryNumber}",
                claimed.AttemptId)).ReviewAttempt!;
        }

        var finalClaim = service.ClaimReview(
            current.AttemptId,
            "reviewer",
            "review-host",
            60,
            "claim-terminal").ReviewAttempt!;
        var final = service.SettleReview(new SettleReviewAttemptRequest(
            new AttemptWriteReference(
                finalClaim.AttemptId,
                finalClaim.LastFence,
                finalClaim.AuthorityEpoch,
                "infra-terminal"),
            "sha-a",
            ReviewTerminalOutcome.InfrastructureFailure,
            "SnapshotUnavailable"));

        Assert.True(final.Accepted);
        Assert.False(service.HasReviewInfrastructureRetryBudget(finalClaim.AttemptId));
        Assert.Equal(
            AttemptAuthorityService.ReviewInfrastructureRetryBudget + 1,
            service.GetTaskProjection("AGT-1").ReviewAttempts.Count);
    }

    [Fact]
    public void Legacy_review_subject_without_result_envelope_is_terminalized_once()
    {
        var now = new DateTime(2026, 7, 25, 10, 0, 0, DateTimeKind.Utc);
        var service = NewService(() => now);
        var (_, legacy) = CompletedRunWithReview(service, "sha-a");

        // A fresh envelope-less subject is inside the terminalization grace (the
        // completion ingest may still be in flight); only one that stayed
        // envelope-less past it is evidence of a pre-plane completion.
        Assert.Empty(service.TerminalizeLegacyReviewSubjectsWithoutResultEnvelope());
        now = now.AddMinutes(16);

        var first = Assert.Single(
            service.TerminalizeLegacyReviewSubjectsWithoutResultEnvelope());
        var terminalAt = first.TerminalAt;
        now = now.AddMinutes(5);
        var second = Assert.Single(
            service.TerminalizeLegacyReviewSubjectsWithoutResultEnvelope());

        Assert.Equal(legacy.AttemptId, first.AttemptId);
        Assert.Equal(AttemptLifecycleState.Failed, first.State);
        Assert.Equal(ReviewTerminalOutcome.InfrastructureFailure, first.Outcome);
        Assert.Equal("SnapshotUnavailable", first.FailureClassification);
        Assert.Equal(
            AttemptAuthorityService.UnmaterializableReviewSubjectReason,
            first.TerminalReason);
        Assert.Equal(terminalAt, second.TerminalAt);
        Assert.DoesNotContain(
            service.GetTaskProjection("AGT-1").ReviewAttempts,
            attempt => attempt.State == AttemptLifecycleState.Pending);
    }

    [Fact]
    public void Persistence_failure_rolls_memory_back_to_last_durable_fence_and_attempt()
    {
        var now = new DateTime(2026, 7, 21, 10, 0, 0, DateTimeKind.Utc);
        var writer = new ControllableAtomicJsonFileWriter();
        var service = NewService(() => now, writer);
        var first = service.AcquireRun(
            "AGT-1", "PROJ-1", null, "runner-a", "host-a", 30, "run-a").RunAttempt!;
        now = now.AddSeconds(31);
        writer.ShouldFail = (_, writeNumber) => writeNumber == 2;

        Assert.Throws<IOException>(() => service.AcquireRun(
            "AGT-1", "PROJ-1", first.AttemptId, "runner-b", "host-b", 30, "run-b"));

        var afterFailure = service.GetTaskProjection("AGT-1");
        Assert.Equal(first.AuthorityEpoch, afterFailure.AuthorityEpoch);
        Assert.Equal(first.AttemptId, afterFailure.CurrentRunAttempt!.AttemptId);
        Assert.Equal(AttemptLifecycleState.Leased, afterFailure.CurrentRunAttempt.State);

        writer.ShouldFail = null;
        var restarted = NewService(() => now);
        var durable = restarted.GetTaskProjection("AGT-1");
        Assert.Equal(afterFailure.AuthorityEpoch, durable.AuthorityEpoch);
        Assert.Equal(afterFailure.CurrentRunAttempt.AttemptId, durable.CurrentRunAttempt!.AttemptId);
        Assert.Equal(afterFailure.CurrentRunAttempt.LastFence, durable.CurrentRunAttempt.LastFence);

        var takeover = service.AcquireRun(
            "AGT-1", "PROJ-1", first.AttemptId, "runner-b", "host-b", 30, "run-b").RunAttempt!;
        Assert.Equal(first.LastFence + 1, takeover.LastFence);
    }

    [Fact]
    public void Startup_migration_archives_terminal_history_beyond_count_and_keeps_current_and_nonterminal_records()
    {
        var now = new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc);
        var service = NewService(() => now, terminalRetentionCount: 1);
        var oldRun = service.AcquireRun(
            "AGT-1", "PROJ-1", null, "runner-a", "host-a", 60, "old-run-create").RunAttempt!;
        service.SettleRun(
            new AttemptWriteReference(
                oldRun.AttemptId,
                oldRun.LastFence,
                oldRun.AuthorityEpoch,
                "old-run-settle"),
            "done",
            "sha-old",
            null);
        var oldReview = service.CreateReviewAttempt(new CreateReviewAttemptRequest(
            "AGT-1",
            "PROJ-1",
            "sha-old",
            oldRun.AttemptId,
            "req",
            "policy",
            [],
            "old-review-create")).ReviewAttempt!;
        var oldClaim = service.ClaimReview(
            oldReview.AttemptId,
            "reviewer",
            "review-host",
            60,
            "old-review-claim").ReviewAttempt!;
        service.SettleReview(new SettleReviewAttemptRequest(
            new AttemptWriteReference(
                oldClaim.AttemptId,
                oldClaim.LastFence,
                oldClaim.AuthorityEpoch,
                "old-review-settle"),
            "sha-old",
            ReviewTerminalOutcome.InfrastructureFailure,
            "SnapshotUnavailable"));

        now = now.AddMinutes(1);
        var currentRun = service.AcquireRun(
            "AGT-1",
            "PROJ-1",
            oldRun.AttemptId,
            "runner-b",
            "host-b",
            60,
            "current-run-create").RunAttempt!;
        service.SettleRun(
            new AttemptWriteReference(
                currentRun.AttemptId,
                currentRun.LastFence,
                currentRun.AuthorityEpoch,
                "current-run-settle"),
            "done",
            "sha-current",
            null);
        var currentReview = service.CreateReviewAttempt(new CreateReviewAttemptRequest(
            "AGT-1",
            "PROJ-1",
            "sha-current",
            currentRun.AttemptId,
            "req",
            "policy",
            [],
            "current-review-create")).ReviewAttempt!;
        var nonterminalRun = service.AcquireRun(
            "AGT-2",
            "PROJ-1",
            null,
            "runner-c",
            "host-c",
            60,
            "nonterminal-run-create").RunAttempt!;

        var livePath = Path.Combine(_root, AttemptAuthorityService.RelativePath);
        var legacyJson = JsonNode.Parse(File.ReadAllText(livePath))!.AsObject();
        legacyJson["schemaVersion"] = 3;
        File.WriteAllText(livePath, legacyJson.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
        }));

        now = now.AddHours(1);
        var legacyLiveJson = File.ReadAllText(livePath);
        var failingWriter = new ControllableAtomicJsonFileWriter
        {
            ShouldFail = (path, _) => string.Equals(
                path,
                livePath,
                StringComparison.OrdinalIgnoreCase),
        };
        Assert.Throws<IOException>(() => NewService(() => now, failingWriter, terminalRetentionCount: 1));
        Assert.Equal(legacyLiveJson, File.ReadAllText(livePath));

        var restarted = NewService(() => now, terminalRetentionCount: 1);
        var archivePath = Assert.Single(
            Directory.GetFiles(
                Path.GetDirectoryName(livePath)!,
                "attempt-authority.archive-*.json"));
        using var liveDocument = JsonDocument.Parse(File.ReadAllText(livePath));
        using var archiveDocument = JsonDocument.Parse(File.ReadAllText(archivePath));

        var liveRunIds = liveDocument.RootElement
            .GetProperty("runAttempts")
            .EnumerateArray()
            .Select(record => record.GetProperty("attemptId").GetString())
            .ToList();
        var liveReviewIds = liveDocument.RootElement
            .GetProperty("reviewAttempts")
            .EnumerateArray()
            .Select(record => record.GetProperty("attemptId").GetString())
            .ToList();
        var archivedRun = Assert.Single(
            archiveDocument.RootElement.GetProperty("runAttempts").EnumerateArray());
        var archivedReview = Assert.Single(
            archiveDocument.RootElement.GetProperty("reviewAttempts").EnumerateArray());

        Assert.DoesNotContain(oldRun.AttemptId, liveRunIds);
        Assert.DoesNotContain(oldReview.AttemptId, liveReviewIds);
        Assert.Contains(currentRun.AttemptId, liveRunIds);
        Assert.Contains(nonterminalRun.AttemptId, liveRunIds);
        Assert.Contains(currentReview.AttemptId, liveReviewIds);
        Assert.Equal(oldRun.AttemptId, archivedRun.GetProperty("attemptId").GetString());
        Assert.Equal(oldReview.AttemptId, archivedReview.GetProperty("attemptId").GetString());
        Assert.Contains(
            "settle:old-run-settle",
            archivedRun.GetProperty("idempotencyKeys").EnumerateArray().Select(key => key.GetString()));
        Assert.Contains(
            "settle:old-review-settle",
            archivedReview.GetProperty("idempotencyKeys").EnumerateArray().Select(key => key.GetString()));

        var liveProjection = restarted.GetTaskProjection("AGT-1");
        var historicalProjection = restarted.GetTaskProjection("AGT-1", includeArchived: true);
        Assert.Single(liveProjection.RunAttempts);
        Assert.Single(liveProjection.ReviewAttempts);
        Assert.Equal(2, historicalProjection.RunAttempts.Count);
        Assert.Equal(2, historicalProjection.ReviewAttempts.Count);
        Assert.Null(restarted.GetRun(oldRun.AttemptId));
        Assert.Null(restarted.GetReview(oldReview.AttemptId));

        var liveAfterMigration = File.ReadAllText(livePath);
        var archiveAfterMigration = File.ReadAllText(archivePath);
        _ = NewService(() => now.AddHours(1), terminalRetentionCount: 1);
        Assert.Equal(liveAfterMigration, File.ReadAllText(livePath));
        Assert.Equal(archiveAfterMigration, File.ReadAllText(archivePath));

        var sameDayWriter = new ControllableAtomicJsonFileWriter();
        var sameDayService = NewService(() => now.AddHours(2), sameDayWriter, terminalRetentionCount: 1);
        sameDayService.AcquireRun(
            "AGT-3",
            "PROJ-1",
            null,
            "runner-d",
            "host-d",
            60,
            "same-day-run-create");
        Assert.Equal(0, sameDayWriter.WritesFor(archivePath));
        Assert.Equal(1, sameDayWriter.WritesFor(livePath));
    }

    [Fact]
    public void Startup_migration_shrinks_representative_young_terminal_snapshot()
    {
        const int runCount = 273;
        const int reviewCount = 11_700;
        const int retentionCount = 2_000;
        var now = new DateTime(2026, 7, 28, 2, 0, 0, DateTimeKind.Utc);
        var padding = new string('x', 1_400);
        var runs = Enumerable.Range(0, runCount)
            .Select(index => new
            {
                attemptId = $"run-{index:D5}",
                taskKey = $"AGT-{index:D5}",
                repositoryId = "PROJ-002",
                state = AttemptLifecycleState.Completed,
                lastFence = 1,
                authorityEpoch = 1,
                createdAt = now.AddHours(-2).AddTicks(index),
                terminalAt = now.AddHours(-1).AddTicks(index),
                terminalOutcome = "done",
                idempotencyKeys = new[] { $"acquire:run-create-{index}", $"settle:run-settle-{index}" },
            })
            .ToList();
        var reviews = Enumerable.Range(0, reviewCount)
            .Select(index => new
            {
                attemptId = $"review-{index:D5}",
                taskKey = $"AGT-{index % runCount:D5}",
                repositoryId = "PROJ-002",
                sourceRunAttemptId = $"run-{index % runCount:D5}",
                state = AttemptLifecycleState.Failed,
                lastFence = 2,
                authorityEpoch = 1,
                createdAt = now.AddMinutes(-30).AddTicks(index),
                terminalAt = now.AddMinutes(-20).AddTicks(index),
                outcome = ReviewTerminalOutcome.InfrastructureFailure,
                failureClassification = "SnapshotUnavailable",
                terminalReason = padding,
                idempotencyKeys = new[] { $"create:review-create-{index}", $"settle:review-settle-{index}" },
                reports = new[]
                {
                    new
                    {
                        idempotencyKey = $"review-settle-{index}",
                        fence = 2,
                        authorityEpoch = 1,
                        materializedResultSha = "0123456789abcdef",
                        outcome = ReviewTerminalOutcome.InfrastructureFailure,
                        failureClassification = "SnapshotUnavailable",
                        reason = padding,
                        authorityStatus = AttemptWriteStatus.Accepted,
                        receivedAt = now.AddMinutes(-20).AddTicks(index),
                    },
                },
            })
            .ToList();
        var livePath = Path.Combine(_root, AttemptAuthorityService.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(livePath)!);
        File.WriteAllText(
            livePath,
            JsonSerializer.Serialize(
                new
                {
                    schemaVersion = 3,
                    authorityEpoch = 1,
                    runAttempts = runs,
                    reviewAttempts = reviews,
                },
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        var beforeBytes = new FileInfo(livePath).Length;

        _ = NewService(() => now, terminalRetentionCount: retentionCount);

        var afterBytes = new FileInfo(livePath).Length;
        var archivePath = Assert.Single(
            Directory.GetFiles(
                Path.GetDirectoryName(livePath)!,
                "attempt-authority.archive-*.json"));
        using var liveDocument = JsonDocument.Parse(File.ReadAllText(livePath));
        using var archiveDocument = JsonDocument.Parse(File.ReadAllText(archivePath));
        var liveReviews = liveDocument.RootElement.GetProperty("reviewAttempts").EnumerateArray().ToList();
        var archivedReviews = archiveDocument.RootElement.GetProperty("reviewAttempts").EnumerateArray().ToList();

        Assert.Equal(runCount, liveDocument.RootElement.GetProperty("runAttempts").GetArrayLength());
        Assert.Equal(retentionCount, liveReviews.Count);
        Assert.Equal(reviewCount - retentionCount, archivedReviews.Count);
        Assert.True(afterBytes < beforeBytes / 4, $"Expected at least a 75% reduction, got {beforeBytes} -> {afterBytes} bytes.");
        Assert.Contains(
            "settle:review-settle-0",
            archivedReviews[0].GetProperty("idempotencyKeys").EnumerateArray().Select(key => key.GetString()));
        Console.WriteLine(
            $"Representative attempt-authority live size: {beforeBytes} -> {afterBytes} bytes; "
            + $"reviews: {reviewCount} -> {liveReviews.Count}; archived: {archivedReviews.Count}.");
    }

    [Fact]
    public void Startup_and_live_projection_do_not_load_archives()
    {
        var service = NewService();
        var run = service.AcquireRun(
            "AGT-1",
            "PROJ-1",
            null,
            "runner",
            "host",
            60,
            "run-create").RunAttempt!;
        var livePath = Path.Combine(_root, AttemptAuthorityService.RelativePath);
        var archivePath = Path.Combine(
            Path.GetDirectoryName(livePath)!,
            "attempt-authority.archive-2026-07-01.json");
        File.WriteAllText(archivePath, "{ invalid archive");

        var restarted = NewService();
        var liveProjection = restarted.GetTaskProjection("AGT-1");

        Assert.Equal(run.AttemptId, liveProjection.CurrentRunAttempt!.AttemptId);
        Assert.Null(restarted.GetRun("run-archived"));
        Assert.Null(restarted.GetReview("review-archived"));
        Assert.Throws<InvalidDataException>(
            () => restarted.GetTaskProjection("AGT-1", includeArchived: true));
    }

    [Fact]
    public void Settlement_compaction_overwrites_interrupted_daily_archive_without_loading_it()
    {
        var now = new DateTime(2026, 7, 28, 3, 0, 0, DateTimeKind.Utc);
        var service = NewService(() => now, terminalRetentionCount: 1);
        var first = service.AcquireRun(
            "AGT-1",
            "PROJ-1",
            null,
            "runner-a",
            "host-a",
            60,
            "first-run-create").RunAttempt!;
        service.SettleRun(
            new AttemptWriteReference(
                first.AttemptId,
                first.LastFence,
                first.AuthorityEpoch,
                "first-run-settle"),
            "done",
            "sha-first",
            null);

        now = now.AddMinutes(1);
        var second = service.AcquireRun(
            "AGT-1",
            "PROJ-1",
            first.AttemptId,
            "runner-b",
            "host-b",
            60,
            "second-run-create").RunAttempt!;
        var livePath = Path.Combine(_root, AttemptAuthorityService.RelativePath);
        var archivePath = Path.Combine(
            Path.GetDirectoryName(livePath)!,
            "attempt-authority.archive-2026-07-28.json");
        File.WriteAllText(archivePath, "{ interrupted archive");

        var settled = service.SettleRun(
            new AttemptWriteReference(
                second.AttemptId,
                second.LastFence,
                second.AuthorityEpoch,
                "second-run-settle"),
            "done",
            "sha-second",
            null);

        Assert.Equal(AttemptWriteStatus.Accepted, settled.Status);
        Assert.Null(service.GetRun(first.AttemptId));
        Assert.Equal(second.AttemptId, service.GetRun(second.AttemptId)!.AttemptId);
        using var archiveDocument = JsonDocument.Parse(File.ReadAllText(archivePath));
        var archivedRun = Assert.Single(
            archiveDocument.RootElement.GetProperty("runAttempts").EnumerateArray());
        Assert.Equal(first.AttemptId, archivedRun.GetProperty("attemptId").GetString());
        Assert.Contains(
            "settle:first-run-settle",
            archivedRun.GetProperty("idempotencyKeys").EnumerateArray().Select(key => key.GetString()));
    }

    private (RunAttemptDto Run, ReviewAttemptDto Review) CompletedRunWithReview(AttemptAuthorityService service, string sha)
    {
        var run = service.AcquireRun("AGT-1", "PROJ-1", null, "runner", "host", 60, "run-create").RunAttempt!;
        service.SettleRun(new AttemptWriteReference(run.AttemptId, run.LastFence, run.AuthorityEpoch, "run-complete"), "done", sha, null);
        var review = service.CreateReviewAttempt(new CreateReviewAttemptRequest(
            "AGT-1", "PROJ-1", sha, run.AttemptId, "req", "policy", [], "review-create")).ReviewAttempt!;
        return (service.GetRun(run.AttemptId)!, review);
    }

    private AttemptAuthorityService NewService(
        Func<DateTime>? now = null,
        IAtomicJsonFileWriter? writer = null,
        int? terminalRetentionCount = null)
    {
        Directory.CreateDirectory(_root);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["AttemptAuthority:TerminalRetentionCount"] = terminalRetentionCount?.ToString(),
        }).Build();
        return new AttemptAuthorityService(
            config,
            NullLogger<AttemptAuthorityService>.Instance,
            now,
            writer);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
