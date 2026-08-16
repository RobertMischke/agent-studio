using System.Net;
using System.Net.Http.Json;
using AgentStudio.DemoReplay;
using AgentStudio.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The S3 compromise proof: assume the replay process is fully owned and its
/// credential stolen, then show the Task Server still refuses every path that
/// could claim or mutate a task. These run against the real networked pipeline,
/// so they exercise the shipped authorization, not a stand-in.
/// </summary>
[Collection(WebApplicationFactorySerialCollection.Name)]
public sealed class DemoReplayCompromiseTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "studio-demo-replay-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Every execution admission path a stolen replay credential could try. The
    /// list is the legacy runner plane plus the task-mutation routes a caller
    /// would reach for after bypassing Angular.
    /// </summary>
    public static TheoryData<string, string> ExecutionRoutes() => new()
    {
        { "POST", "/api/runner/claim" },
        { "POST", "/api/runner/lease/acquire" },
        { "POST", "/api/runner/lease/renew" },
        { "POST", "/api/runner/lease/release" },
        { "POST", "/api/runner/logs" },
        { "POST", "/api/runner/events" },
        { "POST", "/api/runner/artifacts" },
        { "POST", "/api/runner/completion" },
        { "POST", "/api/runner/project-chat/claim" },
        { "POST", "/api/runner/epic-planning-prompt" },
        { "POST", "/api/runner/integration-lease/acquire" },
        { "POST", "/api/tasks/DEMO-4/start" },
        { "POST", "/api/tasks/DEMO-4/continue" },
        { "POST", "/api/tasks/DEMO-4/stop" },
        { "POST", "/api/tasks/DEMO-4/external-completion" },
        { "POST", "/api/tasks" },
        { "PUT", "/api/tasks/DEMO-4" },
        { "DELETE", "/api/tasks/DEMO-4" },
        { "POST", "/api/tasks/batch-move" },
        { "PUT", "/api/runner/Demo App/mode" },
        { "POST", "/api/runner/Demo App/start" },
        { "POST", "/api/attempts/reviews" },
        { "POST", "/api/v1/management/commands" },
    };

    /// <summary>
    /// A replay credential must not be able to read the workspace either. Read
    /// access belongs to the visitor surface, which S4 owns.
    /// </summary>
    public static TheoryData<string> ReadRoutes() =>
    [
        "/api/tasks",
        "/api/runner/status",
        "/api/workspace/summary",
        "/api/tasks/DEMO-4/files/prompt.md",
    ];

    [Theory]
    [MemberData(nameof(ExecutionRoutes))]
    public async Task A_stolen_replay_credential_cannot_reach_any_execution_route(string method, string route)
    {
        using var factory = BuildFactory();
        using var client = AnonymousClient(factory);
        var credential = await EnrollReplayCredentialAsync(factory, client);

        using var request = new HttpRequestMessage(new HttpMethod(method), route)
        {
            Content = JsonContent.Create(new { runnerId = credential.RunnerId, taskKey = "DEMO-4" }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential.Secret);

        using var response = await client.SendAsync(request);

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"{method} {route} answered {(int)response.StatusCode}; a replay credential must be refused.");
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Contains(
            body.GetProperty("error").GetString(),
            new[] { "authentication-required", "runner-scope-denied", "runner-authentication-required" });
    }

    [Theory]
    [MemberData(nameof(ReadRoutes))]
    public async Task A_stolen_replay_credential_cannot_read_the_workspace(string route)
    {
        using var factory = BuildFactory();
        using var client = AnonymousClient(factory);
        var credential = await EnrollReplayCredentialAsync(factory, client);

        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential.Secret);

        using var response = await client.SendAsync(request);

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"GET {route} answered {(int)response.StatusCode}; a replay credential must be refused.");
    }

    /// <summary>
    /// The exclusivity rule is what makes the route matrix above hold for every
    /// future credential: no owner action can mint one that both replays and
    /// claims, and no rotation can widen an existing one.
    /// </summary>
    [Fact]
    public async Task A_credential_cannot_hold_replay_and_execution_scopes_together()
    {
        using var factory = BuildFactory();
        using var client = AnonymousClient(factory);
        var session = await BootstrapOwnerAsync(factory);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/runner-enrollments")
        {
            Content = JsonContent.Create(new
            {
                name = "greedy-replay",
                scopes = new[] { DemoReplayScopes.Replay, RunnerScopes.Claim },
            }),
        };
        request.Headers.Add("X-CSRF-Token", session.Csrf);
        using var response = await session.Browser.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(RunnerScopeCompositionPolicy.ReplayIsExclusive, body.GetProperty("error").GetString());
    }

    /// <summary>The default enrollment must never hand out replay authority by accident.</summary>
    [Fact]
    public async Task The_default_runner_scope_set_excludes_replay()
    {
        using var factory = BuildFactory();
        using var client = AnonymousClient(factory);
        var session = await BootstrapOwnerAsync(factory);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/runner-enrollments")
        {
            Content = JsonContent.Create(new { name = "ordinary-runner" }),
        };
        request.Headers.Add("X-CSRF-Token", session.Csrf);
        using var response = await session.Browser.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var enrollment = await response.Content.ReadFromJsonAsync<OneTimeEnrollmentResponse>();
        Assert.DoesNotContain(DemoReplayScopes.Replay, enrollment!.Scopes);
        Assert.Equal(RunnerScopes.Minimum.Length, enrollment.Scopes.Count);
    }

    /// <summary>
    /// The replay route is mapped only when a verified trace is configured, so a
    /// deployment without one has no replay surface at all.
    /// </summary>
    [Fact]
    public async Task Without_a_configured_trace_the_replay_route_does_not_exist()
    {
        using var factory = BuildFactory();
        using var client = AnonymousClient(factory);
        var credential = await EnrollReplayCredentialAsync(factory, client);

        using var request = new HttpRequestMessage(HttpMethod.Post, DemoReplayEndpoints.EventsRoute)
        {
            Content = JsonContent.Create(new { traceId = "demo-instanz-cycle-1", traceDigest = new string('a', 64), epoch = 1, sequence = 1 }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential.Secret);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Anonymous callers get nothing, with or without a trace configured.</summary>
    [Fact]
    public async Task An_anonymous_caller_cannot_post_replay_events()
    {
        using var factory = BuildFactory(withTrace: true);
        using var client = AnonymousClient(factory);

        using var response = await client.PostAsJsonAsync(
            DemoReplayEndpoints.EventsRoute,
            new { traceId = "demo-instanz-cycle-1", traceDigest = new string('a', 64), epoch = 1, sequence = 1 });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("runner-authentication-required", body.GetProperty("error").GetString());
    }

    /// <summary>
    /// An ordinary coding credential holds the full execution scope set and
    /// still cannot reach the replay plane. The two identities are disjoint in
    /// both directions.
    /// </summary>
    [Fact]
    public async Task An_execution_credential_cannot_reach_the_replay_plane()
    {
        using var factory = BuildFactory(withTrace: true);
        using var client = AnonymousClient(factory);
        var credential = await EnrollAsync(factory, client, "coding-runner", scopes: null);

        using var request = new HttpRequestMessage(HttpMethod.Post, DemoReplayEndpoints.EventsRoute)
        {
            Content = JsonContent.Create(new { traceId = "demo-instanz-cycle-1", traceDigest = new string('a', 64), epoch = 1, sequence = 1 }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential.Secret);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("runner-scope-denied", body.GetProperty("error").GetString());
    }

    /// <summary>
    /// With a real credential and a real trace the plane still refuses a forged
    /// cursor, so possession of the credential is not possession of the scene.
    /// </summary>
    [Fact]
    public async Task A_valid_replay_credential_cannot_forge_a_cursor()
    {
        using var factory = BuildFactory(withTrace: true);
        using var client = AnonymousClient(factory);
        var credential = await EnrollReplayCredentialAsync(factory, client);

        using var request = new HttpRequestMessage(HttpMethod.Post, DemoReplayEndpoints.EventsRoute)
        {
            Content = JsonContent.Create(new { traceId = "demo-instanz-cycle-1", traceDigest = new string('a', 64), epoch = 1, sequence = 1 }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential.Secret);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(DemoReplayDenials.DigestMismatch, body.GetProperty("error").GetString());
    }

    private sealed record OwnerSession(HttpClient Browser, string Csrf);

    private static HttpClient AnonymousClient(WebApplicationFactory<Program> factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://studio.test"),
            HandleCookies = false,
        });

    private static async Task<OwnerSession> BootstrapOwnerAsync(WebApplicationFactory<Program> factory)
    {
        var browser = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://studio.test"),
            HandleCookies = true,
        });
        var bootstrap = await browser.PostAsJsonAsync("/api/auth/bootstrap", new
        {
            username = "demo.owner",
            password = "correct horse battery staple!",
            displayName = "Demo Owner",
        });
        bootstrap.EnsureSuccessStatusCode();
        var auth = await bootstrap.Content.ReadFromJsonAsync<AuthStatusResponse>();
        return new OwnerSession(browser, auth!.CsrfToken!);
    }

    private static Task<OneTimeSecretResponse> EnrollReplayCredentialAsync(
        WebApplicationFactory<Program> factory, HttpClient anonymous)
        => EnrollAsync(factory, anonymous, "demo-runner-replay", [DemoReplayScopes.Replay]);

    private static async Task<OneTimeSecretResponse> EnrollAsync(
        WebApplicationFactory<Program> factory, HttpClient anonymous, string name, string[]? scopes)
    {
        var session = await BootstrapOwnerAsync(factory);
        using var enrollmentRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/runner-enrollments")
        {
            Content = JsonContent.Create(scopes is null ? new { name } : (object)new { name, scopes }),
        };
        enrollmentRequest.Headers.Add("X-CSRF-Token", session.Csrf);
        using var enrollmentResponse = await session.Browser.SendAsync(enrollmentRequest);
        enrollmentResponse.EnsureSuccessStatusCode();
        var enrollment = await enrollmentResponse.Content.ReadFromJsonAsync<OneTimeEnrollmentResponse>();

        var enrolled = await anonymous.PostAsJsonAsync("/api/auth/runner-enroll", new { code = enrollment!.EnrollmentCode });
        enrolled.EnsureSuccessStatusCode();
        session.Browser.Dispose();
        return (await enrolled.Content.ReadFromJsonAsync<OneTimeSecretResponse>())!;
    }

    private WebApplicationFactory<Program> BuildFactory(bool withTrace = false)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _workspace,
                    ["Security:Profile"] = "networked",
                    ["AllowedHosts"] = "studio.test",
                };
                if (withTrace)
                {
                    settings["DemoReplay:Enabled"] = "true";
                    settings["DemoReplay:TracePath"] = DemoReplayIngestionTests.CommittedTracePath();
                }
                config.AddInMemoryCollection(settings);
            });
        });

    public void Dispose()
    {
        try { Directory.Delete(_workspace, true); } catch (IOException) { }
    }
}
