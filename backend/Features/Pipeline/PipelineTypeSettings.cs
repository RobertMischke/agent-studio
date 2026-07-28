namespace AgentStudio.Pipeline;

/// <summary>
/// Selects the project overrides that apply to one pipeline type while
/// preserving the rest of the project settings. Runtime consumers can keep
/// using the established flat step resolver after making this one explicit
/// type selection.
/// </summary>
public static class PipelineTypeSettings
{
    public static ProjectSettings? ForTask(ProjectSettings? settings, TaskInfo task) =>
        ForType(settings, PipelineTypes.Resolve(task));

    public static ProjectSettings? ForType(ProjectSettings? settings, string? pipelineType)
    {
        if (settings is null) return null;
        var type = PipelineTypes.Normalize(pipelineType);
        var legacyApplies = type != PipelineTypes.Planning;
        var steps = TryGet(settings.PipelineStepsByType, type)
            ?? (legacyApplies ? settings.PipelineSteps : null);
        var order = TryGet(settings.PipelineStepOrderByType, type)
            ?? (legacyApplies ? settings.PipelineStepOrder : null);
        return settings with
        {
            PipelineSteps = steps,
            PipelineStepOrder = order,
        };
    }

    private static TValue? TryGet<TValue>(
        IReadOnlyDictionary<string, TValue>? map,
        string type)
        where TValue : class
    {
        if (map is null) return null;
        foreach (var entry in map)
            if (string.Equals(entry.Key, type, StringComparison.OrdinalIgnoreCase))
                return entry.Value;
        return null;
    }
}
