namespace AgentStudio.Runner;

/// <summary>
/// Selects the UI iteration pipeline without reimplementing UI classification.
/// The task signal always comes from <see cref="EvidenceGate.MatchesUiHeuristic"/>;
/// when a change set is available, <see cref="EvidenceGate.ChangeSetTouchesUi"/>
/// supplies the optional rendered-surface confirmation.
/// </summary>
public static class UiTaskPipelineRouter
{
    public static TaskPipeline Select(
        TaskInfo? task,
        ProjectSettings? settings,
        IReadOnlyCollection<string>? changedFiles = null)
    {
        if (task is null) return Pipeline.PipelineCatalogue.Standard;
        if (TaskModes.IsReadOnly(task.Mode)) return Pipeline.PipelineCatalogue.ReadOnly;

        var routingStep = Pipeline.PipelineCatalogue.UiIteration.Pre.First(step =>
            string.Equals(step.Id, Pipeline.PipelineCatalogue.UiPipelineRoutingStepId, StringComparison.Ordinal));
        if (!Pipeline.PipelineStepConfigResolver.IsEnabled(settings, routingStep))
            return Pipeline.PipelineCatalogue.Standard;

        var matchesTask = EvidenceGate.MatchesUiHeuristic(task.TaskType, task.Tags, task.Title);
        var matchesChangeSet = changedFiles is null || EvidenceGate.ChangeSetTouchesUi(changedFiles);
        return matchesTask && matchesChangeSet
            ? Pipeline.PipelineCatalogue.UiIteration
            : Pipeline.PipelineCatalogue.Standard;
    }
}
