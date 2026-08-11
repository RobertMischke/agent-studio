using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RunnerActiveAttemptReportsTests
{
    [Fact]
    public void Coding_report_carries_persisted_fence_epoch_instance_and_project()
    {
        var now = new DateTime(2026, 8, 11, 20, 0, 0, DateTimeKind.Utc);
        var lease = new RunLeaseInfoDto(
            "AGT-2646",
            "runner-coding",
            "Coding Runner",
            "host-a",
            4242,
            "task-server",
            "lease-coding",
            17,
            now,
            now.AddMinutes(2),
            "attempt-coding",
            3);
        var slot = new PersistedRunnerSlot(
            "AGT-2646",
            "slot-attempt",
            lease,
            "run-coding",
            "instance-coding",
            "Agent Studio",
            "https://example.invalid/repository.git",
            "main",
            "task",
            "/worktree",
            "/worker",
            4242,
            now,
            4,
            "running",
            now);

        var report = Assert.Single(RunnerActiveAttemptReports.Coding([slot], 120));

        Assert.Equal(RunnerAttemptKinds.Coding, report.Kind);
        Assert.Equal("attempt-coding", report.AttemptId);
        Assert.Equal("lease-coding", report.LeaseId);
        Assert.Equal(17, report.Fence);
        Assert.Equal(3, report.AuthorityEpoch);
        Assert.Equal("instance-coding", report.LeaseInstanceId);
        Assert.Equal("Agent Studio", report.ProjectId);
    }

    [Fact]
    public void Review_report_carries_immutable_claim_authority()
    {
        var now = new DateTime(2026, 8, 11, 20, 0, 0, DateTimeKind.Utc);
        var attempt = new ReviewAttemptDto(
            "attempt-review",
            "subject-review",
            "AGT-2646",
            1,
            "leased",
            "runner-review",
            "host-a",
            23,
            now,
            null,
            null,
            null,
            null);
        var subject = new ReviewSubjectDto(
            "subject-review",
            "storage-id-2646",
            "run-coding",
            "repository",
            null,
            new string('a', 40),
            null,
            null,
            null,
            "host-coding",
            "policy",
            new ReviewPlanDto([], []),
            now);
        var lease = new ReviewLeaseDto(
            "lease-review",
            attempt.AttemptId,
            subject.SubjectId,
            "runner-review",
            "instance-review",
            "host-a",
            23,
            now,
            now.AddMinutes(2),
            "active",
            "review-attempt-review-f23",
            24000,
            3);
        var slot = new PersistedReviewSlot(
            new ReviewClaimResponse("claimed", attempt, subject, lease),
            "/worker",
            "/workspace",
            4243,
            now,
            "running",
            now);

        var report = Assert.Single(RunnerActiveAttemptReports.Review([slot], 120));

        Assert.Equal(RunnerAttemptKinds.Review, report.Kind);
        Assert.Equal("attempt-review", report.AttemptId);
        // Monolith subjects may expose the storage id used for materialization,
        // while the attempt retains the canonical authority task key.
        Assert.Equal("AGT-2646", report.TaskKey);
        Assert.Equal("lease-review", report.LeaseId);
        Assert.Equal(23, report.Fence);
        Assert.Equal(3, report.AuthorityEpoch);
        Assert.Equal("instance-review", report.LeaseInstanceId);
    }
}
