using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Routing;

namespace AgentStudio.TaskServer;

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
        var admissionPath = PublicDemoTaskServerRouteMatrix.Classify(context.Request.Method, route);
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

public static class PublicDemoTaskServerRouteMatrix
{
    internal const string ExpectedFingerprint =
        "e94126cd35c9975e903e5aad1f4c187c153ded0e339c22545aaeff406553f8c7";

    public static ExecutionAdmissionPath? Classify(string method, string? route)
    {
        if (string.IsNullOrWhiteSpace(route)) return null;
        if (method.ToUpperInvariant() is not ("POST" or "PUT" or "PATCH" or "DELETE"))
            return null;

        if (route.Contains("post-steps", StringComparison.OrdinalIgnoreCase))
            return ExecutionAdmissionPath.PostStep;
        if (route.Contains("claim", StringComparison.OrdinalIgnoreCase)
            || route.Contains("lease/", StringComparison.OrdinalIgnoreCase))
            return ExecutionAdmissionPath.Claim;
        if (route.Contains("review", StringComparison.OrdinalIgnoreCase))
            return ExecutionAdmissionPath.Review;
        if (route.Contains("orchestrator-contexts", StringComparison.OrdinalIgnoreCase))
            return ExecutionAdmissionPath.Chat;
        if (route.Contains("stages/complete", StringComparison.OrdinalIgnoreCase)
            || route.Contains("/runs", StringComparison.OrdinalIgnoreCase))
            return ExecutionAdmissionPath.Start;
        return ExecutionAdmissionPath.Mutation;
    }

    public static PublicDemoTaskServerRouteMatrixProof Capture(EndpointDataSource endpoints)
        => Capture([endpoints]);

    public static PublicDemoTaskServerRouteMatrixProof Capture(
        IEnumerable<EndpointDataSource> endpointSources)
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
        return new PublicDemoTaskServerRouteMatrixProof(fingerprint, expectations);
    }

    public static void ProveAtStartup(
        ExecutionAdmissionPolicy policy,
        IEnumerable<EndpointDataSource> endpointSources)
    {
        if (!policy.IsPublicDemoLocked) return;

        var proof = Capture(endpointSources);
        if (!string.Equals(proof.Fingerprint, ExpectedFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Public demo Task Server startup refused: route expectation matrix mismatch. " +
                $"Expected {ExpectedFingerprint}, observed {proof.Fingerprint} across {proof.Expectations.Count} route expectations.");
        }
    }

    private static string Describe(string method, string route)
    {
        var expectation = Classify(method, route)?.ToString() ?? "AllowRead";
        return $"{method.ToUpperInvariant()} {route} => {expectation}";
    }
}

public sealed record PublicDemoTaskServerRouteMatrixProof(
    string Fingerprint,
    IReadOnlyList<string> Expectations);
