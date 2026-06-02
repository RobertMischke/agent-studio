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
}
