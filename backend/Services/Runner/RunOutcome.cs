using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Why <see cref="ProjectRunner"/> declined to spawn a CLI run for the
/// requested job. Used to drive the busy-project queue path: when the
/// reason is <see cref="ProjectBusy"/> the caller persists the user's
/// intent on the target job and promotes the job to <c>2-ready</c>
/// instead of bouncing the user with a 4xx.
/// </summary>
public enum RunRejectReason
{
    None,
    JobNotFound,
    CliUnavailable,
    ProjectBusy
}

/// <summary>
/// Typed reason a run could not spawn. Includes the active job's id and
/// title when <see cref="Reason"/> is <see cref="RunRejectReason.ProjectBusy"/>
/// so the TaskRunnerService can shape the queued response and the chat meta
/// without re-querying state.
/// </summary>
public sealed record RunRejection(
    RunRejectReason Reason,
    string? Message,
    string? BusyJobId = null,
    string? BusyJobTitle = null);

/// <summary>
/// Thrown by <see cref="OrchestratorApi.Services.TaskRunnerService"/> when a
/// continue / start request cannot be honored AND the failure is not the
/// busy-project-queue case (which becomes a 202). Carries an HTTP status
/// hint for the endpoint layer.
/// </summary>
public sealed class JobOperationException : Exception
{
    public int Status { get; }
    public JobOperationException(string message, int status = 400) : base(message)
    {
        Status = status;
    }
}

/// <summary>
/// Outcome of <see cref="ProjectRunner"/>'s run-spawn entry points.
/// Either <see cref="Execution"/> is non-null (the run started) or
/// <see cref="Rejection"/> is non-null (the runner declined). Mutually
/// exclusive; both null is illegal.
/// </summary>
public sealed record RunOutcome(CliExecution? Execution, RunRejection? Rejection)
{
    public static RunOutcome Started(CliExecution exec) => new(exec, null);
    public static RunOutcome Reject(RunRejection rej) => new(null, rej);
}
