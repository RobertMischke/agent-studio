

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Tests for <see cref="PipelineStepModelDefaults"/>: the pre-run effective
/// model the Overview pipeline shows for each step. It must mirror the runtime
/// default each call site already passes to the resolver (aspect -> orchestrator
/// default, drift -> drift default, prep -> prep fallback) and layer the
/// per-project + per-step overrides over it via
/// <see cref="PipelineStepConfigResolver"/>, so what the operator sees before a
/// run is what the run would actually use.
/// </summary>
public class PipelineStepModelDefaultsTests
{
    private static PipelineStep Step(string id) =>
        PipelineCatalogue.Standard.AllSteps.First(s =>
            string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void DeterministicAndCoreSteps_ResolveNoModel()
    {
        Assert.Null(PipelineStepModelDefaults.Resolve(null, Step(PipelineCatalogue.CoreAgentRunStepId)));
        Assert.Null(PipelineStepModelDefaults.Resolve(null, Step(PipelineCatalogue.LoopGuardStepId)));
        Assert.Null(PipelineStepModelDefaults.Resolve(null, Step(PipelineCatalogue.LintScssStepId)));
        Assert.Null(PipelineStepModelDefaults.Resolve(null, Step(PipelineCatalogue.GitCommitAttributionStepId)));
    }

    [Fact]
    public void AspectStep_FallsBackToOrchestratorDefault_WhenNoOverride()
    {
        var r = PipelineStepModelDefaults.Resolve(null, Step("aspect-code-quality"));
        Assert.NotNull(r);
        Assert.Equal(OrchestratorRunner.DefaultModel, r!.Model);
        Assert.Equal(PipelineStepConfigResolver.ModelSourceRuntime, r.Source);
    }

    [Fact]
    public void DriftStep_FallsBackToDriftDefault_WhenNoOverride()
    {
        var r = PipelineStepModelDefaults.Resolve(null, Step(PipelineCatalogue.DriftAdrCodeStepId));
        Assert.NotNull(r);
        Assert.Equal(DriftPostStepRunner.DefaultModel, r!.Model);
    }

    [Fact]
    public void PrepStep_FallsBackToPrepFallback_WhenNoOverride()
    {
        var r = PipelineStepModelDefaults.Resolve(null, Step(PipelineCatalogue.PreOrchestratorPrepStepId));
        Assert.NotNull(r);
        Assert.Equal(OrchestratorPrepHostedService.PrepFallbackModel, r!.Model);
    }

    [Fact]
    public void ProjectModel_OverridesRuntimeDefault_ForAspect()
    {
        var settings = new ProjectSettings { OrchestratorModel = "claude-sonnet-4-6" };
        var r = PipelineStepModelDefaults.Resolve(settings, Step("aspect-code-quality"));
        Assert.NotNull(r);
        Assert.Equal("claude-sonnet-4-6", r!.Model);
        Assert.Equal(PipelineStepConfigResolver.ModelSourceProject, r.Source);
    }

    [Fact]
    public void StepOverride_WinsOverProjectModel()
    {
        var settings = new ProjectSettings
        {
            OrchestratorModel = "claude-sonnet-4-6",
            PipelineSteps = new Dictionary<string, PipelineStepSetting>
            {
                ["aspect-code-quality"] = new() { Model = "claude-opus-4-7" },
            },
        };
        var r = PipelineStepModelDefaults.Resolve(settings, Step("aspect-code-quality"));
        Assert.NotNull(r);
        Assert.Equal("claude-opus-4-7", r!.Model);
        Assert.Equal(PipelineStepConfigResolver.ModelSourceStep, r.Source);
    }
}
