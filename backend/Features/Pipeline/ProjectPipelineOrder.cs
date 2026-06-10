
namespace AgentStudio.Pipeline;

/// <summary>
/// Applies the per-project pipeline-step order stored in
/// <see cref="ProjectSettings.PipelineStepOrder"/>. Ordering is intentionally
/// bounded to pre/post sections: the core agent run remains fixed, and unknown
/// or newly introduced catalogue steps append in catalogue order.
/// </summary>
public static class ProjectPipelineOrder
{
    public static TaskPipeline Apply(TaskPipeline pipeline, ProjectSettings? settings)
    {
        var order = settings?.PipelineStepOrder;
        if (order == null || order.Count == 0) return pipeline;

        return pipeline with
        {
            Pre = SortSection(pipeline.Pre, order),
            Post = SortSection(pipeline.Post, order),
        };
    }

    public static IReadOnlyList<PipelineStep> SortSteps(
        IReadOnlyList<PipelineStep> steps,
        IReadOnlyList<string>? order)
    {
        if (order == null || order.Count == 0) return steps;
        return SortSection(steps, order);
    }

    private static List<PipelineStep> SortSection(
        IReadOnlyList<PipelineStep> steps,
        IReadOnlyList<string> order)
    {
        if (steps.Count <= 1) return steps.ToList();

        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < order.Count; i++)
        {
            var id = order[i];
            if (string.IsNullOrWhiteSpace(id)) continue;
            rank.TryAdd(id.Trim(), rank.Count);
        }

        if (rank.Count == 0) return steps.ToList();

        return steps
            .Select((step, index) => new
            {
                Step = step,
                Index = index,
                Rank = rank.TryGetValue(step.Id, out var r) ? r : int.MaxValue,
            })
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Index)
            .Select(x => x.Step)
            .ToList();
    }
}
