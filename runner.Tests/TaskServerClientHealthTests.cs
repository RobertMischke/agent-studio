using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public class TaskServerClientHealthTests
{
    [Fact]
    public async Task Register_uses_and_verifies_the_configured_client_identity()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""{"identity":{"id":"agent-runner-01","kind":"service"}}""")
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server") };
        using var client = new TaskServerClient(http, "provisional-runner", "agent-runner-01");

        var adopted = await client.RegisterAsync("ignored-display-name", "service", CancellationToken.None);

        Assert.Equal("agent-runner-01", adopted);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/clients/agent-runner-01", request.PathAndQuery);
        Assert.Equal("agent-runner-01", request.ClientId);
        Assert.Equal(HttpMethod.Get, request.Method);
    }

    [Fact]
    public async Task Register_does_not_create_a_replacement_when_the_configured_identity_is_unknown()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
        {
            Content = new StringContent("client-not-found")
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server") };
        using var client = new TaskServerClient(http, "provisional-runner", "missing-runner");

        var error = await Assert.ThrowsAsync<TaskServerException>(
            () => client.RegisterAsync("would-create-a-different-id", "service", CancellationToken.None));

        Assert.Equal(404, error.StatusCode);
        Assert.Contains("will not create a replacement identity", error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Register_rejects_a_retired_configured_identity()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""{"identity":{"id":"agent-runner-01","kind":"retired"}}""")
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server") };
        using var client = new TaskServerClient(http, "provisional-runner", "agent-runner-01");

        var error = await Assert.ThrowsAsync<TaskServerException>(
            () => client.RegisterAsync("ignored-display-name", "service", CancellationToken.None));

        Assert.Equal(409, error.StatusCode);
        Assert.Contains("identity is retired", error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Register_rejects_a_malformed_success_response()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{}")
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server") };
        using var client = new TaskServerClient(http, "provisional-runner", "agent-runner-01");

        var error = await Assert.ThrowsAsync<TaskServerException>(
            () => client.RegisterAsync("ignored-display-name", "service", CancellationToken.None));

        Assert.Equal(409, error.StatusCode);
        Assert.Contains("different or empty identity", error.Message);
        Assert.Single(handler.Requests);
    }

    /// <summary>
    /// An unreachable server (a closed loopback port stands in for a dropped
    /// reverse tunnel) must be reported as a clean, non-null health reason and
    /// must never throw - that is what lets the runner surface "connection lost"
    /// once instead of a transport-exception cascade through register/lease.
    /// </summary>
    [Fact]
    public async Task Probe_reports_a_reason_when_the_server_is_unreachable()
    {
        // Port 1 is reserved and never listening: connect fails fast (refused).
        var (options, _, _, _) = RunnerOptions.Parse(["--health-check", "--server", "http://127.0.0.1:1"]);
        using var client = new TaskServerClient(options);

        var reason = await client.ProbeHealthAsync(CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public async Task Probe_propagates_a_real_shutdown_rather_than_masking_it_as_a_health_failure()
    {
        var (options, _, _, _) = RunnerOptions.Parse(["--health-check", "--server", "http://127.0.0.1:1"]);
        using var client = new TaskServerClient(options);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.ProbeHealthAsync(cancelled.Token));
    }

    [Fact]
    public async Task Durable_task_server_does_not_probe_legacy_project_chat()
    {
        var handler = new RecordingHandler(_ =>
            throw new InvalidOperationException("The legacy endpoint must not be called."));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://task-server") };
        using var client = new TaskServerClient(
            http,
            "runner-v1",
            usesDurableTaskServer: true);

        var claim = await client.ClaimProjectChatWorkAsync(
            new RemoteChatWorkClaimRequest("runner-v1", "Runner v1", "host-a"),
            CancellationToken.None);

        Assert.Equal(RemoteChatWorkClaimStatuses.Empty, claim.Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Durable_registration_adopts_the_server_owned_host_capacity()
    {
        var now = DateTime.UtcNow.ToString("O");
        var handler = new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent($$"""
                {
                  "runnerId":"runner-v1",
                  "name":"Runner v1",
                  "hostId":"host-a",
                  "instanceId":"host-a:1",
                  "runnerVersion":"1.0.0",
                  "protocolVersion":3,
                  "status":"active",
                  "registeredAt":"{{now}}",
                  "lastSeenAt":"{{now}}",
                  "runtimeCapacity":{
                    "hostId":"host-a",
                    "maxParallelism":7,
                    "targetLoadPercent":80,
                    "rampStrategy":"balanced",
                    "version":1,
                    "updatedAt":"{{now}}"
                  }
                }
                """),
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1") };
        var (options, _, _, _) = RunnerOptions.Parse(
            ["--poll", "--server", "http://127.0.0.1", "--runner-id", "runner-v1", "--max-parallelism", "2"]);
        using var client = new TaskServerClient(
            http,
            "runner-v1",
            usesDurableTaskServer: true,
            options: options);

        await client.RegisterAsync("Runner v1", "service", CancellationToken.None);

        Assert.Equal(7, client.HostMaxParallelism);
        Assert.Equal(HttpMethod.Put, Assert.Single(handler.Requests).Method);
    }

    [Fact]
    public async Task Durable_empty_claim_refreshes_capacity_without_restarting_the_daemon()
    {
        var now = DateTime.UtcNow.ToString("O");
        var handler = new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent($$"""
                {
                  "status":"empty",
                  "message":"No task available.",
                  "runtimeCapacity":{
                    "hostId":"host-a",
                    "maxParallelism":5,
                    "targetLoadPercent":85,
                    "rampStrategy":"conservative",
                    "version":2,
                    "updatedAt":"{{now}}"
                  }
                }
                """),
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1") };
        var (options, _, _, _) = RunnerOptions.Parse(
            ["--poll", "--server", "http://127.0.0.1", "--runner-id", "runner-v1", "--max-parallelism", "2"]);
        using var client = new TaskServerClient(
            http,
            "runner-v1",
            usesDurableTaskServer: true,
            options: options);

        var claim = await client.ClaimAsync(
            new RunnerClaimRequest(
                "runner-v1",
                "Runner v1",
                "host-a",
                1,
                "remote"),
            CancellationToken.None);

        Assert.Equal(RunnerClaimStatus.Empty, claim.Status);
        Assert.Equal(5, client.HostMaxParallelism);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string PathAndQuery, string? ClientId)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((
                request.Method,
                request.RequestUri?.PathAndQuery ?? string.Empty,
                request.Headers.TryGetValues("X-Client-Id", out var values) ? values.SingleOrDefault() : null));
            return Task.FromResult(respond(request));
        }
    }
}
