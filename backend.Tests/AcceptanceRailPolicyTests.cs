using Xunit;

namespace AgentStudio.Tests;

public sealed class AcceptanceRailPolicyTests
{
    public static TheoryData<AcceptanceRailPolicyInput, AcceptanceRailAction> Matrix => new()
    {
        {
            new AcceptanceRailPolicyInput(
                TaskStates.HumanReview,
                TaskModes.Coding,
                IntegrationStatuses.Integrated,
                false,
                false,
                0,
                5,
                false),
            AcceptanceRailAction.Accept
        },
        {
            new AcceptanceRailPolicyInput(
                TaskStates.HumanReview,
                TaskModes.Coding,
                IntegrationStatuses.Integrated,
                false,
                true,
                0,
                5,
                false),
            AcceptanceRailAction.Hold
        },
        {
            new AcceptanceRailPolicyInput(
                TaskStates.HumanReview,
                TaskModes.Concept,
                IntegrationStatuses.Integrated,
                false,
                false,
                0,
                5,
                false),
            AcceptanceRailAction.ConceptHold
        },
        {
            new AcceptanceRailPolicyInput(
                TaskStates.HumanReview,
                TaskModes.Coding,
                IntegrationStatuses.Pending,
                false,
                false,
                0,
                5,
                false),
            AcceptanceRailAction.None
        },
        {
            new AcceptanceRailPolicyInput(
                TaskStates.HumanReview,
                TaskModes.Coding,
                IntegrationStatuses.ConflictSkipped,
                true,
                false,
                4,
                5,
                false),
            AcceptanceRailAction.Requeue
        },
        {
            new AcceptanceRailPolicyInput(
                TaskStates.Escalated,
                TaskModes.Coding,
                IntegrationStatuses.ConflictSkipped,
                true,
                false,
                5,
                5,
                false),
            AcceptanceRailAction.Escalate
        },
        {
            new AcceptanceRailPolicyInput(
                TaskStates.Escalated,
                TaskModes.Coding,
                IntegrationStatuses.ConflictSkipped,
                true,
                false,
                5,
                5,
                true),
            AcceptanceRailAction.None
        },
        {
            new AcceptanceRailPolicyInput(
                TaskStates.Escalated,
                TaskModes.Coding,
                IntegrationStatuses.Integrated,
                false,
                false,
                0,
                5,
                false),
            AcceptanceRailAction.None
        },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Decide_UsesClosedLifecycleMatrix(
        AcceptanceRailPolicyInput input,
        AcceptanceRailAction expected)
    {
        Assert.Equal(expected, AcceptanceRailPolicy.Decide(input).Action);
    }

    [Fact]
    public void Options_AreEnabledWithSafeDefaults()
    {
        var options = AcceptanceRailOptions.Resolve(new ConfigurationBuilder().Build());

        Assert.True(options.Enabled);
        Assert.Equal(3, options.IntervalMinutes);
        Assert.Equal(5, options.MaxRequeues);
        Assert.Empty(options.HoldList);
    }

    [Fact]
    public void Options_ReadKillSwitchRetryLimitAndHoldList()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AcceptanceRail:Enabled"] = "false",
                ["AcceptanceRail:IntervalMinutes"] = "7",
                ["AcceptanceRail:MaxRequeues"] = "9",
                ["AcceptanceRail:HoldList:0"] = "AGT-42",
                ["AcceptanceRail:HoldList:1"] = "Fixture/AGT-43",
            })
            .Build();

        var options = AcceptanceRailOptions.Resolve(configuration);

        Assert.False(options.Enabled);
        Assert.Equal(7, options.IntervalMinutes);
        Assert.Equal(9, options.MaxRequeues);
        Assert.Contains("agt-42", options.HoldList);
        Assert.Contains("FIXTURE/agt-43", options.HoldList);
    }
}
