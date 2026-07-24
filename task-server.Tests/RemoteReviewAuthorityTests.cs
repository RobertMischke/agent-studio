using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class RemoteReviewAuthorityTests
{
    private const string ResultSha = "589c462f589c462f589c462f589c462f589c462f";
    private const string TreeSha = "0123456789abcdef0123456789abcdef01234567";
    private const string RepositoryId = "repo_0123456789abcdef";
    private const string RepositoryUrl = "https://example.invalid/product.git";

    [Fact]
    public async Task Fenced_review_claim_report_cleanup_reaches_human_review_and_is_idempotent()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var source = await SeedReviewSubjectAsync(store);
        await RegisterReviewerAsync(store, "review-a", "review-instance-a", "review-host-a");

        var claim = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "review-instance-a"), "review-a", default);
        Assert.Equal(source.SubjectId, claim.Subject!.SubjectId);
        Assert.Equal(ResultSha, claim.Subject.ExpectedResultSha);
        Assert.NotEqual(source.SourceRunId, claim.Attempt!.AttemptId);

        var renewed = await store.RenewReviewLeaseAsync(
            claim.Attempt.AttemptId,
            new ReviewLeaseRenewRequest(
                "review-a", "review-instance-a", claim.Lease!.LeaseId,
                claim.Lease.Fence, "renew-1"),
            "review-a",
            default);
        Assert.True(renewed.ExpiresAt >= claim.Lease.ExpiresAt);

        var request = PassingReport(claim);
        var report = await store.ReportReviewAsync(claim.Attempt.AttemptId, request, "review-a", default);
        var replay = await store.ReportReviewAsync(claim.Attempt.AttemptId, request, "review-a", default);
        Assert.Equal(report, replay);
        Assert.False(report.RetryScheduled);
        Assert.Equal("5-human-review", report.TaskState);

        var cleanupRequest = new ReviewCleanupRequest(
            "review-a", "review-instance-a", claim.Lease.LeaseId,
            claim.Lease.Fence, "cleanup-1", true);
        var cleanup = await store.CleanupReviewAsync(
            claim.Attempt.AttemptId, cleanupRequest, "review-a", default);
        var cleanupReplay = await store.CleanupReviewAsync(
            claim.Attempt.AttemptId, cleanupRequest, "review-a", default);
        Assert.Equal("cleaned", cleanup.Status);
        Assert.Equal("duplicate", cleanupReplay.Status);

        var task = await TaskAsync(store, source.TaskId);
        Assert.Equal("5-human-review", task.State);
        var audit = await store.ListAuditAsync(0, default);
        Assert.Contains(audit, record => record.Action == "review.claimed");
        Assert.Contains(audit, record => record.Action == "review.reported"
                                         && record.DetailJson.Contains(ResultSha, StringComparison.Ordinal));
        Assert.Contains(audit, record => record.Action == "review.cleaned");
    }

    [Fact]
    public async Task Failed_workspace_cleanup_is_audited_and_retries_the_same_subject()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var subject = await SeedReviewSubjectAsync(store);
        await RegisterReviewerAsync(store, "review-a", "instance-a", "host-a");
        var first = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);
        await store.ReportReviewAsync(
            first.Attempt!.AttemptId, PassingReport(first), "review-a", default);

        var cleanupRequest = new ReviewCleanupRequest(
            "review-a", "instance-a", first.Lease!.LeaseId, first.Lease.Fence,
            "cleanup-failed-1", false, "WorkspaceCleanupFailed");
        var cleanup = await store.CleanupReviewAsync(
            first.Attempt.AttemptId, cleanupRequest, "review-a", default);
        var replay = await store.CleanupReviewAsync(
            first.Attempt.AttemptId, cleanupRequest, "review-a", default);

        Assert.Equal("cleanup-failed", cleanup.Status);
        Assert.True(cleanup.RetryScheduled);
        Assert.Equal("duplicate", replay.Status);
        Assert.True(replay.RetryScheduled);
        Assert.Equal("4-auto-review", (await TaskAsync(store, subject.TaskId)).State);
        Assert.Contains(
            await store.ListAuditAsync(0, default),
            record => record.Action == "review.cleanup-failed"
                      && record.DetailJson.Contains("WorkspaceCleanupFailed", StringComparison.Ordinal));

        var retry = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);
        Assert.Equal(subject.SubjectId, retry.Subject!.SubjectId);
        Assert.NotEqual(first.Attempt.AttemptId, retry.Attempt!.AttemptId);
        Assert.Equal(first.Attempt.AttemptNumber + 1, retry.Attempt.AttemptNumber);
    }

    [Theory]
    [InlineData("RepositoryMismatch")]
    [InlineData("ShaMismatch")]
    [InlineData("DirtyBefore")]
    [InlineData("MutatedAfter")]
    [InlineData("SnapshotUnavailable")]
    public async Task Typed_infrastructure_outcomes_stay_in_auto_review_and_retry_the_same_subject(
        string classification)
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var subject = await SeedReviewSubjectAsync(store);
        await RegisterReviewerAsync(store, "review-a", "instance-a", "host-a");
        var first = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);
        var request = PassingReport(first) with
        {
            Outcome = classification == "SnapshotUnavailable" ? "ReviewInfra" : "Pass",
            FailureClassification = classification == "SnapshotUnavailable" ? classification : null,
            Workspace = PassingReport(first).Workspace with
            {
                RepositoryId = classification == "RepositoryMismatch" ? "repo_wrong" : RepositoryId,
                ActualHead = classification == "ShaMismatch"
                    ? "6130634361306343613063436130634361306343"
                    : ResultSha,
                DirtyBefore = classification == "DirtyBefore",
                DirtyAfter = classification == "MutatedAfter",
            },
        };

        var report = await store.ReportReviewAsync(
            first.Attempt!.AttemptId, request, "review-a", default);

        Assert.Equal("ReviewInfra", report.Outcome);
        Assert.Equal(classification, report.FailureClassification);
        Assert.True(report.RetryScheduled);
        Assert.Equal("4-auto-review", (await TaskAsync(store, subject.TaskId)).State);

        var retry = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);
        Assert.Equal("claimed", retry.Status);
        Assert.Equal(subject.SubjectId, retry.Subject!.SubjectId);
        Assert.NotEqual(first.Attempt.AttemptId, retry.Attempt!.AttemptId);
        Assert.Equal(first.Attempt.AttemptNumber + 1, retry.Attempt.AttemptNumber);
    }

    [Fact]
    public async Task Restart_takeover_raises_review_fence_and_rejects_stale_report()
    {
        using var temp = new TempDirectory();
        var firstStore = Store(temp.Path);
        await firstStore.InitializeAsync();
        await SeedReviewSubjectAsync(firstStore);
        await RegisterReviewerAsync(firstStore, "review-a", "instance-a", "host-a");
        var first = await firstStore.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);

        var restarted = Store(temp.Path);
        await restarted.InitializeAsync();
        await RegisterReviewerAsync(restarted, "review-b", "instance-b", "host-b");
        var takeover = await restarted.ClaimReviewAsync(
            new ReviewClaimRequest("review-b", "instance-b"), "review-b", default);

        Assert.Equal(first.Attempt!.AttemptId, takeover.Attempt!.AttemptId);
        Assert.True(takeover.Lease!.Fence > first.Lease!.Fence);
        var stale = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            restarted.ReportReviewAsync(first.Attempt.AttemptId, PassingReport(first), "review-a", default));
        Assert.Equal("stale-review-fence", stale.Code);

        var accepted = await restarted.ReportReviewAsync(
            takeover.Attempt.AttemptId, PassingReport(takeover), "review-b", default);
        Assert.Equal("Pass", accepted.Outcome);
    }

    [Fact]
    public async Task Three_parallel_reviews_of_one_repository_have_independent_subjects_fences_and_namespaces()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        for (var i = 0; i < 3; i++)
            await SeedReviewSubjectAsync(store, $"Task {i}");
        await RegisterReviewerAsync(store, "review-a", "instance-a", "host-a");

        var claims = new List<ReviewClaimResponse>();
        for (var i = 0; i < 3; i++)
            claims.Add(await store.ClaimReviewAsync(
                new ReviewClaimRequest("review-a", "instance-a", AvailableSlots: 3 - i),
                "review-a",
                default));

        Assert.Equal(3, claims.Select(claim => claim.Subject!.SubjectId).Distinct().Count());
        Assert.Equal(3, claims.Select(claim => claim.Attempt!.AttemptId).Distinct().Count());
        Assert.Equal(3, claims.Select(claim => claim.Lease!.ResourceNamespace).Distinct().Count());
        Assert.All(claims, claim => Assert.Equal(RepositoryId, claim.Subject!.RepositoryId));
    }

    [Fact]
    public async Task Different_host_policy_refuses_same_failure_domain_but_allows_another_review_host()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        await SeedReviewSubjectAsync(store, policyDifferentHost: true, codingHost: "host-a");
        await RegisterReviewerAsync(store, "same-host-review", "instance-a", "host-a");
        await RegisterReviewerAsync(store, "other-host-review", "instance-b", "host-b");

        var refused = await store.ClaimReviewAsync(
            new ReviewClaimRequest("same-host-review", "instance-a"), "same-host-review", default);
        var accepted = await store.ClaimReviewAsync(
            new ReviewClaimRequest("other-host-review", "instance-b"), "other-host-review", default);

        Assert.Equal("empty", refused.Status);
        Assert.Equal("claimed", accepted.Status);
        Assert.Equal("host-b", accepted.Lease!.HostId);
    }

    [Fact]
    public async Task Review_infrastructure_failure_never_creates_a_coding_attempt_or_returns_to_ready()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var subject = await SeedReviewSubjectAsync(store);
        await RegisterReviewerAsync(store, "review-a", "instance-a", "host-a");
        var claim = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);
        var infra = PassingReport(claim) with
        {
            Outcome = "ReviewInfra",
            FailureClassification = "SnapshotUnavailable",
        };

        await store.ReportReviewAsync(claim.Attempt!.AttemptId, infra, "review-a", default);

        await using var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM runs WHERE task_id = $task;";
        command.Parameters.AddWithValue("$task", subject.TaskId);
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        Assert.Equal("4-auto-review", (await TaskAsync(store, subject.TaskId)).State);
    }

    [Fact]
    public async Task Remote_task_done_review_infra_retry_then_all_aspects_pass_reaches_human_review_without_ready()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var subject = await SeedReviewSubjectAsync(store);
        await RegisterReviewerAsync(store, "review-a", "instance-a", "host-a");

        var first = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);
        var unavailable = PassingReport(first) with
        {
            Outcome = "ReviewInfra",
            FailureClassification = "SnapshotUnavailable",
        };
        var infra = await store.ReportReviewAsync(
            first.Attempt!.AttemptId, unavailable, "review-a", default);
        Assert.Equal("4-auto-review", infra.TaskState);
        Assert.Equal("4-auto-review", (await TaskAsync(store, subject.TaskId)).State);

        var retry = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);
        var passed = await store.ReportReviewAsync(
            retry.Attempt!.AttemptId, PassingReport(retry), "review-a", default);

        Assert.Equal("5-human-review", passed.TaskState);
        Assert.Equal("5-human-review", (await TaskAsync(store, subject.TaskId)).State);
        Assert.Equal(8, PassingReport(retry).Verdicts.Count);
        Assert.Equal(
            new[]
            {
                "artifacts",
                "build-tests",
                "code-quality",
                "completion",
                "documentation",
                "evidence",
                "requirements",
                "visual",
            },
            PassingReport(retry).Verdicts.Select(verdict => verdict.Aspect).Order().ToArray());
        Assert.DoesNotContain(
            await store.ListAuditAsync(0, default),
            record => record.Action == "task.updated"
                      && record.DetailJson.Contains("2-ready", StringComparison.Ordinal));
    }

    private static TaskServerStore Store(string dataDirectory)
        => new(Options.Create(new TaskServerOptions { DataDirectory = dataDirectory }), TimeProvider.System);

    private static async Task<ReviewSubjectDto> SeedReviewSubjectAsync(
        TaskServerStore store,
        string title = "Task",
        bool policyDifferentHost = false,
        string codingHost = "coding-host")
    {
        var workspaces = await store.ListWorkspacesAsync(default);
        var workspace = workspaces.FirstOrDefault()
            ?? await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Workspace"), "test", default);
        var projects = await store.ListProjectsAsync(workspace.WorkspaceId, default);
        var project = projects.FirstOrDefault()
            ?? await store.CreateProjectAsync(
                new CreateProjectRequest(workspace.WorkspaceId, "Project", "TS"), "test", default);
        var task = await store.CreateTaskAsync(
            project.ProjectId, new CreateTaskRequest(title, "Do the work", "2-ready"), "test", default);
        var codingId = "coding-" + task.TaskId;
        var codingInstance = "instance-" + task.TaskId;
        await store.RegisterRunnerAsync(
            codingId,
            new RegisterRunnerRequest(
                codingId, codingHost, codingInstance, "1.0.0", TaskServerProtocol.Current,
                [ReviewCapabilities.CodingExecutor]),
            "test",
            default);
        var coding = await store.ClaimAsync(new ClaimRequest(codingId, codingInstance), codingId, default);
        await store.CompleteRunAsync(
            coding.Run!.RunId,
            new CompleteRunRequest(
                codingId, codingInstance, coding.Lease!.LeaseId, coding.Lease.Fence,
                "success", "done", ResultSha, RepositoryId, RepositoryUrl,
                $"refs/results/{task.TaskKey}/{ResultSha}"),
            codingId,
            default);
        return await store.CreateReviewSubjectAsync(
            new CreateReviewSubjectRequest(
                task.TaskId, coding.Run.RunId, RepositoryId, RepositoryUrl, ResultSha,
                $"refs/results/{task.TaskKey}/{ResultSha}", null, null, codingHost,
                "policy-v1", Plan(policyDifferentHost), $"subject-{task.TaskId}"),
            "orchestrator",
            default);
    }

    private static ReviewPlanDto Plan(bool differentHost = false)
    {
        string[] aspects =
        [
            "completion",
            "build-tests",
            "requirements",
            "code-quality",
            "documentation",
            "evidence",
            "artifacts",
            "visual",
        ];
        return new ReviewPlanDto(
            aspects.Select(aspect => new ReviewCommandDto(
                $"step-{aspect}", aspect, "review-tool", [aspect])).ToArray(),
            aspects,
            RequiresVisualReview: true,
            RequireDifferentHostFailureDomain: differentHost);
    }

    private static async Task RegisterReviewerAsync(
        TaskServerStore store,
        string id,
        string instance,
        string host)
        => await store.RegisterRunnerAsync(
            id,
            new RegisterRunnerRequest(
                id, host, instance, "1.0.0", TaskServerProtocol.Current,
                [
                    ReviewCapabilities.ReviewExecutor,
                    ReviewCapabilities.GitMaterialization,
                    ReviewCapabilities.SemanticReview,
                    ReviewCapabilities.VisionReview,
                ]),
            id,
            default);

    private static ReviewReportRequest PassingReport(ReviewClaimResponse claim)
    {
        var subject = claim.Subject!;
        var lease = claim.Lease!;
        var commands = subject.Plan.Commands.Select(command => new ReviewCommandEvidenceDto(
            command.StepId, command.Aspect, command.FileName, command.Arguments,
            ResultSha, ResultSha, TreeSha,
            DateTime.UtcNow.AddSeconds(-1), DateTime.UtcNow, 0, null,
            new string('a', 64), new string('b', 64))).ToArray();
        var verdicts = subject.Plan.RequiredAspects.Select(aspect =>
            new ReviewVerdictDto(aspect, "pass", "Verified", $"{aspect} passed")).ToArray();
        return new ReviewReportRequest(
            lease.ExecutorId, lease.InstanceId, lease.LeaseId, lease.Fence,
            $"report-{claim.Attempt!.AttemptId}", "Pass", null, "all review aspects passed",
            new ReviewWorkspaceProofDto(
                RepositoryId, ResultSha, ResultSha, TreeSha, false, false,
                $"workspace-{claim.Attempt.AttemptId}", lease.ResourceNamespace),
            new ReviewEnvironmentDto(
                lease.HostId, lease.ExecutorId, lease.InstanceId, "linux", "x64", "10.0",
                new Dictionary<string, string> { ["git"] = "2.x" },
                new Dictionary<string, string>
                {
                    ["workspace"] = $"workspace-{claim.Attempt.AttemptId}",
                    ["cache"] = lease.ResourceNamespace,
                    ["ports"] = $"{lease.PortBase}-{lease.PortBase + 7}",
                    ["containers"] = lease.ResourceNamespace,
                    ["databases"] = lease.ResourceNamespace,
                    ["credentials"] = "review-read-only",
                }),
            commands,
            [new ReviewArtifactEvidenceDto("review.json", "application/json", new string('c', 64), 42)],
            verdicts);
    }

    private static async Task<TaskDto> TaskAsync(TaskServerStore store, string taskId)
    {
        var project = Assert.Single(await store.ListProjectsAsync(null, default));
        return (await store.GetTaskAsync(project.ProjectId, taskId, default))!;
    }
}
