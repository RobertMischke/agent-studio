using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Direct matrix over the W34 S4 public read-only edge decision. The visitor
/// boundary is a pure function, so every refusal the dossier requires is pinned
/// here without an HTTP host: unsafe methods, unlisted routes, foreign projects,
/// oversized bodies, cross-origin calls, and plain HTTP.
/// </summary>
public sealed class PublicEdgePolicyTests
{
    private static PublicEdgeContract Contract(
        IReadOnlyList<PublicEdgeRoute>? routes = null,
        long maxBody = 1024,
        int perWindow = 5) => new()
        {
            Routes = routes ?? PublicEdgeAllowlist.Routes,
            Projects = ["demo-app", "demo-platform"],
            MaxRequestBodyBytes = maxBody,
            RequestsPerWindow = perWindow,
            Window = TimeSpan.FromMinutes(1),
            ViewerSessionLifetime = TimeSpan.FromHours(2),
            AllowlistDigest = "sha256:test",
        };

    private static PublicEdgeRequest Request(
        string method = "GET",
        string path = "/api/tasks",
        bool https = true,
        string? origin = null,
        string? host = "demo.example",
        long? contentLength = null,
        bool? projectAllowed = null)
        => new(method, path, https, origin, host, contentLength, projectAllowed);

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public void Unsafe_methods_are_denied_even_on_an_allowlisted_path(string method)
    {
        var decision = PublicEdgePolicy.Decide(Request(method: method), Contract());

        Assert.Equal(PublicEdgeDenial.MethodDenied, decision.Denial);
    }

    [Theory]
    [InlineData("/api/tasks/DEMO-1/start")]
    [InlineData("/api/runner/claim")]
    [InlineData("/api/auth/users")]
    [InlineData("/api/auth/session")]
    [InlineData("/api/v1/management/status")]
    [InlineData("/api/git/history")]
    [InlineData("/api/tasks/DEMO-1/files/prompt.md")]
    [InlineData("/api/diagnostics/last-crash")]
    public void Routes_outside_the_allowlist_are_denied_by_default(string path)
    {
        var decision = PublicEdgePolicy.Decide(Request(path: path), Contract());

        Assert.Equal(PublicEdgeDenial.RouteDenied, decision.Denial);
    }

    [Theory]
    [InlineData("/api/tasks")]
    [InlineData("/api/tasks/grouped")]
    [InlineData("/api/tasks/DEMO-9/timeline")]
    [InlineData("/api/tasks/DEMO-9/results/report/index.html")]
    [InlineData("/api/projects/demo-app/wiki/tree")]
    [InlineData("/api/projects/demo-app/wiki/files/product/overview.md")]
    [InlineData("/api/projects/demo-app/workbenches/DEMO-W4")]
    [InlineData("/api/workbenches")]
    [InlineData("/api/search")]
    [InlineData("/api/public-demo/edge")]
    [InlineData("/api/auth/status")]
    public void The_seeded_browse_surface_stays_readable(string path)
    {
        var decision = PublicEdgePolicy.Decide(Request(path: path), Contract());

        Assert.True(decision.IsAllowed, $"expected {path} to be readable, got {decision.Denial?.Code}");
    }

    [Fact]
    public void Head_and_options_resolve_against_the_get_inventory()
    {
        Assert.True(PublicEdgePolicy.Decide(Request(method: "HEAD"), Contract()).IsAllowed);
        Assert.True(PublicEdgePolicy.Decide(Request(method: "OPTIONS"), Contract()).IsAllowed);
    }

    [Fact]
    public void Plain_http_is_refused_before_anything_else_is_evaluated()
    {
        var decision = PublicEdgePolicy.Decide(Request(https: false, method: "DELETE", path: "/nope"), Contract());

        Assert.Equal(PublicEdgeDenial.HttpsRequired, decision.Denial);
    }

    [Theory]
    [InlineData("https://evil.example", false)]
    [InlineData("null", false)]
    [InlineData("https://demo.example", true)]
    [InlineData(null, true)]
    public void Only_same_origin_callers_are_admitted(string? origin, bool expected)
    {
        var decision = PublicEdgePolicy.Decide(Request(origin: origin), Contract());

        Assert.Equal(expected, decision.IsAllowed);
        if (!expected) Assert.Equal(PublicEdgeDenial.OriginDenied, decision.Denial);
    }

    [Fact]
    public void A_body_over_the_ceiling_is_refused()
    {
        var decision = PublicEdgePolicy.Decide(Request(contentLength: 4096), Contract(maxBody: 1024));

        Assert.Equal(PublicEdgeDenial.BodyTooLarge, decision.Denial);
    }

    [Fact]
    public void A_project_outside_the_seeded_scene_is_refused()
    {
        var decision = PublicEdgePolicy.Decide(Request(projectAllowed: false), Contract());

        Assert.Equal(PublicEdgeDenial.ProjectDenied, decision.Denial);
    }

    [Fact]
    public void An_unresolvable_task_fails_closed_rather_than_counting_as_unscoped()
    {
        var project = PublicDemoProjectScope.AddressedProject(
            "/api/tasks/UNKNOWN-1/timeline",
            queryProject: null,
            resolveTaskProject: _ => null);

        // An unresolvable task yields a sentinel handle, never null, so the
        // caller has to ask the scope and cannot treat it as unscoped.
        Assert.Equal(PublicDemoProjectScope.Unresolved, project);
        Assert.Equal(
            PublicEdgeDenial.ProjectDenied,
            PublicEdgePolicy.Decide(
                Request(path: "/api/tasks/UNKNOWN-1/timeline", projectAllowed: false), Contract()).Denial);
    }

    [Fact]
    public void Health_probes_bypass_the_edge_so_the_vm_stays_observable()
    {
        Assert.True(PublicEdgePolicy.Decide(Request(path: "/healthz", https: false), Contract()).IsAllowed);
    }

    [Fact]
    public void The_hub_admits_its_transport_methods_and_nothing_else()
    {
        // SignalR negotiates with POST and ends a long-poll connection with
        // DELETE. Both are handshake traffic for a hub whose only callable
        // methods join and leave a project-filtered read group.
        Assert.True(PublicEdgePolicy.Decide(Request(path: "/hubs/jobs"), Contract()).IsAllowed);
        Assert.True(PublicEdgePolicy.Decide(Request(path: "/hubs/jobs/negotiate", method: "POST"), Contract()).IsAllowed);
        Assert.True(PublicEdgePolicy.Decide(Request(path: "/hubs/jobs", method: "DELETE"), Contract()).IsAllowed);
        Assert.Equal(
            PublicEdgeDenial.MethodDenied,
            PublicEdgePolicy.Decide(Request(path: "/hubs/jobs", method: "PUT"), Contract()).Denial);
        Assert.Equal(
            PublicEdgeDenial.OriginDenied,
            PublicEdgePolicy.Decide(
                Request(path: "/hubs/jobs", origin: "https://attacker.example"), Contract()).Denial);
    }

    [Fact]
    public void A_catch_all_segment_requires_at_least_one_path_segment()
    {
        Assert.Null(PublicEdgePolicy.Match("GET", "/api/tasks/DEMO-9/results", PublicEdgeAllowlist.Routes));
        Assert.NotNull(PublicEdgePolicy.Match("GET", "/api/tasks/DEMO-9/results/a/b.png", PublicEdgeAllowlist.Routes));
    }

    [Fact]
    public void Sandboxed_routes_are_the_ones_that_can_carry_seeded_html()
    {
        var sandboxed = PublicEdgeAllowlist.Routes.Where(route => route.Sandboxed).Select(route => route.Template).ToList();

        Assert.Contains("/api/projects/{projectName}/wiki/files/{**relPath}", sandboxed);
        Assert.Contains("/api/tasks/{jobId}/results/{**path}", sandboxed);
        Assert.DoesNotContain("/api/tasks", sandboxed);
    }

    [Fact]
    public void The_allowlist_digest_is_stable_and_order_independent()
    {
        var reversed = PublicEdgeAllowlist.Routes.Reverse().ToList();

        Assert.Equal(PublicEdgeAllowlist.Digest(PublicEdgeAllowlist.Routes), PublicEdgeAllowlist.Digest(reversed));
    }

    [Fact]
    public void Startup_refuses_a_contract_that_would_widen_the_visitor_surface()
    {
        var withMutation = Contract(routes: [.. PublicEdgeAllowlist.Routes, new PublicEdgeRoute("POST", "/api/tasks")]);

        var error = Assert.Throws<InvalidOperationException>(() => PublicDemoStartup.Validate(withMutation));
        Assert.Contains("unsafe methods", error.Message);

        var withoutProjects = Contract() with { Projects = [] };
        Assert.Throws<InvalidOperationException>(() => PublicDemoStartup.Validate(withoutProjects));

        PublicDemoStartup.Validate(Contract());
    }

    [Fact]
    public void The_fixed_window_admits_up_to_the_limit_and_then_rolls_over()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero));
        var limiter = new PublicEdgeRateLimiter(Contract(perWindow: 3), time);

        Assert.True(limiter.Admit("viewer-a"));
        Assert.True(limiter.Admit("viewer-a"));
        Assert.True(limiter.Admit("viewer-a"));
        Assert.False(limiter.Admit("viewer-a"));

        // A second visitor keeps its own budget.
        Assert.True(limiter.Admit("viewer-b"));

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(limiter.Admit("viewer-a"));
    }
}
