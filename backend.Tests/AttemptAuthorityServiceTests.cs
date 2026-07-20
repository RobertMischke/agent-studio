using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

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
        var projection = restarted.GetTaskProjection("AGT-1");

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
    public void New_result_supersedes_old_review_and_late_report_is_retained_but_cannot_settle()
    {
        var service = NewService();
        var (_, oldReview) = CompletedRunWithReview(service, "sha-a");
        var oldClaim = service.ClaimReview(oldReview.AttemptId, "reviewer-a", "host-a", 60, "claim-old").ReviewAttempt!;

        var runB = service.AcquireRun("AGT-1", "PROJ-1", oldReview.SourceRunAttemptId, "runner-b", "host-b", 60, "run-b").RunAttempt!;
        service.SettleRun(new AttemptWriteReference(runB.AttemptId, runB.LastFence, runB.AuthorityEpoch, "complete-b"), "done", "sha-b", null);

        var late = service.SettleReview(new SettleReviewAttemptRequest(
            new AttemptWriteReference(oldClaim.AttemptId, oldClaim.LastFence, oldClaim.AuthorityEpoch, "late-a"),
            "sha-a", ReviewTerminalOutcome.Pass));
        service.CreateReviewAttempt(new CreateReviewAttemptRequest(
            "AGT-1", "PROJ-1", "sha-b", runB.AttemptId, "req", "policy", [], "review-b"));
        var projection = service.GetTaskProjection("AGT-1");

        Assert.Equal(AttemptWriteStatus.Superseded, late.Status);
        Assert.Contains(projection.ReviewAttempts, x => x.AttemptId == oldReview.AttemptId && x.State == AttemptLifecycleState.Superseded);
        Assert.Equal("sha-b", projection.CurrentReviewSubject!.ExpectedResultSha);
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

    private (RunAttemptDto Run, ReviewAttemptDto Review) CompletedRunWithReview(AttemptAuthorityService service, string sha)
    {
        var run = service.AcquireRun("AGT-1", "PROJ-1", null, "runner", "host", 60, "run-create").RunAttempt!;
        service.SettleRun(new AttemptWriteReference(run.AttemptId, run.LastFence, run.AuthorityEpoch, "run-complete"), "done", sha, null);
        var review = service.CreateReviewAttempt(new CreateReviewAttemptRequest(
            "AGT-1", "PROJ-1", sha, run.AttemptId, "req", "policy", [], "review-create")).ReviewAttempt!;
        return (service.GetRun(run.AttemptId)!, review);
    }

    private AttemptAuthorityService NewService(Func<DateTime>? now = null)
    {
        Directory.CreateDirectory(_root);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
        }).Build();
        return new AttemptAuthorityService(config, NullLogger<AttemptAuthorityService>.Instance, now);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
