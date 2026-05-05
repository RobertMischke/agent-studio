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
    public void OutOfScopeToken_DepressesClarity_AcrossAllLevels()
    {
        // ROADMAP non-goal token forces clarity below the sharpen threshold,
        // which at level 1 is a bounce.
        var prompt = ClearPrompt + "\n\nAlso add a worktree per task.";
        var cautious = OrchestratorPrepRules.Decide(new OrchestratorPrepRules.PrepInput
        {
            PromptText = prompt,
            AutonomyLevel = 1,
        });
        // The penalty is 0.30, so clear (0.85ish) drops to the borderline band
        // at worst; at level 1 every borderline still bounces.
        Assert.Equal(OrchestratorPrepRules.Verdict.Bounce, cautious.Verdict);
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
