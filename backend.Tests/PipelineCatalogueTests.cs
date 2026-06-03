using OrchestratorApi.Models;
using OrchestratorApi.Services.Pipeline;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Pin the Phase-1 <c>standard-task-pipeline</c> definition: the
/// catalogue stays in sync with <see cref="AspectRunnerService.Catalogue"/>,
/// the four aspect post-steps are marked parallel, the
/// git-commit-attribution step is implemented (ADR
/// "Commit-Attribution-Regel") and ordered before the decision, and the
/// orchestrator-decision step depends on the aspect steps so a
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
        // Pre: the auto-mode loop guard (Ralph-loop early detection) leads,
        // followed by the opt-in orchestrator-prep step that replaced the
        // standalone 1a-orchestrator-prep backlog lane, then the deterministic
        // reissue open-items check that foregrounds leftover items on a re-issue.
        Assert.Equal(3, p.Pre.Count);
        Assert.Equal(PipelineCatalogue.LoopGuardStepId, p.Pre[0].Id);
        Assert.Equal(PipelineCatalogue.PreOrchestratorPrepStepId, p.Pre[1].Id);
        Assert.Equal(PipelineCatalogue.PreReissueOpenItemsStepId, p.Pre[2].Id);
        Assert.Single(p.Core);
        Assert.Equal(PipelineCatalogue.CoreAgentRunStepId, p.Core[0].Id);
        Assert.Equal(StepKind.Core, p.Core[0].Kind);
        Assert.False(p.Core[0].Idempotent); // Core agent runs are not safe to re-run blindly.

        // Post: 4 aspects + commit-attribution + lint-scss + regression-radar
        // + orchestrator decision + 5 opt-in drift dimensions (DRIFT Nachtrag).
        Assert.Equal(13, p.Post.Count);
    }

    [Fact]
    public void StandardPipeline_LoopGuard_IsFirstStep_Deterministic_AndDefaultOn()
    {
        // Ralph-loop early detection: the loop guard ships as the single Pre
        // step so it leads AllSteps and the Overview renders it as the first
        // row ("frueh markiert"). It is deterministic (no model) and on by
        // default - the StuckLoopGuard breaker is a safety net, not opt-in.
        var p = PipelineCatalogue.Standard;
        var guard = p.Pre[0];
        Assert.Equal(PipelineCatalogue.LoopGuardStepId, guard.Id);
        Assert.Equal(StepKind.Module, guard.Kind);
        Assert.True(guard.Idempotent);
        Assert.True(guard.DefaultEnabled);
        Assert.Null(guard.Model);
        Assert.Equal(PipelineCatalogue.LoopGuardStepId, p.AllSteps.First().Id);
    }

    [Fact]
    public void StandardPipeline_OrchestratorPrep_IsOptInParallelPreStep_ModelResolvedPerProject()
    {
        // ARCH: orchestrator-prep moved out of the 1a-orchestrator-prep backlog
        // lane into an optional, parallelisable pre-coding pipeline step. It is
        // a deterministic Module (no LLM today) that defaults OFF (opt-in per
        // project) and carries no hardcoded model, so the runtime resolves the
        // project's selected model via PipelineStepConfigResolver.
        var p = PipelineCatalogue.Standard;
        var prep = p.Pre.First(s => s.Id == PipelineCatalogue.PreOrchestratorPrepStepId);
        Assert.Equal(StepKind.Module, prep.Kind);
        Assert.Equal(StepRunMode.Parallel, prep.RunMode); // must not block throughput
        Assert.True(prep.Idempotent);
        Assert.False(prep.DefaultEnabled); // opt-in
        Assert.Null(prep.Model); // resolved per project, not hardcoded

        // Project model selection flows through when the step itself sets none.
        var settings = new ProjectSettings { OrchestratorModel = "claude-sonnet-4-5" };
        Assert.Equal(
            "claude-sonnet-4-5",
            PipelineStepConfigResolver.ResolveModel(settings, prep, "fallback-model"));
        // A per-step override still wins over the project model.
        var withOverride = new ProjectSettings
        {
            OrchestratorModel = "claude-sonnet-4-5",
            PipelineSteps = new Dictionary<string, PipelineStepSetting>
            {
                [PipelineCatalogue.PreOrchestratorPrepStepId] = new() { Enabled = true, Model = "claude-opus-4-1" },
            },
        };
        Assert.True(PipelineStepConfigResolver.IsEnabled(withOverride, prep));
        Assert.Equal(
            "claude-opus-4-1",
            PipelineStepConfigResolver.ResolveModel(withOverride, prep, "fallback-model"));
    }

    [Fact]
    public void StandardPipeline_ReissueOpenItems_IsDeterministicDefaultOnPreStep_AfterPrep()
    {
        // The reissue open-items check ships as a deterministic (no model)
        // Module pre-step that defaults ON - an unfinished re-issue is a
        // correctness signal, not an opt-in pass. It runs after the loop guard
        // and orchestrator-prep so it leads into the core run.
        var p = PipelineCatalogue.Standard;
        var step = p.Pre.First(s => s.Id == PipelineCatalogue.PreReissueOpenItemsStepId);
        Assert.Equal(StepKind.Module, step.Kind);
        Assert.Equal(StepRunMode.Sequential, step.RunMode);
        Assert.True(step.Idempotent);
        Assert.True(step.DefaultEnabled);
        Assert.Null(step.Model);

        var prepIndex = p.Pre.FindIndex(s => s.Id == PipelineCatalogue.PreOrchestratorPrepStepId);
        var reissueIndex = p.Pre.FindIndex(s => s.Id == PipelineCatalogue.PreReissueOpenItemsStepId);
        Assert.True(reissueIndex > prepIndex,
            $"reissue open-items (idx {reissueIndex}) must come after orchestrator-prep (idx {prepIndex})");
    }

    [Fact]
    public void StandardPipeline_DriftSteps_AreOptInPostSteps_DefaultOff_AfterTheDecision()
    {
        // DRIFT Nachtrag: the five drift dimensions ship as Kind=Drift
        // post-steps that default OFF (an opt-in expensive pass) and run after
        // the auto-review decision so the task's own work is settled before
        // drift is measured.
        var p = PipelineCatalogue.Standard;
        var driftSteps = p.Post.Where(s => s.Kind == StepKind.Drift).ToList();
        Assert.Equal(5, driftSteps.Count);

        // The ids on the pipeline match the catalogue constants one-to-one.
        Assert.Equal(
            PipelineCatalogue.DriftStepIds.OrderBy(x => x, StringComparer.Ordinal),
            driftSteps.Select(s => s.Id).OrderBy(x => x, StringComparer.Ordinal));

        var decisionIndex = p.Post.FindIndex(s => s.Id == PipelineCatalogue.OrchestratorDecisionStepId);
        foreach (var step in driftSteps)
        {
            Assert.False(step.DefaultEnabled, $"{step.Id} drift step must default OFF (opt-in)");
            Assert.True(step.Idempotent, $"{step.Id} drift step must be idempotent");
            Assert.Contains(PipelineCatalogue.OrchestratorDecisionStepId, step.DependsOn);
            var stepIndex = p.Post.FindIndex(s => s.Id == step.Id);
            Assert.True(stepIndex > decisionIndex,
                $"{step.Id} (idx {stepIndex}) must come after the orchestrator-decision (idx {decisionIndex})");
        }
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
        // ADR "Commit-Attribution-Regel": the deterministic attribution
        // behaviour is implemented in CommitAttributionService and runs from
        // the transition service on the 3-progress -> 4-auto-review move -
        // ahead of this executor's bracket. So within the executor the slot
        // stays a reserved record (Stub) that surfaces as "planned".
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
    public void StandardPipeline_RegressionRadar_IsToolStep_WithAspectDependencies_AndNotAStub()
    {
        // Regression radar runs as a deterministic Tool post-step after the
        // aspect verdicts and before the orchestrator decision, mirroring the
        // lint-scss slot. Unlike commit-attribution it is implemented today,
        // so it is not a stub. It is reporting-only (no reissue), but it still
        // depends on the aspects so a DAG-resolver schedules it after them.
        var p = PipelineCatalogue.Standard;
        var radarStep = p.Post.First(s => s.Id == PipelineCatalogue.RegressionRadarStepId);
        Assert.Equal(StepKind.Tool, radarStep.Kind);
        Assert.False(radarStep.Stub);
        Assert.True(radarStep.Idempotent);
        Assert.True(radarStep.DefaultEnabled); // default-on, configurable off per project
        foreach (var aspectId in PipelineCatalogue.AspectStepIds)
        {
            Assert.Contains(aspectId, radarStep.DependsOn);
        }

        // Ordered between lint-scss and the orchestrator decision.
        var lintIndex = p.Post.FindIndex(s => s.Id == PipelineCatalogue.LintScssStepId);
        var radarIndex = p.Post.FindIndex(s => s.Id == PipelineCatalogue.RegressionRadarStepId);
        var decisionIndex = p.Post.FindIndex(s => s.Id == PipelineCatalogue.OrchestratorDecisionStepId);
        Assert.True(lintIndex < radarIndex,
            $"lint-scss (idx {lintIndex}) must precede regression-radar (idx {radarIndex})");
        Assert.True(radarIndex < decisionIndex,
            $"regression-radar (idx {radarIndex}) must precede orchestrator-decision (idx {decisionIndex})");
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
        Assert.NotNull(PipelineCatalogue.Get(PipelineCatalogue.ReadOnlyPipelineId));
        Assert.Null(PipelineCatalogue.Get("does-not-exist"));
    }

    [Fact]
    public void ReadOnlyPipeline_OmitsEveryGitStep_KeepsEverythingElse()
    {
        // Read-only-Pipeline fuer planning/research: the git steps are dropped so
        // a planning / research run does no worktree / commit / merge / teardown
        // work - it only renders the prompt, runs the agent, and produces a
        // report. The single catalogue git step today is commit-attribution.
        var standard = PipelineCatalogue.Standard;
        var ro = PipelineCatalogue.ReadOnly;

        Assert.Equal(PipelineCatalogue.ReadOnlyPipelineId, ro.Id);

        // No step in any section is a git step.
        foreach (var step in ro.AllSteps)
            Assert.DoesNotContain(step.Id, PipelineCatalogue.GitStepIds);

        // The commit-attribution slot is present on standard but gone here.
        Assert.Contains(standard.Post, s => s.Id == PipelineCatalogue.GitCommitAttributionStepId);
        Assert.DoesNotContain(ro.Post, s => s.Id == PipelineCatalogue.GitCommitAttributionStepId);

        // Every non-git step from standard survives, in the same relative order,
        // so the variant tracks the standard pipeline as it grows.
        var expected = standard.AllSteps
            .Where(s => !PipelineCatalogue.GitStepIds.Contains(s.Id))
            .Select(s => s.Id)
            .ToList();
        Assert.Equal(expected, ro.AllSteps.Select(s => s.Id).ToList());

        // The core agent run and all Pre steps (loop guard + orchestrator prep
        // + reissue open-items check) are not git steps, so they remain.
        Assert.Single(ro.Core);
        Assert.Equal(PipelineCatalogue.CoreAgentRunStepId, ro.Core[0].Id);
        Assert.Equal(3, ro.Pre.Count);
        Assert.Equal(PipelineCatalogue.LoopGuardStepId, ro.Pre[0].Id);
        Assert.Equal(PipelineCatalogue.PreOrchestratorPrepStepId, ro.Pre[1].Id);
        Assert.Equal(PipelineCatalogue.PreReissueOpenItemsStepId, ro.Pre[2].Id);

        // Exactly the one git step was removed from Post.
        Assert.Equal(standard.Post.Count - 1, ro.Post.Count);
    }

    [Fact]
    public void ForMode_SelectsReadOnlyForPlanningAndResearch_StandardOtherwise()
    {
        Assert.Same(PipelineCatalogue.ReadOnly, PipelineCatalogue.ForMode("planning"));
        Assert.Same(PipelineCatalogue.ReadOnly, PipelineCatalogue.ForMode("research"));
        Assert.Same(PipelineCatalogue.Standard, PipelineCatalogue.ForMode("coding"));
        Assert.Same(PipelineCatalogue.Standard, PipelineCatalogue.ForMode(null));
        Assert.Same(PipelineCatalogue.Standard, PipelineCatalogue.ForMode("anything-else"));
    }

    [Fact]
    public void ReadOnlyPipeline_HasNoDanglingDependsOnEdges()
    {
        // Filtering out the git steps must not leave a surviving step depending on
        // a removed id (a DAG-resolver would deadlock on the missing node).
        var ro = PipelineCatalogue.ReadOnly;
        var presentIds = ro.AllSteps.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var step in ro.AllSteps)
            foreach (var dep in step.DependsOn)
                Assert.Contains(dep, presentIds);
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
