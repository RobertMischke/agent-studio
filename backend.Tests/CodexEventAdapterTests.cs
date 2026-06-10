

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the Codex JSONL -> CliRunEvent mapping. Fixtures are real frame
/// shapes captured from <c>codex exec --json</c> on codex-cli 0.128.0;
/// the adapter is intentionally close to the App Server protocol so a
/// future transport migration replaces only the I/O layer.
/// </summary>
public class CodexEventAdapterTests
{
    private const string Jk = "::codex-test";

    [Fact]
    public void ThreadStarted_EmitsSessionStarted()
    {
        const string frame = """{"type":"thread.started","thread_id":"019dee65-7a9b-7843-bfd9-06e555fff02b"}""";
        var ss = Assert.IsType<CliRunEvent.SessionStarted>(
            Assert.Single(CodexEventAdapter.Map(frame, Jk).ToList()));
        Assert.Equal("019dee65-7a9b-7843-bfd9-06e555fff02b", ss.SessionId);
    }

    [Fact]
    public void SessionMetaLegacy_AlsoEmitsSessionStarted()
    {
        const string frame = """{"type":"session_meta","session_id":"abc"}""";
        var ss = Assert.IsType<CliRunEvent.SessionStarted>(
            Assert.Single(CodexEventAdapter.Map(frame, Jk).ToList()));
        Assert.Equal("abc", ss.SessionId);
    }

    [Fact]
    public void TurnStarted_EmitsTurnStarted()
    {
        Assert.IsType<CliRunEvent.TurnStarted>(
            Assert.Single(CodexEventAdapter.Map("""{"type":"turn.started"}""", Jk).ToList()));
    }

    [Fact]
    public void ItemCompletedAgentMessage_EmitsOutputDelta()
    {
        const string frame = """{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"Hi"}}""";
        var od = Assert.IsType<CliRunEvent.OutputDelta>(
            Assert.Single(CodexEventAdapter.Map(frame, Jk).ToList()));
        Assert.Equal("Hi", od.Text);
    }

    [Fact]
    public void ItemStartedCommandCall_EmitsToolStarted()
    {
        const string frame = """{"type":"item.started","item":{"type":"command_call","command":"ls -la"}}""";
        var ts = Assert.IsType<CliRunEvent.ToolStarted>(
            Assert.Single(CodexEventAdapter.Map(frame, Jk).ToList()));
        Assert.Equal("command_call", ts.ToolName);
        Assert.Equal("ls -la", ts.Argument);
    }

    [Fact]
    public void ItemCompletedFileChange_EmitsToolCompleted()
    {
        const string frame = """{"type":"item.completed","item":{"type":"file_change","file_path":"a.cs"}}""";
        var tc = Assert.IsType<CliRunEvent.ToolCompleted>(
            Assert.Single(CodexEventAdapter.Map(frame, Jk).ToList()));
        Assert.Equal("file_change", tc.ToolName);
        Assert.False(tc.IsError);
    }

    [Fact]
    public void TurnCompleted_EmitsTurnCompleted_WithUsage()
    {
        const string frame = """{"type":"turn.completed","usage":{"input_tokens":22267,"cached_input_tokens":6528,"output_tokens":10,"reasoning_output_tokens":0}}""";
        var tc = Assert.IsType<CliRunEvent.TurnCompleted>(
            Assert.Single(CodexEventAdapter.Map(frame, Jk).ToList()));
        Assert.NotNull(tc.UsageSummary);
        Assert.Contains("input=22267", tc.UsageSummary);
        Assert.Contains("cached=6528", tc.UsageSummary);
        Assert.Contains("output=10", tc.UsageSummary);
        Assert.Contains("reasoning=0", tc.UsageSummary);
    }

    [Fact]
    public void TurnFailed_EmitsTurnFailed_WithErrorMessage()
    {
        const string frame = """{"type":"turn.failed","error":{"message":"rate limit exceeded"}}""";
        var tf = Assert.IsType<CliRunEvent.TurnFailed>(
            Assert.Single(CodexEventAdapter.Map(frame, Jk).ToList()));
        Assert.Equal("rate limit exceeded", tf.Reason);
    }

    [Fact]
    public void ItemCompletedUpdatePlan_EmitsPlanUpdated_WithNormalizedStatuses()
    {
        const string frame = """
        {"type":"item.completed","item":{"type":"update_plan","plan":[
          {"step":"Survey the repo","status":"completed"},
          {"step":"Write the patch","status":"in_progress"},
          {"step":"Run the suite","status":"pending"}
        ]}}
        """;
        // update_plan also emits a tool event (symmetric with Claude's
        // TodoWrite -> ToolStarted); PlanReader filters it from sub-actions.
        var plan = Assert.Single(CodexEventAdapter.Map(frame, Jk).OfType<CliRunEvent.PlanUpdated>().ToList());
        Assert.Equal("codex/update_plan", plan.Source);
        Assert.Equal(3, plan.Items.Count);
        Assert.Equal("done", plan.Items[0].Status);
        Assert.Equal("active", plan.Items[1].Status);
        Assert.Equal("pending", plan.Items[2].Status);
        Assert.Equal("Survey the repo", plan.Items[0].Title);
    }

    [Fact]
    public void ItemStartedUpdatePlan_AlsoEmitsPlanUpdated()
    {
        const string frame = """
        {"type":"item.started","item":{"type":"update_plan","plan":[
          {"step":"First","status":"in_progress"}]}}
        """;
        var plan = Assert.Single(CodexEventAdapter.Map(frame, Jk).OfType<CliRunEvent.PlanUpdated>().ToList());
        Assert.Single(plan.Items);
        Assert.Equal("active", plan.Items[0].Status);
    }

    [Fact]
    public void UpdatePlanContentAlias_IsAcceptedAsStepTitle()
    {
        const string frame = """
        {"type":"item.completed","item":{"type":"update_plan","plan":[
          {"content":"Title via content alias","status":"completed"}]}}
        """;
        var plan = Assert.Single(CodexEventAdapter.Map(frame, Jk).OfType<CliRunEvent.PlanUpdated>().ToList());
        Assert.Equal("Title via content alias", plan.Items[0].Title);
    }

    [Fact]
    public void ItemCompletedReasoning_EmitsHeartbeat_NotToolCompleted()
    {
        // ASS-1671: a Codex reasoning block is a liveness ping, not a tool.
        // It must reset the watchdog silence clock (Heartbeat is an activity
        // signal) without being logged as a phantom "reasoning" tool call.
        const string frame = """{"type":"item.completed","item":{"type":"reasoning","text":"Let me think about this..."}}""";
        var evt = Assert.Single(CodexEventAdapter.Map(frame, Jk).ToList());
        Assert.IsType<CliRunEvent.Heartbeat>(evt);
        Assert.IsNotType<CliRunEvent.ToolCompleted>(evt);
        Assert.Equal(Jk, evt.TaskKey);
        Assert.True(RunPhaseTransitions.IsActivitySignal(evt),
            "A reasoning Heartbeat must reset the silence clock.");
    }

    [Fact]
    public void ItemStartedReasoning_EmitsHeartbeat_NotToolStarted()
    {
        const string frame = """{"type":"item.started","item":{"type":"reasoning"}}""";
        var evt = Assert.Single(CodexEventAdapter.Map(frame, Jk).ToList());
        Assert.IsType<CliRunEvent.Heartbeat>(evt);
        Assert.IsNotType<CliRunEvent.ToolStarted>(evt);
    }

    [Fact]
    public void ReasoningHeartbeat_KeepsPhaseUnchanged_NoFalseToolExecuting()
    {
        // The reasoning ping must not advance the phase: during pre-turn xhigh
        // reasoning the run is still PromptConsumed, and mis-advancing to
        // ToolExecuting would mask a genuine pre-turn hang under the wider
        // tool budget.
        const string frame = """{"type":"item.completed","item":{"type":"reasoning","text":"thinking"}}""";
        var evt = Assert.Single(CodexEventAdapter.Map(frame, Jk).ToList());
        Assert.Equal(RunPhase.PromptConsumed,
            RunPhaseTransitions.Apply(RunPhase.PromptConsumed, evt));
    }

    [Fact]
    public void ReasoningPings_KeepXhighRunHealthy_WhileNoFramesGetsKilled()
    {
        // Acceptance (ASS-1671): an xhigh run that reasons silently for >7 min
        // is NOT killed because each reasoning frame resets the silence clock -
        // not merely because the PromptConsumed budget is wide. A run that
        // emits NO frames at all over the same window is still killed.
        var cfg = new WatchdogConfig(
            Enabled: true, WarmUpGraceSeconds: 0, QuietSeconds: 30,
            SuspiciousSeconds: 60, HungSeconds: 120, TickIntervalSeconds: 5);

        // Simulate 8 minutes of pre-turn reasoning: a reasoning frame every
        // 90 s. Track the silence clock exactly as ProjectRunner.OnRunEventReceived
        // does (reset on every IsActivitySignal event).
        const string reasoning = """{"type":"item.completed","item":{"type":"reasoning","text":"step"}}""";
        var lastActivity = 0.0;
        var phase = RunPhase.PromptConsumed;
        for (var t = 90.0; t <= 480.0; t += 90.0)
        {
            // Before the ping arrives, silence has only grown to the inter-frame
            // gap - well under the 1200 s kill backstop, never Hung.
            var silenceBeforePing = t - lastActivity;
            var state = PhaseAwareWatchdog.DecideState(silenceBeforePing, t, phase, cfg);
            Assert.NotEqual(WatchdogState.Hung, state);

            var evt = Assert.Single(CodexEventAdapter.Map(reasoning, Jk).ToList());
            phase = RunPhaseTransitions.Apply(phase, evt);
            if (RunPhaseTransitions.IsActivitySignal(evt)) lastActivity = t;
        }
        // After 8 min of pings the run never went Hung and is still PromptConsumed.
        Assert.Equal(RunPhase.PromptConsumed, phase);

        // Counter-case: the SAME 480 s window with NO frames at all is killed -
        // the silence clock is never reset, so it crosses the 1200 s backstop
        // eventually and is Suspicious well before that. Prove it would die.
        Assert.Equal(WatchdogState.Hung,
            PhaseAwareWatchdog.DecideState(1250, 1400, RunPhase.PromptConsumed, cfg));
    }

    [Fact]
    public void UnknownType_EmitsUnknown()
    {
        const string frame = """{"type":"experimental.beta.thing","payload":1}""";
        var u = Assert.IsType<CliRunEvent.Unknown>(
            Assert.Single(CodexEventAdapter.Map(frame, Jk).ToList()));
        Assert.Contains("experimental.beta.thing", u.Sample);
    }

    [Fact]
    public void NonJsonOrEmpty_EmitsNothing()
    {
        Assert.Empty(CodexEventAdapter.Map("", Jk));
        Assert.Empty(CodexEventAdapter.Map("not-json", Jk));
        Assert.Empty(CodexEventAdapter.Map("[1,2,3]", Jk));
    }

    [Fact]
    public void MalformedJson_DoesNotThrow_AndEmitsNothing()
    {
        Assert.Empty(CodexEventAdapter.Map("{type:\"missing-quotes\"}", Jk));
        Assert.Empty(CodexEventAdapter.Map("{\"type\":\"thread.started\",", Jk));
    }
}
