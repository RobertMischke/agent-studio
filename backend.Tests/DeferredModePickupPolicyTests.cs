using Xunit;

namespace AgentStudio.Tests;

public sealed class DeferredModePickupPolicyTests
{
    [Theory]
    [InlineData("manual")]
    [InlineData("paused")]
    public void PendingManualSideChange_BlocksNewAutoPick(string pendingMode)
        => Assert.False(DeferredModePickupPolicy.AllowsAutoPickup(pendingMode));

    [Theory]
    [InlineData(null)]
    [InlineData("auto-single")]
    [InlineData("auto-continuous")]
    public void NoPendingManualSideChange_AllowsAutoPick(string? pendingMode)
        => Assert.True(DeferredModePickupPolicy.AllowsAutoPickup(pendingMode));
}
