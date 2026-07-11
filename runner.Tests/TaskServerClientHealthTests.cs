using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public class TaskServerClientHealthTests
{
    /// <summary>
    /// An unreachable server (a closed loopback port stands in for a dropped
    /// reverse tunnel) must be reported as a clean, non-null health reason and
    /// must never throw - that is what lets the runner surface "connection lost"
    /// once instead of a transport-exception cascade through register/lease.
    /// </summary>
    [Fact]
    public async Task Probe_reports_a_reason_when_the_server_is_unreachable()
    {
        // Port 1 is reserved and never listening: connect fails fast (refused).
        var (options, _, _, _) = RunnerOptions.Parse(["--health-check", "--server", "http://127.0.0.1:1"]);
        using var client = new TaskServerClient(options);

        var reason = await client.ProbeHealthAsync(CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public async Task Probe_propagates_a_real_shutdown_rather_than_masking_it_as_a_health_failure()
    {
        var (options, _, _, _) = RunnerOptions.Parse(["--health-check", "--server", "http://127.0.0.1:1"]);
        using var client = new TaskServerClient(options);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.ProbeHealthAsync(cancelled.Token));
    }
}
