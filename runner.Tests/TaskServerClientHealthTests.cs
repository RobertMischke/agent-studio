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

    [Fact]
    public async Task Review_plane_only_compatibility_enables_review_but_keeps_coding_on_legacy_routes()
    {
        const string response = """
            {
              "supported": true,
              "server": {
                "current": 2,
                "minimumSupported": 1,
                "maximumSupported": 2,
                "serverVersion": "1.0.0",
                "serverId": "orchestrator-monolith",
                "clientKinds": ["runner", "review-runner"],
                "capabilities": ["review-plane"]
              }
            }
            """;
        var codingHandler = new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(response)
        });
        using var codingHttp = new HttpClient(codingHandler) { BaseAddress = new Uri("http://task-server") };
        using var coding = new TaskServerClient(codingHttp, "coding-runner");

        await coding.EnsureCompatibleAsync(CancellationToken.None);

        Assert.False(coding.UsesDurableTaskServer);
        Assert.Contains("\"clientKind\":\"runner\"", codingHandler.Requests.Single().Body);

        var reviewHandler = new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(response)
        });
        using var reviewHttp = new HttpClient(reviewHandler) { BaseAddress = new Uri("http://task-server") };
        using var review = new TaskServerClient(reviewHttp, "review-runner", role: "review");

        await review.EnsureCompatibleAsync(CancellationToken.None);

        Assert.True(review.UsesDurableTaskServer);
        Assert.Contains("\"clientKind\":\"review-runner\"", reviewHandler.Requests.Single().Body);
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

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string PathAndQuery, string? ClientId, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((
                request.Method,
                request.RequestUri?.PathAndQuery ?? string.Empty,
                request.Headers.TryGetValues("X-Client-Id", out var values) ? values.SingleOrDefault() : null,
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond(request);
        }
    }
}
