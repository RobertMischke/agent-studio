

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the canonical sandbox / OS-permission blocker recognition.
/// Adding a new pattern is a one-line append in
/// <see cref="AgentEnvironmentDetector.Patterns"/>; this test pins the
/// existing pattern set so a regex regression cannot silently break the
/// runtime hook in <c>CliExecutionServiceBase.CheckEnvironmentBlocker</c>.
/// </summary>
public class AgentEnvironmentDetectorTests
{
    [Theory]
    [InlineData("windows sandbox: runner error: CreateProcessAsUserW failed: 1312", "codex-windows-sandbox")]
    [InlineData("error: CreateProcessAsUserW failed: 1312 (winapi 0x520)", "windows-logon-1312")]
    [InlineData("Codex refused: sandbox_permissions denies disk-full-access", "codex-sandbox-permissions")]
    [InlineData("  └ Permission denied and could not request permission from user", "claude-permission-denied-tool")]
    [InlineData("Error: EACCES: permission denied, open '/etc/shadow'", "posix-eacces")]
    [InlineData("Operation not permitted (EPERM)", "posix-eperm")]
    [InlineData("D:\\Projects: Access is denied.", "windows-access-denied")]
    public void IsSandboxBlocker_FiresForEveryCanonicalPattern(string line, string expectedId)
    {
        var match = AgentEnvironmentDetector.Match(line);
        Assert.NotNull(match);
        Assert.Equal(expectedId, match!.Id);
        Assert.True(AgentEnvironmentDetector.IsSandboxBlocker(line));
    }

    [Theory]
    [InlineData("Reading files…")]
    [InlineData("[[TASK_DONE]]")]
    [InlineData("")]
    [InlineData(null)]
    public void IsSandboxBlocker_DoesNotFireOnNormalAgentOutput(string? line)
    {
        Assert.Null(AgentEnvironmentDetector.Match(line));
        Assert.False(AgentEnvironmentDetector.IsSandboxBlocker(line));
    }

    [Fact]
    public void MatchRuntimeBlocker_SkipsAgentCommandOutputContainingNeedle()
    {
        // 2026-06-02 regression: a re-queued codex/claude ticket grepped this
        // detector's own tests, so the needle surfaced inside a codex
        // command_execution event and self-tripped codex-windows-sandbox,
        // killing the run. The runtime entry point must ignore tool-echo lines.
        const string toolEcho =
            "{\"type\":\"item.completed\",\"item\":{\"id\":\"item_1\",\"type\":\"command_execution\"," +
            "\"command\":\"rg -n windows-sandbox\",\"aggregated_output\":" +
            "\"AgentEnvironmentDetectorTests.cs:17: windows sandbox: runner error: CreateProcessAsUserW failed: 1312\"}}";

        Assert.True(AgentEnvironmentDetector.IsAgentToolEcho(toolEcho));
        Assert.Null(AgentEnvironmentDetector.MatchRuntimeBlocker(toolEcho));
        // The pure pattern matcher still sees the needle — the guard lives one layer up.
        Assert.NotNull(AgentEnvironmentDetector.Match(toolEcho));
    }

    [Fact]
    public void MatchRuntimeBlocker_StillFiresForBareRuntimeErrorLine()
    {
        // A genuine wrapper/stderr error (the shape the canonical patterns
        // target) is NOT tool-echo and must still trip the blocker.
        const string runtimeErr = "windows sandbox: runner error: CreateProcessAsUserW failed: 1312";
        Assert.False(AgentEnvironmentDetector.IsAgentToolEcho(runtimeErr));
        var m = AgentEnvironmentDetector.MatchRuntimeBlocker(runtimeErr);
        Assert.NotNull(m);
        Assert.Equal("codex-windows-sandbox", m!.Id);
    }

    [Theory]
    [InlineData("Error: EACCES: permission denied, open '/etc/shadow'")]   // bare stderr
    [InlineData("Reading files…")]                                          // not JSON
    [InlineData("{\"type\":\"agent_message\",\"text\":\"done\"}")]          // JSON but not tool I/O
    public void IsAgentToolEcho_FalseForNonToolLines(string line)
    {
        Assert.False(AgentEnvironmentDetector.IsAgentToolEcho(line));
    }

    [Theory]
    [InlineData("Error: EACCES: permission denied, open '/etc/shadow'")]    // real eacces
    [InlineData("EPERM: operation not permitted, unlink '/srv/x'")]         // real eperm
    public void MatchRuntimeBlocker_FiresForRealPosixPermissionError(string line)
    {
        // A genuine bare-stderr permission error still trips the runtime hook.
        Assert.False(AgentEnvironmentDetector.IsAgentToolEcho(line));
        Assert.NotNull(AgentEnvironmentDetector.MatchRuntimeBlocker(line));
    }

    [Theory]
    [InlineData("I'll add the EACCES needle to AgentEnvironmentDetector")]  // agent narration
    [InlineData("    Id: \"posix-eacces\",")]                                // editing this detector's source
    [InlineData("rg -n EACCES backend/ shows 4 hits")]                       // grep narration
    [InlineData("the EPERM path should also be hardened")]                   // narration
    public void MatchRuntimeBlocker_SkipsBareTokenWithoutDenialIndicator(string line)
    {
        // 2026-06-09 regression: the c7ccd2b7-gate hardening task (and others
        // that edit/narrate the token) self-tripped posix-eacces and were killed
        // even though no real permission error occurred. Match() (pure) still sees
        // the needle for the lock tests, but the runtime hook must NOT trip without
        // a co-located permission-denial indicator.
        Assert.NotNull(AgentEnvironmentDetector.Match(line));
        Assert.Null(AgentEnvironmentDetector.MatchRuntimeBlocker(line));
    }

    [Fact]
    public void IsAgentToolEcho_TrueForClaudeToolResult()
    {
        const string toolResult =
            "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\"," +
            "\"content\":\"EACCES: permission denied\"}]}}";
        Assert.True(AgentEnvironmentDetector.IsAgentToolEcho(toolResult));
        Assert.Null(AgentEnvironmentDetector.MatchRuntimeBlocker(toolResult));
    }

    [Theory]
    [InlineData("file has been updated successfully")]
    [InlineData("File updated successfully")]
    [InlineData("AgentMessageBusStore.cs has been updated successfully")]
    public void IsRecoverySignal_FiresForSuccessfulEditAfterTransientEacces(string line)
    {
        Assert.True(AgentEnvironmentDetector.IsRecoverySignal(line));
    }

    [Fact]
    public void Diagnose_IncludesCliTypeAndRecoveryHint()
    {
        var match = AgentEnvironmentDetector.Match("windows sandbox: runner error: CreateProcessAsUserW failed: 1312");
        Assert.NotNull(match);
        var diagnosis = AgentEnvironmentDetector.Diagnose(match!, "codex");
        Assert.Contains("codex", diagnosis, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sandbox_mode", diagnosis, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImmediateTerminate_IsSetForUnambiguousCodexAndLogonPatterns()
    {
        var codex = AgentEnvironmentDetector.Match("windows sandbox: runner error: foo");
        var logon = AgentEnvironmentDetector.Match("CreateProcessAsUserW failed: 1312");
        Assert.True(codex!.ImmediateTerminate);
        Assert.True(logon!.ImmediateTerminate);
    }

    [Fact]
    public void AnalyzerPicksUpSyntheticMarker_AsEnvironmentBlocker()
    {
        // The runtime detector writes a synthetic
        // [environment-blocker] <diagnosis> system line; the analyzer
        // must classify the run as Unknown + EnvironmentBlocker.
        var diagnosis = AgentEnvironmentDetector.Diagnose(
            AgentEnvironmentDetector.Match("windows sandbox: runner error: x")!, "codex");
        var ts = DateTime.UtcNow;
        var lines = new List<CliOutputLine>
        {
            new() { Timestamp = ts, Stream = "stdout", Text = "windows sandbox: runner error: x" },
            new() { Timestamp = ts.AddMilliseconds(1), Stream = "system", Text = $"[environment-blocker] {diagnosis}" }
        };
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, status: "stopped", durationSeconds: 3.2);
        Assert.Equal(RunIssueKind.EnvironmentBlocker, outcome.IssueKind);
        Assert.False(outcome.MatchedSentinel);
        Assert.Contains("codex", outcome.Summary ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }
}
