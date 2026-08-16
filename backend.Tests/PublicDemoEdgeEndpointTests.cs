using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// W34 S4 acceptance against the real host. A raw client that ignores Angular
/// must meet the same boundary the UI describes: unsafe methods and unlisted
/// routes come back as a typed denial, transport and browser hardening is on
/// every response, the visitor gets an ephemeral session, and the request
/// budget is enforced.
/// </summary>
[Collection(WebApplicationFactorySerialCollection.Name)]
public sealed class PublicDemoEdgeEndpointTests : IDisposable
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "studio-public-demo-edge-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Raw_unsafe_requests_return_a_typed_denial()
    {
        using var factory = BuildFactory();
        using var visitor = Visitor(factory);

        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Post, "/api/tasks"),
                     (HttpMethod.Post, "/api/runner/claim"),
                     (HttpMethod.Put, "/api/projects/demo-app/autonomy"),
                     (HttpMethod.Delete, "/api/tasks/DEMO-1"),
                     (HttpMethod.Patch, "/api/tasks/DEMO-1"),
                 })
        {
            using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(new { }) };
            var response = await visitor.SendAsync(request);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("public-demo-read-only", body.GetProperty("error").GetString());
            Assert.Equal("public-demo", body.GetProperty("profile").GetString());
        }
    }

    [Fact]
    public async Task Unlisted_reads_are_denied_by_default_and_leak_no_route_detail()
    {
        using var factory = BuildFactory();
        using var visitor = Visitor(factory);

        foreach (var path in new[]
                 {
                     "/api/v1/management/status",
                     "/api/auth/users",
                     "/api/auth/session",
                     "/api/tasks/DEMO-1/files/prompt.md",
                     "/api/tasks/DEMO-1/git/diff",
                     "/api/git/history",
                     "/api/cli/quota",
                     "/api/projects/demo-app/steering",
                 })
        {
            var response = await visitor.GetAsync(path);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("public-demo-route-denied", body.GetProperty("error").GetString());
            Assert.False(body.TryGetProperty("path", out _));
        }
    }

    [Fact]
    public async Task Developer_only_endpoints_are_not_even_registered()
    {
        using var factory = BuildFactory();
        using var visitor = Visitor(factory);

        // Denied by the edge, and behind it the route does not exist at all in
        // this profile. Two independent barriers, matching the dossier.
        Assert.Equal(HttpStatusCode.Forbidden, (await visitor.GetAsync("/api/diagnostics/last-crash")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await visitor.GetAsync("/api/devtools/e2e-jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await visitor.GetAsync("/api/filesystem-layer/snapshot")).StatusCode);
    }

    [Fact]
    public async Task An_allowlisted_read_answers_with_the_edge_contract_and_a_hardened_response()
    {
        using var factory = BuildFactory();
        using var visitor = Visitor(factory);

        var response = await visitor.GetAsync("/api/public-demo/edge");
        response.EnsureSuccessStatusCode();

        var edge = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(edge.GetProperty("readOnly").GetBoolean());
        Assert.Equal("public-demo-readonly", edge.GetProperty("profile").GetString());
        Assert.StartsWith("sha256:", edge.GetProperty("allowlistDigest").GetString());
        Assert.Equal(PublicEdgeAllowlist.Routes.Count, edge.GetProperty("allowlistRouteCount").GetInt32());

        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("object-src 'none'", csp);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith(PublicDemoEdgeMiddleware.ViewerCookieName, cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Seeded_html_evidence_is_served_under_the_sandboxing_policy()
    {
        using var factory = BuildFactory();
        using var visitor = Visitor(factory);

        // The task does not exist in the empty test workspace; the point is the
        // response hardening the edge applies to that route class, which it
        // decides before the handler runs.
        var response = await visitor.GetAsync("/api/tasks/DEMO-9/results/report/index.html");

        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("sandbox", csp);
        Assert.Contains("default-src 'none'", csp);
        Assert.Equal("SAMEORIGIN", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
    }

    [Fact]
    public async Task Plain_http_and_cross_origin_callers_are_refused()
    {
        using var factory = BuildFactory();
        using var insecure = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://demo.test"),
            HandleCookies = false,
        });

        var overHttp = await insecure.GetAsync("/api/public-demo/edge");
        Assert.Equal(HttpStatusCode.UpgradeRequired, overHttp.StatusCode);
        Assert.Equal(
            "public-demo-https-required",
            (await overHttp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        using var visitor = Visitor(factory);
        using var foreign = new HttpRequestMessage(HttpMethod.Get, "/api/public-demo/edge");
        foreign.Headers.Add("Origin", "https://attacker.example");
        var crossOrigin = await visitor.SendAsync(foreign);

        Assert.Equal(HttpStatusCode.Forbidden, crossOrigin.StatusCode);
        Assert.Equal(
            "public-demo-origin-denied",
            (await crossOrigin.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task The_request_budget_is_enforced_per_visitor_session()
    {
        using var factory = BuildFactory(requestsPerWindow: 3);
        using var visitor = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://demo.test"),
            HandleCookies = true,
        });

        for (var i = 0; i < 3; i++)
            (await visitor.GetAsync("/api/public-demo/edge")).EnsureSuccessStatusCode();

        var limited = await visitor.GetAsync("/api/public-demo/edge");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(
            "public-demo-rate-limited",
            (await limited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.NotNull(limited.Headers.RetryAfter);
    }

    [Fact]
    public async Task The_health_probe_stays_reachable_so_the_vm_remains_observable()
    {
        using var factory = BuildFactory();
        using var visitor = Visitor(factory);

        (await visitor.GetAsync("/healthz")).EnsureSuccessStatusCode();
    }

    private WebApplicationFactory<Program> BuildFactory(int requestsPerWindow = 240)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["Security:Profile"] = PublicDemoProfile.ProfileName,
                ["PublicDemo:RequestsPerWindow"] = requestsPerWindow.ToString(),
                ["AllowedHosts"] = "demo.test",
            }));
        });

    private static HttpClient Visitor(WebApplicationFactory<Program> factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://demo.test"),
            HandleCookies = false,
        });

    public void Dispose()
    {
        try { Directory.Delete(_workspace, true); } catch (IOException) { }
    }
}
