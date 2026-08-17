using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TaskServer.Tests;

public sealed class PublicDemoExecutionProfileTests
{
    public static IReadOnlyList<(string Method, string Path)> ExecutionRoutes { get; } =
    [
        // Forged coding, review, orchestration, and permit claims.
        ("POST", "/api/v1/runners/forged/claims"),
        ("POST", "/api/v1/runners/forged/review-claims"),
        ("POST", "/api/v1/orchestration/claims"),
        ("POST", "/api/v1/work-permits/forged/accept"),

        // Run start/continuation authority and completion paths.
        ("POST", "/api/v1/orchestration/projects/demo/runs"),
        ("POST", "/api/v1/runs/forged/reconcile"),
        ("POST", "/api/v1/runs/forged/lease/renew"),
        ("POST", "/api/v1/runs/forged/completion"),

        // Review execution and reporting surfaces.
        ("POST", "/api/v1/reviews/subjects"),
        ("POST", "/api/v1/reviews/attempts/forged/lease/renew"),
        ("POST", "/api/v1/reviews/attempts/forged/report"),
        ("POST", "/api/v1/reviews/attempts/forged/cleanup"),

        // Project and task chat context writes.
        ("POST", "/api/v1/orchestrator-contexts/projects/demo/turns"),
        ("POST", "/api/v1/orchestrator-contexts/projects/demo/tasks/DEMO-1/turns"),

        // Both post-step authority mutations.
        ("POST", "/api/v1/runs/forged/post-steps/example/claim"),
        ("POST", "/api/v1/runs/forged/post-steps/example/complete"),
    ];

    [Fact]
    public async Task Public_demo_denies_forged_requests_before_protocol_authentication_or_binding()
    {
        using var temp = new TempDirectory();
        await using var factory = new TaskServerFactory(
            temp.Path,
            DeploymentProfiles.PublicDemoReadonly);
        using var client = factory.CreateClient();
        foreach (var (method, path) in ExecutionRoutes)
            await AssertExecutionDisabledAsync(client, method, path, path);

        var endpointSource = factory.Services.GetRequiredService<EndpointDataSource>();
        var deniedInventory = EnumerateDeniedInventory(endpointSource).ToArray();
        Assert.NotEmpty(deniedInventory);
        foreach (var (method, path, route) in deniedInventory)
            await AssertExecutionDisabledAsync(client, method, path, route);
    }

    private static async Task AssertExecutionDisabledAsync(
        HttpClient client,
        string method,
        string path,
        string route)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = JsonContent.Create(new { forged = true }),
        };
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(
            response.StatusCode == HttpStatusCode.Forbidden,
            $"{method} {route} materialized as {path} returned {(int)response.StatusCode} instead of 403.");
        Assert.Equal(ExecutionAdmissionDecision.DisabledCode, body.GetProperty("error").GetString());
        Assert.Equal(ExecutionAdmissionDecision.DisabledMessage, body.GetProperty("message").GetString());
    }

    private static IEnumerable<(string Method, string Path, string Route)> EnumerateDeniedInventory(
        EndpointDataSource endpointSource)
    {
        foreach (var endpoint in endpointSource.Endpoints.OfType<RouteEndpoint>())
        {
            var route = endpoint.RoutePattern.RawText ?? endpoint.DisplayName ?? "<unnamed>";
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
            foreach (var method in methods)
            {
                if (PublicDemoTaskServerRouteMatrix.Classify(method, route) is not null)
                    yield return (method.ToUpperInvariant(), Materialize(endpoint.RoutePattern), route);
            }
        }
    }

    private static string Materialize(RoutePattern pattern)
        => "/" + string.Join('/', pattern.PathSegments.Select(segment =>
            string.Concat(segment.Parts.Select(part => part switch
            {
                RoutePatternLiteralPart literal => literal.Content,
                RoutePatternSeparatorPart separator => separator.Content,
                RoutePatternParameterPart parameter => ParameterValue(parameter),
                _ => throw new InvalidOperationException($"Unsupported route part {part.GetType().Name}."),
            }))));

    private static string ParameterValue(RoutePatternParameterPart parameter)
    {
        var policies = string.Join(',', parameter.ParameterPolicies.Select(policy => policy.Content));
        if (policies.Contains("int", StringComparison.OrdinalIgnoreCase)) return "1";
        if (policies.Contains("guid", StringComparison.OrdinalIgnoreCase))
            return "00000000-0000-0000-0000-000000000001";
        return parameter.Name == "taskKey" ? "DEMO-1" : "forged";
    }

    [Fact]
    public void Route_expectation_inventory_is_pinned()
    {
        using var temp = new TempDirectory();
        using var factory = new TaskServerFactory(temp.Path, "task-server");
        using var client = factory.CreateClient();
        var proof = PublicDemoTaskServerRouteMatrix.Capture(
            factory.Services.GetRequiredService<EndpointDataSource>());

        Assert.Equal(PublicDemoTaskServerRouteMatrix.ExpectedFingerprint, proof.Fingerprint);
    }

    [Fact]
    public void Public_demo_startup_proof_rejects_an_unreviewed_route_matrix()
    {
        var endpointBuilder = new RouteEndpointBuilder(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/api/v1/new-execution"),
            order: 0);
        endpointBuilder.Metadata.Add(new HttpMethodMetadata(["POST"]));
        var source = new DefaultEndpointDataSource(endpointBuilder.Build());

        var exception = Assert.Throws<InvalidOperationException>(() =>
            PublicDemoTaskServerRouteMatrix.ProveAtStartup(
                new ExecutionAdmissionPolicy(DeploymentProfiles.PublicDemoReadonly),
                [source]));

        Assert.Contains("startup refused", exception.Message, StringComparison.Ordinal);
        Assert.Contains("route expectation matrix mismatch", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_profile_identity_refuses_startup()
    {
        using var temp = new TempDirectory();
        using var factory = new TaskServerFactory(temp.Path, "public-demo");

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Unknown Task Server deployment profile", exception.ToString(), StringComparison.Ordinal);
    }

    private sealed class TaskServerFactory(
        string dataDirectory,
        string deploymentProfile)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            // The deployment profile is a startup identity, so place it in
            // host configuration before Program captures its immutable policy.
            builder.UseSetting("TaskServer:DeploymentProfile", deploymentProfile);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskServer:DataDirectory"] = dataDirectory,
                    ["TaskServer:ListenUrl"] = string.Empty,
                    ["TaskServer:DeploymentProfile"] = deploymentProfile,
                }));
        }
    }
}
