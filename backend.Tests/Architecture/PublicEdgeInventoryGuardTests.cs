using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// W34 S4 inventory guard. The public demo publishes an explicit read
/// allowlist, and the allowlist is only worth anything if it still describes
/// the routes the server actually registers.
///
/// <list type="bullet">
///   <item>Every allowlist entry must resolve to a live registered endpoint, so
///   a renamed or deleted route fails here instead of silently shrinking the
///   demo to a wall of 403s.</item>
///   <item>The allowlist must contain safe methods only.</item>
///   <item>Every mutating route the server registers must be outside the
///   allowlist. Default-deny already guarantees this, and the guard makes a
///   regression in the matcher visible.</item>
/// </list>
///
/// <para>
/// The test also writes the full classified route inventory next to the test
/// assembly. That file is the S4 evidence artifact: reviewers can read exactly
/// which of the server's routes the public edge exposes.
/// </para>
/// </summary>
[Collection(WebApplicationFactorySerialCollection.Name)]
public sealed class PublicEdgeInventoryGuardTests : IDisposable
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "studio-public-edge-inventory-" + Guid.NewGuid().ToString("N"));

    public const string InventoryFileName = "public-demo-endpoint-inventory.json";

    [Fact]
    public void The_allowlist_describes_routes_the_server_actually_registers()
    {
        var registered = RegisteredRoutes();
        WriteInventory(registered);

        var unmatched = PublicEdgeAllowlist.Routes
            .Where(entry => !registered.Any(route =>
                string.Equals(route.Method, entry.Method, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Normalize(route.Template), Normalize(entry.Template), StringComparison.OrdinalIgnoreCase)))
            .Select(entry => $"{entry.Method} {entry.Template}")
            .ToList();

        Assert.True(
            unmatched.Count == 0,
            "W34 S4: every public-demo allowlist entry must match a registered route template exactly.\n"
            + "These entries no longer resolve, so the public demo would answer 403 for a surface it claims to publish:\n  "
            + string.Join("\n  ", unmatched)
            + $"\nThe classified inventory was written to {InventoryFileName} beside the test assembly.");
    }

    [Fact]
    public void The_allowlist_admits_safe_methods_only()
    {
        var unsafeEntries = PublicEdgeAllowlist.Routes
            .Where(entry => !PublicEdgePolicy.IsSafeMethod(entry.Method))
            .Select(entry => $"{entry.Method} {entry.Template}")
            .ToList();

        Assert.True(
            unsafeEntries.Count == 0,
            "W34 S4: the public demo launches read-only. Remove these entries:\n  " + string.Join("\n  ", unsafeEntries));
    }

    [Fact]
    public void No_mutating_route_is_reachable_through_the_allowlist()
    {
        var reachable = RegisteredRoutes()
            .Where(route => !PublicEdgePolicy.IsSafeMethod(route.Method))
            .Where(route => PublicEdgePolicy.Match(route.Method, Sample(route.Template), PublicEdgeAllowlist.Routes) is not null)
            .Select(route => $"{route.Method} {route.Template}")
            .ToList();

        Assert.True(
            reachable.Count == 0,
            "W34 S4: a mutating route resolved against the public read allowlist. The edge would admit it:\n  "
            + string.Join("\n  ", reachable));
    }

    private sealed record RegisteredRoute(string Method, string Template);

    private List<RegisteredRoute> RegisteredRoutes()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["Security:Profile"] = PublicDemoProfile.ProfileName,
            }));
        });

        // Touching Services forces the host to build so the endpoint table exists.
        var sources = factory.Services.GetRequiredService<EndpointDataSource>();
        return sources.Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint =>
                (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                .Select(method => new RegisteredRoute(method, "/" + endpoint.RoutePattern.RawText?.TrimStart('/'))))
            .Distinct()
            .OrderBy(route => route.Template, StringComparer.Ordinal)
            .ThenBy(route => route.Method, StringComparer.Ordinal)
            .ToList();
    }

    private static void WriteInventory(IReadOnlyList<RegisteredRoute> registered)
    {
        var inventory = registered.Select(route => new
        {
            method = route.Method,
            template = route.Template,
            publicDemo = PublicEdgePolicy.Match(route.Method, Sample(route.Template), PublicEdgeAllowlist.Routes) is { } matched
                ? matched.Sandboxed ? "allowed-sandboxed" : "allowed"
                : "denied",
        }).ToList();

        var payload = new
        {
            slice = "W34-S4",
            profile = PublicDemoProfile.ProfileName,
            allowlistDigest = PublicEdgeAllowlist.Digest(PublicEdgeAllowlist.Routes),
            registeredRouteCount = inventory.Count,
            allowedRouteCount = inventory.Count(entry => entry.publicDemo != "denied"),
            routes = inventory,
        };

        File.WriteAllText(
            Path.Combine(AppContext.BaseDirectory, InventoryFileName),
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8);
    }

    /// <summary>Turn a route template into a concrete path so the matcher can be exercised against it.</summary>
    private static string Sample(string template)
    {
        var builder = new StringBuilder();
        foreach (var segment in template.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            builder.Append('/');
            builder.Append(segment.StartsWith('{')
                ? segment.StartsWith("{**", StringComparison.Ordinal) ? "sample/leaf" : "sample"
                : segment);
        }

        return builder.Length == 0 ? "/" : builder.ToString();
    }

    private static string Normalize(string template) => "/" + template.Trim('/');

    public void Dispose()
    {
        try { Directory.Delete(_workspace, true); } catch (IOException) { }
    }
}
