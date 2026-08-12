using Xunit;

namespace AgentStudio.Tests.Architecture;

public sealed class VisualQaRetryBreakerTest
{
    [Fact]
    public void VisualDefectAutoRetryBudget_IsExactlyOne()
    {
        Assert.Equal(1, VisualQaPolicy.MaxAutomaticDefectRetries);
    }
}
