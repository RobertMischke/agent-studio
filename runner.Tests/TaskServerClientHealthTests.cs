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
