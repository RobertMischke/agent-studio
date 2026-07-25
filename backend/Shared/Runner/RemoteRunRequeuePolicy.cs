namespace AgentStudio.Shared;

/// <summary>
/// Pure server-side guard for returning an interrupted remote run to Ready.
/// Lease expiry alone is insufficient: the grace must have elapsed and the
/// assigned runner must explicitly report that the task is not active locally.
/// </summary>
public static class RemoteRunRequeuePolicy
{
    public static RemoteRunRequeueDecision Decide(RemoteRunRequeueFacts facts)
    {
        if (facts.SecondsSinceLastAuthorityActivity < facts.GraceSeconds)
            return new RemoteRunRequeueDecision(
                RemoteRunRequeueAction.WaitForGrace,
                "remote-requeue-grace",
                $"lease authority has been silent for {facts.SecondsSinceLastAuthorityActivity:F0}s " +
                $"(< {facts.GraceSeconds:F0}s grace)");

        if (!facts.RunnerRespondedWithActiveSet)
            return new RemoteRunRequeueDecision(
                RemoteRunRequeueAction.WaitForRunner,
                "remote-runner-status-unknown",
                "the assigned runner has not answered with its active task set");

        if (facts.RunnerReportsTaskActive)
            return new RemoteRunRequeueDecision(
                RemoteRunRequeueAction.KeepProgress,
                "remote-runner-still-active",
                "the assigned runner reports that the original run process is still active");

        return new RemoteRunRequeueDecision(
            RemoteRunRequeueAction.Requeue,
            "remote-runner-confirmed-inactive",
            "grace elapsed and the assigned runner confirms that the task is no longer active");
    }
}

public sealed record RemoteRunRequeueFacts(
    double SecondsSinceLastAuthorityActivity,
    double GraceSeconds,
    bool RunnerRespondedWithActiveSet,
    bool RunnerReportsTaskActive);

public sealed record RemoteRunRequeueDecision(
    RemoteRunRequeueAction Action,
    string ReasonCode,
    string Detail);

public enum RemoteRunRequeueAction
{
    WaitForGrace,
    WaitForRunner,
    KeepProgress,
    Requeue,
}
