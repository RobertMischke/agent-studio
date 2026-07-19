namespace AgentStudio.Pipeline;

/// <summary>
/// Backend-owned explanation of a task's effective post-step activation. The
/// frontend renders these facts verbatim instead of re-deriving catalogue,
/// project-override, and run-condition precedence.
/// </summary>
public sealed record PostStepActivation(
    string State,
    string Source,
    string Reason);

public static class PostStepActivationProjection
{
    public const string Active = "active";
    public const string Inactive = "inactive";
    public const string Skipped = "skipped";
    public const string GlobalSource = "global";
    public const string ProjectSource = "project";
    public const string ConditionSource = "condition";

    public static PostStepActivation Build(
        PipelineStep step,
        PipelineStepSetting? configured,
        PipelineStepExecution? execution,
        PipelineExecutionRecord? record,
        TaskInfo task)
    {
        var hasEnabledOverride = configured?.Enabled.HasValue == true;
        var source = hasEnabledOverride ? ProjectSource : GlobalSource;
        var enabled = configured?.Enabled ?? step.DefaultEnabled;
        if (!enabled)
        {
            var reason = hasEnabledOverride
                ? "Disabled by the project override."
                : "Disabled by the global catalogue default.";
            return new PostStepActivation(Inactive, source, reason);
        }

        var condition = configured?.Condition;
        var conditionToken = PipelineStepConditions.Normalize(condition?.When);
        var hasCondition = conditionToken is not null and not PipelineStepConditions.Always;
        if (execution?.Status == PipelineStepStatus.Skipped)
        {
            if (hasCondition)
            {
                return new PostStepActivation(
                    Skipped,
                    ConditionSource,
                    PreciseSkipReason(execution.Reason, condition!));
            }

            return new PostStepActivation(
                Skipped,
                source,
                string.IsNullOrWhiteSpace(execution.Reason)
                    ? "The enabled step was skipped by the runtime."
                    : execution.Reason!);
        }

        if (hasCondition && TryEvaluate(condition!, record, task, out var matched) && !matched)
        {
            return new PostStepActivation(
                Skipped,
                ConditionSource,
                $"Condition \"{DescribeCondition(condition!)}\" does not match this task run.");
        }

        if (hasCondition)
        {
            var verb = execution is { Status: not PipelineStepStatus.Pending }
                ? "matched this run"
                : "controls whether the step runs";
            return new PostStepActivation(
                Active,
                ConditionSource,
                $"Enabled by {SourceLabel(source)}; condition \"{DescribeCondition(condition!)}\" {verb}.");
        }

        return new PostStepActivation(
            Active,
            source,
            hasEnabledOverride
                ? "Enabled by the project override."
                : "Enabled by the global catalogue default.");
    }

    private static bool TryEvaluate(
        PipelineStepCondition condition,
        PipelineExecutionRecord? record,
        TaskInfo task,
        out bool matched)
    {
        var token = PipelineStepConditions.Normalize(condition.When);
        var context = new PipelineStepConditionContext
        {
            AnyAspectFailed = record?.Steps.Any(step =>
                step.Kind == StepKind.Aspect && step.Status == PipelineStepStatus.Failed) == true,
            TaskType = task.TaskType,
            Tags = task.Tags,
        };
        switch (token)
        {
            case PipelineStepConditions.Never:
            case PipelineStepConditions.TaskType:
            case PipelineStepConditions.Tag:
                matched = PipelineStepConditionEvaluator.Matches(condition, context);
                return true;
            case PipelineStepConditions.OnAspectFail when record?.IsComplete == true:
                matched = PipelineStepConditionEvaluator.Matches(condition, context);
                return true;
            default:
                matched = false;
                return false;
        }
    }

    private static string PreciseSkipReason(string? recordedReason, PipelineStepCondition condition)
    {
        if (!string.IsNullOrWhiteSpace(recordedReason)
            && !recordedReason.Contains("disabled by config or condition", StringComparison.OrdinalIgnoreCase))
        {
            return recordedReason;
        }

        return $"Condition \"{DescribeCondition(condition)}\" did not match this run.";
    }

    private static string SourceLabel(string source) =>
        source == ProjectSource ? "the project override" : "the global catalogue default";

    internal static string DescribeCondition(PipelineStepCondition condition)
    {
        var token = PipelineStepConditions.Normalize(condition.When);
        return token switch
        {
            PipelineStepConditions.Never => "never run",
            PipelineStepConditions.OnAbort => "run ended in abort",
            PipelineStepConditions.OnNonzeroExit => "CORE exited non-zero",
            PipelineStepConditions.OnAspectFail => "an aspect failed",
            PipelineStepConditions.TaskType => $"task type is '{condition.Value?.Trim() ?? "(missing)"}'",
            PipelineStepConditions.Tag => $"task has tag '{condition.Value?.Trim() ?? "(missing)"}'",
            _ => "always run",
        };
    }
}
