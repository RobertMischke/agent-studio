using Xunit;

namespace AgentStudio.Tests.Architecture;

public sealed class QualityAnalysisRetryBreakerTest
{
    [Fact]
    public void AngularRuleSteeredRetryBudget_IsExactlyOne()
    {
        Assert.Equal(1, QualityAnalysisSteeredRetryPolicy.MaxAutomaticRetries);
    }
}
