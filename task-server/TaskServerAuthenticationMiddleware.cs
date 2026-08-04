using System.Security.Cryptography;
using System.Text;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;

namespace AgentStudio.TaskServer;

public sealed class TaskServerAuthenticationMiddleware(
    RequestDelegate next,
    TaskServerBootstrapOptions bootstrap,
    IOptions<TaskServerOptions> configuredOptions)
{
    private readonly TaskServerOptions _configuredOptions = configuredOptions.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!bootstrap.RequiresAuthentication
            || !context.Request.Path.StartsWithSegments("/api/v1")
            || IsOpenPath(context.Request.Path))
        {
            await next(context);
            return;
        }

        var authorization = context.Request.Headers.Authorization.FirstOrDefault();
        const string prefix = "Bearer ";
        var presented = authorization?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true
            ? authorization[prefix.Length..].Trim()
            : null;
        var accepted = bootstrap.UsesLegacyRoleAuthentication
            ? AcceptsLegacyRoleCredential(context.Request, presented)
            : Matches(presented, bootstrap.AuthenticationToken);
        if (!accepted)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            var runnerMutation = IsRunnerMutation(context.Request);
            await context.Response.WriteAsJsonAsync(new ApiError(
                "authentication-required",
                bootstrap.UsesLegacyRoleAuthentication && runnerMutation
                    ? "A valid Runner service credential is required."
                    : bootstrap.UsesLegacyRoleAuthentication
                        ? "A valid Agent Studio credential is required."
                        : "A valid Task Server bearer credential is required."));
            return;
        }

        await next(context);
    }

    private static bool IsOpenPath(PathString path)
        => path.StartsWithSegments("/healthz")
           || path.StartsWithSegments("/readyz")
           || path.Equals("/api/v1/protocol", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/api/v1/protocol/compatibility", StringComparison.OrdinalIgnoreCase);

    private bool AcceptsLegacyRoleCredential(
        HttpRequest request,
        string? presented)
    {
        var studioToken = _configuredOptions.StudioBearerToken;
        var runnerToken = _configuredOptions.RunnerBearerToken;
        if (string.IsNullOrWhiteSpace(studioToken)
            || string.IsNullOrWhiteSpace(runnerToken))
        {
            throw new InvalidOperationException(
                "Legacy authenticated mode requires separate StudioBearerToken and RunnerBearerToken values.");
        }
        if (Matches(studioToken, runnerToken))
        {
            throw new InvalidOperationException(
                "StudioBearerToken and RunnerBearerToken must be distinct credentials.");
        }
        return IsRunnerMutation(request)
            ? Matches(presented, runnerToken)
            : Matches(presented, studioToken);
    }

    private static bool IsRunnerMutation(HttpRequest request)
        => !HttpMethods.IsGet(request.Method)
           && (request.Path.StartsWithSegments("/api/v1/runners")
               || request.Path.StartsWithSegments("/api/v1/runs")
               || request.Path.StartsWithSegments("/api/v1/work-permits"));

    private static bool Matches(string? presented, string? expected)
    {
        if (presented is null || expected is null) return false;
        var presentedBytes = Encoding.UTF8.GetBytes(presented);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return presentedBytes.Length == expectedBytes.Length
               && CryptographicOperations.FixedTimeEquals(presentedBytes, expectedBytes);
    }
}
