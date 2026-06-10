using AgentStudio.Cli;
using AgentStudio.Shared;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the per-CLI completion-contract registry served by
/// <c>GET /api/cli/contracts</c>. Pure data, no I/O — these run on every
/// default <c>dotnet test</c>. The contract strings mirror the live adapter
/// mappings (see <c>Execution/Adapters/*EventAdapter.cs</c>); if an adapter's
/// completion frame changes, the registry and this test move with it.
/// </summary>
public class CliCompletionContractsTests
{
    [Fact]
    public void CoversEverySupportedCliExactlyOnce()
    {
        var covered = CliCompletionContracts.All.Select(c => c.CliType).ToList();
        Assert.Equal(CliTypes.All.Length, covered.Count);
        Assert.Equal(CliTypes.All.Length, covered.Distinct().Count());
        foreach (var cli in CliTypes.All)
            Assert.Contains(cli, covered);
    }

    [Fact]
    public void EveryFieldIsPopulated()
    {
        foreach (var c in CliCompletionContracts.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Transport), $"{c.CliType}: Transport");
            Assert.False(string.IsNullOrWhiteSpace(c.SessionStartSignal), $"{c.CliType}: SessionStartSignal");
            Assert.False(string.IsNullOrWhiteSpace(c.CompletionSignal), $"{c.CliType}: CompletionSignal");
            Assert.False(string.IsNullOrWhiteSpace(c.FailureSignal), $"{c.CliType}: FailureSignal");
            Assert.False(string.IsNullOrWhiteSpace(c.UsageSource), $"{c.CliType}: UsageSource");
            Assert.False(string.IsNullOrWhiteSpace(c.Notes), $"{c.CliType}: Notes");
        }
    }

    [Theory]
    [InlineData(CliTypes.Claude, "result")]
    [InlineData(CliTypes.Codex, "turn.completed")]
    [InlineData(CliTypes.Gemini, "result")]
    public void TypedAdaptersExposeTheirCompletionFrame(string cliType, string completionFrame)
    {
        var c = CliCompletionContracts.All.Single(x => x.CliType == cliType);
        Assert.True(c.Typed);
        Assert.Contains(completionFrame, c.CompletionSignal);
    }

    [Fact]
    public void CopilotIsNotTyped_AndIsExitBased()
    {
        var copilot = CliCompletionContracts.All.Single(x => x.CliType == CliTypes.Copilot);
        Assert.False(copilot.Typed);
        Assert.Contains("exit", copilot.CompletionSignal, StringComparison.OrdinalIgnoreCase);
    }
}
