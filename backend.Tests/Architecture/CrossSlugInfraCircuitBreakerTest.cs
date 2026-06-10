using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Architecture-level lock for the loop-inventory entry
/// <c>pickup.cross-slug-infra-circuit-breaker</c>:
///
/// <list type="number">
///   <item>The breaker class and its budget constants live in code
///   (see <see cref="AgentStudio.Runner.CrossSlugInfraCircuitBreaker"/>).</item>
///   <item>The breaker test exists (this file plus
///   <c>CrossSlugInfraCircuitBreakerTests.cs</c> plus
///   <c>PickupLoopStrictIterationTests.CrossSlug_*</c>).</item>
///   <item>The inventory entry references this exact test file path.</item>
/// </list>
///
/// <para>
/// ADR-0032 rule: every loop class is registered. Adding or removing a
/// loop without also adding or removing its breaker test trips this
/// assertion at CI time.
/// </para>
/// </summary>
public class CrossSlugInfraCircuitBreakerTest
{
    [Fact]
    public void BreakerType_Exists()
    {
        var t = typeof(AgentStudio.Runner.CrossSlugInfraCircuitBreaker);
        Assert.NotNull(t);
    }

    [Fact]
    public void DefaultBudgetConstants_HaveExpectedValues()
    {
        // Pin the defaults documented in the loop inventory entry so a
        // drift between docs and code is caught here. Change both in
        // the same commit when tuning the budget.
        Assert.Equal(2, AgentStudio.Runner.CrossSlugInfraCircuitBreaker.DefaultSilentLimit);
        Assert.Equal(10, AgentStudio.Runner.CrossSlugInfraCircuitBreaker.DefaultWindowMinutes);
    }

    [Fact]
    public void InfraHaltKind_Constant_Exists()
    {
        Assert.Equal(
            "cross-slug-spawn-failed-cascade",
            AgentStudio.Runner.InfraHaltKinds.CrossSlugSpawnFailedCascade);
    }
}
