using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Cli;
using Xunit;

namespace OrchestratorApi.Tests;

public class GeminiCliServiceTests
{
    private static GeminiCliService NewService()
    {
        var cfg = new ConfigurationBuilder().Build();
        return new GeminiCliService(NullLogger<GeminiCliService>.Instance, cfg);
    }

    [Fact]
    public void IsCompatibleSessionName_AcceptsUuidsRejectsEverythingElse()
    {
        var svc = NewService();

        Assert.True(svc.IsCompatibleSessionName("1936e314-4af2-4efb-b588-e1355a32ad16"));
        Assert.False(svc.IsCompatibleSessionName(null));
        Assert.False(svc.IsCompatibleSessionName(""));
        Assert.False(svc.IsCompatibleSessionName("latest"));     // valid for the CLI but cross-CLI compat says no
        Assert.False(svc.IsCompatibleSessionName("5"));          // numeric index — same reason
        Assert.False(svc.IsCompatibleSessionName("taskboard-claude-1234")); // copilot-style slug
    }

    [Fact]
    public void TransformReadLine_InitFrameYieldsSessionMarker()
    {
        var svc = NewService();
        var raw = new CliOutputLine
        {
            Stream = "stdout",
            Text = """{"type":"init","timestamp":"2026-04-28T11:07:03Z","session_id":"abc12345-1111-2222-3333-444455556666","model":"auto-gemini-3"}""",
            Timestamp = DateTime.UtcNow
        };

        var lines = svc.TransformReadLine(raw).ToList();

        Assert.Single(lines);
        Assert.Contains("Session init abc12345-1111-2222-3333-444455556666", lines[0].Text);
        Assert.Contains("auto-gemini-3", lines[0].Text);
    }

    [Fact]
    public void TransformReadLine_AssistantMessageBecomesPlainText()
    {
        var svc = NewService();
        var raw = new CliOutputLine
        {
            Stream = "stdout",
            Text = """{"type":"message","role":"assistant","content":"PONG","delta":true}""",
            Timestamp = DateTime.UtcNow
        };

        var lines = svc.TransformReadLine(raw).ToList();

        Assert.Single(lines);
        Assert.Equal("PONG", lines[0].Text);
    }

    [Fact]
    public void TransformReadLine_UserMessageIsSuppressed()
    {
        // The CLI echoes our own prompt back as a user message — there's no
        // value in seeing it twice in the Activity Log.
        var svc = NewService();
        var raw = new CliOutputLine
        {
            Stream = "stdout",
            Text = """{"type":"message","role":"user","content":"Reply with PONG"}""",
            Timestamp = DateTime.UtcNow
        };

        Assert.Empty(svc.TransformReadLine(raw));
    }

    [Fact]
    public void TransformReadLine_ResultFrameIncludesTokensAndDuration()
    {
        var svc = NewService();
        var raw = new CliOutputLine
        {
            Stream = "stdout",
            Text = """{"type":"result","status":"success","stats":{"total_tokens":7170,"input_tokens":6952,"output_tokens":37,"duration_ms":4590,"tool_calls":0}}""",
            Timestamp = DateTime.UtcNow
        };

        var lines = svc.TransformReadLine(raw).ToList();

        Assert.Single(lines);
        Assert.Contains("Result success", lines[0].Text);
        Assert.Contains("7170 tokens", lines[0].Text);
        Assert.Contains("4590ms", lines[0].Text);
        Assert.Equal("stdout", lines[0].Stream);
    }

    [Fact]
    public void TransformReadLine_StderrPassesThroughUntouched()
    {
        // The "YOLO mode is enabled." warning prints on stderr — must not be
        // swallowed by the JSON parser branch.
        var svc = NewService();
        var raw = new CliOutputLine
        {
            Stream = "stderr",
            Text = "YOLO mode is enabled. All tool calls will be automatically approved.",
            Timestamp = DateTime.UtcNow
        };

        var lines = svc.TransformReadLine(raw).ToList();

        Assert.Single(lines);
        Assert.Equal(raw, lines[0]);
    }

    [Fact]
    public void TransformReadLine_NonJsonStdoutPassesThroughUntouched()
    {
        var svc = NewService();
        var raw = new CliOutputLine
        {
            Stream = "stdout",
            Text = "Some plain status line",
            Timestamp = DateTime.UtcNow
        };

        var lines = svc.TransformReadLine(raw).ToList();

        Assert.Single(lines);
        Assert.Equal(raw, lines[0]);
    }

    [Fact]
    public void TransformReadLine_ToolUseMapsToMarkerLine_RealFrameShape()
    {
        // Frame shape verified against gemini-cli v0.39.1: tool_name + parameters,
        // not name + input as in Claude's stream-json.
        var svc = NewService();
        var raw = new CliOutputLine
        {
            Stream = "stdout",
            Text = """{"type":"tool_use","tool_name":"run_shell_command","tool_id":"x","parameters":{"command":"echo hello"}}""",
            Timestamp = DateTime.UtcNow
        };

        var lines = svc.TransformReadLine(raw).ToList();

        Assert.Single(lines);
        Assert.StartsWith("● Run", lines[0].Text);
        Assert.Contains("echo hello", lines[0].Text);
    }

    [Fact]
    public void TransformReadLine_ToolResultSuccessProducesNoLine()
    {
        // Success tool_result frames are payload-less in Gemini — nothing to log.
        var svc = NewService();
        var raw = new CliOutputLine
        {
            Stream = "stdout",
            Text = """{"type":"tool_result","tool_id":"x","status":"success"}""",
            Timestamp = DateTime.UtcNow
        };

        Assert.Empty(svc.TransformReadLine(raw));
    }

    [Fact]
    public void TransformReadLine_ToolResultFailureSurfacesStatus()
    {
        var svc = NewService();
        var raw = new CliOutputLine
        {
            Stream = "stdout",
            Text = """{"type":"tool_result","tool_id":"x","status":"error"}""",
            Timestamp = DateTime.UtcNow
        };

        var lines = svc.TransformReadLine(raw).ToList();

        Assert.Single(lines);
        Assert.Contains("error", lines[0].Text);
    }
}
