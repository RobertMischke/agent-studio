using OrchestratorApi.Models;
using OrchestratorApi.Services.Runner;

namespace OrchestratorApi.Services.Pipeline;

/// <summary>
/// Static catalogue of pipeline definitions. Phase 1 ships exactly one:
/// <c>standard-task-pipeline</c>, derived from today's
/// <c>3-progress -> 4-auto-review</c> flow. Pre-steps are reserved
/// slots (no runtime today; future tasks plug requirement-clarification
/// / context-retrieval / skill-readiness here). The Core step is the
/// CLI agent run owned by <see cref="TaskRunnerService"/>. Post-steps
/// run the four <see cref="StepKind.Aspect"/> verdicts in parallel
/// (the load-bearing behavioural change in this phase), the
/// git-commit-attribution slot (ADR "Commit-Attribution-Regel";
/// behaviour implemented in
/// <see cref="OrchestratorApi.Services.Tasks.CommitAttributionService"/>
/// and run from the transition service, so the executor records it as
/// planned), a <see cref="StepKind.Tool"/> lint-scss step, and an
/// <see cref="StepKind.Orchestrator"/> decision step that reads the
/// aspect verdicts.
///
/// The catalogue is a code constant on purpose: YAML loading buys
/// nothing in Phase 1 and a YAML schema would have to be co-versioned
/// with the in-memory model. Phase 2 may externalise this when
/// per-project pipeline customisation lands.
/// </summary>
public static class PipelineCatalogue
{
    public const string StandardPipelineId = "standard-task-pipeline";

    /// <summary>
    /// The four aspect step ids ship as parallel post-steps. Kept in
    /// sync with <see cref="AspectRunnerService.Catalogue"/> -
    /// <see cref="PipelineCatalogueAsserts.AspectStepsMatchAspectRunnerCatalogue"/>
    /// fails the build if they drift.
    /// </summary>
    public static readonly string[] AspectStepIds =
    {
        "aspect-requirement-fit",
        "aspect-code-quality",
        "aspect-documentation-impact",
        "aspect-tests-and-evidence",
    };

    public const string CoreAgentRunStepId = "core-agent-run";

    /// <summary>
    /// Pre-step that surfaces the auto-mode Ralph-loop guard
    /// (<c>OrchestratorApi.Services.Runner.StuckLoopGuard</c>) in the
    /// pipeline table. It is the first row of <see cref="TaskPipeline.AllSteps"/>
    /// so a forming or stopped loop is visible early - before the core run and
    /// the aspect verdicts. Deterministic (no LLM); the recording lives in
    /// <c>ProjectRunner</c>: <see cref="PipelineStepStatus.Passed"/> with no
    /// verdict while healthy, <see cref="PipelineStepStatus.Passed"/> with a
    /// <c>looping</c> verdict while a loop builds under budget, and
    /// <see cref="PipelineStepStatus.Failed"/> with a <c>loop-detected</c>
    /// verdict when the circuit-breaker fires.
    /// </summary>
    public const string LoopGuardStepId = "pre-loop-guard";

    /// <summary>
    /// The five drift dimensions ship as opt-in <see cref="StepKind.Drift"/>
    /// post-steps (DRIFT Nachtrag): four LLM dimensions plus the rule-based
    /// code-pattern check. They default <c>DefaultEnabled = false</c> because a
    /// drift run is an expensive extra pass an operator turns on per project;
    /// the trigger is wired in <c>DriftPostStepRunner</c>, which reuses the
    /// existing <c>*DriftAnalysisService</c> + <c>DriftReportStore</c>. The id
    /// suffix after <c>post-drift-</c> selects the dimension; keep it in sync
    /// with <c>DriftPostStepRunner</c>'s dispatch.
    /// </summary>
    public const string DriftAdrCodeStepId = "post-drift-adr-code";
    public const string DriftSoftwareArchitectureStepId = "post-drift-software-architecture";
    public const string DriftDocsMarketingStepId = "post-drift-docs-marketing";
    public const string DriftSpecTaskJobStepId = "post-drift-spec-task-job";
    public const string DriftCodePatternStepId = "post-drift-code-pattern";

    public static readonly string[] DriftStepIds =
    {
        DriftAdrCodeStepId,
        DriftSoftwareArchitectureStepId,
        DriftDocsMarketingStepId,
        DriftSpecTaskJobStepId,
        DriftCodePatternStepId,
    };

    public const string GitCommitAttributionStepId = "post-git-commit-attribution";
    /// <summary>
    /// Post-step that runs <c>npx stylelint</c> over the frontend SCSS tree
    /// after the agent run finishes. Verdict drives the
    /// <see cref="OrchestratorApi.Services.Pipeline.LintScssRunner"/> mode
    /// (off/warn/fail) and may trigger a reissue back to <c>2-ready</c>
    /// when configured to fail. See ASS-563.
    /// </summary>
    public const string LintScssStepId = "post-lint-scss";
    /// <summary>
    /// Post-step that runs the Regression Radar spec-change analysis after the
    /// agent run finishes. Deterministic (no LLM): it diffs the run's SHA range
    /// and classifies each changed spec as intended / at-risk / drift. Reporting
    /// only - the verdict surfaces in the pipeline list but never triggers a
    /// reissue. Behaviour lives in
    /// <see cref="OrchestratorApi.Services.RegressionRadar.RegressionRadarService"/>.
    /// </summary>
    public const string RegressionRadarStepId = "post-regression-radar";
    public const string OrchestratorDecisionStepId = "post-orchestrator-decision";

    private static readonly TaskPipeline StandardPipeline = BuildStandardPipeline();

    public static TaskPipeline Standard => StandardPipeline;

    public static TaskPipeline? Get(string id) =>
        string.Equals(id, StandardPipelineId, StringComparison.OrdinalIgnoreCase) ? StandardPipeline : null;

    public static IReadOnlyList<TaskPipeline> All { get; } = [StandardPipeline];

    private static TaskPipeline BuildStandardPipeline()
    {
        var aspects = new List<PipelineStep>();
        foreach (var aspectId in AspectStepIds)
        {
            var bareId = aspectId.StartsWith("aspect-", StringComparison.Ordinal)
                ? aspectId.Substring("aspect-".Length)
                : aspectId;
            if (!AspectRunnerService.Catalogue.TryGetValue(bareId, out var def))
            {
                // Catalogue mismatch would trip PipelineCatalogueTests.cs;
                // keep going so the runtime still has a step record.
                aspects.Add(new PipelineStep
                {
                    Id = aspectId,
                    DisplayName = aspectId,
                    Kind = StepKind.Aspect,
                    RunMode = StepRunMode.Parallel,
                    Idempotent = true,
                });
                continue;
            }
            aspects.Add(new PipelineStep
            {
                Id = aspectId,
                DisplayName = def.Title,
                Kind = StepKind.Aspect,
                RunMode = StepRunMode.Parallel,
                Idempotent = true,
            });
        }

        return new TaskPipeline
        {
            Id = StandardPipelineId,
            DisplayName = "Standard task pipeline",
            Version = 1,
            Pre =
            [
                new PipelineStep
                {
                    Id = LoopGuardStepId,
                    DisplayName = "Loop check",
                    Kind = StepKind.Module,
                    RunMode = StepRunMode.Sequential,
                    Idempotent = true,
                },
            ],
            Core =
            [
                new PipelineStep
                {
                    Id = CoreAgentRunStepId,
                    DisplayName = "Agent execution",
                    Kind = StepKind.Core,
                    RunMode = StepRunMode.Sequential,
                    Idempotent = false,
                },
            ],
            Post =
            [
                .. aspects,
                new PipelineStep
                {
                    Id = GitCommitAttributionStepId,
                    DisplayName = "Git commit attribution",
                    Kind = StepKind.Tool,
                    RunMode = StepRunMode.Sequential,
                    DependsOn = [.. AspectStepIds],
                    Idempotent = true,
                    // The deterministic attribution behaviour IS implemented
                    // (CommitAttributionService, run from TaskTransitionService on
                    // the 3-progress -> 4-auto-review transition). It runs ahead
                    // of this executor bracket, so within the executor the slot
                    // stays a reserved record that surfaces as "planned".
                    Stub = true,
                },
                new PipelineStep
                {
                    Id = LintScssStepId,
                    DisplayName = "Frontend stylelint",
                    Kind = StepKind.Tool,
                    RunMode = StepRunMode.Sequential,
                    DependsOn = [.. AspectStepIds],
                    Idempotent = true,
                },
                new PipelineStep
                {
                    Id = RegressionRadarStepId,
                    DisplayName = "Regression radar",
                    Kind = StepKind.Tool,
                    RunMode = StepRunMode.Sequential,
                    DependsOn = [.. AspectStepIds],
                    Idempotent = true,
                },
                new PipelineStep
                {
                    Id = OrchestratorDecisionStepId,
                    DisplayName = "Auto-review decision",
                    Kind = StepKind.Orchestrator,
                    RunMode = StepRunMode.Sequential,
                    DependsOn = [.. AspectStepIds],
                    Idempotent = true,
                },
                .. BuildDriftSteps(),
            ],
        };
    }

    // The opt-in drift post-steps. They run after the auto-review decision (so
    // the task's own work is settled before drift is measured) and default off
    // - an absent override leaves them disabled. Four are LLM dimensions that
    // accept a per-step model; code-pattern is rule-based but still carries a
    // model so an operator can opt into the optional LLM verdict-enrichment pass.
    private static IEnumerable<PipelineStep> BuildDriftSteps()
    {
        var dependsOnReview = new[] { OrchestratorDecisionStepId };
        (string Id, string Name)[] dims =
        {
            (DriftAdrCodeStepId, "Drift: ADR / Code"),
            (DriftSoftwareArchitectureStepId, "Drift: Software / Architecture"),
            (DriftDocsMarketingStepId, "Drift: Docs / Marketing"),
            (DriftSpecTaskJobStepId, "Drift: Spec / Task / Job"),
            (DriftCodePatternStepId, "Drift: Code-Pattern (rule-based)"),
        };
        foreach (var (id, name) in dims)
        {
            yield return new PipelineStep
            {
                Id = id,
                DisplayName = name,
                Kind = StepKind.Drift,
                RunMode = StepRunMode.Sequential,
                DependsOn = [.. dependsOnReview],
                Idempotent = true,
                DefaultEnabled = false,
            };
        }
    }
}
