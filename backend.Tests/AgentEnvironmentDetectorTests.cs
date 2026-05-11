using OrchestratorApi.Models;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

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
