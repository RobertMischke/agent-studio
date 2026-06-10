namespace AgentStudio.Cli;

/// <summary>
/// Pure-function library that maps a current <see cref="RunPhase"/> +
/// the next <see cref="CliRunEvent"/> to the resulting phase. Lives
/// alongside <see cref="CliRunEvent"/> instead of inside the runner so
/// the transition matrix can be unit-tested in isolation; the runner
/// just calls <see cref="Apply"/> and stores the result.
///
/// <para>
/// Forward-only by design: a turn progresses
/// <c>Spawning -&gt; SessionInitializing -&gt; PromptConsumed -&gt;
/// TurnInProgress -&gt; (OutputDelta | ToolExecuting)* -&gt; TurnCompleted | TurnFailed
/// </c>. <see cref="OutputDelta"/> is sticky inside <see cref="TurnInProgress"/>:
/// re-entering it from <see cref="ToolExecuting"/> is normal (a tool
/// finished and the model resumed talking). <see cref="NeedsInput"/>
/// is a terminal-for-this-turn state that the runner persists until the
/// user replies, then the next run starts a fresh phase chain.
/// </para>
/// <para>
/// The runner clamps backwards transitions to "stay where you are" and
/// emits a meta line; it does not crash. CLIs sometimes emit out-of-
/// order frames around tool calls and we prefer being lenient over
/// dead-ending a live agent.
/// </para>
/// </summary>
public static class RunPhaseTransitions
{
    /// <summary>
    /// Returns the phase the run should be in after <paramref name="evt"/>
    /// is applied to the current phase <paramref name="current"/>. Pure
    /// function: same inputs -> same output, no side effects, no I/O.
    /// </summary>
    public static RunPhase Apply(RunPhase current, CliRunEvent evt) => evt switch
    {
        CliRunEvent.RunStarted          => RunPhase.Spawning,
        CliRunEvent.SessionInitializing => RunPhase.SessionInitializing,
        CliRunEvent.SessionStarted      => RunPhase.PromptConsumed,
        CliRunEvent.TurnStarted         => RunPhase.TurnInProgress,
        CliRunEvent.OutputDelta         => RunPhase.OutputDelta,
        CliRunEvent.ToolStarted         => RunPhase.ToolExecuting,
        CliRunEvent.ToolCompleted       => current == RunPhase.ToolExecuting ? RunPhase.TurnInProgress : current,
        CliRunEvent.Heartbeat           => current,
        CliRunEvent.TurnCompleted       => RunPhase.TurnCompleted,
        CliRunEvent.TurnFailed          => RunPhase.TurnFailed,
        CliRunEvent.NeedsInput          => RunPhase.NeedsInput,
        CliRunEvent.ApprovalRequested   => RunPhase.NeedsInput,
        CliRunEvent.RateLimitObserved   => current,
        CliRunEvent.ProcessExited       => RunPhase.Exited,
        CliRunEvent.Killed              => RunPhase.Killed,
        CliRunEvent.Unknown             => current == RunPhase.Spawning ? RunPhase.Unknown : current,
        _                                => current
    };

    /// <summary>
    /// Returns true when an <see cref="OutputDelta"/> or equivalent activity
    /// signal would reset the silence clock. Used by the watchdog to decide
    /// whether a typed event counts as "the agent is alive". Heartbeats and
    /// tool starts/completes count; phase-only events (SessionInitializing)
    /// do not count as activity inside an active turn.
    /// </summary>
    public static bool IsActivitySignal(CliRunEvent evt) => evt switch
    {
        CliRunEvent.OutputDelta       => true,
        CliRunEvent.ToolStarted       => true,
        CliRunEvent.ToolCompleted     => true,
        CliRunEvent.Heartbeat         => true,
        CliRunEvent.RateLimitObserved => true,
        CliRunEvent.SessionStarted    => true,
        CliRunEvent.TurnStarted       => true,
        // An `Unknown` frame is still output: the CLI wrote *something*
        // to stdout that the adapter could not classify. Treating it as
        // silence would punish the orchestrator for adapter coverage
        // gaps (a new claude-code stream-json frame variant, an
        // experimental codex event). Counting it as activity is the
        // defensive choice; the unknown-sample is captured downstream
        // for diagnosis.
        CliRunEvent.Unknown           => true,
        _                              => false
    };
}
