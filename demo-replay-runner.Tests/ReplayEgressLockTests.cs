using System.Net;
using Xunit;

namespace AgentStudio.DemoReplayRunner.Tests;

/// <summary>
/// The replay image ships with default-deny egress. These tests pin the
/// in-process half of that control so a future change cannot quietly widen what
/// this service is able to reach.
/// </summary>
public sealed class ReplayEgressLockTests
{
    private static readonly Uri Origin = new("https://demo.agent-studio.test");

    [Theory]
    [InlineData("https://demo.agent-studio.test/api/runner/replay/events")]
    [InlineData("https://demo.agent-studio.test/healthz")]
    [InlineData("https://DEMO.agent-studio.test/healthz")]
    public void The_replay_ingest_and_the_health_probe_are_reachable(string uri)
    {
        using var lockHandler = new ReplayEgressLock(Origin, new RecordingHandler());

        Assert.True(lockHandler.Allows(new Uri(uri)));
    }

    [Theory]
    [InlineData("https://demo.agent-studio.test/api/runner/claim")]
    [InlineData("https://demo.agent-studio.test/api/runner/events")]
    [InlineData("https://demo.agent-studio.test/api/runner/lease/acquire")]
    [InlineData("https://demo.agent-studio.test/api/runner/completion")]
    [InlineData("https://demo.agent-studio.test/api/tasks/DEMO-1/start")]
    [InlineData("https://demo.agent-studio.test/api/runner/replay/events/extra")]
    [InlineData("https://demo.agent-studio.test/hubs/jobs")]
    public void No_execution_route_is_reachable_from_this_service(string uri)
    {
        using var lockHandler = new ReplayEgressLock(Origin, new RecordingHandler());

        Assert.False(lockHandler.Allows(new Uri(uri)));
    }

    [Theory]
    [InlineData("https://someone-else.test/api/runner/replay/events")]
    [InlineData("http://demo.agent-studio.test/api/runner/replay/events")]
    [InlineData("https://demo.agent-studio.test:8443/api/runner/replay/events")]
    public void Another_origin_is_never_reachable_even_on_an_allowed_path(string uri)
    {
        using var lockHandler = new ReplayEgressLock(Origin, new RecordingHandler());

        Assert.False(lockHandler.Allows(new Uri(uri)));
    }

    [Fact]
    public async Task A_refused_request_never_reaches_the_transport()
    {
        var inner = new RecordingHandler();
        using var client = new HttpClient(new ReplayEgressLock(Origin, inner)) { BaseAddress = Origin };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.PostAsync("/api/runner/claim", content: null));

        Assert.Empty(inner.Requests);
    }

    [Fact]
    public async Task An_allowed_request_passes_through_untouched()
    {
        var inner = new RecordingHandler();
        using var client = new HttpClient(new ReplayEgressLock(Origin, inner)) { BaseAddress = Origin };

        using var response = await client.GetAsync(ReplayEgressLock.HealthPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([ReplayEgressLock.HealthPath], inner.Requests);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
