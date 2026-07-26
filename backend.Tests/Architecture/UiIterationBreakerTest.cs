using Xunit;

namespace AgentStudio.Tests.Architecture;

public sealed class UiIterationBreakerTest
{
    [Fact]
    public void FeedbackContinuation_AtConfiguredCap_MustEscalate()
    {
        var review = new UiIterationReviewContract
        {
            Iteration = UiIterationGate.DefaultMaxIterations,
            MaxIterations = UiIterationGate.DefaultMaxIterations,
            CapReached = true,
        };

        Assert.True(UiIterationGate.MustEscalateFeedbackContinuation(review));
        Assert.Equal(UiIterationGateAction.EscalateCapReached,
            UiIterationGate.Evaluate(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                UiIterationGate.DefaultMaxIterations + 1,
                UiIterationGate.DefaultMaxIterations).Action);
    }
}
