using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// W34 §8 S4 "Public read-only edge" (AGT-2669), depending on the S2 route
/// inventory (AGT-2667, <see cref="PublicDemoExecutionProfileTests"/>). S2
/// denies a catalogued set of execution routes by identity; this edge is the
/// broader net behind it: every unsafe method is denied outright and every
/// safe method must match an explicit read allowlist.
/// </summary>
[Collection(WebApplicationFactorySerialCollection.Name)]
public sealed class PublicDemoReadOnlyEdgeTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(),
        "studio-public-demo-edge-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("local", "GET", true, true)]
    [InlineData("networked", "POST", true, true)]
    [InlineData(ExecutionAdmissionPolicy.PublicDemoProfile, "GET", true, true)]
    [InlineData(ExecutionAdmissionPolicy.PublicDemoProfile, "HEAD", true, true)]
    [InlineData(ExecutionAdmissionPolicy.PublicDemoProfile, "OPTIONS", true, true)]
    [InlineData(ExecutionAdmissionPolicy.PublicDemoProfile, "GET", false, false)]
    [InlineData(ExecutionAdmissionPolicy.PublicDemoProfile, "POST", true, false)]
    [InlineData(ExecutionAdmissionPolicy.PublicDemoProfile, "PUT", true, false)]
    [InlineData(ExecutionAdmissionPolicy.PublicDemoProfile, "PATCH", true, false)]
    [InlineData(ExecutionAdmissionPolicy.PublicDemoProfile, "DELETE", true, false)]
    [InlineData(ExecutionAdmissionPolicy.PublicDemoProfile, "DELETE", false, false)]
    public void Edge_policy_matrix(string profile, string method, bool pathAllowlisted, bool expectedAllowed)
    {
        var decision = PublicDemoEdgePolicy.Decide(profile, method, pathAllowlisted);

        Assert.Equal(expectedAllowed, decision.Allowed);
        if (!expectedAllowed)
            Assert.Equal(PublicDemoEdgePolicy.ReadOnlyDeniedCode, decision.Code);
    }

    [Theory]
    [InlineData("/api/environment", true)]
    [InlineData("/api/tasks", true)]
    [InlineData("/api/tasks/DEMO-1", true)]
    [InlineData("/api/projects", true)]
    [InlineData("/api/projects/demo-app/wiki/home", true)]
    [InlineData("/api/projects/demo-app/workbenches", true)]
    [InlineData("/api/projects/demo-app/workbenches/DEMO-W1/references", true)]
    [InlineData("/api/workbenches/DEMO-W1", true)]
    [InlineData("/hubs/jobs", true)]
    [InlineData("/hubs/jobs/negotiate", true)]
    [InlineData("/api/projects/demo-app/security", false)]
    [InlineData("/api/projects/demo-app/security/reviews", false)]
    [InlineData("/api/projects/demo-app/security/files/report.json", false)]
    [InlineData("/api/projects/demo-app/token-usage", false)]
    [InlineData("/api/projects/demo-app/proposals", false)]
    [InlineData("/api/projects/demo-app/settings", false)]
    [InlineData("/api/projects/demo-app", false)]
    [InlineData("/api/audits/run-1", false)]
    [InlineData("/api/pipeline/health", false)]
    [InlineData("/api/workspaces", false)]
    [InlineData("/api/git/status", false)]
    [InlineData("/api/definitely-not-a-real-endpoint", false)]
    public void Read_allowlist_matches_only_the_declared_browse_surface(string path, bool expectedAllowed)
    {
        Assert.Equal(expectedAllowed, PublicDemoReadAllowlist.Allows(path));
    }

    [Fact]
    public void Viewer_session_store_issues_touches_and_expires_sliding_sessions()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-20T09:00:00Z"));
        var store = new PublicDemoViewerSessionStore(clock) { SessionLifetime = TimeSpan.FromMinutes(30) };

        var id = store.Issue();
        Assert.False(string.IsNullOrWhiteSpace(id));
        Assert.True(store.Touch(id));

        clock.Advance(TimeSpan.FromMinutes(29));
        Assert.True(store.Touch(id), "still inside the sliding window after the refresh above");

        clock.Advance(TimeSpan.FromMinutes(31));
        Assert.False(store.Touch(id), "31 minutes without activity exceeds the 30-minute sliding window");

        Assert.False(store.Touch(null));
        Assert.False(store.Touch("unknown-session-id"));
    }

    [Fact]
    public async Task Allowlisted_read_succeeds_and_issues_a_hardened_ephemeral_viewer_cookie()
    {
        await WithPublicDemoProfile(async () =>
        {
            using var factory = BuildFactory();
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://demo.test"),
                HandleCookies = false,
            });

            var response = await client.GetAsync("/api/environment");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(body.GetProperty("publicDemo").GetProperty("active").GetBoolean());
            Assert.Equal(
                ExecutionAdmissionPolicy.PublicDemoProfile,
                body.GetProperty("publicDemo").GetProperty("profile").GetString());

            Assert.Equal(
                "default-src 'self'",
                response.Headers.GetValues("Content-Security-Policy").Single().Split(';')[0].Trim());
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());

            var setCookie = response.Headers.GetValues("Set-Cookie").Single(v =>
                v.StartsWith(PublicDemoViewerSessionStore.CookieName + "=", StringComparison.Ordinal));
            Assert.Contains("HttpOnly", setCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Secure", setCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("SameSite=Strict", setCookie, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Non_allowlisted_read_is_denied_even_though_the_route_exists()
    {
        await WithPublicDemoProfile(async () =>
        {
            using var factory = BuildFactory();
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://demo.test"),
            });

            var response = await client.GetAsync("/api/audits/run-1");

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(PublicDemoEdgePolicy.ReadOnlyDeniedCode, body.GetProperty("error").GetString());
            Assert.Equal(ExecutionAdmissionPolicy.PublicDemoProfile, body.GetProperty("profile").GetString());
        });
    }

    [Fact]
    public async Task Unsafe_method_on_a_non_execution_tagged_mutation_still_gets_the_typed_edge_denial()
    {
        await WithPublicDemoProfile(async () =>
        {
            using var factory = BuildFactory();
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://demo.test"),
            });

            // /api/tags carries no ExecutionRouteMetadata (it is not part of
            // the S2 catalogue), so this proves the edge's blanket net
            // catches mutations S2 was never told about, rather than S2
            // masking the gap.
            var response = await client.PostAsJsonAsync("/api/tags", new { id = "x", label = "X", color = "#fff" });

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(PublicDemoEdgePolicy.ReadOnlyDeniedCode, body.GetProperty("error").GetString());
        });
    }

    [Fact]
    public async Task Plain_http_is_rejected_before_any_allowlist_check()
    {
        await WithPublicDemoProfile(async () =>
        {
            using var factory = BuildFactory();
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("http://demo.test"),
            });

            var response = await client.GetAsync("/api/environment");

            Assert.Equal((HttpStatusCode)426, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("https-required", body.GetProperty("error").GetString());
        });
    }

    [Fact]
    public async Task Cross_origin_request_is_denied()
    {
        await WithPublicDemoProfile(async () =>
        {
            using var factory = BuildFactory();
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://demo.test"),
            });
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/environment");
            request.Headers.Add("Origin", "https://not-the-demo.example");

            var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        });
    }

    [Fact]
    public async Task Hub_negotiate_post_is_not_caught_by_the_blanket_unsafe_method_denial()
    {
        await WithPublicDemoProfile(async () =>
        {
            using var factory = BuildFactory();
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://demo.test"),
            });

            var negotiate = await client.PostAsync("/hubs/jobs/negotiate?negotiateVersion=1", content: null);

            Assert.NotEqual(HttpStatusCode.Forbidden, negotiate.StatusCode);
        });
    }

    private WebApplicationFactory<Program> BuildFactory(
        IReadOnlyDictionary<string, string?>? overrides = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _workspace,
                    ["Security:Profile"] = ExecutionAdmissionPolicy.PublicDemoProfile,
                };
                if (overrides is not null)
                    foreach (var (key, value) in overrides)
                        values[key] = value;
                configuration.AddInMemoryCollection(values);
            });
        });

    // WebApplicationFactory layers its ConfigureAppConfiguration override on
    // top of Program.cs's own configuration sources, but a handful of values
    // (StartupExecutionAdmission, the Kestrel body cap, the rate-limiter
    // partition function) are read and captured once, early, before that
    // override is visible - the same reason PublicDemoExecutionProfileTests
    // sets this environment variable. AddEnvironmentVariables() is one of the
    // very first sources WebApplication.CreateBuilder wires up, so it is
    // visible to those early reads too. The serial collection this class
    // runs under makes the process-wide env var safe between test methods.
    private static async Task WithPublicDemoProfile(Func<Task> body)
    {
        var previous = Environment.GetEnvironmentVariable("Security__Profile");
        Environment.SetEnvironmentVariable("Security__Profile", ExecutionAdmissionPolicy.PublicDemoProfile);
        try { await body(); }
        finally { Environment.SetEnvironmentVariable("Security__Profile", previous); }
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, true); } catch { }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
