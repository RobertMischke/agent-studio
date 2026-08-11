using System.Globalization;
using System.Text.Json;
using AgentStudio.Shared;

namespace AgentStudio.Runner;

/// <summary>
/// Builds the compact unified-ledger rows around one agent run. Keeping this
/// projection pure makes the no-defaults and no-duplicate-copy contract
/// testable without constructing a full project runner.
/// </summary>
internal static class RunTimelineEventFactory
{
    public static TimelineEvent AgentRunFinished(CliExecution execution, string? runId)
    {
        var status = execution.Status ?? "unknown";
        var details = new Dictionary<string, string>
        {
            ["status"] = status,
        };
        if (execution.DurationSeconds is double duration)
            details["durationSeconds"] = duration.ToString("0.0", CultureInfo.InvariantCulture);

        return new TimelineEvent
        {
            Ts = DateTime.UtcNow,
            Kind = TimelineEventKinds.AgentRunFinished,
            Actor = TimelineActors.Agent,
            RunId = runId,
            Summary = $"Run {status}" +
                      (execution.DurationSeconds is double seconds
                          ? $" after {seconds.ToString("F1", CultureInfo.InvariantCulture)}s"
                          : ""),
            Details = details,
        };
    }

    public static (CliExecutionContext Context, TimelineEvent Event) ExecutionContext(
        string cliType,
        CliExecution execution,
        CliExecutionContext described,
        string? runId)
    {
        var context = described with
        {
            Model = string.IsNullOrWhiteSpace(described.Model) ? execution.Model : described.Model,
            ThinkingLevel = execution.ThinkingLevel,
        };
        var details = new Dictionary<string, string>
        {
            ["cli"] = cliType,
            ["source"] = context.Source,
            ["sources"] = context.Sources.Count.ToString(CultureInfo.InvariantCulture),
            ["model"] = context.Model ?? string.Empty,
            ["thinkingLevel"] = context.ThinkingLevel ?? string.Empty,
            ["sourceItems"] = JsonSerializer.Serialize(context.Sources.Select(source => new
            {
                kind = source.Kind,
                label = source.Label,
                path = source.Path,
                exists = source.Exists,
                detail = source.Detail,
            })),
        };
        var mcpCount = context.Sources.Count(source => source.Kind == CliContextSourceKinds.Mcp);
        if (mcpCount > 0)
            details["mcp"] = mcpCount.ToString(CultureInfo.InvariantCulture);

        return (context, new TimelineEvent
        {
            Ts = DateTime.UtcNow,
            Kind = TimelineEventKinds.ExecutionContext,
            Actor = TimelineActors.System,
            RunId = runId,
            Summary = "Execution context:" +
                      (string.IsNullOrWhiteSpace(context.Model) ? "" : $" model {context.Model},") +
                      (string.IsNullOrWhiteSpace(context.ThinkingLevel) ? "" : $" thinking {context.ThinkingLevel},") +
                      $" {context.Sources.Count} sources",
            Details = details,
        });
    }
}
