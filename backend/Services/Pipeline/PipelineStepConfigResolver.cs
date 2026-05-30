using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Pipeline;

/// <summary>
/// Pure resolver for the per-project pipeline-step overrides stored in
/// <see cref="ProjectSettings.PipelineSteps"/>. It is the single place
/// that turns "what did the operator configure for this project" into the
/// concrete <c>enabled</c> / <c>model</c> / <c>mode</c> a step runs with.
///
/// <para>
/// Lookup is tolerant: a step can be addressed by its full pipeline id
/// (<c>aspect-code-quality</c>, <c>post-lint-scss</c>) or by the bare
/// suffix (<c>code-quality</c>, <c>lint-scss</c>) so per-project config
/// stays terse, matching the same convenience the per-task
/// <see cref="PostStepConfigResolver"/> already offers. All lookups are
/// case-insensitive.
/// </para>
///
/// <para>
/// Resolution order:
/// <list type="bullet">
///   <item><c>enabled</c>: step override -&gt; <c>true</c> (steps run by default).</item>
///   <item><c>model</c>: step override -&gt; the step's own catalogue
///         <see cref="PipelineStep.Model"/> -&gt; project
///         <see cref="ProjectSettings.OrchestratorModel"/> -&gt; the
///         caller-supplied runtime default.</item>
///   <item><c>mode</c>: step override -&gt; caller-supplied built-in default.</item>
/// </list>
/// </para>
/// </summary>
public static class PipelineStepConfigResolver
{
    /// <summary>Prefixes the catalogue uses; stripped to support bare-suffix lookup.</summary>
    private static readonly string[] StepIdPrefixes = { "aspect-", "post-", "pre-" };

    /// <summary>
    /// Resolve the per-step override for a given step id, accepting either
    /// the full pipeline id or the bare suffix. Returns null when the
    /// project has no override for the step.
    /// </summary>
    public static PipelineStepSetting? Lookup(ProjectSettings? settings, string stepId)
    {
        var map = settings?.PipelineSteps;
        if (map == null || map.Count == 0 || string.IsNullOrWhiteSpace(stepId)) return null;

        if (TryGet(map, stepId, out var direct)) return direct;

        // full id -> bare suffix (e.g. "aspect-code-quality" -> "code-quality")
        foreach (var prefix in StepIdPrefixes)
        {
            if (stepId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var bare = stepId.Substring(prefix.Length);
                if (TryGet(map, bare, out var bareHit)) return bareHit;
            }
        }

        // bare suffix -> full id (e.g. "code-quality" -> "aspect-code-quality")
        foreach (var prefix in StepIdPrefixes)
        {
            if (TryGet(map, prefix + stepId, out var prefixedHit)) return prefixedHit;
        }

        return null;
    }

    /// <summary>True unless the project explicitly disabled the step.</summary>
    public static bool IsEnabled(ProjectSettings? settings, string stepId)
        => Lookup(settings, stepId)?.Enabled ?? true;

    /// <summary>
    /// Resolve the model for a step addressed by id, with no catalogue
    /// step in hand (the aspect runner only knows the bare aspect id).
    /// </summary>
    public static string ResolveModel(ProjectSettings? settings, string stepId, string runtimeDefault)
    {
        var stepModel = Lookup(settings, stepId)?.Model;
        if (!string.IsNullOrWhiteSpace(stepModel)) return stepModel!.Trim();
        var projectModel = settings?.OrchestratorModel;
        if (!string.IsNullOrWhiteSpace(projectModel)) return projectModel!.Trim();
        return runtimeDefault;
    }

    /// <summary>
    /// Resolve the model for a catalogue step, layering the step's own
    /// default <see cref="PipelineStep.Model"/> between the project
    /// override and the project orchestrator model.
    /// </summary>
    public static string ResolveModel(ProjectSettings? settings, PipelineStep step, string runtimeDefault)
    {
        var stepOverride = Lookup(settings, step.Id)?.Model;
        if (!string.IsNullOrWhiteSpace(stepOverride)) return stepOverride!.Trim();
        if (!string.IsNullOrWhiteSpace(step.Model)) return step.Model!.Trim();
        var projectModel = settings?.OrchestratorModel;
        if (!string.IsNullOrWhiteSpace(projectModel)) return projectModel!.Trim();
        return runtimeDefault;
    }

    /// <summary>
    /// Resolve the gate mode for a step. Project override wins; otherwise
    /// the caller-supplied built-in default (today
    /// <see cref="PostStepConfigResolver.BuiltInDefault"/>).
    /// </summary>
    public static PostStepMode ResolveMode(ProjectSettings? settings, string stepId, PostStepMode builtInDefault)
    {
        var raw = Lookup(settings, stepId)?.Mode;
        return PostStepConfigResolver.ParseMode(raw) ?? builtInDefault;
    }

    private static bool TryGet(IReadOnlyDictionary<string, PipelineStepSetting> map, string key, out PipelineStepSetting? value)
    {
        // The map is deserialized from project-settings.json with default
        // (ordinal) comparison, so do an explicit case-insensitive scan
        // rather than trusting the dictionary's comparer.
        foreach (var kv in map)
        {
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = kv.Value;
                return true;
            }
        }
        value = null;
        return false;
    }
}
