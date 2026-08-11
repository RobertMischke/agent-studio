using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Pure wire projection from durable host slots to registration facts.</summary>
internal static class RunnerActiveAttemptReports
{
    public static IReadOnlyList<PersistedRunnerSlot> VerifiableCoding(
        IEnumerable<PersistedRunnerSlot> slots)
        => slots.Where(slot =>
            {
                var observation = DurableAgentProcess.InspectForReattach(slot);
                return observation.Result is not null || observation.IsLive;
            })
            .ToArray();

    public static IReadOnlyList<PersistedReviewSlot> VerifiableReview(
        IEnumerable<PersistedReviewSlot> slots)
        => slots.Where(slot =>
                DurableReviewProcess.HasCompleted(slot)
                || DurableReviewProcess.VerifyLive(slot, out _))
            .ToArray();

    public static IReadOnlyList<RunnerActiveAttemptDto> Coding(
        IEnumerable<PersistedRunnerSlot> slots,
        int requestedTtlSeconds)
        => slots.Select(slot => new RunnerActiveAttemptDto(
                RunnerAttemptKinds.Coding,
                slot.Lease.AttemptId ?? slot.RunId ?? slot.AttemptId,
                slot.TaskKey,
                slot.Lease.LeaseId,
                slot.Lease.FencingToken,
                slot.Lease.AuthorityEpoch,
                slot.LeaseInstanceId ?? string.Empty,
                slot.UpdatedAtUtc,
                requestedTtlSeconds,
                slot.Phase,
                slot.Lease.ExpiresAt,
                slot.ProjectId))
            .ToArray();

    public static IReadOnlyList<RunnerActiveAttemptDto> Review(
        IEnumerable<PersistedReviewSlot> slots,
        int requestedTtlSeconds)
        => slots.Select(slot => new RunnerActiveAttemptDto(
                RunnerAttemptKinds.Review,
                slot.AttemptId,
                slot.Claim.Attempt!.TaskId,
                slot.Claim.Lease!.LeaseId,
                slot.Claim.Lease.Fence,
                slot.Claim.Lease.AuthorityEpoch,
                slot.Claim.Lease.InstanceId,
                slot.UpdatedAtUtc,
                requestedTtlSeconds,
                slot.Phase,
                slot.Claim.Lease.ExpiresAt))
            .ToArray();
}
