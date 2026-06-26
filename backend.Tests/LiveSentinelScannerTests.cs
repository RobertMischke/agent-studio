using AgentStudio.Shared;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the fix for the dominant "tasks never complete" incident: the live
/// sentinel scanner must NOT fire on a [[TASK_DONE]] that appears in file
/// content the agent read (the backend code/AGENTS.md are full of sentinel
/// literals) or in agent prose - only on the agent's OWN standalone terminal
/// sentinel line. See docs/wiki/concepts/runner-stability-incidents.html.
/// </summary>
public class LiveSentinelScannerTests
{
    private static CliOutputLine L(string stream, string text) => new() { Stream = stream, Text = text };

    [Fact]
    public void Sentinel_InToolResult_UserStream_DoesNotFire()
    {
        // The exact root cause: the agent READ a file (tool result -> "user"
        // stream) whose content contains the literal. This must NOT stop the run.
        var lines = new[]
        {
            L("assistant", "Let me read the analyzer."),
            L("user", "    if (DonePattern.IsMatch(tail)) return Done; // matches [[TASK_DONE]] sentinel"),
            L("assistant", "Now I'll continue analyzing the next file."),
        };
        Assert.False(LiveSentinelScanner.HasStandaloneAgentSentinel(lines));
    }

    [Fact]
    public void Sentinel_MentionedInAgentProse_DoesNotFire()
    {
        var lines = new[]
        {
            L("assistant", "When I'm finished I will emit [[TASK_DONE]] on its own line, but I'm not done yet."),
        };
        Assert.False(LiveSentinelScanner.HasStandaloneAgentSentinel(lines));
    }

    [Theory]
    [InlineData("[[TASK_DONE]]")]
    [InlineData("  [[TASK_DONE]]  ")]
    [InlineData("**[[TASK_DONE]]**")]
    [InlineData("> [[TASK_BLOCKED: cannot find the file]]")]
    [InlineData("[[TASK_NEEDS_INPUT: which branch?]]")]
    public void StandaloneAgentSentinelLine_Fires(string sentinelLine)
    {
        var lines = new[]
        {
            L("assistant", "All files analyzed; writing the report."),
            L("assistant", sentinelLine),
        };
        Assert.True(LiveSentinelScanner.HasStandaloneAgentSentinel(lines));
    }

    [Fact]
    public void StandaloneSentinel_OnSystemStream_DoesNotFire()
    {
        // The init/system frame can echo the contract; never treat it as terminal.
        var lines = new[] { L("system", "[[TASK_DONE]]") };
        Assert.False(LiveSentinelScanner.HasStandaloneAgentSentinel(lines));
    }

    [Fact]
    public void EmptyOrNull_DoesNotFire()
    {
        Assert.False(LiveSentinelScanner.HasStandaloneAgentSentinel(null));
        Assert.False(LiveSentinelScanner.HasStandaloneAgentSentinel(System.Array.Empty<CliOutputLine>()));
    }
}
