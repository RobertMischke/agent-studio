using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

[Collection(WebApplicationFactorySerialCollection.Name)]
public sealed class PublicDemoExecutionProfileTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(),
        "studio-public-demo-execution-" + Guid.NewGuid().ToString("N"));

    public static TheoryData<string, ExecutionAdmissionPath> PolicyCases => new()
    {
        { "claim", ExecutionAdmissionPath.Claim },
        { "start", ExecutionAdmissionPath.Start },
        { "continue", ExecutionAdmissionPath.Continue },
        { "review", ExecutionAdmissionPath.Review },
        { "chat", ExecutionAdmissionPath.Chat },
        { "preview", ExecutionAdmissionPath.Preview },
        { "post-step", ExecutionAdmissionPath.PostStep },
        { "mutation", ExecutionAdmissionPath.Mutation },
        { "repository-tool", ExecutionAdmissionPath.RepositoryTool },
    };

    public static IReadOnlyList<(string Method, string Path)> ExecutionRoutes { get; } =
    [
        // Current and compatibility claim surfaces.
        ("POST", "/api/runner/claim"),
        ("POST", "/api/runner/lease/acquire"),
        ("POST", "/api/runner/integration-lease/acquire"),
        ("POST", "/api/runner/project-chat/claim"),
        ("POST", "/api/runner/project-chat/renew"),
        ("POST", "/api/runner/project-chat/complete"),
        ("POST", "/api/v1/runners/forged/review-claims"),

        // Direct task, project-runner, intake, and post-step starts.
        ("POST", "/api/tasks/forged/start"),
        ("POST", "/api/runner/forged/start"),
        ("POST", "/api/tasks/forged/intake"),
        ("POST", "/api/tasks/forged/pipeline/steps/code-review/run"),
        ("POST", "/api/projects/PROJ-123/urls/demo/start"),

        // Continue, retry, and re-evaluation surfaces.
        ("POST", "/api/tasks/forged/continue"),
        ("POST", "/api/tasks/forged/re-evaluate"),

        // Local and separated-review compatibility routes.
        ("POST", "/api/tasks/forged/code-review"),
        ("POST", "/api/v1/reviews/attempts/forged/lease/renew"),
        ("POST", "/api/v1/reviews/attempts/forged/report"),
        ("POST", "/api/v1/reviews/attempts/forged/cleanup"),
        ("POST", "/api/attempts/reviews"),
        ("POST", "/api/attempts/reviews/forged/claim"),
        ("POST", "/api/attempts/reviews/forged/renew"),
        ("POST", "/api/attempts/reviews/forged/settle"),
        ("POST", "/api/admin/prompts/review-all"),
        ("POST", "/api/admin/prompts/example/review"),
        ("POST", "/api/projects/forged/design/actions/council-review"),

        // Project, task, and global chat execution routes.
        ("POST", "/api/runner/forged/orchestrator-chat"),
        ("POST", "/api/runner/project:forged/orchestrator-chat"),
        ("POST", "/api/runner/task:forged/DEMO-1/orchestrator-chat"),
        ("POST", "/api/orchestrator/sessions/global/turns"),
        ("POST", "/api/orchestrator/sessions/project:forged/turns"),
        ("POST", "/api/orchestrator/sessions/task:forged/DEMO-1/turns"),
        ("POST", "/api/runner/forged/orchestrator-chat/attachments"),

        // Preview/probe routes and repository tool reads.
        ("POST", "/api/tasks/forged/merge/preview"),
        ("POST", "/api/admin/prompts/example/preview"),
        ("POST", "/api/projects/forged/pipeline-steps/example/probe"),
        ("GET", "/api/git/summary"),
        ("GET", "/api/tasks/forged/git/status"),
        ("GET", "/api/cli/_probe/codex"),
        ("GET", "/api/projects/PROJ-123/urls/demo/diagnostic"),
    ];

    [Theory]
    [MemberData(nameof(PolicyCases))]
    public void Pure_policy_denies_every_execution_admission_path_in_public_demo(
        string _,
        ExecutionAdmissionPath path)
    {
        var policy = new ExecutionAdmissionPolicy(DeploymentProfiles.PublicDemoReadonly);

        var decision = policy.Decide(path);

        Assert.False(decision.Allowed);
        Assert.Equal(ExecutionAdmissionDecision.DisabledCode, decision.Code);
        Assert.Equal(ExecutionAdmissionDecision.DisabledMessage, decision.Message);
    }

    [Fact]
    public void Pure_policy_preserves_execution_outside_public_demo()
    {
        var decision = new ExecutionAdmissionPolicy("networked")
            .Decide(ExecutionAdmissionPath.Claim);

        Assert.True(decision.Allowed);
    }

    [Fact]
    public async Task Public_demo_returns_the_same_typed_denial_for_every_execution_route()
    {
        await using var factory = BuildFactory(DeploymentProfiles.PublicDemoReadonly);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://studio.test"),
            HandleCookies = false,
        });
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
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT" or "PATCH" or "DELETE")
            request.Content = JsonContent.Create(new { forged = true });

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
                if (PublicDemoRouteMatrix.Classify(method, route) is not null)
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
        if (policies.Contains("PROJ-", StringComparison.OrdinalIgnoreCase)) return "PROJ-123";
        if (policies.Contains("int", StringComparison.OrdinalIgnoreCase)) return "1";
        if (policies.Contains("guid", StringComparison.OrdinalIgnoreCase))
            return "00000000-0000-0000-0000-000000000001";
        return parameter.Name switch
        {
            "projId" => "PROJ-123",
            "taskKey" => "DEMO-1",
            _ => "forged",
        };
    }

    [Fact]
    public void Local_v1_route_inventory_is_pinned()
    {
        using var factory = BuildFactory(SecurityProfiles.Networked);
        using var client = factory.CreateClient();
        var proof = PublicDemoRouteMatrix.Capture(
            factory.Services.GetRequiredService<EndpointDataSource>());

        Assert.Equal(PublicDemoRouteMatrix.LocalV1Fingerprint, proof.Fingerprint);
    }

    [Fact]
    public void Proxied_v1_route_inventory_is_pinned()
    {
        using var factory = BuildFactory(
            SecurityProfiles.Networked,
            new Dictionary<string, string?> { ["TaskServer:BaseUrl"] = "http://127.0.0.1:5071" });
        using var client = factory.CreateClient();
        var proof = PublicDemoRouteMatrix.Capture(
            factory.Services.GetRequiredService<EndpointDataSource>());

        Assert.Equal(PublicDemoRouteMatrix.ProxiedV1Fingerprint, proof.Fingerprint);
    }

    [Fact]
    public void Public_demo_startup_proof_rejects_an_unreviewed_route_matrix()
    {
        var endpointBuilder = new RouteEndpointBuilder(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/api/new-execution"),
            order: 0);
        endpointBuilder.Metadata.Add(new HttpMethodMetadata(["POST"]));
        var source = new DefaultEndpointDataSource(endpointBuilder.Build());

        var exception = Assert.Throws<InvalidOperationException>(() =>
            PublicDemoRouteMatrix.ProveAtStartup(
                new ExecutionAdmissionPolicy(DeploymentProfiles.PublicDemoReadonly),
                [source],
                mapsLocalV1: true));

        Assert.Contains("startup refused", exception.Message, StringComparison.Ordinal);
        Assert.Contains("route expectation matrix mismatch", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_profile_identity_refuses_startup()
    {
        using var factory = BuildFactory("public-demo");

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Unknown Security:Profile", exception.ToString(), StringComparison.Ordinal);
    }

    private WebApplicationFactory<Program> BuildFactory(
        string profile,
        IReadOnlyDictionary<string, string?>? overrides = null)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            // The deployment profile is a startup identity, so place it in
            // host configuration before Program captures its immutable policy.
            builder.UseSetting("Security:Profile", profile);
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _workspace,
                    ["Security:Profile"] = profile,
                    ["Runner:Role"] = "test-subject",
                    ["AllowedHosts"] = "studio.test",
                };
                if (overrides is not null)
                    foreach (var (key, value) in overrides)
                        values[key] = value;
                configuration.AddInMemoryCollection(values);
            });
        });

    public void Dispose()
    {
        try { Directory.Delete(_workspace, true); } catch { }
    }
}
