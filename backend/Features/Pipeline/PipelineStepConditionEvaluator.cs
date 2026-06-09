using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Pipeline;

/// <summary>
/// Run-time facts about a finished task run that a
/// <see cref="PipelineStepCondition"/> is evaluated against. Populated by the
/// runner at the point a conditional step is about to fire (today: the
/// abort-review gate in <c>ProjectRunner</c>). Pure value type so the
/// evaluator stays trivially unit-testable.
/// </summary>
public readonly record struct PipelineStepConditionContext
{
    /// <summary>The run ended in an abort / notify-and-stop outcome.</summary>
    public bool Aborted { get; init; }

    /// <summary>The CLI process exit code, when known.</summary>
    public int? ExitCode { get; init; }

    /// <summary>At least one review aspect failed for this run.</summary>
    public bool AnyAspectFailed { get; init; }

    /// <summary>The task's structural type (see <see cref="TaskInfo.TaskType"/>).</summary>
    public string? TaskType { get; init; }

    /// <summary>The task's tags (see <see cref="TaskInfo.Tags"/>).</summary>
    public IReadOnlyCollection<string>? Tags { get; init; }
}

/// <summary>
/// Pure evaluator for <see cref="PipelineStepConditions"/>. Given a step's
/// configured condition and the facts of a finished run, decides whether the
/// step's condition is satisfied. Knows nothing about the enabled flag - the
/// caller (see <see cref="PipelineStepConfigResolver.ShouldRun(ProjectSettings?, PipelineStep, PipelineStepConditionContext)"/>)
/// combines this with enablement.
/// </summary>
public static class PipelineStepConditionEvaluator
{
    /// <summary>
    /// True when the condition is satisfied for the run. A null, blank, unknown
    /// or <see cref="PipelineStepConditions.Always"/> condition always matches;
    /// <see cref="PipelineStepConditions.Never"/> never matches. Value-bearing
    /// tokens (<c>task-type</c>, <c>tag</c>) with a missing value fail closed.
    /// </summary>
    public static bool Matches(PipelineStepCondition? condition, PipelineStepConditionContext ctx)
    {
        var when = PipelineStepConditions.Normalize(condition?.When);
        if (when == null || when == PipelineStepConditions.Always) return true;

        return when switch
        {
            PipelineStepConditions.Never => false,
            PipelineStepConditions.OnAbort => ctx.Aborted,
            PipelineStepConditions.OnNonzeroExit => ctx.ExitCode is int ec && ec != 0,
            PipelineStepConditions.OnAspectFail => ctx.AnyAspectFailed,
            PipelineStepConditions.TaskType => MatchesScalar(condition?.Value, ctx.TaskType),
            PipelineStepConditions.Tag => MatchesTag(condition?.Value, ctx.Tags),
            _ => true,
        };
    }

    private static bool MatchesScalar(string? expected, string? actual)
        => !string.IsNullOrWhiteSpace(expected)
           && !string.IsNullOrWhiteSpace(actual)
           && string.Equals(expected!.Trim(), actual!.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool MatchesTag(string? expected, IReadOnlyCollection<string>? tags)
    {
        if (string.IsNullOrWhiteSpace(expected) || tags == null || tags.Count == 0) return false;
        var want = expected.Trim();
        foreach (var tag in tags)
        {
            if (!string.IsNullOrWhiteSpace(tag) && string.Equals(tag.Trim(), want, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
