using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RemoteEnvironmentPreparationRetryTests
{
    [Fact]
    public async Task Clone_or_worktree_failure_retries_three_times_then_escalates()
    {
        var attempts = 0;
        var logs = new List<string>();

        var failure = await Assert.ThrowsAsync<RemoteEnvironmentPreparationException>(() =>
            RemoteTaskRunner.RetryEnvironmentPreparationAsync<string>(
                _ =>
                {
                    attempts++;
                    throw new InvalidOperationException("clone authentication failed");
                },
                logs.Add,
                CancellationToken.None,
                (_, _) => Task.CompletedTask));

        Assert.Equal(RemoteTaskRunner.MaxEnvironmentPreparationAttempts, attempts);
        Assert.Equal(RemoteTaskRunner.MaxEnvironmentPreparationAttempts, failure.Attempts);
        Assert.Equal(RemoteTaskRunner.MaxEnvironmentPreparationAttempts, logs.Count);
        Assert.All(logs, line => Assert.Contains("remote-environment-preparation-failed", line));
    }

    [Fact]
    public async Task Successful_retry_returns_without_consuming_remaining_attempts()
    {
        var attempts = 0;

        var result = await RemoteTaskRunner.RetryEnvironmentPreparationAsync(
            _ =>
            {
                attempts++;
                if (attempts < 2)
                    throw new InvalidOperationException("transient fetch failure");
                return Task.FromResult("develop");
            },
            _ => { },
            CancellationToken.None,
            (_, _) => Task.CompletedTask);

        Assert.Equal("develop", result);
        Assert.Equal(2, attempts);
    }
}
