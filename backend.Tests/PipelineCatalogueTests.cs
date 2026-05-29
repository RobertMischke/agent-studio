using OrchestratorApi.Models;
using OrchestratorApi.Services.Pipeline;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Pin the Phase-1 <c>standard-task-pipeline</c> definition: the
/// catalogue stays in sync with <see cref="AspectRunnerService.Catalogue"/>,
/// the four aspect post-steps are marked parallel, the
/// git-commit-attribution slot is a stub the follow-up task will fill,
/// and the orchestrator-decision step depends on the aspect steps so a
/// DAG-resolver cannot run it ahead of its inputs.
/// </summary>
public class PipelineCatalogueTests
{
    [Fact]
    public void StandardPipeline_HasExpectedSections()
    {
        var p = PipelineCatalogue.Standard;
        Assert.Equal(PipelineCatalogue.StandardPipelineId, p.Id);
        Assert.Equal(1, p.Version);
        Assert.Empty(p.Pre); // Phase 1 ships no pre-steps; reserved slot.
        Assert.Single(p.Core);
        Assert.Equal(PipelineCatalogue.CoreAgentRunStepId, p.Core[0].Id);
        Assert.Equal(StepKind.Core, p.Core[0].Kind);
        Assert.False(p.Core[0].Idempotent); // Core agent runs are not safe to re-run blindly.

        // Post: 4 aspects + commit-attribution stub + lint-scss + orchestrator decision.
        Assert.Equal(7, p.Post.Count);
    }

    [Fact]
    public void StandardPipeline_AllFourAspectStepsRunParallel()
    {
        var p = PipelineCatalogue.Standard;
        var aspectSteps = p.Post.Where(s => s.Kind == StepKind.Aspect).ToList();
        Assert.Equal(4, aspectSteps.Count);
        foreach (var step in aspectSteps)
        {
            Assert.Equal(StepRunMode.Parallel, step.RunMode);
            Assert.True(step.Idempotent, $"{step.Id} aspect must be idempotent");
            // Aspect step ids on the pipeline are namespaced with the
            // `aspect-` prefix so they cannot collide with non-aspect ids;
            // strip the prefix to look up the underlying AspectDefinition.
            var bareId = step.Id.Substring("aspect-".Length);
            Assert.True(AspectRunnerService.Catalogue.ContainsKey(bareId),
                $"pipeline aspect step '{step.Id}' has no entry in AspectRunnerService.Catalogue");
        }
    }

    [Fact]
    public void StandardPipeline_GitCommitAttributionIsStub_WithAspectDependencies()
    {
        var p = PipelineCatalogue.Standard;
        var commitStep = p.Post.First(s => s.Id == PipelineCatalogue.GitCommitAttributionStepId);
        Assert.Equal(StepKind.Tool, commitStep.Kind);
        Assert.True(commitStep.Stub);
        Assert.True(commitStep.Idempotent);
        // Commit attribution reads aspect MDs, so it depends on every aspect.
        foreach (var aspectId in PipelineCatalogue.AspectStepIds)
        {
            Assert.Contains(aspectId, commitStep.DependsOn);
        }
    }

    [Fact]
    public void StandardPipeline_LintScss_IsToolStep_WithAspectDependencies_AndNotAStub()
    {
        // ASS-563: lint-scss must run after the aspect verdicts (so the
        // pre-existing aspect work is not wasted by an early lint reissue)
        // but before the orchestrator decision (so a fail verdict can
        // route through the reissue path instead of accept-as-done).
        // Unlike commit-attribution, this step is implemented today.
        var p = PipelineCatalogue.Standard;
        var lintStep = p.Post.First(s => s.Id == PipelineCatalogue.LintScssStepId);
        Assert.Equal(StepKind.Tool, lintStep.Kind);
        Assert.False(lintStep.Stub);
        Assert.True(lintStep.Idempotent);
        foreach (var aspectId in PipelineCatalogue.AspectStepIds)
        {
            Assert.Contains(aspectId, lintStep.DependsOn);
        }

        // The orchestrator-decision step must come AFTER lint-scss in the
        // Post list, so a deterministic executor runs lint first and the
        // decision step sees its verdict.
        var lintIndex = p.Post.FindIndex(s => s.Id == PipelineCatalogue.LintScssStepId);
        var decisionIndex = p.Post.FindIndex(s => s.Id == PipelineCatalogue.OrchestratorDecisionStepId);
        Assert.True(lintIndex < decisionIndex,
            $"lint-scss (idx {lintIndex}) must precede orchestrator-decision (idx {decisionIndex}) in Post list");
    }

    [Fact]
    public void StandardPipeline_OrchestratorDecision_DependsOnAllAspects()
    {
        var p = PipelineCatalogue.Standard;
        var decisionStep = p.Post.First(s => s.Id == PipelineCatalogue.OrchestratorDecisionStepId);
        Assert.Equal(StepKind.Orchestrator, decisionStep.Kind);
        Assert.True(decisionStep.Idempotent);
        foreach (var aspectId in PipelineCatalogue.AspectStepIds)
        {
            Assert.Contains(aspectId, decisionStep.DependsOn);
        }
    }

    [Fact]
    public void Get_ReturnsStandardForCanonicalId_NullForUnknown()
    {
        Assert.NotNull(PipelineCatalogue.Get(PipelineCatalogue.StandardPipelineId));
        Assert.NotNull(PipelineCatalogue.Get("STANDARD-TASK-PIPELINE")); // case-insensitive
        Assert.Null(PipelineCatalogue.Get("does-not-exist"));
    }

    [Fact]
    public void AspectStepIds_MatchAspectRunnerCatalogueOneToOne()
    {
        // The four pipeline aspect step ids must each map to an entry in
        // AspectRunnerService.Catalogue (with the "aspect-" prefix stripped).
        // Without this guard the pipeline could list aspects the runner
        // does not know how to execute (silent skip + missing aspect MD).
        foreach (var stepId in PipelineCatalogue.AspectStepIds)
        {
            Assert.StartsWith("aspect-", stepId);
            var bareId = stepId.Substring("aspect-".Length);
            Assert.True(AspectRunnerService.Catalogue.ContainsKey(bareId),
                $"AspectStepIds entry '{stepId}' has no underlying AspectDefinition");
        }
        // The reverse direction: every aspect definition the runner knows
        // about should appear on the pipeline. Drift here means the
        // pipeline view would miss an aspect that actually runs.
        foreach (var bareId in AspectRunnerService.Catalogue.Keys)
        {
            var pipelineStepId = $"aspect-{bareId}";
            Assert.Contains(pipelineStepId, PipelineCatalogue.AspectStepIds);
        }
    }
}
