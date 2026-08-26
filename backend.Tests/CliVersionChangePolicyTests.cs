using Xunit;

namespace AgentStudio.Tests;

public sealed class CliVersionChangePolicyTests
{
    [Fact]
    public void Evaluate_NoPriorVersion_RecordsObservationWithoutFalseChange()
    {
        var result = CliVersionChangePolicy.Evaluate(null, "codex-cli 0.149.0");

        Assert.True(result.Available);
        Assert.False(result.Changed);
        Assert.Null(result.PreviousVersion);
        Assert.Equal("codex-cli 0.149.0", result.CurrentVersion);
    }

    [Fact]
    public void Evaluate_DifferentVersion_ReportsDrift()
    {
        var result = CliVersionChangePolicy.Evaluate("codex-cli 0.144.1", "codex-cli 0.149.0");

        Assert.True(result.Available);
        Assert.True(result.Changed);
        Assert.Equal("codex-cli 0.144.1", result.PreviousVersion);
        Assert.Equal("codex-cli 0.149.0", result.CurrentVersion);
    }

    [Fact]
    public void Evaluate_WhitespaceOnlyCurrentVersion_ReportsUnavailable()
    {
        var result = CliVersionChangePolicy.Evaluate("claude 2.1.202", "  ");

        Assert.False(result.Available);
        Assert.False(result.Changed);
        Assert.Null(result.CurrentVersion);
    }
}
