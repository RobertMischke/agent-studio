
using Xunit;
using RunOutcome = CodingAgentRunner.Model.RunOutcome;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the per-event activity-signal classification. The
/// silence-clock reset hinges on this: a signal classified as activity
/// keeps the run "alive" from the watchdog's perspective; a non-signal
/// lets silence accumulate until the budget triggers.
/// </summary>
public class RunPhaseTransitionsTests
{
    [Theory]
    [InlineData(typeof(CliRunEvent.OutputDelta))]
    [InlineData(typeof(CliRunEvent.ToolStarted))]
    [InlineData(typeof(CliRunEvent.ToolCompleted))]
    [InlineData(typeof(CliRunEvent.Heartbeat))]
    [InlineData(typeof(CliRunEvent.RateLimitObserved))]
    [InlineData(typeof(CliRunEvent.SessionStarted))]
    [InlineData(typeof(CliRunEvent.TurnStarted))]
    [InlineData(typeof(CliRunEvent.Unknown))]   // 2026-05-06 hardening
    public void IsActivitySignal_TrueForExpectedKinds(System.Type kind)
    {
        var evt = MakeEvent(kind);
        Assert.True(RunPhaseTransitions.IsActivitySignal(evt),
            $"Expected {kind.Name} to count as activity (resets silence clock).");
    }

    [Theory]
    [InlineData(typeof(CliRunEvent.RunStarted))]          // spawn-only, no protocol activity yet
    [InlineData(typeof(CliRunEvent.SessionInitializing))] // structural phase; no real bytes
    [InlineData(typeof(CliRunEvent.TurnCompleted))]       // terminal-for-turn
    [InlineData(typeof(CliRunEvent.TurnFailed))]
    [InlineData(typeof(CliRunEvent.NeedsInput))]
    [InlineData(typeof(CliRunEvent.ApprovalRequested))]
    [InlineData(typeof(CliRunEvent.RunEnded))]
    public void IsActivitySignal_FalseForExpectedKinds(System.Type kind)
    {
        var evt = MakeEvent(kind);
        Assert.False(RunPhaseTransitions.IsActivitySignal(evt),
            $"Expected {kind.Name} to NOT count as activity.");
    }

    [Fact]
    public void Apply_ToolStartedThenCompleted_LandsBackInTurnInProgress()
    {
        var phase = RunPhaseTransitions.Apply(RunPhase.OutputDelta,
            new CliRunEvent.ToolStarted("Bash", "dotnet build"));
        Assert.Equal(RunPhase.ToolExecuting, phase);

        phase = RunPhaseTransitions.Apply(phase,
            new CliRunEvent.ToolCompleted("Bash", IsError: false, FirstLine: "ok"));
        Assert.Equal(RunPhase.TurnInProgress, phase);
    }

    [Fact]
    public void Apply_UnknownInsideRun_DoesNotMovePhase()
    {
        var phase = RunPhaseTransitions.Apply(RunPhase.OutputDelta, new CliRunEvent.Unknown("noise"));
        Assert.Equal(RunPhase.OutputDelta, phase);
    }

    private static CliRunEvent MakeEvent(System.Type t)
    {
        if (t == typeof(CliRunEvent.OutputDelta))         return new CliRunEvent.OutputDelta("...");
        if (t == typeof(CliRunEvent.ToolStarted))         return new CliRunEvent.ToolStarted("Bash", null);
        if (t == typeof(CliRunEvent.ToolCompleted))       return new CliRunEvent.ToolCompleted("Bash", false, null);
        if (t == typeof(CliRunEvent.Heartbeat))           return new CliRunEvent.Heartbeat();
        if (t == typeof(CliRunEvent.RateLimitObserved))   return new CliRunEvent.RateLimitObserved("seven_day", "allowed_warning", 0, null, false);
        if (t == typeof(CliRunEvent.SessionStarted))      return new CliRunEvent.SessionStarted("uuid");
        if (t == typeof(CliRunEvent.TurnStarted))         return new CliRunEvent.TurnStarted();
        if (t == typeof(CliRunEvent.Unknown))             return new CliRunEvent.Unknown("?");
        if (t == typeof(CliRunEvent.RunStarted))          return new CliRunEvent.RunStarted(123, "claude", null);
        if (t == typeof(CliRunEvent.SessionInitializing)) return new CliRunEvent.SessionInitializing();
        if (t == typeof(CliRunEvent.TurnCompleted))       return new CliRunEvent.TurnCompleted(null);
        if (t == typeof(CliRunEvent.TurnFailed))          return new CliRunEvent.TurnFailed("err");
        if (t == typeof(CliRunEvent.NeedsInput))          return new CliRunEvent.NeedsInput("ask");
        if (t == typeof(CliRunEvent.ApprovalRequested))   return new CliRunEvent.ApprovalRequested("approve?");
        if (t == typeof(CliRunEvent.RunEnded))            return new CliRunEvent.RunEnded(RunOutcome.Completed, null, 0, 1.0);
        throw new System.NotSupportedException($"unknown test event type: {t}");
    }
}
