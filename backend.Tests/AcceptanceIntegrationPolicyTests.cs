using AgentStudio.Pipeline;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using System.Text.Json;

using Xunit;

namespace AgentStudio.Tests;

public sealed class AcceptanceIntegrationPolicyTests
{
    [Theory]
    [InlineData(MergeIntoIntegrationOutcome.Merged, false, true, AcceptedIntegrationLaneDecision.Complete)]
    [InlineData(MergeIntoIntegrationOutcome.AlreadyMerged, false, true, AcceptedIntegrationLaneDecision.Complete)]
    [InlineData(MergeIntoIntegrationOutcome.NoTaskBranch, false, true, AcceptedIntegrationLaneDecision.ReturnToHumanReview)]
    [InlineData(MergeIntoIntegrationOutcome.Error, false, true, AcceptedIntegrationLaneDecision.ReturnToHumanReview)]
    [InlineData(MergeIntoIntegrationOutcome.Conflict, false, true, AcceptedIntegrationLaneDecision.ReturnToHumanReview)]
    [InlineData(MergeIntoIntegrationOutcome.NoTaskBranch, true, true, AcceptedIntegrationLaneDecision.Complete)]
    [InlineData(MergeIntoIntegrationOutcome.NoTaskBranch, false, false, AcceptedIntegrationLaneDecision.Complete)]
    public void WorkerOutcomeMatrix_DecidesAcceptedLane(
        MergeIntoIntegrationOutcome outcome,
        bool operatorOverride,
        bool integrationRequired,
        AcceptedIntegrationLaneDecision expected)
    {
        Assert.Equal(
            expected,
            AcceptanceIntegrationPolicy.Decide(outcome, operatorOverride, integrationRequired));
    }

    [Theory]
    [InlineData("concept")]
    [InlineData("decision")]
    public void ConceptAndDecisionTaskTypes_ExpectNoIntegration(string taskType)
    {
        Assert.False(AcceptanceIntegrationPolicy.IsIntegrationRequired(new TaskInfo
        {
            Mode = TaskModes.Coding,
            Kind = TaskKinds.Task,
            TaskType = taskType,
        }));
    }

    [Theory]
    [InlineData("concept")]
    [InlineData("decision")]
    public void PersistedNoBranchTaskTypes_ArePreservedAsAnExpectation(string taskType)
    {
        using var document = JsonDocument.Parse($$"""{"taskType":"{{taskType}}"}""");

        Assert.True(TaskScannerService.ReadNoBranchExpected(document.RootElement));
    }

    [Fact]
    public void ExplicitNoBranchExpectation_ExemptsCodingCard()
    {
        Assert.False(AcceptanceIntegrationPolicy.IsIntegrationRequired(new TaskInfo
        {
            Mode = TaskModes.Coding,
            Kind = TaskKinds.Task,
            TaskType = TaskTypes.Chore,
            NoBranchExpected = true,
        }));
    }

    [Theory]
    [InlineData(null, null, "Null")]
    [InlineData(null, IntegrationStatuses.Pending, "Null")]
    [InlineData(null, IntegrationStatuses.Integrated, null)]
    [InlineData("error", IntegrationStatuses.Pending, "Error")]
    [InlineData("no-branch", IntegrationStatuses.NoBranch, "NoTaskBranch")]
    [InlineData("operator-override", IntegrationStatuses.Pending, null)]
    public void AcceptedInventory_ClassifiesSilentHistoricalOutcomes(
        string? verdict,
        string? integrationStatus,
        string? expected)
    {
        var step = verdict is null ? null : new PipelineStepExecution { Verdict = verdict };
        var status = integrationStatus is null
            ? null
            : new TaskIntegrationStatus { Status = integrationStatus };

        Assert.Equal(expected, AcceptedIntegrationInventorySweep.ClassifyFinding(step, status));
    }
}
