using Xunit;

namespace AgentStudio.Tests;

public sealed class ProviderQuotaWaitPolicyTests
{
    private static readonly DateTime Now = new(2026, 8, 24, 0, 20, 0, DateTimeKind.Utc);
    private static readonly QuotaWaitStatus Wait = new(
        CliTypes.Claude,
        Now.AddHours(-2),
        Now,
        120,
        "claude: limited until reset");

    [Theory]
    [InlineData("ready", true, "healthy", true, true)]
    [InlineData("limited", true, "healthy", true, false)]
    [InlineData("ready", false, "healthy", true, false)]
    [InlineData("ready", true, "draining", true, false)]
    [InlineData("ready", true, "healthy", false, false)]
    public void Resume_requires_due_wait_fresh_ready_capability_and_automatic_project(
        string status,
        bool fresh,
        string health,
        bool automatic,
        bool expected)
    {
        var result = ProviderQuotaWaitPolicy.CanResume(
            Wait,
            Now,
            automatic,
            [new ProviderCapabilityAvailability(CliTypes.Claude, status, fresh, health)]);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void A_ready_codex_capability_does_not_resume_a_claude_wait()
        => Assert.False(ProviderQuotaWaitPolicy.CanResume(
            Wait,
            Now,
            true,
            [new ProviderCapabilityAvailability(CliTypes.Codex, "ready", true, "healthy")]));
}
