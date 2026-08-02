using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class ResultRefGcTests
{
    [Fact]
    public async Task Sweep_deletes_only_expired_reviewed_superseded_refs_and_keeps_current_attempt()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var (_, project, task) = await SeedReadyTaskAsync(store);
        await store.RegisterRunnerAsync(
            "runner-a",
            Runner("instance-a"),
            "test",
            default);

        var first = await CompleteRunWithHandoffAsync(store);
        await RecordTerminalReviewAsync(
            store,
            task.TaskId,
            first.RunId,
            first.ResultRef);
        task = (await store.GetTaskAsync(project.ProjectId, task.TaskId, default))!;
        await MoveAsync(store, project.ProjectId, task, "6-completed");

        clock.Advance(TimeSpan.FromDays(2));
        task = (await store.GetTaskAsync(project.ProjectId, task.TaskId, default))!;
        await MoveAsync(store, project.ProjectId, task, "2-ready");
        var current = await CompleteRunWithHandoffAsync(store);
        task = (await store.GetTaskAsync(project.ProjectId, task.TaskId, default))!;
        await MoveAsync(store, project.ProjectId, task, "6-completed");
        clock.Advance(TimeSpan.FromDays(2));

        var deleter = new RecordingResultRefDeleter();
        var result = await store.SweepResultRefsAsync(deleter, default);

        Assert.Equal([first.ResultRef], deleter.DeletedRefs);
        Assert.Contains(
            result.Decisions,
            decision => decision.RunId == first.RunId
                        && decision.Action == ResultRefGcAction.Deleted);
        Assert.Contains(
            result.Decisions,
            decision => decision.RunId == current.RunId
                        && decision.Action == ResultRefGcAction.Spared
                        && decision.Reason == "current-attempt");

        var second = await store.SweepResultRefsAsync(deleter, default);
        Assert.DoesNotContain(
            second.Decisions,
            decision => decision.RunId == first.RunId);
        Assert.Equal([first.ResultRef], deleter.DeletedRefs);
        var audit = await store.ListAuditAsync(0, default);
        Assert.Contains(
            audit,
            record => record.Action == "result-ref-gc.deleted"
                      && record.TargetId == first.RunId);
    }

    [Fact]
    public async Task Sweep_spares_expired_superseded_ref_while_card_review_is_active()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var (_, project, task) = await SeedReadyTaskAsync(store);
        await store.RegisterRunnerAsync(
            "runner-a",
            Runner("instance-a"),
            "test",
            default);

        var first = await CompleteRunWithHandoffAsync(store);
        await RecordTerminalReviewAsync(
            store,
            task.TaskId,
            first.RunId,
            first.ResultRef);
        task = (await store.GetTaskAsync(project.ProjectId, task.TaskId, default))!;
        await MoveAsync(store, project.ProjectId, task, "6-completed");
        clock.Advance(TimeSpan.FromDays(2));
        task = (await store.GetTaskAsync(project.ProjectId, task.TaskId, default))!;
        await MoveAsync(store, project.ProjectId, task, "2-ready");
        var current = await CompleteRunWithHandoffAsync(store);
        var activeSubject = await CreateReviewSubjectAsync(
            store,
            task.TaskId,
            current.RunId,
            current.ResultRef);
        clock.Advance(TimeSpan.FromDays(2));

        var deleter = new RecordingResultRefDeleter();
        var result = await store.SweepResultRefsAsync(deleter, default);

        Assert.Empty(deleter.DeletedRefs);
        Assert.Contains(
            result.Decisions,
            decision => decision.RunId == first.RunId
                        && decision.Action == ResultRefGcAction.Spared
                        && decision.Reason == "card-not-accepted");
        Assert.Contains(
            result.Decisions,
            decision => decision.RunId == current.RunId
                        && decision.Action == ResultRefGcAction.Spared
                        && decision.Reason == "current-attempt");
        Assert.NotNull(await store.GetReviewSubjectAsync(
            activeSubject.SubjectId,
            default));
    }

    [Fact]
    public async Task Failed_newer_run_without_a_result_does_not_unprotect_current_review_subject()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var (_, project, task) = await SeedReadyTaskAsync(store);
        await store.RegisterRunnerAsync(
            "runner-a",
            Runner("instance-a"),
            "test",
            default);

        var resultBearing = await CompleteRunWithHandoffAsync(store);
        await RecordTerminalReviewAsync(
            store,
            task.TaskId,
            resultBearing.RunId,
            resultBearing.ResultRef);
        task = (await store.GetTaskAsync(project.ProjectId, task.TaskId, default))!;
        await MoveAsync(store, project.ProjectId, task, "6-completed");
        clock.Advance(TimeSpan.FromDays(2));
        task = (await store.GetTaskAsync(project.ProjectId, task.TaskId, default))!;
        await MoveAsync(store, project.ProjectId, task, "2-ready");
        var failed = await store.ClaimAsync(
            new ClaimRequest("runner-a", "instance-a"),
            "test",
            default);
        await store.ReleaseLeaseAsync(
            failed.Run!.RunId,
            new LeaseReleaseRequest(
                "runner-a",
                "instance-a",
                failed.Lease!.LeaseId,
                failed.Lease.Fence,
                "runner-process-missing"),
            "test",
            default);
        task = (await store.GetTaskAsync(project.ProjectId, task.TaskId, default))!;
        await MoveAsync(store, project.ProjectId, task, "6-completed");
        clock.Advance(TimeSpan.FromDays(2));

        var deleter = new RecordingResultRefDeleter();
        var result = await store.SweepResultRefsAsync(deleter, default);

        Assert.Empty(deleter.DeletedRefs);
        Assert.Contains(
            result.Decisions,
            decision => decision.RunId == resultBearing.RunId
                        && decision.Action == ResultRefGcAction.Spared
                        && decision.Reason == "current-attempt");
    }

    private static async Task<(string RunId, string ResultRef)> CompleteRunWithHandoffAsync(
        TaskServerStore store)
    {
        var claim = await store.ClaimAsync(
            new ClaimRequest("runner-a", "instance-a"),
            "test",
            default);
        var runId = claim.Run!.RunId;
        var resultSha = new string(
            runId[^1] is >= 'a' and <= 'f' ? runId[^1] : '2',
            40);
        var resultRef =
            $"refs/heads/agent-studio/results/{runId}/{resultSha}";
        var envelope = new ImmutableResultEnvelope(
            "repo-project",
            runId,
            new string('1', 40),
            resultSha,
            resultRef,
            null,
            new string('3', 64),
            RepositoryUrl: "https://example.invalid/repository.git");
        var digest = ResultEnvelopeDigest.Compute(envelope);
        var handoff = await store.AcknowledgeResultHandoffAsync(
            runId,
            new ResultHandoffRequest(
                "runner-a",
                "instance-a",
                claim.Lease!.LeaseId,
                claim.Lease.Fence,
                1,
                $"handoff:{runId}",
                digest,
                envelope),
            "runner-a",
            default);
        await store.CompleteRunAsync(
            runId,
            new CompleteRunRequest(
                "runner-a",
                "instance-a",
                claim.Lease.LeaseId,
                claim.Lease.Fence,
                "success",
                ResultEnvelopeDigest: handoff.EnvelopeDigest,
                IdempotencyKey: $"completion:{runId}",
                Sequence: 2),
            "runner-a",
            default);
        return (runId, resultRef);
    }

    private static async Task RecordTerminalReviewAsync(
        TaskServerStore store,
        string taskId,
        string runId,
        string resultRef)
    {
        var subject = await CreateReviewSubjectAsync(
            store,
            taskId,
            runId,
            resultRef);
        await using var connection = new SqliteConnection(
            $"Data Source={store.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE review_attempts
               SET status = 'cleaned',
                   outcome = 'Pass',
                   reported_at = '2026-07-01T00:00:00.0000000Z',
                   cleaned_at = '2026-07-01T00:00:01.0000000Z'
             WHERE subject_id = $subject;
            """;
        command.Parameters.AddWithValue("$subject", subject.SubjectId);
        await command.ExecuteNonQueryAsync();
    }

    private static Task<ReviewSubjectDto> CreateReviewSubjectAsync(
        TaskServerStore store,
        string taskId,
        string runId,
        string resultRef)
        => store.CreateReviewSubjectAsync(
            new CreateReviewSubjectRequest(
                taskId,
                runId,
                "repo-project",
                "https://example.invalid/repository.git",
                resultRef.Split('/')[^1],
                resultRef,
                null,
                null,
                "host-a",
                "review-policy",
                new ReviewPlanDto(
                    [new ReviewCommandDto(
                        "build",
                        "quality",
                        "dotnet",
                        ["test"])],
                    ["quality"]),
                $"review:{runId}"),
            "test",
            default);

    private static async Task MoveAsync(
        TaskServerStore store,
        string projectId,
        TaskDto task,
        string state)
        => _ = await store.UpdateTaskAsync(
            projectId,
            task.TaskId,
            new UpdateTaskRequest(null, null, state, task.Version),
            "test",
            default);

    private static TaskServerStore Store(
        string dataDirectory,
        TimeProvider clock)
        => new(
            Options.Create(new TaskServerOptions
            {
                DataDirectory = dataDirectory,
                ResultRetentionDays = 1,
                ResultRefGcBatchSize = 50,
            }),
            clock);

    private static RegisterRunnerRequest Runner(string instance)
        => new(
            "runner",
            "host-a",
            instance,
            "1.0.0",
            TaskServerProtocol.Current,
            [ReviewCapabilities.CodingExecutor]);

    private static async Task<(WorkspaceDto Workspace, ProjectDto Project, TaskDto Task)>
        SeedReadyTaskAsync(TaskServerStore store)
    {
        var workspace = await store.CreateWorkspaceAsync(
            new CreateWorkspaceRequest("Workspace"),
            "test",
            default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(
                workspace.WorkspaceId,
                "Project",
                "GC"),
            "test",
            default);
        var task = await store.CreateTaskAsync(
            project.ProjectId,
            new CreateTaskRequest(
                "Task",
                "Do the work",
                "2-ready"),
            "test",
            default);
        return (workspace, project, task);
    }

    private sealed class RecordingResultRefDeleter : IResultRefDeleter
    {
        public List<string> DeletedRefs { get; } = [];

        public Task<ResultRefDeleteResult> DeleteAsync(
            string repositoryUrl,
            string immutableRemoteRef,
            CancellationToken ct)
        {
            DeletedRefs.Add(immutableRemoteRef);
            return Task.FromResult(new ResultRefDeleteResult(true));
        }
    }
}
