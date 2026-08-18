using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-W34 slice S4 end to end: the real host booted in the public-demo profile.
/// These are the external probes the dossier asks for - a raw unsafe request, a
/// denied endpoint, a plain-HTTP request, and the browser boundary headers - run
/// against the actual middleware order rather than the policy in isolation.
/// </summary>
[Collection(WebApplicationFactorySerialCollection.Name)]
public sealed partial class PublicDemoEdgeEndpointTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "studio-public-demo-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RawUnsafeRequests_ReturnTheTypedReadOnlyDenial()
    {
        using var factory = BuildFactory();
        using var client = CreateClient(factory);

        foreach (var request in new HttpRequestMessage[]
                 {
                     new(HttpMethod.Post, "/api/tasks") { Content = JsonContent.Create(new { title = "probe" }) },
                     new(HttpMethod.Delete, "/api/tasks/DEMO-14"),
                     new(HttpMethod.Post, "/api/auth/login") { Content = JsonContent.Create(new { username = "a", password = "b" }) },
                     new(HttpMethod.Post, "/api/runner/claim"),
                 })
        {
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            var denial = await response.Content.ReadFromJsonAsync<PublicDemoDenial>();
            Assert.NotNull(denial);
            Assert.Equal("public-demo-read-only", denial!.Error);
            Assert.Equal("public-demo", denial.Profile);
            Assert.True(denial.ReadOnly);
        }
    }

    [Theory]
    [InlineData("/api/diagnostics")]
    [InlineData("/api/devtools/e2e-jobs")]
    [InlineData("/api/filesystem-layer/snapshot")]
    [InlineData("/api/auth/users")]
    [InlineData("/api/v1/management/status")]
    [InlineData("/api/watch-paths")]
    [InlineData("/api/tasks/DEMO-14/files/prompt.md")]
    public async Task NonAllowlistedReads_AreDeniedWithoutReachingAHandler(string path)
    {
        using var factory = BuildFactory();
        using var client = CreateClient(factory);

        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var denial = await response.Content.ReadFromJsonAsync<PublicDemoDenial>();
        Assert.Equal("public-demo-endpoint-denied", denial?.Error);
    }

    [Fact]
    public async Task AnAllowlistedRead_IsServedAndCarriesTheBrowserBoundary()
    {
        using var factory = BuildFactory();
        using var client = CreateClient(factory);

        using var response = await client.GetAsync("/api/auth/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(
            PublicDemoBrowserBoundary.ApplicationCsp,
            response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("same-origin", response.Headers.GetValues("Cross-Origin-Resource-Policy").Single());

        var status = await response.Content.ReadFromJsonAsync<AuthStatusResponse>();
        Assert.Equal(SecurityProfiles.PublicDemo, status?.Profile);
    }

    [Fact]
    public async Task TheViewerCookie_IsEphemeralHttpOnlySecureAndSameSite()
    {
        using var factory = BuildFactory();
        using var client = CreateClient(factory);

        using var response = await client.GetAsync("/api/auth/status");
        var cookie = Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(PublicDemoViewerSessions.CookieName + "=", StringComparison.Ordinal));

        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        // No Expires / Max-Age: the boundary dies with the browser session.
        Assert.DoesNotContain("expires=", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("max-age=", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlainHttpAndCrossOriginRequests_AreRefused()
    {
        using var factory = BuildFactory();
        using var insecure = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://demo.test"),
            HandleCookies = false,
        });
        using var plain = await insecure.GetAsync("/api/auth/status");
        Assert.Equal(HttpStatusCode.UpgradeRequired, plain.StatusCode);

        using var client = CreateClient(factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/status");
        request.Headers.Add("Origin", "https://attacker.test");
        using var foreignOrigin = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, foreignOrigin.StatusCode);
        Assert.Equal(
            "public-demo-cross-origin-denied",
            (await foreignOrigin.Content.ReadFromJsonAsync<PublicDemoDenial>())?.Error);
    }

    [Fact]
    public async Task TheRequestBudget_ShedsAFloodWithATypedDenial()
    {
        using var factory = BuildFactory(requestsPerMinute: 5);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://demo.test"),
            HandleCookies = true,
        });

        HttpStatusCode last = HttpStatusCode.OK;
        for (var i = 0; i < 12 && last != HttpStatusCode.TooManyRequests; i++)
        {
            using var response = await client.GetAsync("/api/auth/status");
            last = response.StatusCode;
            if (last == HttpStatusCode.TooManyRequests)
                Assert.Equal(
                    "public-demo-rate-limited",
                    (await response.Content.ReadFromJsonAsync<PublicDemoDenial>())?.Error);
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }

    /// <summary>
    /// The S4 counterpart to the S2 route inventory. Every route the host maps is
    /// checked against the edge: an unsafe verb must land on the read-only denial,
    /// and a read that is not on the allowlist must be unreachable. A newly mapped
    /// route therefore arrives denied by default; nobody has to remember to add it
    /// to a denylist.
    /// </summary>
    [Fact]
    public void EveryMappedRoute_IsEitherAllowlistedOrDeniedByDefault()
    {
        using var factory = BuildFactory();
        using var scope = factory.Services.CreateScope();
        var endpoints = scope.ServiceProvider.GetRequiredService<EndpointDataSource>().Endpoints;

        var limits = new PublicDemoOptions().ToLimits();
        var reachableUnsafe = new List<string>();

        foreach (var endpoint in endpoints.OfType<RouteEndpoint>())
        {
            var path = "/" + endpoint.RoutePattern.RawText?.TrimStart('/');
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];
            foreach (var method in methods)
            {
                if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)) continue;
                var verdict = PublicDemoEdgePolicy.Evaluate(
                    new PublicDemoRequest(method, path, true, true, null), limits);
                if (!verdict.Denied || verdict.Error != "public-demo-read-only")
                    reachableUnsafe.Add($"{method} {path} -> {verdict.Error}");
            }
        }

        // The SignalR negotiate POST is the one deliberate exception.
        reachableUnsafe.RemoveAll(entry =>
            entry.StartsWith("POST /hubs/jobs/negotiate", StringComparison.OrdinalIgnoreCase));

        Assert.True(reachableUnsafe.Count == 0,
            "Mutating routes must be unreachable from the public demo edge:\n  "
            + string.Join("\n  ", reachableUnsafe));
    }

    /// <summary>
    /// The allowlist must not rot. Every entry has to correspond to a route the
    /// host actually maps, so a deleted or renamed endpoint cannot leave a stale
    /// hole behind that a future route silently walks into.
    /// </summary>
    [Fact]
    public void EveryAllowlistEntry_MatchesARouteTheHostMaps()
    {
        using var factory = BuildFactory();
        using var scope = factory.Services.CreateScope();
        var samples = scope.ServiceProvider.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => SamplePath(endpoint.RoutePattern.RawText))
            .ToList();

        var stale = PublicDemoRoutes.Allowed
            .Where(entry => !samples.Any(sample => PublicDemoRoutes.Matches(entry, sample)))
            .ToList();

        Assert.True(stale.Count == 0,
            "Allowlist entries without a matching mapped route:\n  " + string.Join("\n  ", stale));
    }

    /// <summary>
    /// Turn a route template into one concrete path the host would serve, so the
    /// allowlist is checked with the same matcher production uses rather than by
    /// comparing two spellings of a wildcard.
    /// </summary>
    private static string SamplePath(string? routePattern)
    {
        var segments = ("/" + (routePattern ?? string.Empty).TrimStart('/'))
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.Contains("{**", StringComparison.Ordinal)
                ? "sample/sample"
                : Parameter().Replace(segment, "sample"));
        return "/" + string.Join('/', segments);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\{[^}]*\}")]
    private static partial System.Text.RegularExpressions.Regex Parameter();

    private WebApplicationFactory<Program> BuildFactory(int requestsPerMinute = 5000)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _workspace,
                    ["Security:Profile"] = SecurityProfiles.PublicDemo,
                    ["PublicDemo:RequestsPerMinute"] = requestsPerMinute.ToString(),
                    ["AllowedHosts"] = "demo.test",
                }));
        });

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://demo.test"),
            HandleCookies = false,
        });

    public void Dispose()
    {
        try { Directory.Delete(_workspace, true); }
        catch (Exception ex) { Console.Error.WriteLine($"[PublicDemoEdgeEndpointTests] workspace cleanup failed: {ex.Message}"); }
    }
}
