using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the pure parser that lifts Claude's stream-json init frame into a
/// <see cref="ClaudeInitContext"/> (ASS-1739 / T1a). The CLI already reports the
/// model, effective permission mode, cwd, and wired-in MCP servers in this
/// frame; the parser is the only thing that turns it into the read-only
/// execution-context surface, so a regression here silently empties the panel.
/// Fixtures are real frame shapes; the tests run in milliseconds with no live
/// process.
/// </summary>
public class ClaudeInitContextParserTests
{
    [Fact]
    public void FullInitFrame_ParsesScalarsAndMcpServers()
    {
        const string frame = """
        {"type":"system","subtype":"init","session_id":"a1b2c3d4-e5f6-4789-abcd-ef0123456789","cwd":"C:/work/repo","model":"claude-opus-4-8","permissionMode":"bypassPermissions","apiKeySource":"none","output_style":"default","mcp_servers":[{"name":"gmail","status":"connected"},{"name":"drive","status":"failed"}],"tools":["Read","Edit","Bash"],"slash_commands":["/help","/clear"]}
        """;

        Assert.True(ClaudeInitContextParser.TryParse(frame, out var ctx));
        Assert.NotNull(ctx);
        Assert.Equal("a1b2c3d4-e5f6-4789-abcd-ef0123456789", ctx!.SessionId);
        Assert.Equal("C:/work/repo", ctx.Cwd);
        Assert.Equal("claude-opus-4-8", ctx.Model);
        Assert.Equal("bypassPermissions", ctx.PermissionMode);
        Assert.Equal("none", ctx.ApiKeySource);
        Assert.Equal("default", ctx.OutputStyle);
        Assert.Equal(3, ctx.ToolCount);
        Assert.Equal(2, ctx.SlashCommandCount);
        Assert.Collection(ctx.McpServers,
            s => { Assert.Equal("gmail", s.Name); Assert.Equal("connected", s.Status); },
            s => { Assert.Equal("drive", s.Name); Assert.Equal("failed", s.Status); });
    }

    [Fact]
    public void PartialInitFrame_ToleratesMissingFields()
    {
        const string frame = """
        {"type":"system","subtype":"init","session_id":"x","model":"claude-sonnet-4-6"}
        """;

        Assert.True(ClaudeInitContextParser.TryParse(frame, out var ctx));
        Assert.NotNull(ctx);
        Assert.Equal("claude-sonnet-4-6", ctx!.Model);
        Assert.Null(ctx.Cwd);
        Assert.Null(ctx.PermissionMode);
        Assert.Empty(ctx.McpServers);
        Assert.Equal(0, ctx.ToolCount);
    }

    [Fact]
    public void NonInitSystemFrame_ReturnsFalse()
    {
        const string frame = """{"type":"system","subtype":"hello"}""";
        Assert.False(ClaudeInitContextParser.TryParse(frame, out var ctx));
        Assert.Null(ctx);
    }

    [Fact]
    public void NonSystemFrame_ReturnsFalse()
    {
        const string frame = """{"type":"assistant","message":{"content":[]}}""";
        Assert.False(ClaudeInitContextParser.TryParse(frame, out var ctx));
        Assert.Null(ctx);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{ broken json")]
    [InlineData(null)]
    public void MalformedOrEmpty_ReturnsFalse(string? line)
    {
        Assert.False(ClaudeInitContextParser.TryParse(line, out var ctx));
        Assert.Null(ctx);
    }

    [Fact]
    public void McpServerWithoutName_IsSkipped()
    {
        const string frame = """
        {"type":"system","subtype":"init","mcp_servers":[{"status":"connected"},{"name":"ok","status":"connected"}]}
        """;
        Assert.True(ClaudeInitContextParser.TryParse(frame, out var ctx));
        var server = Assert.Single(ctx!.McpServers);
        Assert.Equal("ok", server.Name);
    }
}
