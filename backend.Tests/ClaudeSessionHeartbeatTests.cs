
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Pin the path-encoding rule that turns a Windows working directory
/// into the folder name claude-code uses under
/// <c>~/.claude/projects/</c>. Empirical observation, not a documented
/// claude-code contract — if Anthropic changes the encoding, this test
/// fails loudly and the side-channel heartbeat helper goes back to the
/// drawing board.
/// </summary>
public class ClaudeSessionHeartbeatTests
{
    [Fact]
    public void ResolveSessionFile_EncodesWindowsPathAsObservedOnDisk()
    {
        // Empirical, copied from the live ~/.claude/projects/ folder:
        //   C:\Projects\agent-taskboard-devspace\agent-taskboard-dev
        //     -> C--Projects-agent-taskboard-devspace-agent-taskboard-dev
        var path = ClaudeSessionHeartbeat.ResolveSessionFile(
            sessionId: "abc-123",
            workingDirectory: @"C:\Projects\agent-taskboard-devspace\agent-taskboard-dev");

        Assert.NotNull(path);
        Assert.Contains("C--Projects-agent-taskboard-devspace-agent-taskboard-dev", path);
        Assert.EndsWith("abc-123.jsonl", path);
    }

    [Fact]
    public void ResolveSessionFile_ForwardSlashesEncodeToHyphens()
    {
        var path = ClaudeSessionHeartbeat.ResolveSessionFile("xyz", "/home/user/repo");
        Assert.NotNull(path);
        Assert.Contains("-home-user-repo", path);
    }

    [Fact]
    public void ResolveSessionFile_NullSessionId_ReturnsNonNullButPathHasNoUuid()
    {
        // Defensive: empty session id should not crash; caller checks
        // null/empty before instantiating the watcher.
        var path = ClaudeSessionHeartbeat.ResolveSessionFile("",
            @"C:\anything");
        Assert.NotNull(path);
        Assert.EndsWith(".jsonl", path);
    }
}
