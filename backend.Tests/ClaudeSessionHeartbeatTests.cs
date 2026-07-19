
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

    [Fact]
    public void ResolveSessionFile_CleanContext_WatchesConfigDir_NotDefaultHome()
    {
        // Regression guard for the "runs never complete / backlog never drains"
        // incident. Clean context is the DEFAULT: claude redirects its session
        // transcript to CLAUDE_CONFIG_DIR (a per-run temp dir). The liveness
        // watcher must resolve the session file UNDER that dir; watching the
        // default ~/.claude makes it see permanent silence on every clean run
        // and the watchdog kills the actively-working CLI mid-run (exit=-1 ->
        // InfraCrash -> escalate). See process-termination-scenarios.html.
        var configDir = Path.Combine(Path.GetTempPath(), "atp-clean-context", "claude-abc123");

        var path = ClaudeSessionHeartbeat.ResolveSessionFile("sess-1", @"C:\foo\bar", configDir);

        Assert.NotNull(path);
        Assert.StartsWith(configDir, path!);
        Assert.Contains(Path.Combine("projects", "C--foo-bar"), path!);
        Assert.EndsWith("sess-1.jsonl", path!);
        // CLAUDE_CONFIG_DIR replaces ~/.claude wholesale: no .claude segment.
        Assert.DoesNotContain(".claude", path!);
    }

    [Fact]
    public void ResolveSessionFile_DefaultContext_StillWatchesDotClaude()
    {
        var path = ClaudeSessionHeartbeat.ResolveSessionFile("sess-2", @"C:\foo\bar", configDir: null);

        Assert.NotNull(path);
        Assert.Contains(Path.Combine(".claude", "projects", "C--foo-bar"), path!);
        Assert.EndsWith("sess-2.jsonl", path!);
    }
}
