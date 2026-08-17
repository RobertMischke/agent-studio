using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;

namespace AgentStudio.Security;

public sealed record ExecutionRouteMetadata(ExecutionAdmissionPath Path);

public sealed record PublicDemoExecutionExpectationMetadata(
    ExecutionAdmissionPath Path,
    string ExpectedCode);

/// <summary>
/// Immutable startup-profile view used at every local execution boundary.
/// </summary>
public sealed class StartupExecutionAdmission(string startupProfile)
{
    public string StartupProfile { get; } = startupProfile;

    public bool IsPublicDemo => string.Equals(
        StartupProfile,
        ExecutionAdmissionPolicy.PublicDemoProfile,
        StringComparison.OrdinalIgnoreCase);

    public ExecutionAdmissionDecision Decide(ExecutionAdmissionPath path)
        => ExecutionAdmissionPolicy.Decide(StartupProfile, path);

    public void Demand(ExecutionAdmissionPath path)
    {
        var decision = Decide(path);
        if (!decision.Allowed) throw new ExecutionAdmissionDeniedException(path, decision);
    }
}

public sealed class ExecutionAdmissionDeniedException(
    ExecutionAdmissionPath path,
    ExecutionAdmissionDecision decision)
    : InvalidOperationException(decision.Message)
{
    public ExecutionAdmissionPath Path { get; } = path;
    public ExecutionAdmissionDecision Decision { get; } = decision;
}

public static class PublicDemoExecutionEndpointExtensions
{
    public static RouteHandlerBuilder WithPublicDemoExecutionDenied(
        this RouteHandlerBuilder builder,
        ExecutionAdmissionPath path)
        => builder
            .WithMetadata(new ExecutionRouteMetadata(path))
            .WithMetadata(new PublicDemoExecutionExpectationMetadata(
                path,
                ExecutionAdmissionPolicy.ExecutionDisabledCode));

    public static IApplicationBuilder UsePublicDemoExecutionLock(
        this IApplicationBuilder app)
        => app.UseMiddleware<PublicDemoExecutionLockMiddleware>();
}

public sealed class PublicDemoExecutionLockMiddleware(
    RequestDelegate next,
    StartupExecutionAdmission admission)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var route = context.GetEndpoint()?.Metadata.GetMetadata<ExecutionRouteMetadata>();
        if (route is null)
        {
            await next(context);
            return;
        }

        var decision = admission.Decide(route.Path);
        if (decision.Allowed)
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new
        {
            error = decision.Code,
            message = decision.Message,
            profile = admission.StartupProfile,
            admissionPath = ExecutionAdmissionPolicy.PathName(route.Path),
        });
    }
}

public sealed record PublicDemoExecutionRoute(
    string Method,
    string Pattern,
    ExecutionAdmissionPath Path,
    string ExpectedCode);

public static class PublicDemoExecutionRouteInventory
{
    public static IReadOnlyList<PublicDemoExecutionRoute> ValidateStartup(
        IEndpointRouteBuilder endpoints,
        StartupExecutionAdmission admission)
    {
        if (!admission.IsPublicDemo) return [];

        var policyFailures = ExecutionAdmissionPolicy.AllPaths
            .Where(path => ExecutionAdmissionPolicy.Decide(admission.StartupProfile, path).Allowed)
            .ToList();
        if (policyFailures.Count > 0)
        {
            throw new InvalidOperationException(
                "The public demo execution policy is not deny-only for: " +
                string.Join(", ", policyFailures.Select(ExecutionAdmissionPolicy.PathName)));
        }

        var routes = new List<PublicDemoExecutionRoute>();
        var missingExpectations = new List<string>();
        foreach (var endpoint in endpoints.DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>())
        {
            var execution = endpoint.Metadata.GetMetadata<ExecutionRouteMetadata>();
            if (execution is null) continue;

            var expectation = endpoint.Metadata.GetMetadata<PublicDemoExecutionExpectationMetadata>();
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"];
            var pattern = endpoint.RoutePattern.RawText ?? endpoint.DisplayName ?? "<unknown>";
            if (expectation is null
                || expectation.Path != execution.Path
                || !string.Equals(
                    expectation.ExpectedCode,
                    ExecutionAdmissionPolicy.ExecutionDisabledCode,
                    StringComparison.Ordinal))
            {
                missingExpectations.Add($"{string.Join('|', methods)} {pattern}");
                continue;
            }

            routes.AddRange(methods.Select(method => new PublicDemoExecutionRoute(
                method,
                pattern,
                execution.Path,
                expectation.ExpectedCode)));
        }

        if (missingExpectations.Count > 0)
        {
            throw new InvalidOperationException(
                "Executable routes lack a public-demo expectation: " +
                string.Join(", ", missingExpectations.Order(StringComparer.Ordinal)));
        }

        var missingPaths = ExecutionAdmissionPolicy.AllPaths
            .Except(routes.Select(route => route.Path))
            .ToList();
        if (missingPaths.Count > 0)
        {
            throw new InvalidOperationException(
                "The public demo route matrix does not cover: " +
                string.Join(", ", missingPaths.Select(ExecutionAdmissionPolicy.PathName)));
        }

        if (routes.Count == 0)
            throw new InvalidOperationException("The public demo execution route matrix is empty.");

        return routes
            .OrderBy(route => route.Pattern, StringComparer.Ordinal)
            .ThenBy(route => route.Method, StringComparer.Ordinal)
            .ToList();
    }
}
