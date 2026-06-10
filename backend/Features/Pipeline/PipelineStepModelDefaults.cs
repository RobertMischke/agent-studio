

namespace AgentStudio.Pipeline;

/// <summary>
/// Maps a catalogue <see cref="PipelineStep"/> to the runtime-default model its
/// LLM-backed work falls back to when neither a per-step nor a per-project
/// override is set. It mirrors the exact constant each runtime call site already
/// passes to <see cref="PipelineStepConfigResolver.ResolveModel(ProjectSettings?, PipelineStep, string, string?)"/>,
/// so the pre-run pipeline view can show the same effective model the run would
/// actually use: aspect verdicts fall back to the orchestrator default
/// (<see cref="OrchestratorRunner.DefaultModel"/>), drift dimensions to the drift
/// default (<see cref="DriftPostStepRunner.DefaultModel"/>), the opt-in prep pass
/// to its fallback (<see cref="OrchestratorPrepHostedService.PrepFallbackModel"/>).
///
/// <para>
/// Deterministic steps (loop guard, reissue check, git-commit attribution,
/// lint-scss, regression radar, the orchestrator gate rows) and the core agent
/// run do not resolve a per-step LLM model through the resolver - the core run
/// uses the task's own model, the gate rows are policy code - so they return
/// null here and the pre-run view shows no resolved model for them.
/// </para>
/// </summary>
public static class PipelineStepModelDefaults
{
    /// <summary>
    /// The runtime-default model for a step, or null when the step does not
    /// resolve a per-step LLM model through <see cref="PipelineStepConfigResolver"/>.
    /// </summary>
    public static string? RuntimeDefaultFor(PipelineStep step) => step.Kind switch
    {
        StepKind.Aspect => OrchestratorRunner.DefaultModel,
        StepKind.Drift => DriftPostStepRunner.DefaultModel,
        StepKind.Module when string.Equals(
            step.Id, PipelineCatalogue.PreOrchestratorPrepStepId, StringComparison.OrdinalIgnoreCase)
            => OrchestratorPrepHostedService.PrepFallbackModel,
        _ => null,
    };

    /// <summary>True when the step resolves a per-step LLM model pre-run.</summary>
    public static bool UsesModel(PipelineStep step) => RuntimeDefaultFor(step) is not null;

    /// <summary>
    /// Resolve the effective model a step would run with right now (before any
    /// run), layering the per-project + per-step overrides over the step's
    /// runtime default via <see cref="PipelineStepConfigResolver"/>. Returns null
    /// when the step resolves no per-step LLM model (see the class summary).
    /// </summary>
    public static PipelineStepConfigResolver.ModelResolution? Resolve(
        ProjectSettings? settings, PipelineStep step)
    {
        var runtimeDefault = RuntimeDefaultFor(step);
        if (runtimeDefault is null) return null;
        return PipelineStepConfigResolver.ResolveModelWithSource(settings, step, runtimeDefault);
    }
}
