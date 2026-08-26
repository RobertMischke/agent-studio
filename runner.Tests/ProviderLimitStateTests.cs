using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ProviderLimitStateTests
{
    [Fact]
    public void Claude_limit_closes_only_claude_and_exposes_one_half_open_probe_at_reset()
    {
        var now = new DateTimeOffset(2026, 8, 23, 22, 0, 0, TimeSpan.Zero);
        var limits = new ProviderLimitState();

        var limited = limits.ObserveLimit(
            AgentCliProcess.ClaudeCli,
            "You've hit your session limit; resets_at=2026-08-23T22:20:00Z",
            now);

        Assert.Equal(ProviderLimitStates.Limited, limited.State);
        Assert.False(limited.ClaimEligible);
        Assert.Null(limits.Current(AgentCliProcess.CodexCli, now));

        var halfOpen = limits.Current(AgentCliProcess.ClaudeCli, now.AddMinutes(20));
        Assert.Equal(ProviderLimitStates.HalfOpen, halfOpen!.State);
        Assert.True(halfOpen.ClaimEligible);
        Assert.True(limits.TryBeginHalfOpenClaim(AgentCliProcess.ClaudeCli, now.AddMinutes(20)));
        Assert.False(limits.TryBeginHalfOpenClaim(AgentCliProcess.ClaudeCli, now.AddMinutes(20)));

        limits.ObserveOutcome(
            AgentCliProcess.ClaudeCli,
            SuccessfulDecision(),
            "[[TASK_DONE]]",
            now.AddMinutes(21));

        Assert.Null(limits.Current(AgentCliProcess.ClaudeCli, now.AddMinutes(21)));
    }

    [Fact]
    public void Inconclusive_half_open_probe_schedules_another_probe_instead_of_sticking()
    {
        var now = new DateTimeOffset(2026, 8, 23, 22, 0, 0, TimeSpan.Zero);
        var limits = new ProviderLimitState();
        limits.ObserveLimit(
            AgentCliProcess.ClaudeCli,
            "You've hit your session limit; resets_at=2026-08-23T22:20:00Z",
            now);
        Assert.True(limits.TryBeginHalfOpenClaim(
            AgentCliProcess.ClaudeCli,
            now.AddMinutes(20)));

        var inconclusive = ExecutionOutcomeAdapter.Classify(new ExecutionRawFacts(
            "attempt-2",
            ExecutionAttemptKind.Coding,
            StdErr: "temporary transport failure",
            ExitCode: 1));
        limits.ObserveOutcome(
            AgentCliProcess.ClaudeCli,
            inconclusive,
            "temporary transport failure",
            now.AddMinutes(20));

        var current = Assert.IsType<ProviderLimitSnapshot>(limits.Current(
            AgentCliProcess.ClaudeCli,
            now.AddMinutes(21)));
        Assert.Equal(ProviderLimitStates.Limited, current.State);
        Assert.Equal(now.AddMinutes(25), current.RetryAt);
    }

    [Fact]
    public void Limit_survives_daemon_restart_until_its_reset()
    {
        var root = Path.Combine(Path.GetTempPath(), $"provider-limit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var now = new DateTimeOffset(2026, 8, 23, 22, 0, 0, TimeSpan.Zero);
            var beforeRestart = new ProviderLimitState(root);
            beforeRestart.ObserveLimit(
                AgentCliProcess.ClaudeCli,
                "You've hit your session limit; resets_at=2026-08-23T22:20:00Z",
                now);

            var afterRestart = new ProviderLimitState(root);
            var restored = Assert.IsType<ProviderLimitSnapshot>(afterRestart.Current(
                AgentCliProcess.ClaudeCli,
                now.AddMinutes(5)));

            Assert.Equal(ProviderLimitStates.Limited, restored.State);
            Assert.Equal(now.AddMinutes(20), restored.RetryAt);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ExecutionOutcomeDecision SuccessfulDecision()
        => ExecutionOutcomeAdapter.Classify(new ExecutionRawFacts(
            "attempt-1",
            ExecutionAttemptKind.Coding,
            FinalAssistantOutput: "[[TASK_DONE]]",
            StdOut: "[[TASK_DONE]]",
            ExitCode: 0));
}
