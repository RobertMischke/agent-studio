using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ProviderLimitStateTests
{
    [Theory]
    [InlineData("You've hit your session limit · resets 12:20am")]
    [InlineData("session limit reached, resets 00:20")]
    public void Parses_claude_session_limit_clock(string output)
    {
        var observed = new DateTime(2026, 8, 23, 22, 0, 0, DateTimeKind.Utc);

        var reset = ProviderLimitParser.ParseResetAt(output, observed);

        Assert.NotNull(reset);
        Assert.True(reset > observed);
        Assert.True(reset <= observed.AddHours(27));
    }

    [Fact]
    public void Parses_structured_claude_reset_epoch()
    {
        var reset = ProviderLimitParser.ParseResetAt(
            "status=rejected resetsAt=1787530800",
            new DateTime(2026, 8, 23, 20, 0, 0, DateTimeKind.Utc));

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1787530800).UtcDateTime, reset);
    }

    [Fact]
    public async Task Limit_clears_only_after_reset_and_successful_probe()
    {
        var root = Path.Combine(Path.GetTempPath(), $"provider-limit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var observed = new DateTime(2026, 8, 23, 22, 0, 0, DateTimeKind.Utc);
            var state = new ProviderLimitState(root);
            var limit = state.Observe("claude", "session limit reached", observed);
            var probes = 0;

            Assert.False(await state.ProbeRecoveryAsync(
                "claude",
                observed.AddMinutes(1),
                _ => { probes++; return Task.FromResult(true); },
                CancellationToken.None));
            Assert.Equal(0, probes);

            Assert.False(await state.ProbeRecoveryAsync(
                "claude",
                limit.LimitedUntil,
                _ => { probes++; return Task.FromResult(false); },
                CancellationToken.None));
            Assert.NotNull(state.Current("claude"));

            var retryAt = state.Current("claude")!.LimitedUntil;
            Assert.True(await state.ProbeRecoveryAsync(
                "claude",
                retryAt,
                _ => { probes++; return Task.FromResult(true); },
                CancellationToken.None));
            Assert.Null(state.Current("claude"));
            Assert.Equal(2, probes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Persisted_limit_self_resumes_after_runner_restart()
    {
        var root = Path.Combine(Path.GetTempPath(), $"provider-limit-restart-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var observed = new DateTime(2026, 8, 23, 22, 0, 0, DateTimeKind.Utc);
            var firstProcess = new ProviderLimitState(root);
            var limit = firstProcess.Observe(
                "claude",
                "You've hit your session limit · resets 12:20am",
                observed);

            var restartedProcess = new ProviderLimitState(root);
            Assert.Equal(limit.LimitedUntil, restartedProcess.Current("claude")?.LimitedUntil);
            Assert.True(await restartedProcess.ProbeRecoveryAsync(
                "claude",
                limit.LimitedUntil,
                _ => Task.FromResult(true),
                CancellationToken.None));

            Assert.Null(new ProviderLimitState(root).Current("claude"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
