using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Cli;
using Xunit;
using System.Linq;
using System;

namespace OrchestratorApi.Tests;

public class AntigravityCliServiceTests
{
    private static AntigravityCliService NewService()
    {
        var cfg = new ConfigurationBuilder().Build();
        return new AntigravityCliService(NullLogger<AntigravityCliService>.Instance, cfg);
    }

    [Fact]
    public void IsCompatibleSessionName_AcceptsUuidsRejectsEverythingElse()
    {
        var svc = NewService();

        Assert.True(svc.IsCompatibleSessionName("1936e314-4af2-4efb-b588-e1355a32ad16"));
        Assert.False(svc.IsCompatibleSessionName(null));
        Assert.False(svc.IsCompatibleSessionName(""));
        Assert.False(svc.IsCompatibleSessionName("latest"));
        Assert.False(svc.IsCompatibleSessionName("5"));
        Assert.False(svc.IsCompatibleSessionName("taskboard-claude-1234"));
    }

    [Fact]
    public void TransformReadLine_InitFrameYieldsSessionMarker()
    {
        var svc = NewService();
        var raw = new CliOutputLine
        {
            Stream = "stdout",
            Text = """{"conversationId":"abc12345-1111-2222-3333-444455556666"}""",
            Timestamp = DateTime.UtcNow
        };

        var lines = svc.TransformReadLine(raw).ToList();

        // Yields session init marker followed by raw JSON (because it's not a recognized type)
        Assert.Equal(2, lines.Count);
        Assert.Contains("Session init abc12345-1111-2222-3333-444455556666", lines[0].Text);
        Assert.Equal(raw.Text, lines[1].Text);
    }

    [Fact]
    public void TransformReadLine_AssistantMessageBecomesPlainText()
    {
        var svc = NewService();
        var raw = new CliOutputLine
        {
            Stream = "stdout",
            Text = """{"type":"message","role":"assistant","content":"PONG"}""",
            Timestamp = DateTime.UtcNow
        };

        var lines = svc.TransformReadLine(raw).ToList();

        Assert.Single(lines);
        Assert.Equal("PONG", lines[0].Text);
    }

    [Fact]
    public void TransformReadLine_UserMessageIsSuppressed()
    {
        var svc = NewService();
        var raw = new CliOutputLine
        {
            Stream = "stdout",
            Text = """{"type":"message","role":"user","content":"hi"}""",
            Timestamp = DateTime.UtcNow
        };

        Assert.Empty(svc.TransformReadLine(raw));
    }

    [Fact]
    public void TransformReadLine_ToolCallMapsToMarkerLine()
    {
        var svc = NewService();
        var raw = new CliOutputLine
        {
            Stream = "stdout",
            Text = """{"type":"tool_call","tool_name":"run_shell_command","parameters":{"command":"echo hello"}}""",
            Timestamp = DateTime.UtcNow
        };

        var lines = svc.TransformReadLine(raw).ToList();

        Assert.Single(lines);
        Assert.StartsWith("● Run", lines[0].Text);
        Assert.Contains("echo hello", lines[0].Text);
    }

    [Fact]
    public void TransformReadLine_ToolResultFailureSurfacesStatus()
    {
        var svc = NewService();
        var raw = new CliOutputLine
        {
            Stream = "stdout",
            Text = """{"type":"tool_result","status":"error"}""",
            Timestamp = DateTime.UtcNow
        };

        var lines = svc.TransformReadLine(raw).ToList();

        Assert.Single(lines);
        Assert.Contains("error", lines[0].Text);
    }

    [Fact]
    public void TransformReadLine_ResultFrame()
    {
        var svc = NewService();
        var raw = new CliOutputLine
        {
            Stream = "stdout",
            Text = """{"type":"result","status":"success"}""",
            Timestamp = DateTime.UtcNow
        };

        var lines = svc.TransformReadLine(raw).ToList();

        Assert.Single(lines);
        Assert.Equal("● Result success", lines[0].Text);
    }

    [Fact]
    public void TransformReadLine_ResponseBlockBecomesPlainText()
    {
        var svc = NewService();
        var raw = new CliOutputLine
        {
            Stream = "stdout",
            Text = """{"response":{"text":"Response Text Here"}}""",
            Timestamp = DateTime.UtcNow
        };

        var lines = svc.TransformReadLine(raw).ToList();

        Assert.Single(lines);
        Assert.Equal("Response Text Here", lines[0].Text);
    }

    [Fact]
    public void TransformReadLine_StderrPassesThroughUntouched()
    {
        var svc = NewService();
        var raw = new CliOutputLine
        {
            Stream = "stderr",
            Text = "Some stderr warning",
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
            Text = "Plain stdout status line",
            Timestamp = DateTime.UtcNow
        };

        var lines = svc.TransformReadLine(raw).ToList();

        Assert.Single(lines);
        Assert.Equal(raw, lines[0]);
    }
}
