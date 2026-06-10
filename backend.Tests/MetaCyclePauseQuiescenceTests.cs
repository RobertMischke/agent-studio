using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the pause-then-wait contract for the meta-cycle: after PausePickup
/// blocks new pickups, the cycle must wait for the runner's active job to
/// finish before inspection or any UpdateStableThenResume action. Without
/// this wait, a freshly paused project still has a CLI process running and
/// the script that tears down the backend kills it mid-flight.
/// </summary>
public class MetaCyclePauseQuiescenceTests
{
    [Fact]
    public async Task NoActiveJob_ReturnsAlreadyIdle_Immediately()
    {
        var time = new FakeTimeProvider();
        var outcome = await MetaCycleHostedService.WaitForQuiescenceAsync(
            getActiveJobId: () => null,
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromSeconds(1),
            time: time,
            ct: CancellationToken.None);

        Assert.Equal(MetaCycleHostedService.QuiescenceWaitResult.AlreadyIdle, outcome.Result);
        Assert.Null(outcome.LastSeenActiveJobId);
        Assert.Equal(TimeSpan.Zero, outcome.Waited);
    }

    [Fact]
    public async Task ActiveJobThatFinishes_ReturnsBecameIdle()
    {
        var time = new FakeTimeProvider();
        var calls = 0;
        Func<string?> getActive = () =>
        {
            calls++;
            // First two probes see a running job; third probe sees null.
            return calls < 3 ? "job-1" : null;
        };

        var waitTask = MetaCycleHostedService.WaitForQuiescenceAsync(
            getActiveJobId: getActive,
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromSeconds(2),
            time: time,
            ct: CancellationToken.None);

        // Step the fake clock past two poll intervals so the wait loop
        // observes the third call returning null.
        while (!waitTask.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(2));
            await Task.Yield();
        }

        var outcome = await waitTask;
        Assert.Equal(MetaCycleHostedService.QuiescenceWaitResult.BecameIdle, outcome.Result);
        Assert.Equal("job-1", outcome.LastSeenActiveJobId);
        Assert.True(outcome.Waited > TimeSpan.Zero);
    }

    [Fact]
    public async Task ActiveJobThatNeverFinishes_TimesOut_AndReturnsLastSeenId()
    {
        var time = new FakeTimeProvider();
        var waitTask = MetaCycleHostedService.WaitForQuiescenceAsync(
            getActiveJobId: () => "stuck-job",
            timeout: TimeSpan.FromSeconds(10),
            pollInterval: TimeSpan.FromSeconds(2),
            time: time,
            ct: CancellationToken.None);

        while (!waitTask.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(2));
            await Task.Yield();
        }

        var outcome = await waitTask;
        Assert.Equal(MetaCycleHostedService.QuiescenceWaitResult.TimedOut, outcome.Result);
        Assert.Equal("stuck-job", outcome.LastSeenActiveJobId);
        Assert.True(outcome.Waited >= TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Cancellation_PropagatesAsOperationCanceled()
    {
        var time = new FakeTimeProvider();
        using var cts = new CancellationTokenSource();

        var waitTask = MetaCycleHostedService.WaitForQuiescenceAsync(
            getActiveJobId: () => "still-busy",
            timeout: TimeSpan.FromMinutes(30),
            pollInterval: TimeSpan.FromSeconds(2),
            time: time,
            ct: cts.Token);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waitTask);
    }
}
