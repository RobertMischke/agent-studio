using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

internal sealed record OrchestrationSettlementDecision(
    string RunStatus,
    OrchestrationStage NextStage,
    int ReissueAttempts,
    string? TaskState,
    bool IsTerminal,
    string? SupersededReason = null);

internal static class OrchestrationSettlementPolicy
{
    internal static OrchestrationSettlementDecision Decide(
        OrchestrationAction action,
        IReadOnlyList<OrchestrationStage> stages,
        OrchestrationStage currentStage,
        int currentReissueAttempts,
        int maxReissueAttempts,
        string currentTaskState,
        long expectedTaskVersion,
        long currentTaskVersion)
    {
        if (!string.Equals(currentTaskState, "4-auto-review", StringComparison.Ordinal))
        {
            return new OrchestrationSettlementDecision(
                "superseded",
                currentStage,
                currentReissueAttempts,
                null,
                true,
                $"Task moved to '{currentTaskState}' before settlement.");
        }

        if (expectedTaskVersion > 0 && currentTaskVersion != expectedTaskVersion)
        {
            return new OrchestrationSettlementDecision(
                "superseded",
                currentStage,
                currentReissueAttempts,
                null,
                true,
                $"Task version changed from {expectedTaskVersion} to {currentTaskVersion} before settlement.");
        }

        return action switch
        {
            OrchestrationAction.Continue => Continue(stages, currentStage, currentReissueAttempts),
            OrchestrationAction.Reissue => Reissue(
                currentStage,
                currentReissueAttempts,
                maxReissueAttempts),
            OrchestrationAction.Escalate => Terminal(
                "escalated", currentStage, currentReissueAttempts, "5e-escalated"),
            OrchestrationAction.Complete => Terminal(
                "completed", currentStage, currentReissueAttempts, "5-human-review"),
            OrchestrationAction.Fail => Terminal(
                "failed", currentStage, currentReissueAttempts, "5e-escalated"),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };
    }

    private static OrchestrationSettlementDecision Continue(
        IReadOnlyList<OrchestrationStage> stages,
        OrchestrationStage currentStage,
        int reissueAttempts)
    {
        var index = stages.IndexOf(currentStage);
        if (index >= 0 && index + 1 < stages.Count)
        {
            return new OrchestrationSettlementDecision(
                "pending",
                stages[index + 1],
                reissueAttempts,
                null,
                false);
        }

        return Terminal("completed", currentStage, reissueAttempts, "5-human-review");
    }

    private static OrchestrationSettlementDecision Reissue(
        OrchestrationStage currentStage,
        int currentReissueAttempts,
        int maxReissueAttempts)
    {
        var reissueAttempts = currentReissueAttempts + 1;
        return reissueAttempts <= maxReissueAttempts
            ? Terminal("reissued", currentStage, reissueAttempts, "2-ready")
            : Terminal("escalated", currentStage, reissueAttempts, "5e-escalated");
    }

    private static OrchestrationSettlementDecision Terminal(
        string status,
        OrchestrationStage currentStage,
        int reissueAttempts,
        string taskState)
        => new(status, currentStage, reissueAttempts, taskState, true);
}

internal static class OrchestrationStageListExtensions
{
    internal static int IndexOf(
        this IReadOnlyList<OrchestrationStage> stages,
        OrchestrationStage stage)
    {
        for (var index = 0; index < stages.Count; index++)
        {
            if (stages[index] == stage) return index;
        }

        return -1;
    }
}
