using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// Builds the fenced authority inventory sent with runner registration. A slot
/// is reported only when the host can still prove its exact process generation
/// or has a durable terminal result waiting to be delivered.
/// </summary>
public static class RunnerActiveAttemptReporter
{
    public static IReadOnlyList<Contract.RunnerActiveAttempt> Coding(
        IEnumerable<PersistedRunnerSlot> slots)
    {
        var active = new List<Contract.RunnerActiveAttempt>();
        foreach (var slot in slots)
        {
            var observed = slot;
            if ((slot.ProcessId is null || slot.ProcessStartedAtUtc is null)
                && DurableAgentProcess.TryRecoverIdentity(slot, out var recovered, out _))
            {
                observed = recovered;
            }
            var observation = DurableAgentProcess.InspectForReattach(observed);
            if (!observation.IsLive && observation.Result is null) continue;
            var attemptId = observed.RunId ?? observed.Lease.AttemptId ?? observed.AttemptId;
            if (string.IsNullOrWhiteSpace(attemptId)) continue;
            active.Add(new Contract.RunnerActiveAttempt(
                Contract.RunnerAttemptKinds.Coding,
                attemptId,
                observed.TaskKey,
                observed.Lease.LeaseId,
                observed.Lease.FencingToken,
                observed.Lease.AuthorityEpoch,
                observed.LeaseInstanceId));
        }
        return active;
    }

    public static IReadOnlyList<Contract.RunnerActiveAttempt> Review(
        IEnumerable<PersistedReviewSlot> slots)
    {
        var active = new List<Contract.RunnerActiveAttempt>();
        foreach (var slot in slots)
        {
            var observed = slot;
            if ((slot.ProcessId is null || slot.ProcessStartedAtUtc is null)
                && DurableReviewProcess.TryRecoverIdentity(slot, out var recovered, out _))
            {
                observed = recovered;
            }
            if (!DurableReviewProcess.HasCompleted(observed)
                && !DurableReviewProcess.VerifyLive(observed, out _))
            {
                continue;
            }
            var lease = observed.Claim.Lease;
            if (lease is null) continue;
            active.Add(new Contract.RunnerActiveAttempt(
                Contract.RunnerAttemptKinds.Review,
                observed.AttemptId,
                observed.Claim.Attempt?.TaskId ?? string.Empty,
                lease.LeaseId,
                lease.Fence,
                lease.AuthorityEpoch,
                lease.InstanceId));
        }
        return active;
    }
}
