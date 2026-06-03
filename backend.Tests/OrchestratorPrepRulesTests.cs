using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Pure-function tests for ADR-0026 orchestrator-prep rules. The rule
/// engine is the load-bearing knob; if these break, the kanban will move
/// tasks to the wrong lane regardless of where the hosted service runs.
/// </summary>
public class OrchestratorPrepRulesTests
{
    private const string ClearPrompt = """
        # Add data-testid to lane rails

        ## Read first
        - frontend/src/app/components/job-column.ts

        ## Done when
        - The kanban-spec.spec.ts Playwright test selects rails by data-testid.
        - frontend/src/app/components/job-column.ts has a [attr.data-testid] on each rail.

        ## Notes
        Stable, mechanical change. Touch only the rail wrapper element.
        """;

    private const string AmbiguousPrompt = "fix the export thing";

    // Borderline prompt: long enough to clear the under-30-word penalty
    // and mentions a file so the path-bonus fires, but no explicit
    // acceptance criteria, no Read first, no spec reference.
    private const string BorderlinePrompt = """
        Tighten the retry policy for stale ssh sockets in backend/Services/Net/SshClient.cs.
        Lower the retry budget so a brief network blip does not stall a long-running job.
        Today the policy uses an exponential backoff capped at sixty seconds with five
        retries, which means a real outage can stall the runner for several minutes.
        Pick a tighter cap and revisit the retry counter once the new bound has bedded
        in over a few weeks of normal traffic. Keep the existing logging so the dashboard
        timeline does not lose context across the change.
        """;

    [Fact]
    public void Level0_AlwaysHolds_RegardlessOfClarity()
    {
        var clear = OrchestratorPrepRules.Decide(new OrchestratorPrepRules.PrepInput
        {
            PromptText = ClearPrompt,
            AutonomyLevel = 0,
        });
        Assert.Equal(OrchestratorPrepRules.Verdict.Hold, clear.Verdict);

        var ambiguous = OrchestratorPrepRules.Decide(new OrchestratorPrepRules.PrepInput
        {
            PromptText = AmbiguousPrompt,
            AutonomyLevel = 0,
        });
        Assert.Equal(OrchestratorPrepRules.Verdict.Hold, ambiguous.Verdict);
    }

    [Fact]
    public void Level2_ClearPromptAccepts_AmbiguousPromptIterates()
    {
        var clear = OrchestratorPrepRules.Decide(new OrchestratorPrepRules.PrepInput
        {
            PromptText = ClearPrompt,
            AutonomyLevel = 2,
        });
        Assert.Equal(OrchestratorPrepRules.Verdict.Accept, clear.Verdict);
        Assert.True(clear.Clarity >= OrchestratorPrepRules.AcceptThreshold,
            $"clear prompt should land at clarity >= {OrchestratorPrepRules.AcceptThreshold}, got {clear.Clarity}");

        var ambiguous = OrchestratorPrepRules.Decide(new OrchestratorPrepRules.PrepInput
        {
            PromptText = AmbiguousPrompt,
            AutonomyLevel = 2,
        });
        Assert.Equal(OrchestratorPrepRules.Verdict.Iterate, ambiguous.Verdict);
    }

    [Fact]
    public void Level2_BorderlineAccepts_Level1BorderlineBounces()
    {
        var balanced = OrchestratorPrepRules.Decide(new OrchestratorPrepRules.PrepInput
        {
            PromptText = BorderlinePrompt,
            AutonomyLevel = 2,
        });
        // Borderline should not be in the iterate band (>= 0.40) and at
        // level 2 it ships forward.
        Assert.True(balanced.Clarity >= OrchestratorPrepRules.SharpenThreshold,
            $"borderline prompt should be >= {OrchestratorPrepRules.SharpenThreshold}, got {balanced.Clarity}");
        Assert.Equal(OrchestratorPrepRules.Verdict.Accept, balanced.Verdict);

        var cautious = OrchestratorPrepRules.Decide(new OrchestratorPrepRules.PrepInput
        {
            PromptText = BorderlinePrompt,
            AutonomyLevel = 1,
        });
        Assert.Equal(OrchestratorPrepRules.Verdict.Bounce, cautious.Verdict);
        Assert.Equal(OrchestratorPrepRules.BounceReason.UnderSpecified, cautious.BounceReason);
    }

    [Fact]
    public void Level4_NeverBounces_AmbiguousIteratesNotBounces_AndCapExitAccepts()
    {
        // Below cap: even an ambiguous prompt iterates rather than bouncing.
        var iterating = OrchestratorPrepRules.Decide(new OrchestratorPrepRules.PrepInput
        {
            PromptText = AmbiguousPrompt,
            AutonomyLevel = 4,
            Iteration = 1,
            MaxIterations = 3,
        });
        Assert.Equal(OrchestratorPrepRules.Verdict.Iterate, iterating.Verdict);
        Assert.Equal(OrchestratorPrepRules.BounceReason.None, iterating.BounceReason);

        // At cap: fully-auto accepts and writes a [supervisor] note.
        var capExit = OrchestratorPrepRules.Decide(new OrchestratorPrepRules.PrepInput
        {
            PromptText = AmbiguousPrompt,
            AutonomyLevel = 4,
            Iteration = 3,
            MaxIterations = 3,
        });
        Assert.Equal(OrchestratorPrepRules.Verdict.Accept, capExit.Verdict);
        Assert.NotNull(capExit.Note);
        Assert.Contains("[supervisor]", capExit.Note);
        Assert.Contains("fully-auto", capExit.Note);
    }

    [Fact]
    public void Level2_CapReached_BouncesWithIterationCapReason()
    {
        var capExit = OrchestratorPrepRules.Decide(new OrchestratorPrepRules.PrepInput
        {
            PromptText = AmbiguousPrompt,
            AutonomyLevel = 2,
            Iteration = 3,
            MaxIterations = 3,
        });
        Assert.Equal(OrchestratorPrepRules.Verdict.Bounce, capExit.Verdict);
        Assert.Equal(OrchestratorPrepRules.BounceReason.IterationCap, capExit.BounceReason);
    }

    [Fact]
    public void WorktreeToken_NoLongerDepressesClarity()
    {
        // ADR-0052 lifted the intra-project-parallelism non-goal: "worktree" /
        // "branch-per-task" / "intra-project parallel" are no longer out-of-scope
        // tokens and must NOT depress the prep clarity score. The token is now
        // neutral, so a clear prompt that mentions it decides the same as without.
        var baseline = OrchestratorPrepRules.Decide(new OrchestratorPrepRules.PrepInput
        {
            PromptText = ClearPrompt,
            AutonomyLevel = 1,
        });
        var withToken = OrchestratorPrepRules.Decide(new OrchestratorPrepRules.PrepInput
        {
            PromptText = ClearPrompt + "\n\nAlso add a worktree per task.",
            AutonomyLevel = 1,
        });
        Assert.Equal(baseline.Verdict, withToken.Verdict);
    }

    [Fact]
    public void HumanDecisionNeededSlug_BouncesAtLevel4_OverridingNeverBounces()
    {
        // A card minted explicitly for a human decision must bounce no matter
        // how clear the prompt looks or how high the autonomy is (the bounce
        // admits to 2-ready, where the runner's pickup sweep then herds the
        // marker to 5-human-review). The slug prefix is a semantic marker, so
        // it overrides the "level 4 never bounces" doctrine. AC#3 of the
        // human-decision-needed routing bug.
        var decision = OrchestratorPrepRules.Decide(new OrchestratorPrepRules.PrepInput
        {
            // ClearPrompt would otherwise score well above the accept band.
            PromptText = ClearPrompt,
            Slug = "human-decision-needed-bug-card-delete-button-has-no-effect",
            AutonomyLevel = 4,
            Iteration = 1,
            MaxIterations = 3,
        });

        Assert.Equal(OrchestratorPrepRules.Verdict.Bounce, decision.Verdict);
        Assert.Equal(OrchestratorPrepRules.BounceReason.HumanDecisionNeededMarker, decision.BounceReason);
        Assert.Equal(0.0, decision.Clarity);
    }

    [Fact]
    public void HumanDecisionNeededSlug_BouncesAcrossEveryAutonomyLevelAndCapState()
    {
        // The marker override sits ahead of every autonomy branch: the level-0
        // hold and the cap-exit accept must not swallow it either.
        foreach (var level in new[] { 0, 1, 2, 3, 4 })
        foreach (var iteration in new[] { 0, 3 })
        {
            var decision = OrchestratorPrepRules.Decide(new OrchestratorPrepRules.PrepInput
            {
                PromptText = AmbiguousPrompt,
                Slug = "human-decision-needed-xyz",
                AutonomyLevel = level,
                Iteration = iteration,
                MaxIterations = 3,
            });
            Assert.Equal(OrchestratorPrepRules.Verdict.Bounce, decision.Verdict);
            Assert.Equal(OrchestratorPrepRules.BounceReason.HumanDecisionNeededMarker, decision.BounceReason);
        }
    }

    [Fact]
    public void NonHumanDecisionSlug_DoesNotTriggerMarkerOverride()
    {
        // Sanity: the prefix match is anchored. A card that merely mentions a
        // human decision in its body, or whose slug contains the phrase
        // mid-string, still flows through the normal bands.
        var decision = OrchestratorPrepRules.Decide(new OrchestratorPrepRules.PrepInput
        {
            PromptText = ClearPrompt,
            Slug = "fix-human-decision-needed-banner-styling",
            AutonomyLevel = 4,
        });
        Assert.Equal(OrchestratorPrepRules.Verdict.Accept, decision.Verdict);
        Assert.NotEqual(OrchestratorPrepRules.BounceReason.HumanDecisionNeededMarker, decision.BounceReason);
    }

    [Fact]
    public void Decide_ClampsAutonomyLevelOutsideZeroFour()
    {
        var below = OrchestratorPrepRules.Decide(new OrchestratorPrepRules.PrepInput
        {
            PromptText = ClearPrompt,
            AutonomyLevel = -1,
        });
        // Clamps to 0 (manual): clear prompt held back, queue does not move.
        Assert.Equal(OrchestratorPrepRules.Verdict.Hold, below.Verdict);

        var above = OrchestratorPrepRules.Decide(new OrchestratorPrepRules.PrepInput
        {
            PromptText = AmbiguousPrompt,
            AutonomyLevel = 99,
            Iteration = 3,
            MaxIterations = 3,
        });
        // Clamps to 4 (fully-auto): cap-exit accepts with note.
        Assert.Equal(OrchestratorPrepRules.Verdict.Accept, above.Verdict);
    }
}
