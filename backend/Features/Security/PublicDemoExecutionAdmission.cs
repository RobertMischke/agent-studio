using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Routing;

namespace AgentStudio.Security;

/// <summary>
/// Server-side execution boundary for the startup-only public demo profile.
/// It runs before authentication and model binding so a forged Runner identity
/// or malformed payload cannot reach an execution handler.
/// </summary>
public sealed class PublicDemoExecutionAdmissionMiddleware(
    RequestDelegate next,
    ExecutionAdmissionPolicy policy)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!policy.IsPublicDemoLocked)
        {
            await next(context);
            return;
        }

        var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        var admissionPath = PublicDemoRouteMatrix.Classify(context.Request.Method, route);
        if (admissionPath is null)
        {
            await next(context);
            return;
        }

        var decision = policy.Decide(admissionPath.Value);
        if (decision.Allowed)
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            error = decision.Code,
            message = decision.Message,
        }));
    }
}

public static class PublicDemoExecutionAdmissionExtensions
{
    public static IApplicationBuilder UsePublicDemoExecutionAdmission(this IApplicationBuilder app)
        => app.UseMiddleware<PublicDemoExecutionAdmissionMiddleware>();
}

/// <summary>
/// Complete route-and-expectation inventory used by the public-demo launch
/// proof. Every registered route contributes to the digest, including allowed
/// read routes. Adding any endpoint therefore fails both the build guard and a
/// public-demo startup until its expectation has been reviewed and the pinned
/// digest is updated.
/// </summary>
public static class PublicDemoRouteMatrix
{
    // Filled from the direct inventory tests. Separate values are intentional:
    // the monolith compatibility plane has explicit v1 routes, while the
    // separated topology has one proxy route whose upstream is independently
    // locked by the Task Server profile.
    internal const string LocalV1Fingerprint =
        "6667b59bd9ef0e9fdca3422fa7261caa9679afa367b434cde7e070dc2bae88d7";
    internal const string ProxiedV1Fingerprint =
        "70777eed7b6ed2fab734317fdbbc784b13b13fd1ea32abf649943e161364fd35";

    public static ExecutionAdmissionPath? Classify(string method, string? route)
    {
        if (string.IsNullOrWhiteSpace(route)) return null;

        var normalizedMethod = method.ToUpperInvariant();
        if (normalizedMethod is "POST" or "PUT" or "PATCH" or "DELETE")
            return ClassifyMutation(route);

        if (normalizedMethod is not ("GET" or "HEAD" or "OPTIONS" or "*"))
            return ExecutionAdmissionPath.Mutation;

        return IsRepositoryOrDiagnosticToolRoute(route)
            ? ExecutionAdmissionPath.RepositoryTool
            : null;
    }

    public static PublicDemoRouteMatrixProof Capture(EndpointDataSource endpoints)
        => Capture([endpoints]);

    public static PublicDemoRouteMatrixProof Capture(IEnumerable<EndpointDataSource> endpointSources)
    {
        var expectations = endpointSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint =>
            {
                var route = endpoint.RoutePattern.RawText ?? endpoint.DisplayName ?? "<unnamed>";
                var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
                return methods is { Count: > 0 }
                    ? methods.Select(method => Describe(method, route))
                    : [Describe("*", route)];
            })
            .Order(StringComparer.Ordinal)
            .ToArray();
        var payload = string.Join('\n', expectations);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        return new PublicDemoRouteMatrixProof(fingerprint, expectations);
    }

    public static void ProveAtStartup(
        ExecutionAdmissionPolicy policy,
        IEnumerable<EndpointDataSource> endpointSources,
        bool mapsLocalV1)
    {
        if (!policy.IsPublicDemoLocked) return;

        var proof = Capture(endpointSources);
        var expected = mapsLocalV1 ? LocalV1Fingerprint : ProxiedV1Fingerprint;
        if (!string.Equals(proof.Fingerprint, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Public demo startup refused: route expectation matrix mismatch. " +
                $"Expected {expected}, observed {proof.Fingerprint} across {proof.Expectations.Count} route expectations.");
        }
    }

    private static string Describe(string method, string route)
    {
        var expectation = Classify(method, route)?.ToString() ?? "AllowRead";
        return $"{method.ToUpperInvariant()} {route} => {expectation}";
    }

    private static ExecutionAdmissionPath ClassifyMutation(string route)
    {
        if (route.Contains("post-steps", StringComparison.OrdinalIgnoreCase))
            return ExecutionAdmissionPath.PostStep;
        if (route.Contains("claim", StringComparison.OrdinalIgnoreCase)
            || route.Contains("lease/acquire", StringComparison.OrdinalIgnoreCase))
            return ExecutionAdmissionPath.Claim;
        if (route.Contains("continue", StringComparison.OrdinalIgnoreCase)
            || route.Contains("resume", StringComparison.OrdinalIgnoreCase)
            || route.Contains("reissue", StringComparison.OrdinalIgnoreCase)
            || route.Contains("re-evaluate", StringComparison.OrdinalIgnoreCase))
            return ExecutionAdmissionPath.Continue;
        if (route.Contains("review", StringComparison.OrdinalIgnoreCase))
            return ExecutionAdmissionPath.Review;
        if (route.Contains("chat", StringComparison.OrdinalIgnoreCase)
            || route.Contains("/turns", StringComparison.OrdinalIgnoreCase))
            return ExecutionAdmissionPath.Chat;
        if (route.Contains("preview", StringComparison.OrdinalIgnoreCase)
            || route.Contains("probe", StringComparison.OrdinalIgnoreCase)
            || route.Contains("validate", StringComparison.OrdinalIgnoreCase))
            return ExecutionAdmissionPath.Preview;
        if (route.EndsWith("/start", StringComparison.OrdinalIgnoreCase)
            || route.Contains("/pipeline/steps/", StringComparison.OrdinalIgnoreCase)
            || route.Contains("/test-runs", StringComparison.OrdinalIgnoreCase)
            || route.EndsWith("/intake", StringComparison.OrdinalIgnoreCase))
            return ExecutionAdmissionPath.Start;
        return ExecutionAdmissionPath.Mutation;
    }

    private static bool IsRepositoryOrDiagnosticToolRoute(string route)
        => route.StartsWith("/api/git", StringComparison.OrdinalIgnoreCase)
           || route.Contains("/git/", StringComparison.OrdinalIgnoreCase)
           || route.Contains("/commit", StringComparison.OrdinalIgnoreCase)
           || route.StartsWith("/api/cli/_probe", StringComparison.OrdinalIgnoreCase)
           || (route.StartsWith("/api/cli/", StringComparison.OrdinalIgnoreCase)
               && route.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
           || route.EndsWith("/diagnostic", StringComparison.OrdinalIgnoreCase);
}

public sealed record PublicDemoRouteMatrixProof(
    string Fingerprint,
    IReadOnlyList<string> Expectations);
