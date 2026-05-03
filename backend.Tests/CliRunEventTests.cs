using OrchestratorApi.Services.Cli;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the <see cref="RunPhaseTransitions"/> matrix so that adapters
/// and the watchdog can rely on a stable phase model. The matrix is
/// pure-function, no I/O, no live process; these run on every default
/// <c>dotnet test</c>.
/// </summary>
public class CliRunEventTests
{
    [Fact]
    public void RunStarted_TakesUsToSpawning()
    {
        var p = RunPhaseTransitions.Apply(
            RunPhase.Spawning,
            new CliRunEvent.RunStarted(1234, "claude", "claude-haiku-4-5"));
        Assert.Equal(RunPhase.Spawning, p);
    }

    [Fact]
    public void SessionInitializing_AdvancesFromSpawning()
    {
        var p = RunPhaseTransitions.Apply(RunPhase.Spawning, new CliRunEvent.SessionInitializing());
        Assert.Equal(RunPhase.SessionInitializing, p);
    }

    [Fact]
    public void SessionStarted_AdvancesToPromptConsumed()
    {
        var p = RunPhaseTransitions.Apply(
            RunPhase.SessionInitializing,
            new CliRunEvent.SessionStarted("abc"));
        Assert.Equal(RunPhase.PromptConsumed, p);
    }

    [Fact]
    public void TurnStarted_AdvancesToTurnInProgress()
    {
        var p = RunPhaseTransitions.Apply(
            RunPhase.PromptConsumed,
            new CliRunEvent.TurnStarted());
        Assert.Equal(RunPhase.TurnInProgress, p);
    }

    [Fact]
    public void OutputDelta_TakesUsToOutputDelta()
    {
        var p = RunPhaseTransitions.Apply(
            RunPhase.TurnInProgress,
            new CliRunEvent.OutputDelta("hi"));
        Assert.Equal(RunPhase.OutputDelta, p);
    }

    [Fact]
    public void ToolStarted_OverridesToToolExecuting()
    {
        var p = RunPhaseTransitions.Apply(
            RunPhase.OutputDelta,
            new CliRunEvent.ToolStarted("Read", "/some/file.cs"));
        Assert.Equal(RunPhase.ToolExecuting, p);
    }

    [Fact]
    public void ToolCompleted_FromToolExecuting_FallsBackToTurnInProgress()
    {
        var p = RunPhaseTransitions.Apply(
            RunPhase.ToolExecuting,
            new CliRunEvent.ToolCompleted("Read", false, "first-line"));
        Assert.Equal(RunPhase.TurnInProgress, p);
    }

    [Fact]
    public void ToolCompleted_OutsideToolExecuting_StaysPut()
    {
        // A late ToolCompleted should not silently regress us. If we are
        // already past the tool (e.g. OutputDelta resumed), staying in
        // OutputDelta is the right move.
        var p = RunPhaseTransitions.Apply(
            RunPhase.OutputDelta,
            new CliRunEvent.ToolCompleted("Read", false, "first-line"));
        Assert.Equal(RunPhase.OutputDelta, p);
    }

    [Fact]
    public void Heartbeat_KeepsCurrentPhase()
    {
        Assert.Equal(RunPhase.OutputDelta,
            RunPhaseTransitions.Apply(RunPhase.OutputDelta, new CliRunEvent.Heartbeat()));
        Assert.Equal(RunPhase.ToolExecuting,
            RunPhaseTransitions.Apply(RunPhase.ToolExecuting, new CliRunEvent.Heartbeat()));
    }

    [Fact]
    public void RateLimit_KeepsCurrentPhase()
    {
        var rl = new CliRunEvent.RateLimitObserved("five_hour", "allowed", 0, null, false);
        Assert.Equal(RunPhase.PromptConsumed,
            RunPhaseTransitions.Apply(RunPhase.PromptConsumed, rl));
    }

    [Fact]
    public void TurnCompleted_AdvancesToTurnCompleted()
    {
        var p = RunPhaseTransitions.Apply(
            RunPhase.OutputDelta,
            new CliRunEvent.TurnCompleted("input=10 output=20"));
        Assert.Equal(RunPhase.TurnCompleted, p);
    }

    [Fact]
    public void NeedsInput_OrApprovalRequested_BothLandInNeedsInput()
    {
        Assert.Equal(RunPhase.NeedsInput,
            RunPhaseTransitions.Apply(RunPhase.OutputDelta, new CliRunEvent.NeedsInput("clarify the column")));
        Assert.Equal(RunPhase.NeedsInput,
            RunPhaseTransitions.Apply(RunPhase.ToolExecuting, new CliRunEvent.ApprovalRequested("Edit foo.cs")));
    }

    [Fact]
    public void ProcessExited_AdvancesToExited()
    {
        var p = RunPhaseTransitions.Apply(
            RunPhase.OutputDelta,
            new CliRunEvent.ProcessExited(0, "completed", 12.3));
        Assert.Equal(RunPhase.Exited, p);
    }

    [Fact]
    public void Killed_AdvancesToKilled()
    {
        var p = RunPhaseTransitions.Apply(
            RunPhase.OutputDelta,
            new CliRunEvent.Killed("watchdog"));
        Assert.Equal(RunPhase.Killed, p);
    }

    [Fact]
    public void Unknown_FromSpawning_LandsInUnknown()
    {
        // We have observed nothing useful yet; stay honest about the gap.
        var p = RunPhaseTransitions.Apply(
            RunPhase.Spawning,
            new CliRunEvent.Unknown("garbled-bytes"));
        Assert.Equal(RunPhase.Unknown, p);
    }

    [Fact]
    public void Unknown_AfterAValidPhase_DoesNotRegress()
    {
        // A noisy line in the middle of a run should not knock us back.
        var p = RunPhaseTransitions.Apply(
            RunPhase.OutputDelta,
            new CliRunEvent.Unknown("garbled-bytes"));
        Assert.Equal(RunPhase.OutputDelta, p);
    }

    [Fact]
    public void IsActivitySignal_OnlyTrueForActualActivity()
    {
        Assert.True(RunPhaseTransitions.IsActivitySignal(new CliRunEvent.OutputDelta("x")));
        Assert.True(RunPhaseTransitions.IsActivitySignal(new CliRunEvent.ToolStarted("Read", null)));
        Assert.True(RunPhaseTransitions.IsActivitySignal(new CliRunEvent.ToolCompleted("Read", false, null)));
        Assert.True(RunPhaseTransitions.IsActivitySignal(new CliRunEvent.Heartbeat()));
        Assert.True(RunPhaseTransitions.IsActivitySignal(new CliRunEvent.SessionStarted("uuid")));
        Assert.True(RunPhaseTransitions.IsActivitySignal(new CliRunEvent.TurnStarted()));
        Assert.True(RunPhaseTransitions.IsActivitySignal(new CliRunEvent.RateLimitObserved(null, null, 0, null, false)));

        Assert.False(RunPhaseTransitions.IsActivitySignal(
            new CliRunEvent.RunStarted(1, "claude", null)));
        Assert.False(RunPhaseTransitions.IsActivitySignal(new CliRunEvent.SessionInitializing()));
        Assert.False(RunPhaseTransitions.IsActivitySignal(new CliRunEvent.NeedsInput("x")));
        Assert.False(RunPhaseTransitions.IsActivitySignal(new CliRunEvent.Unknown("x")));
    }
}
