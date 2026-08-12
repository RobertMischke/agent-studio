

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Frame-level snapshot tests for <see cref="CodexOutputRenderer"/>. Each test
/// pins one <c>codex exec --json</c> frame shape onto the marker-line vocabulary
/// the frontend activity-log <c>classifyAction</c> understands - the same
/// vocabulary <see cref="ClaudeOutputRenderer"/> emits, so a Codex run reads as
/// cleanly as a Claude run.
///
/// <para>
/// The renderer is pure and dependency-free, so these tests construct it with a
/// plain <c>new()</c> - no process, no Codex behavior constructor graph. The
/// matching skill at <c>docs/system/cli/skills/cli-codex.md</c> documents the frame
/// catalogue these tests lock.
/// </para>
/// </summary>
public class CodexOutputRendererTests
{
    private static readonly CodexOutputRenderer Renderer = new();

    private static CliOutputLine Stdout(string json) => new()
    {
        Stream = "stdout",
        Text = json,
        Timestamp = DateTime.UtcNow
    };

    private static List<CliOutputLine> Render(string json) => Renderer.Render(Stdout(json)).ToList();

    [Fact]
    public void ThreadStarted_ProducesSessionMarker()
    {
        var lines = Render("""{"type":"thread.started","thread_id":"01993b1d-5816-7950-9f04-e6c46e09cf72"}""");

        Assert.Single(lines);
        Assert.Equal("● Session 01993b1d-5816-7950-9f04-e6c46e09cf72", lines[0].Text);
    }

    [Fact]
    public void SessionMeta_LegacyPayloadId_ProducesSessionMarker()
    {
        var lines = Render("""{"type":"session_meta","payload":{"id":"01993b1d-5816-7950-9f04-e6c46e09cf72"}}""");

        Assert.Single(lines);
        Assert.Equal("● Session 01993b1d-5816-7950-9f04-e6c46e09cf72", lines[0].Text);
    }

    [Fact]
    public void SessionMeta_RootSessionId_ProducesSessionMarker()
    {
        var lines = Render("""{"type":"session_meta","session_id":"01993b1d-5816-7950-9f04-e6c46e09cf72"}""");

        Assert.Single(lines);
        Assert.Equal("● Session 01993b1d-5816-7950-9f04-e6c46e09cf72", lines[0].Text);
    }

    [Fact]
    public void TurnStarted_IsSuppressed()
    {
        var lines = Render("""{"type":"turn.started"}""");

        Assert.Empty(lines);
    }

    [Fact]
    public void TurnCompleted_WithUsage_SumsInputAndOutputTokens()
    {
        var lines = Render("""{"type":"turn.completed","usage":{"input_tokens":1200,"cached_input_tokens":900,"output_tokens":345,"reasoning_output_tokens":50}}""");

        Assert.Single(lines);
        Assert.Equal("● Turn completed (tokens: 1545)", lines[0].Text);
        Assert.Equal("stdout", lines[0].Stream);
    }

    [Fact]
    public void TurnCompleted_WithoutUsage_OmitsTokenTail()
    {
        var lines = Render("""{"type":"turn.completed"}""");

        Assert.Single(lines);
        Assert.Equal("● Turn completed", lines[0].Text);
    }

    [Fact]
    public void TurnFailed_ProducesStderrMarkerWithReason()
    {
        var lines = Render("""{"type":"turn.failed","error":{"message":"rate limit exceeded"}}""");

        Assert.Single(lines);
        Assert.Equal("● Turn failed: rate limit exceeded", lines[0].Text);
        Assert.Equal("stderr", lines[0].Stream);
    }

    [Fact]
    public void TurnFailed_MissingError_FallsBackToGenericReason()
    {
        var lines = Render("""{"type":"turn.failed"}""");

        Assert.Single(lines);
        Assert.Equal("● Turn failed: error", lines[0].Text);
        Assert.Equal("stderr", lines[0].Stream);
    }

    [Fact]
    public void ItemStarted_IsSuppressedToAvoidDuplicateLines()
    {
        // item.completed renders the same item; emitting item.started too would
        // double every tool line in the Activity Log.
        var lines = Render("""{"type":"item.started","item":{"type":"command_execution","command":"ls -la"}}""");

        Assert.Empty(lines);
    }

    [Fact]
    public void ItemCompleted_AgentMessage_SplitsMultiLineText()
    {
        var lines = Render("""{"type":"item.completed","item":{"type":"agent_message","text":"Hello\nWorld"}}""");

        Assert.Equal(2, lines.Count);
        Assert.Equal("Hello", lines[0].Text);
        Assert.Equal("World", lines[1].Text);
    }

    [Fact]
    public void ItemCompleted_Reasoning_IsSuppressed()
    {
        var lines = Render("""{"type":"item.completed","item":{"type":"reasoning","text":"Let me think about this..."}}""");

        Assert.Empty(lines);
    }

    [Fact]
    public void ItemCompleted_CommandExecution_ProducesRunMarker()
    {
        var lines = Render("""{"type":"item.completed","item":{"type":"command_execution","command":"npm test","exit_code":0}}""");

        Assert.Single(lines);
        Assert.Equal("● Run npm test", lines[0].Text);
        Assert.Equal("stdout", lines[0].Stream);
    }

    [Fact]
    public void ItemCompleted_CommandWithNonZeroExit_GoesToStderr()
    {
        var lines = Render("""{"type":"item.completed","item":{"type":"command_execution","command":"npm test","exit_code":1}}""");

        Assert.Single(lines);
        Assert.Equal("● Run npm test", lines[0].Text);
        Assert.Equal("stderr", lines[0].Stream);
    }

    [Fact]
    public void ItemCompleted_Command_CollapsesNewlinesToSingleLine()
    {
        var lines = Render("""{"type":"item.completed","item":{"type":"local_shell_call","command":"echo a\necho b"}}""");

        Assert.Single(lines);
        Assert.Equal("● Run echo a echo b", lines[0].Text);
    }

    [Fact]
    public void ItemCompleted_FileChange_ProducesEditMarker()
    {
        var lines = Render("""{"type":"item.completed","item":{"type":"file_change","path":"src/foo.ts"}}""");

        Assert.Single(lines);
        Assert.Equal("● Edit src/foo.ts", lines[0].Text);
    }

    [Fact]
    public void ItemCompleted_FileChange_NestedChangesArray_ProducesEditMarker()
    {
        var lines = Render("""{"type":"item.completed","item":{"type":"file_change","changes":[{"path":"src/bar.ts"}]}}""");

        Assert.Single(lines);
        Assert.Equal("● Edit src/bar.ts", lines[0].Text);
    }

    [Fact]
    public void ItemCompleted_WebSearch_ProducesSearchWebMarker()
    {
        var lines = Render("""{"type":"item.completed","item":{"type":"web_search","query":"dotnet 10 release date"}}""");

        Assert.Single(lines);
        Assert.Equal("● Search web dotnet 10 release date", lines[0].Text);
    }

    [Fact]
    public void ItemCompleted_UpdatePlan_ProducesTodoUpdateMarker()
    {
        var lines = Render("""{"type":"item.completed","item":{"type":"update_plan","plan":[{"step":"a","status":"completed"}]}}""");

        Assert.Empty(lines);
    }

    [Fact]
    public void ItemCompleted_UnknownItemType_FallsBackToTypeMarker()
    {
        var lines = Render("""{"type":"item.completed","item":{"type":"mcp_tool_call","server":"x"}}""");

        Assert.Single(lines);
        Assert.Equal("● mcp_tool_call", lines[0].Text);
    }

    [Fact]
    public void UnknownFrameType_FallsBackToTypeMarker_NeverLeaksRawJson()
    {
        var lines = Render("""{"type":"some_new_frame","blob":{"a":1}}""");

        Assert.Single(lines);
        Assert.Equal("● some_new_frame", lines[0].Text);
    }

    [Fact]
    public void StderrLine_PassesThroughUnchanged()
    {
        var raw = new CliOutputLine { Stream = "stderr", Text = "boom", Timestamp = DateTime.UtcNow };

        var lines = Renderer.Render(raw).ToList();

        Assert.Single(lines);
        Assert.Same(raw, lines[0]);
    }

    [Fact]
    public void RealCodexTranscript_ToolRouterExitDiagnostic_IsSuppressed()
    {
        // Captured from AGT-2081 on Windows: a harmless Get-Process miss emitted
        // the duplicate immediately before the authoritative completed item.
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "cli", "codex",
            "agt-2081-tool-router-exit.log");
        var transcript = CliOutputLogParser.ParseLines(
            File.ReadLines(fixturePath),
            new DateTime(2026, 7, 11, 0, 0, 0, DateTimeKind.Utc));
        var rendered = transcript.SelectMany(Renderer.Render).ToList();

        var command = Assert.Single(rendered);
        Assert.Equal("\u25cf Run Get-Process dotnet,vstest.console,testhost -ErrorAction SilentlyContinue", command.Text);
        Assert.Equal("stderr", command.Stream);

        // Drive the same raw stdout frame through the typed parser too. This
        // proves that the router diagnostic was the orphan: the authoritative
        // frame is a valid failed tool completion, not a missing tool-result.
        var completedFrame = Assert.Single(transcript, line =>
            line.Stream == "stdout" && line.Text.Contains("\"item.completed\"", StringComparison.Ordinal));
        var toolResult = Assert.IsType<CliRunEvent.ToolCompleted>(
            Assert.Single(BuiltInCliBehaviors.MapCodexFrame(completedFrame.Text, "AGT-2081").ToList()));
        Assert.Equal("command_execution", toolResult.ToolName);
        Assert.True(toolResult.IsError);
        var captured = BuiltInCliBehaviors.TryExtractCommandExecution(completedFrame.Text);
        Assert.Equal(1, captured?.ExitCode);
        Assert.Contains("ProcessName : dotnet", captured?.OutputTail);
        Assert.Contains("ProcessName : testhost", captured?.OutputTail);
        Assert.DoesNotContain("ProcessName : vstest.console", captured?.OutputTail);
        Assert.DoesNotContain(rendered, line =>
            line.Text.Contains("codex_core::tools::router", StringComparison.Ordinal)
            || line.Text.Contains("expected: tool-result", StringComparison.Ordinal));
    }

    [Fact]
    public void SimilarStderrWithoutCodexRouterPrefix_RemainsVisible()
    {
        var raw = new CliOutputLine
        {
            Stream = "stderr",
            Text = "tool router reported an application exit code: 1",
            Timestamp = DateTime.UtcNow
        };

        Assert.Same(raw, Assert.Single(Renderer.Render(raw)));
    }

    [Fact]
    public void NonJsonStdout_PassesThroughUnchanged()
    {
        var lines = Render("plain text, not json");

        Assert.Single(lines);
        Assert.Equal("plain text, not json", lines[0].Text);
        Assert.Equal("stdout", lines[0].Stream);
    }

    [Fact]
    public void AgentMessage_PreservesUmlautsAndEmoji()
    {
        // Encoding edge case from the agent hints: non-ASCII must survive intact.
        var lines = Render("""{"type":"item.completed","item":{"type":"agent_message","text":"Grüße 🎉 fertig"}}""");

        Assert.Single(lines);
        Assert.Equal("Grüße 🎉 fertig", lines[0].Text);
    }
}
