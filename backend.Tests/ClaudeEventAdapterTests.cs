

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the Claude stream-json -> CliRunEvent mapping. Each fixture
/// is a real frame shape captured from <c>claude -p ... --output-format
/// stream-json --verbose</c>; the test asserts what typed event(s) the
/// adapter emits. Pure-function tests, no live process, run in
/// milliseconds on every default <c>dotnet test</c>.
/// </summary>
public class ClaudeEventAdapterTests
{
    private const string Jk = "::test-job";

    [Fact]
    public void SystemInitFrame_EmitsSessionStarted_WithSessionId()
    {
        const string frame = """
        {"type":"system","subtype":"init","session_id":"a1b2c3d4-e5f6-4789-abcd-ef0123456789","tools":[]}
        """;
        var events = ClaudeEventAdapter.Map(frame, Jk).ToList();
        Assert.Single(events);
        var ss = Assert.IsType<CliRunEvent.SessionStarted>(events[0]);
        Assert.Equal("a1b2c3d4-e5f6-4789-abcd-ef0123456789", ss.SessionId);
    }

    [Fact]
    public void SystemFrame_NonInitSubtype_EmitsSessionInitializing()
    {
        const string frame = """
        {"type":"system","subtype":"hello"}
        """;
        var events = ClaudeEventAdapter.Map(frame, Jk).ToList();
        Assert.Single(events);
        Assert.IsType<CliRunEvent.SessionInitializing>(events[0]);
    }

    [Fact]
    public void RateLimitEvent_EmitsRateLimitObserved_WithFields()
    {
        const string frame = """
        {"type":"rate_limit_event","rate_limit_info":{"rateLimitType":"five_hour","status":"allowed","resetsAt":1777999999,"overageStatus":"allowed","isUsingOverage":false}}
        """;
        var events = ClaudeEventAdapter.Map(frame, Jk).ToList();
        var rl = Assert.IsType<CliRunEvent.RateLimitObserved>(Assert.Single(events));
        Assert.Equal("five_hour", rl.Window);
        Assert.Equal("allowed", rl.Status);
        Assert.Equal(1777999999L, rl.ResetsAt);
        Assert.False(rl.IsUsingOverage);
    }

    [Fact]
    public void AssistantTextFrame_EmitsOutputDelta()
    {
        const string frame = """
        {"type":"assistant","message":{"content":[{"type":"text","text":"Hello there"}]}}
        """;
        var events = ClaudeEventAdapter.Map(frame, Jk).ToList();
        var od = Assert.IsType<CliRunEvent.OutputDelta>(Assert.Single(events));
        Assert.Equal("Hello there", od.Text);
    }

    [Fact]
    public void AssistantToolUseFrame_EmitsToolStarted_WithArgument()
    {
        const string frame = """
        {"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{"file_path":"C:/foo.cs"}}]}}
        """;
        var events = ClaudeEventAdapter.Map(frame, Jk).ToList();
        var ts = Assert.IsType<CliRunEvent.ToolStarted>(Assert.Single(events));
        Assert.Equal("Read", ts.ToolName);
        Assert.Equal("C:/foo.cs", ts.Argument);
    }

    [Fact]
    public void AssistantBashFrame_TakesCommandAsArgument()
    {
        const string frame = """
        {"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"npm test"}}]}}
        """;
        var ts = Assert.IsType<CliRunEvent.ToolStarted>(
            Assert.Single(ClaudeEventAdapter.Map(frame, Jk).ToList()));
        Assert.Equal("Bash", ts.ToolName);
        Assert.Equal("npm test", ts.Argument);
    }

    [Fact]
    public void AssistantMixedTextAndToolUse_EmitsBothInOrder()
    {
        const string frame = """
        {"type":"assistant","message":{"content":[
          {"type":"text","text":"Reading the file"},
          {"type":"tool_use","name":"Read","input":{"file_path":"a.cs"}}
        ]}}
        """;
        var events = ClaudeEventAdapter.Map(frame, Jk).ToList();
        Assert.Equal(2, events.Count);
        Assert.IsType<CliRunEvent.OutputDelta>(events[0]);
        Assert.IsType<CliRunEvent.ToolStarted>(events[1]);
    }

    [Fact]
    public void TodoWriteFrame_EmitsToolStartedThenPlanUpdated_WithNormalizedStatuses()
    {
        const string frame = """
        {"type":"assistant","message":{"content":[{"type":"tool_use","name":"TodoWrite","input":{"todos":[
          {"content":"Read the spec","status":"completed","activeForm":"Reading the spec"},
          {"content":"Wire the adapter","status":"in_progress","activeForm":"Wiring the adapter"},
          {"content":"Add tests","status":"pending","activeForm":"Adding tests"}
        ]}}]}}
        """;
        var events = ClaudeEventAdapter.Map(frame, Jk).ToList();
        Assert.Equal(2, events.Count);
        Assert.IsType<CliRunEvent.ToolStarted>(events[0]);
        var plan = Assert.IsType<CliRunEvent.PlanUpdated>(events[1]);
        Assert.Equal("claude/TodoWrite", plan.Source);
        Assert.Equal(3, plan.Items.Count);
        Assert.Equal("done", plan.Items[0].Status);
        Assert.Equal("active", plan.Items[1].Status);
        Assert.Equal("pending", plan.Items[2].Status);
        Assert.Equal("Read the spec", plan.Items[0].Title);
    }

    [Fact]
    public void TodoWritePlanItemId_IsStableAcrossWhitespaceAndCase()
    {
        const string a = """
        {"type":"assistant","message":{"content":[{"type":"tool_use","name":"TodoWrite","input":{"todos":[
          {"content":"Wire the adapter","status":"pending"}]}}]}}
        """;
        const string b = """
        {"type":"assistant","message":{"content":[{"type":"tool_use","name":"TodoWrite","input":{"todos":[
          {"content":"  wire   the   ADAPTER ","status":"in_progress"}]}}]}}
        """;
        var idA = Assert.IsType<CliRunEvent.PlanUpdated>(ClaudeEventAdapter.Map(a, Jk).ToList()[1]).Items[0].Id;
        var idB = Assert.IsType<CliRunEvent.PlanUpdated>(ClaudeEventAdapter.Map(b, Jk).ToList()[1]).Items[0].Id;
        Assert.Equal(idA, idB);
    }

    [Fact]
    public void TodoWriteWithNoUsableTodos_EmitsOnlyToolStarted()
    {
        const string frame = """
        {"type":"assistant","message":{"content":[{"type":"tool_use","name":"TodoWrite","input":{"todos":[
          {"content":"   ","status":"pending"}]}}]}}
        """;
        var events = ClaudeEventAdapter.Map(frame, Jk).ToList();
        Assert.IsType<CliRunEvent.ToolStarted>(Assert.Single(events));
    }

    [Fact]
    public void AssistantThinkingPart_DroppedFromTypedStream()
    {
        const string frame = """
        {"type":"assistant","message":{"content":[{"type":"thinking","thinking":"long reasoning..."}]}}
        """;
        var events = ClaudeEventAdapter.Map(frame, Jk).ToList();
        Assert.Empty(events);
    }

    [Fact]
    public void UserToolResultFrame_EmitsToolCompleted_WithFirstLine()
    {
        const string frame = """
        {"type":"user","message":{"content":[{"type":"tool_result","is_error":false,"content":"line one\nline two"}]}}
        """;
        var events = ClaudeEventAdapter.Map(frame, Jk).ToList();
        var tc = Assert.IsType<CliRunEvent.ToolCompleted>(Assert.Single(events));
        Assert.False(tc.IsError);
        Assert.Equal("line one", tc.FirstLine);
    }

    [Fact]
    public void UserToolResultFrame_ErrorFlag_PreservedOnEvent()
    {
        const string frame = """
        {"type":"user","message":{"content":[{"type":"tool_result","is_error":true,"content":"command failed"}]}}
        """;
        var tc = Assert.IsType<CliRunEvent.ToolCompleted>(
            Assert.Single(ClaudeEventAdapter.Map(frame, Jk).ToList()));
        Assert.True(tc.IsError);
        Assert.Equal("command failed", tc.FirstLine);
    }

    [Fact]
    public void ResultFrameSuccess_EmitsTurnCompleted_WithUsage()
    {
        const string frame = """
        {"type":"result","subtype":"success","is_error":false,"result":"hi","usage":{"input_tokens":12,"output_tokens":5,"cache_read_input_tokens":100}}
        """;
        var tc = Assert.IsType<CliRunEvent.TurnCompleted>(
            Assert.Single(ClaudeEventAdapter.Map(frame, Jk).ToList()));
        Assert.NotNull(tc.UsageSummary);
        Assert.Contains("input=12", tc.UsageSummary);
        Assert.Contains("output=5", tc.UsageSummary);
        Assert.Contains("cache_read=100", tc.UsageSummary);
    }

    [Fact]
    public void ResultFrameError_EmitsTurnFailed()
    {
        const string frame = """
        {"type":"result","subtype":"error_max_turns","is_error":true}
        """;
        var tf = Assert.IsType<CliRunEvent.TurnFailed>(
            Assert.Single(ClaudeEventAdapter.Map(frame, Jk).ToList()));
        Assert.Equal("error_max_turns", tf.Reason);
    }

    [Fact]
    public void UnknownTopLevelType_EmitsUnknownWithSample()
    {
        const string frame = """
        {"type":"experimental_new_thing","payload":42}
        """;
        var u = Assert.IsType<CliRunEvent.Unknown>(
            Assert.Single(ClaudeEventAdapter.Map(frame, Jk).ToList()));
        Assert.Contains("experimental_new_thing", u.Sample);
    }

    [Fact]
    public void NonJsonOrEmptyInput_EmitsNoEvents()
    {
        Assert.Empty(ClaudeEventAdapter.Map("", Jk));
        Assert.Empty(ClaudeEventAdapter.Map("not-json", Jk));
        Assert.Empty(ClaudeEventAdapter.Map("[1,2,3]", Jk)); // top-level array, ignore
    }

    [Fact]
    public void MalformedJson_DoesNotThrow_AndEmitsNothing()
    {
        // Adapter must be defensive: a partial line or a JSON parse error
        // surfaces as zero events (the line still hits the raw on-disk log
        // through the existing CliOutputLine flow).
        Assert.Empty(ClaudeEventAdapter.Map("{type:\"missing-quotes\"}", Jk));
        Assert.Empty(ClaudeEventAdapter.Map("{\"type\":\"system\",", Jk));
    }
}
