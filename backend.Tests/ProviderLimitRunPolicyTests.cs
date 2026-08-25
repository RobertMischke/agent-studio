using Xunit;

namespace AgentStudio.Tests;

public sealed class ProviderLimitRunPolicyTests
{
    [Theory]
    [InlineData(RunIssueKind.QuotaExhausted, "claude", ProviderLimitRunDisposition.WaitWithoutTaskFailure)]
    [InlineData(RunIssueKind.QuotaExhausted, null, ProviderLimitRunDisposition.ContinueOutcomePolicy)]
    [InlineData(RunIssueKind.InfraCrash, "claude", ProviderLimitRunDisposition.ContinueOutcomePolicy)]
    [InlineData(RunIssueKind.OrchestratorInconclusive, "claude", ProviderLimitRunDisposition.ContinueOutcomePolicy)]
    public void Account_limit_is_the_only_run_that_bypasses_task_failure_policy(
        RunIssueKind issueKind,
        string? cliType,
        ProviderLimitRunDisposition expected)
    {
        Assert.Equal(expected, ProviderLimitRunPolicy.Decide(issueKind, cliType));
    }

    [Theory]
    [InlineData("You've hit your session limit · resets 12:20am")]
    [InlineData("rate_limit_exceeded; reset in 2 h")]
    public void Reset_parser_schedules_a_future_retry(string output)
    {
        var observed = new DateTime(2026, 8, 23, 22, 0, 0, DateTimeKind.Utc);

        var reset = ProjectRunner.ParseProviderLimitResetAt(output, observed);

        Assert.NotNull(reset);
        Assert.True(reset > observed);
    }
}
