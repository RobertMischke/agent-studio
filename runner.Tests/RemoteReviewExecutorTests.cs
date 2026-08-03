using System.Net;
using System.Text.Json;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RemoteReviewExecutorTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _reviewRoot = Path.Combine(
        Path.GetTempPath(),
        "remote-review-executor-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Graceful_shutdown_reports_interrupted_work_before_cleanup()
    {
        var handler = new InterruptedArtifactHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server") };
        var options = Options();
        using var client = new TaskServerClient(
            http,
            options.RunnerId,
            usesDurableTaskServer: true,
            options: options);
        var logs = new List<string>();
        using var shutdown = new CancellationTokenSource();
        var state = new ReviewStateStore(options.StateDir);
        var execution = new RemoteReviewExecutor(options, client, state, logs.Add)
            .RunClaimedAsync(Claim(), shutdown.Token);

        await handler.ArtifactRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await shutdown.CancelAsync();

        Assert.Equal(3, await execution.WaitAsync(TimeSpan.FromSeconds(5)));
        var report = Assert.Single(handler.Reports);
        Assert.Equal("ReviewInfra", report.Outcome);
        Assert.Equal("ExecutorRestarted", report.FailureClassification);
        Assert.Contains("daemon stopped during workspace preparation", report.Summary, StringComparison.Ordinal);
        Assert.Contains("Lost work extent: 0 of 0 review commands completed", report.Summary, StringComparison.Ordinal);
        Assert.Equal(17, report.Fence);
        Assert.Equal(23, report.AuthorityEpoch);

        var cleanup = Assert.Single(handler.Cleanups);
        Assert.True(cleanup.WorkspaceRemoved);
        Assert.Null(cleanup.FailureClassification);
        Assert.Contains(
            logs,
            line => line.Contains(
                "classification=ExecutorRestarted",
                StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(_reviewRoot, "review-attempt-1-f17")));
        Assert.Empty(state.LoadAll());
    }

    public void Dispose()
    {
        try { Directory.Delete(_reviewRoot, recursive: true); }
        catch { /* best effort */ }
    }

    private RunnerOptions Options() => new()
    {
        ServerUrl = "http://task-server",
        RunnerId = "review-runner",
        RunnerName = "review-runner",
        Hostname = "review-host",
        BackendName = "test",
        Role = "review",
        WorkDir = _reviewRoot,
        ReviewWorkDir = _reviewRoot,
        StateDir = Path.Combine(_reviewRoot, "state"),
        BaseBranch = "main",
        CliBin = "test",
        CliArgs = "",
        TtlSeconds = 120,
        HeartbeatSeconds = 30,
    };

    private static ReviewClaimResponse Claim()
    {
        var now = new DateTime(2026, 8, 2, 15, 0, 0, DateTimeKind.Utc);
        var attempt = new ReviewAttemptDto(
            "attempt-1",
            "subject-1",
            "AGT-2471",
            1,
            "leased",
            "review-runner",
            "review-host",
            17,
            now,
            null,
            null,
            null,
            null);
        var subject = new ReviewSubjectDto(
            "subject-1",
            "AGT-2471",
            "run-1",
            "example/repository",
            null,
            new string('a', 40),
            null,
            "bundle",
            new string('b', 64),
            "coding-host",
            "policy-v1",
            new ReviewPlanDto([], []),
            now);
        var lease = new ReviewLeaseDto(
            "lease-1",
            attempt.AttemptId,
            subject.SubjectId,
            "review-runner",
            "instance-1",
            "review-host",
            17,
            now,
            now.AddMinutes(2),
            "active",
            "review-attempt-1-f17",
            25000,
            23);
        return new ReviewClaimResponse("claimed", attempt, subject, lease);
    }

    private sealed class InterruptedArtifactHandler : HttpMessageHandler
    {
        public TaskCompletionSource ArtifactRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<ReviewReportRequest> Reports { get; } = [];
        public List<ReviewCleanupRequest> Cleanups { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (request.Method == HttpMethod.Get && path.EndsWith("/artifacts/bundle/content", StringComparison.Ordinal))
            {
                ArtifactRequested.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The cancelled artifact request must not complete.");
            }

            if (path.EndsWith("/report", StringComparison.Ordinal))
            {
                Reports.Add(await ReadAsync<ReviewReportRequest>(request));
                return JsonResponse(new ReviewReportDto(
                    "report-1",
                    "attempt-1",
                    "subject-1",
                    "ReviewInfra",
                    "ExecutorRestarted",
                    "retry required",
                    new string('c', 64),
                    DateTime.UtcNow,
                    true,
                    "4-auto-review"));
            }

            if (path.EndsWith("/cleanup", StringComparison.Ordinal))
            {
                Cleanups.Add(await ReadAsync<ReviewCleanupRequest>(request));
                return JsonResponse(new ReviewCleanupResponse(
                    "cleaned",
                    "attempt-1",
                    DateTime.UtcNow,
                    true));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("unexpected test request"),
            };
        }

        private static async Task<T> ReadAsync<T>(HttpRequestMessage request)
            => JsonSerializer.Deserialize<T>(
                   await request.Content!.ReadAsStringAsync(),
                   Json)
               ?? throw new InvalidDataException($"Request body was not valid {typeof(T).Name} JSON.");

        private static HttpResponseMessage JsonResponse<T>(T value)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(value, Json)),
            };
    }
}
