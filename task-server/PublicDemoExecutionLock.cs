using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;

namespace AgentStudio.TaskServer;

public sealed record TaskServerExecutionRouteMetadata(ExecutionAdmissionPath Path);

public sealed record TaskServerPublicDemoExpectationMetadata(
    ExecutionAdmissionPath Path,
    string ExpectedCode);

public sealed class TaskServerStartupExecutionAdmission
{
    public const string StandardProfile = "standard";
    public const string LocalCompatibilityProfile = "local-compatibility";

    public TaskServerStartupExecutionAdmission(IConfiguration configuration)
    {
        StartupProfile = (configuration["TASK_SERVER_PROFILE"] ?? StandardProfile).Trim().ToLowerInvariant();
        if (StartupProfile is not (StandardProfile
            or LocalCompatibilityProfile
            or ExecutionAdmissionPolicy.PublicDemoProfile))
        {
            throw new InvalidOperationException(
                $"Unsupported TASK_SERVER_PROFILE '{StartupProfile}'.");
        }
    }

    public string StartupProfile { get; }

    public bool IsPublicDemo => string.Equals(
        StartupProfile,
        ExecutionAdmissionPolicy.PublicDemoProfile,
        StringComparison.Ordinal);

    public ExecutionAdmissionDecision Decide(ExecutionAdmissionPath path)
        => ExecutionAdmissionPolicy.Decide(StartupProfile, path);
}

public static class TaskServerPublicDemoExecutionExtensions
{
    public static RouteHandlerBuilder WithPublicDemoExecutionDenied(
        this RouteHandlerBuilder builder,
        ExecutionAdmissionPath path)
        => builder
            .WithMetadata(new TaskServerExecutionRouteMetadata(path))
            .WithMetadata(new TaskServerPublicDemoExpectationMetadata(
                path,
                ExecutionAdmissionPolicy.ExecutionDisabledCode));

    public static IApplicationBuilder UsePublicDemoExecutionLock(
        this IApplicationBuilder app)
        => app.UseMiddleware<TaskServerPublicDemoExecutionLockMiddleware>();
}

public sealed class TaskServerPublicDemoExecutionLockMiddleware(
    RequestDelegate next,
    TaskServerStartupExecutionAdmission admission)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var route = context.GetEndpoint()?.Metadata.GetMetadata<TaskServerExecutionRouteMetadata>();
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
        await context.Response.WriteAsJsonAsync(new ApiError(
            decision.Code,
            decision.Message,
            new
            {
                profile = admission.StartupProfile,
                admissionPath = ExecutionAdmissionPolicy.PathName(route.Path),
            }));
    }
}

public sealed record TaskServerPublicDemoExecutionRoute(
    string Method,
    string Pattern,
    ExecutionAdmissionPath Path,
    string ExpectedCode);

public static class TaskServerPublicDemoExecutionRouteInventory
{
    public static IReadOnlyList<TaskServerPublicDemoExecutionRoute> ValidateStartup(
        IEndpointRouteBuilder endpoints,
        TaskServerStartupExecutionAdmission admission)
    {
        if (!admission.IsPublicDemo) return [];

        var policyFailures = ExecutionAdmissionPolicy.AllPaths
            .Where(path => admission.Decide(path).Allowed)
            .ToList();
        if (policyFailures.Count > 0)
        {
            throw new InvalidOperationException(
                "The Task Server public demo policy is not deny-only for: " +
                string.Join(", ", policyFailures.Select(ExecutionAdmissionPolicy.PathName)));
        }

        var routes = new List<TaskServerPublicDemoExecutionRoute>();
        var missingExpectations = new List<string>();
        foreach (var endpoint in endpoints.DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>())
        {
            var execution = endpoint.Metadata.GetMetadata<TaskServerExecutionRouteMetadata>();
            if (execution is null) continue;

            var expectation = endpoint.Metadata.GetMetadata<TaskServerPublicDemoExpectationMetadata>();
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

            routes.AddRange(methods.Select(method => new TaskServerPublicDemoExecutionRoute(
                method,
                pattern,
                execution.Path,
                expectation.ExpectedCode)));
        }

        if (missingExpectations.Count > 0)
        {
            throw new InvalidOperationException(
                "Executable Task Server routes lack a public-demo expectation: " +
                string.Join(", ", missingExpectations.Order(StringComparer.Ordinal)));
        }

        var routeMatrixPaths = new[]
        {
            ExecutionAdmissionPath.Claim,
            ExecutionAdmissionPath.Start,
            ExecutionAdmissionPath.Continue,
            ExecutionAdmissionPath.Review,
            ExecutionAdmissionPath.Chat,
            ExecutionAdmissionPath.PostStep,
        };
        var missingPaths = routeMatrixPaths
            .Except(routes.Select(route => route.Path))
            .ToList();
        if (missingPaths.Count > 0)
        {
            throw new InvalidOperationException(
                "The Task Server public demo route matrix does not cover: " +
                string.Join(", ", missingPaths.Select(ExecutionAdmissionPolicy.PathName)));
        }

        return routes
            .OrderBy(route => route.Pattern, StringComparer.Ordinal)
            .ThenBy(route => route.Method, StringComparer.Ordinal)
            .ToList();
    }
}
