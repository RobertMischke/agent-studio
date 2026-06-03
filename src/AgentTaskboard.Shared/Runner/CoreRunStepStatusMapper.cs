using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Pure mapping from a terminal CLI run status to the CORE
/// "Agent execution" pipeline step status. Sibling of
/// <see cref="RunStatusClassifier"/>: inputs in, status out, no side effects.
///
/// <para>
/// The CORE step answers a single question - "did the agent run execute?" -
/// which the deterministic <see cref="RunStatusClassifier"/> has already
/// settled into <see cref="Models.CliExecution.Status"/>. So the only honest
/// input here is that classified status, never the OS exit code.
/// </para>
///
/// <para>
/// Load-bearing rule (the bug this guards against): a sentinel-detected or
/// silent-completion run is a successful completion, but the orchestrator
/// kills the lingering process to release it, and <c>Process.Kill</c> hands
/// back <c>exitCode = -1</c> on Windows. <see cref="RunStatusClassifier"/>
/// deliberately treats those as <see cref="RunStatuses.Completed"/> regardless
/// of the kill-induced exit code. Re-gating the CORE step on
/// <c>exitCode is null or 0</c> re-introduced the exact coupling the
/// classifier removed: every deterministic-completion run (the normal happy
/// path on Windows) was marked <see cref="PipelineStepStatus.Failed"/>, so the
/// CORE step never showed as completed. Keying off the classified status alone
/// fixes that.
/// </para>
/// </summary>
public static class CoreRunStepStatusMapper
{
    /// <summary>
    /// Map a finished run's <see cref="Models.CliExecution"/> to its CORE step
    /// status. Reads only <see cref="Models.CliExecution.Status"/>; the exit
    /// code is intentionally ignored (see the type remarks).
    /// </summary>
    public static PipelineStepStatus From(CliExecution execution) =>
        From(execution.Status);

    /// <summary>
    /// Map a classified terminal run status to the CORE step status:
    /// <see cref="RunStatuses.Completed"/> -> <see cref="PipelineStepStatus.Passed"/>,
    /// everything else (failed / stopped / cancelled) -> <see cref="PipelineStepStatus.Failed"/>.
    /// </summary>
    public static PipelineStepStatus From(string? runStatus) =>
        string.Equals(runStatus, RunStatuses.Completed, StringComparison.OrdinalIgnoreCase)
            ? PipelineStepStatus.Passed
            : PipelineStepStatus.Failed;

    /// <summary>
    /// Resolve the terminal CORE step's status AND its failure reason from a
    /// finished run. The reason is <c>null</c> on a <see cref="PipelineStepStatus.Passed"/>
    /// run (a completed run carries no failure note) and a short
    /// <c>"agent run {status} (exit {code})"</c> string otherwise.
    ///
    /// <para>
    /// Both halves of the decision live here, not just the status, so the
    /// load-bearing invariant - "a run the classifier calls
    /// <see cref="RunStatuses.Completed"/> is Passed with no reason, whatever
    /// the OS exit code" - is locked by one unit test at the exact decision the
    /// call site (<c>ProjectRunner.RecordCoreRunFinish</c>) uses. The original
    /// bug was an <c>exitCode is null or 0</c> gate at that call site, not in a
    /// pure helper, so a pure-status test alone could not catch it; pulling the
    /// status + reason pair into one tested function closes that gap and stops a
    /// future re-introduction of an exit-code gate from slipping in unguarded.
    /// </para>
    /// </summary>
    public static (PipelineStepStatus Status, string? Reason) Resolve(CliExecution execution)
    {
        var status = From(execution);
        return (status, status == PipelineStepStatus.Passed ? null : DescribeFailure(execution));
    }

    /// <summary>
    /// Short, human-readable reason for a non-Passed CORE run, e.g.
    /// <c>"agent run failed (exit 1)"</c>. Surfaced in the Overview pipeline
    /// row's failure note.
    /// </summary>
    private static string DescribeFailure(CliExecution execution)
    {
        var status = string.IsNullOrWhiteSpace(execution.Status) ? "unknown" : execution.Status;
        return execution.ExitCode is int code
            ? $"agent run {status} (exit {code})"
            : $"agent run {status}";
    }
}
