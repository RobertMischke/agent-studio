using System.Diagnostics;
using System.Globalization;

namespace OrchestratorApi.Endpoints.Tasks;

/// <summary>
/// Adds lightweight timing around task API operations. The 30 ms budget is
/// enforced by perf benchmarks; this filter makes production regressions
/// visible through Server-Timing and structured logs without changing the
/// endpoint contracts.
/// </summary>
internal sealed class TaskOperationTimingFilter : IEndpointFilter
{
    public const double BudgetMs = 30.0;

    private readonly ILogger<TaskOperationTimingFilter> _logger;

    public TaskOperationTimingFilter(ILogger<TaskOperationTimingFilter> logger)
    {
        _logger = logger;
    }

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            return await next(context);
        }
        finally
        {
            sw.Stop();
            var http = context.HttpContext;
            var operation = OperationName(http);
            var elapsedMs = sw.Elapsed.TotalMilliseconds;

            if (!http.Response.HasStarted)
            {
                http.Response.Headers.Append(
                    "Server-Timing",
                    "task-op;dur=" + elapsedMs.ToString("0.###", CultureInfo.InvariantCulture));
            }

            var exceeded = elapsedMs > BudgetMs;
            if (exceeded)
            {
                _logger.LogWarning(
                    "task-operation-timing operation={Operation} method={Method} path={Path} elapsedMs={ElapsedMs:0.###} budgetMs={BudgetMs:0.###} exceeded=true statusCode={StatusCode}",
                    operation,
                    http.Request.Method,
                    http.Request.Path.Value,
                    elapsedMs,
                    BudgetMs,
                    http.Response.StatusCode);
            }
            else
            {
                _logger.LogDebug(
                    "task-operation-timing operation={Operation} method={Method} path={Path} elapsedMs={ElapsedMs:0.###} budgetMs={BudgetMs:0.###} exceeded=false statusCode={StatusCode}",
                    operation,
                    http.Request.Method,
                    http.Request.Path.Value,
                    elapsedMs,
                    BudgetMs,
                    http.Response.StatusCode);
            }
        }
    }

    private static string OperationName(HttpContext http)
    {
        var pattern = (http.GetEndpoint() as Microsoft.AspNetCore.Routing.RouteEndpoint)
            ?.RoutePattern.RawText;
        return string.IsNullOrWhiteSpace(pattern)
            ? $"{http.Request.Method} {http.Request.Path.Value}"
            : $"{http.Request.Method} {pattern}";
    }
}
