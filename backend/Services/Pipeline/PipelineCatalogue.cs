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
    /// The read-only variant for planning / research modes. Same steps as
    /// <see cref="StandardPipelineId"/> minus the git pre/post steps (see
    /// <see cref="GitStepIds"/>): render prompt -&gt; run agent -&gt; produce
    /// report -&gt; status, with no worktree / commit / merge / teardown. Chosen
    /// per run by <see cref="ForMode"/>.
    /// </summary>
    public const string ReadOnlyPipelineId = "read-only-task-pipeline";

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
    /// Optional, parallelisable pre-coding step that surfaces the ADR-0026
    /// orchestrator-prep pass (prompt-clarity scoring / accept-bounce-iterate)
    /// in the pipeline table. It replaces the standalone
    /// <c>1a-orchestrator-prep</c> backlog lane: prep now runs in-place on
    /// <c>1-preparation</c> cards inside
    /// <c>OrchestratorApi.Services.Supervisor.OrchestratorPrepHostedService</c>
    /// and admits accepted cards straight to <c>2-ready</c>, so the active flow
    /// has prep before the coding run without a dedicated lane. It is a
    /// <see cref="StepKind.Module"/> deterministic heuristic today (no LLM) yet
    /// carries the resolved per-project model
    /// (<see cref="PipelineStepConfigResolver.ResolveModel(ProjectSettings?, PipelineStep, string)"/>)
    /// so it respects the project model selection rather than hardcoding one.
    /// It runs decoupled from the coding latch (<see cref="StepRunMode.Parallel"/>)
    /// so it never blocks throughput, and defaults
    /// <c>DefaultEnabled = false</c> because prep is an opt-in pass an operator
    /// turns on per project (mirrors the <c>Orchestrator:PrepEnabled</c> kill
    /// switch). The recording lives in <c>OrchestratorPrepHostedService</c>:
    /// <see cref="PipelineStepStatus.Passed"/> on accept,
    /// <see cref="PipelineStepStatus.Failed"/> on bounce,
    /// <see cref="PipelineStepStatus.Running"/> while it iterates.
    /// </summary>
    public const string PreOrchestratorPrepStepId = "pre-orchestrator-prep";

    /// <summary>
    /// Deterministic pre-step that runs before the core agent run on a
    /// re-issued card: it detects whether the run is a re-issue carrying open
    /// items from the previous run (the auto-review follow-up reason, unchecked
    /// checklist boxes, or aspect concern/block summaries) and, on a hit, has
    /// <c>ProjectRunner</c> foreground those items into the run prompt + post an
    /// orchestrator intervention note rather than letting the orchestrator
    /// blindly restart. The decision logic lives in the pure
    /// <see cref="OrchestratorApi.Services.Runner.ReissueOpenItemsPreCheck"/>;
    /// the recording is best-effort observability, never a state-machine input.
    /// It is deterministic (no LLM) and on by default - an unfinished re-issue
    /// is a correctness signal, not an opt-in pass.
    /// </summary>
    public const string PreReissueOpenItemsStepId = "pre-reissue-open-items";

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

    /// <summary>
    /// Post-core completeness check that runs immediately after the core agent
    /// run, before the aspect verdicts. It is the deterministic
    /// <c>CompletionGate</c> scan of the run's own close-out evidence
    /// (status Open Items / Notes, the Result line, and the log tail) for
    /// unfinished-work signals: open checklist boxes, self-reported build / test
    /// failures, or a silent finish with leftover items. A hit short-circuits the
    /// accept and reissues with the items foregrounded so a task can never be
    /// accepted while its own evidence says it is unfinished. It surfaces in the
    /// Overview pipeline as the FIRST "Orchestrator-Review" row (the final
    /// <see cref="OrchestratorDecisionStepId"/> row is the second one). Recorded
    /// by <c>ReviewDecisionOrchestrator</c>.
    ///
    /// <para>
    /// <b>Placement decision (ASS-643 / ASS-744, resolved).</b> The feature spec
    /// asked for the gate "nach git commit attribution, vor/um die
    /// Auto-Review-Decision". That requirement is met: the deterministic
    /// commit-attribution (<see cref="GitCommitAttributionStepId"/>) runs at the
    /// 3-progress -&gt; 4-auto-review transition in
    /// <c>TaskTransitionService</c> - strictly BEFORE this gate, which runs in
    /// <c>ReviewDecisionOrchestrator.ProcessDoneAsync</c> once the card is in
    /// 4-auto-review - and the gate runs BEFORE the final
    /// <see cref="OrchestratorDecisionStepId"/> ruling. So at runtime the gate is
    /// after attribution and before the decision exactly as specified. It is
    /// listed as the first POST row (ahead of the aspect verdicts) on purpose,
    /// for two reasons: (1) it short-circuits an unfinished close-out before
    /// spending the expensive parallel aspect review, and (2) the Overview then
    /// reads honestly on a reissue - the aspects sit BELOW a gate that stopped
    /// before they ran, rather than showing four aspect rows stuck "pending"
    /// underneath a reissue verdict. Moving the row after the aspect/tool steps
    /// would invert that and misrepresent a gate-reissue run, so the placement is
    /// kept and pinned by <c>PipelineCatalogueTests</c>.
    /// </para>
    /// </summary>
    public const string OrchestratorReviewStepId = "post-orchestrator-review";
    public const string OrchestratorDecisionStepId = "post-orchestrator-decision";

    /// <summary>
    /// Display name for the post-core completeness check
    /// (<see cref="OrchestratorReviewStepId"/>): the EARLY gate that runs straight
    /// after the core run, before the aspect verdicts. It is deliberately distinct
    /// from <see cref="FinalOrchestratorReviewDisplayName"/> so the two
    /// orchestrator-review rows never read as the same step: this one is an early
    /// static scan / open-items check (verdict <c>complete</c> / <c>reissue</c> /
    /// <c>escalate</c>) and carries NO "final verdict" semantics.
    /// </summary>
    public const string PostCoreReviewDisplayName = "Post-Core Orchestrator-Review";

    /// <summary>
    /// Display name for the FINAL accept / reissue / escalate decision
    /// (<see cref="OrchestratorDecisionStepId"/>): the orchestrator's single
    /// ruling after the parallel aspects and tools. This is the ONLY row the FE
    /// tags as the "final verdict"; the post-core review row above
    /// (<see cref="PostCoreReviewDisplayName"/>) is a distinct early gate.
    /// </summary>
    public const string FinalOrchestratorReviewDisplayName = "Final Orchestrator-Review";

    /// <summary>
    /// The "Abbruch-Review" (post-abort review) step id. Unlike every other
    /// catalogue step this one is <em>abort-triggered</em>, not part of the
    /// linear post-bracket: it runs only after a non-clean CLI run end
    /// (watchdog timeout, non-zero exit, unexpected stop), so it is exposed as
    /// the standalone <see cref="AbortReviewStep"/> definition rather than
    /// inserted into <see cref="TaskPipeline.Post"/>. It defaults
    /// <see cref="PipelineStep.DefaultEnabled"/> = false because it is an extra
    /// LLM pass an operator turns on per project via the same
    /// <see cref="ProjectSettings.PipelineSteps"/> override mechanism the drift
    /// and aspect steps use. When it runs, <c>ProjectRunner</c> records a
    /// <see cref="StepKind.Orchestrator"/> step execution (verdict + reasoning)
    /// into <c>pipeline-execution.json</c> so it surfaces in the Overview
    /// pipeline view like the auto-review aspects.
    /// Implemented by <c>PostAbortReviewStepService</c>; the rule-engine
    /// decision is owned by <c>PostAbortReviewDecider</c>.
    /// </summary>
    public const string PostAbortReviewStepId = "post-abort-review";

    /// <summary>
    /// Standalone definition for the abort-triggered <see cref="PostAbortReviewStepId"/>
    /// step. Kept off the static Post list (it does not run in the normal
    /// post-bracket) but defined here so the per-project config resolver and
    /// the runtime step-execution recorder share one id, display name, default
    /// (opt-in / off), and model-resolution path.
    /// </summary>
    public static PipelineStep AbortReviewStep { get; } = new()
    {
        Id = PostAbortReviewStepId,
        DisplayName = "Abort review",
        // An orchestrator-style decision step: it consumes evidence and chooses
        // rerun / reissue / accept / human-review, the same shape as the
        // auto-review decision step, so it reuses StepKind.Orchestrator rather
        // than introducing a new kind the frontend would have to learn.
        Kind = StepKind.Orchestrator,
        RunMode = StepRunMode.Sequential,
        Idempotent = true,
        // Opt-in per project: an extra LLM pass the operator turns on.
        DefaultEnabled = false,
    };

    /// <summary>
    /// Steps whose work mutates the git tree (worktree create, commit + push,
    /// merge / integration, teardown). Today the only catalogue git step is the
    /// commit-attribution slot; future worktree / merge steps add their ids here.
    /// The read-only pipeline filters these out so a planning / research run does
    /// no git work - <see cref="BuildReadOnlyPipeline"/> drops any step whose id
    /// is in this set. The git side effects that live outside the catalogue
    /// (auto-commit, push, completed-push) are gated separately in
    /// <c>TaskTransitionService</c> on the same <see cref="TaskModes.IsReadOnly"/>
    /// predicate.
    /// </summary>
    public static readonly HashSet<string> GitStepIds = new(StringComparer.Ordinal)
    {
        GitCommitAttributionStepId,
    };

    private static readonly TaskPipeline StandardPipeline = BuildStandardPipeline();
    private static readonly TaskPipeline ReadOnlyPipeline = BuildReadOnlyPipeline();

    public static TaskPipeline Standard => StandardPipeline;
    public static TaskPipeline ReadOnly => ReadOnlyPipeline;

    /// <summary>
    /// Select the pipeline for a task's execution mode: read-only modes
    /// (planning / research) get the git-free <see cref="ReadOnly"/> variant;
    /// everything else gets <see cref="Standard"/>.
    /// </summary>
    public static TaskPipeline ForMode(string? mode) =>
        TaskModes.IsReadOnly(mode) ? ReadOnlyPipeline : StandardPipeline;

    public static TaskPipeline? Get(string id) =>
        string.Equals(id, StandardPipelineId, StringComparison.OrdinalIgnoreCase) ? StandardPipeline
        : string.Equals(id, ReadOnlyPipelineId, StringComparison.OrdinalIgnoreCase) ? ReadOnlyPipeline
        : null;

    public static IReadOnlyList<TaskPipeline> All { get; } = [StandardPipeline, ReadOnlyPipeline];

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
                PromptTemplate = def.PromptTemplate,
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
                new PipelineStep
                {
                    Id = PreOrchestratorPrepStepId,
                    DisplayName = "Orchestrator prep",
                    Kind = StepKind.Module,
                    // Decoupled from the coding latch (runs on 1-preparation
                    // cards in OrchestratorPrepHostedService), so it never
                    // blocks the active flow into 3-progress.
                    RunMode = StepRunMode.Parallel,
                    Idempotent = true,
                    // Opt-in per project: prep is an extra pre-coding pass an
                    // operator turns on, so an absent override leaves it off.
                    DefaultEnabled = false,
                },
                new PipelineStep
                {
                    Id = PreReissueOpenItemsStepId,
                    DisplayName = "Reissue open-items check",
                    Kind = StepKind.Module,
                    // Runs deterministically in the pre-bracket ahead of the core
                    // run; foregrounds leftover open items on a re-issue.
                    RunMode = StepRunMode.Sequential,
                    Idempotent = true,
                    // Default-on: a re-issue that still has open items is a
                    // correctness signal the orchestrator should not skip.
                    DefaultEnabled = true,
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
                    PromptTemplate = "prompt.md",
                },
            ],
            Post =
            [
                new PipelineStep
                {
                    Id = OrchestratorReviewStepId,
                    DisplayName = PostCoreReviewDisplayName,
                    Kind = StepKind.Orchestrator,
                    // Runs straight after the core run, ahead of the aspect
                    // verdicts, so an unfinished close-out is caught before any
                    // expensive review pass. No intra-section dependency.
                    RunMode = StepRunMode.Sequential,
                    Idempotent = true,
                },
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
                    DisplayName = FinalOrchestratorReviewDisplayName,
                    Kind = StepKind.Orchestrator,
                    RunMode = StepRunMode.Sequential,
                    DependsOn = [.. AspectStepIds],
                    Idempotent = true,
                },
                .. BuildDriftSteps(),
            ],
        };
    }

    // The read-only variant for planning / research modes. It is the standard
    // pipeline with every git step (see GitStepIds) filtered out of all three
    // sections, so a read-only run does no worktree / commit / merge / teardown
    // work: render prompt -> run agent -> produce report -> status. Filtering the
    // standard definition (rather than hand-listing steps) keeps the two
    // pipelines in lock-step as the standard pipeline grows.
    private static TaskPipeline BuildReadOnlyPipeline()
    {
        static List<PipelineStep> WithoutGitSteps(IEnumerable<PipelineStep> steps) =>
            steps.Where(s => !GitStepIds.Contains(s.Id)).ToList();

        return StandardPipeline with
        {
            Id = ReadOnlyPipelineId,
            DisplayName = "Read-only task pipeline",
            Pre = WithoutGitSteps(StandardPipeline.Pre),
            Core = WithoutGitSteps(StandardPipeline.Core),
            Post = WithoutGitSteps(StandardPipeline.Post),
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
