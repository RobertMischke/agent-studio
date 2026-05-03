using OrchestratorApi.Models;
using OrchestratorApi.Services;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the inflight-guard rule so two concurrent summary calls for the
/// same job cannot fire two Haiku subprocesses against the same status.md.
/// The bug this guards against was visible as the protocol-pane "Generating
/// protocol..." spinner stalling for the full HaikuTimeoutSeconds window
/// while a stale call held the slot.
/// </summary>
public class SummaryGenerationInflightTests
{
    private static readonly DateTime Now = new(2026, 5, 3, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NoPriorState_NotInflight()
    {
        Assert.False(SummaryGenerationService.IsInflight(null, Now, 90));
    }

    [Fact]
    public void Generating_StartedJustNow_IsInflight()
    {
        var prev = new JobSummaryState
        {
            Status = JobSummaryStatus.Generating,
            StartedAt = Now.AddSeconds(-5)
        };
        Assert.True(SummaryGenerationService.IsInflight(prev, Now, 90));
    }

    [Fact]
    public void Generating_StartedLongerAgoThanTimeout_NotInflight()
    {
        var prev = new JobSummaryState
        {
            Status = JobSummaryStatus.Generating,
            StartedAt = Now.AddSeconds(-200)
        };
        // Treated as stuck so the user can recover by hitting Regenerate.
        Assert.False(SummaryGenerationService.IsInflight(prev, Now, 90));
    }

    [Fact]
    public void Ready_NeverInflight()
    {
        var prev = new JobSummaryState
        {
            Status = JobSummaryStatus.Ready,
            StartedAt = Now.AddSeconds(-1)
        };
        Assert.False(SummaryGenerationService.IsInflight(prev, Now, 90));
    }

    [Fact]
    public void Failed_NeverInflight()
    {
        var prev = new JobSummaryState
        {
            Status = JobSummaryStatus.Failed,
            StartedAt = Now.AddSeconds(-1)
        };
        Assert.False(SummaryGenerationService.IsInflight(prev, Now, 90));
    }

    [Fact]
    public void Generating_WithoutStartedAt_NotInflight()
    {
        // Defensive: an upstream that forgot to set StartedAt must not
        // wedge the slot forever.
        var prev = new JobSummaryState { Status = JobSummaryStatus.Generating };
        Assert.False(SummaryGenerationService.IsInflight(prev, Now, 90));
    }
}
