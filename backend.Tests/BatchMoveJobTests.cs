using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class BatchMoveJobTests : IDisposable
{
    private readonly string _watchPath = Path.Combine(
        Path.GetTempPath(),
        "atp-batch-job-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _taskRepository = Path.Combine(
        Path.GetTempPath(),
        "atp-batch-job-repository-" + Guid.NewGuid().ToString("N"));

    public BatchMoveJobTests()
    {
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { /* best-effort */ }
        try { Directory.Delete(_taskRepository, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Endpoint_ReportsProgress_ContinuesAfterItemFailure_AndDoesNotBlockReviewRead()
    {
        var executor = new GatedExecutor();
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["WatchPaths:0:Name"] = "batch-job-test",
                        ["WatchPaths:0:Path"] = _watchPath,
                        ["WatchPaths:0:RootPath"] = _watchPath,
                        ["TaskRepository"] = _taskRepository,
                    });
                });
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IBatchMoveItemExecutor>();
                    services.AddSingleton<IBatchMoveItemExecutor>(executor);
                });
            });

        using var client = factory.CreateClient();
        using var warmup = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, warmup.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/tasks/batch-move")
        {
            Content = JsonContent.Create(new BatchMoveRequest
            {
                Items =
                [
                    new() { JobId = "alpha", WatchPath = _watchPath, TargetState = TaskStates.Archive },
                    new() { JobId = "beta", WatchPath = _watchPath, TargetState = TaskStates.Archive },
                    new() { JobId = "gamma", WatchPath = _watchPath, TargetState = TaskStates.Archive },
                ],
            }),
        };
        request.Headers.Add("X-Client-Id", "local-default");

        var enqueueTimer = Stopwatch.StartNew();
        using var response = await client.SendAsync(request);
        enqueueTimer.Stop();
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(enqueueTimer.Elapsed < TimeSpan.FromSeconds(1));
        var accepted = await response.Content.ReadFromJsonAsync<BatchMoveJobResponse>();
        Assert.NotNull(accepted);
        Assert.Equal($"/api/tasks/batch-move/{accepted!.Id}", response.Headers.Location?.ToString());
        Assert.True(accepted.Status is BatchMoveJobStates.Queued or BatchMoveJobStates.Running);
        Assert.Equal(3, accepted.Total);

        await executor.SecondItemStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var running = await client.GetFromJsonAsync<BatchMoveJobResponse>(
            $"/api/tasks/batch-move/{accepted.Id}");
        Assert.NotNull(running);
        Assert.Equal(BatchMoveJobStates.Running, running!.Status);
        Assert.Equal(1, running.Completed);
        Assert.Equal("moved", Assert.Single(running.Results).Status);

        // The background worker is deliberately paused inside item two. A
        // review-plane read must still complete, proving the batch does not
        // occupy the request path or hold a global task-index/lane lock.
        using var reviewCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var reviewTimer = Stopwatch.StartNew();
        using var review = await client.GetAsync(
            "/api/projects/batch-job-test/review-decisions-pending",
            reviewCts.Token);
        reviewTimer.Stop();
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        Assert.True(reviewTimer.Elapsed < TimeSpan.FromSeconds(1));

        executor.ReleaseSecondItem.TrySetResult();

        BatchMoveJobResponse? completed = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            completed = await client.GetFromJsonAsync<BatchMoveJobResponse>(
                $"/api/tasks/batch-move/{accepted.Id}");
            if (completed is not null && BatchMoveJobStates.IsTerminal(completed.Status)) break;
            await Task.Delay(20);
        }

        Assert.NotNull(completed);
        Assert.Equal(BatchMoveJobStates.Completed, completed!.Status);
        Assert.Equal(3, completed.Completed);
        Assert.Equal(2, completed.Succeeded);
        Assert.Equal(1, completed.Failed);
        Assert.Equal(["moved", "conflict", "moved"], completed.Results.Select(result => result.Status).ToArray());
        Assert.Equal("simulated collision", completed.Results[1].Message);
    }

    private sealed class GatedExecutor : IBatchMoveItemExecutor
    {
        private int _calls;
        public TaskCompletionSource SecondItemStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSecondItem { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<BatchMoveItemResult> ExecuteAsync(
            BatchMoveItem item,
            CancellationToken cancellationToken,
            string cause)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 2)
            {
                SecondItemStarted.TrySetResult();
                await ReleaseSecondItem.Task.WaitAsync(cancellationToken);
                return new BatchMoveItemResult
                {
                    JobId = item.JobId,
                    Status = "conflict",
                    Message = "simulated collision",
                };
            }

            return new BatchMoveItemResult { JobId = item.JobId, Status = "moved" };
        }
    }
}
