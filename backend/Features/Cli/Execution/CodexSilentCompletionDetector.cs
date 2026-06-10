

namespace AgentStudio.Cli;

/// <summary>
/// Recognises the Codex "silent completion" hang shape: a successful
/// <c>item.completed</c> (<c>type=command_execution</c>, <c>exit_code=0</c>)
/// frame after which Codex never emits a closing <c>turn.completed</c> or
/// terminal sentinel - the agent has internally decided the task is done but
/// fails to sign off, leaving the process alive and stdout-silent.
///
/// <para>
/// <b>Why a dedicated detector instead of widening the watchdog.</b>
/// The <see cref="PhaseAwareWatchdog"/> kill path treats the run as a
/// failure: the process is force-killed, the lane stays in <c>3-progress</c>,
/// and the user has to triage manually. The actual disk state typically
/// shows real, completed work. Recognising the shape one layer up lets the
/// runner finalize gracefully (Completed status, normal move to
/// <c>4-auto-review</c>, aspect calls run, tag <c>outcome:silent-finish</c>)
/// so the lane reflects what really happened: "agent finished but did not
/// sign off, please double-check".
/// </para>
///
/// <para>
/// <b>Trigger contract.</b> The detector fires exactly when all of the
/// following hold:
/// <list type="bullet">
///   <item>The run's most recently observed CLI event was a
///         <c>command_execution</c> <see cref="CliRunEvent.ToolCompleted"/>
///         that reported a zero exit code (the canonical silent-completion
///         shape; non-zero exits often legitimately precede another
///         reasoning turn).</item>
///   <item>The phase tracker is in <see cref="RunPhase.TurnInProgress"/>,
///         <see cref="RunPhase.OutputDelta"/>, or
///         <see cref="RunPhase.ToolExecuting"/> - i.e. the model should be
///         either producing output or starting another tool but is not.</item>
///   <item>Silence since the trigger frame meets or exceeds
///         <see cref="DefaultSilenceSeconds"/> (60 s). Comfortably shorter
///         than the realistic <c>ToolExecuting</c> work budget so genuine
///         long tools are not misclassified, and well below the
///         <c>HungSeconds</c> kill threshold so we beat the watchdog to it.</item>
/// </list>
/// </para>
///
/// <para>
/// The detector is intentionally Codex-only. Claude's stream-json mode emits
/// a terminal <c>result</c> frame after the last sentinel and the
/// <c>SentinelDetected</c> path already handles the lingering-process case;
/// Gemini and Copilot have different completion contracts. Extending to
/// another CLI means adding a parallel detector here, not generalising this
/// one.
/// </para>
/// </summary>
public static class CodexSilentCompletionDetector
{
    /// <summary>
    /// Silence (since the trigger frame) at which the detector reports a
    /// silent completion. Picked at 60 s because the agent task contract
    /// asks for the sign-off to land within seconds of the last meaningful
    /// frame; one full minute is generous without burning the realistic
    /// tool-execution window in <see cref="PhaseBudget.For(RunPhase)"/>.
    /// </summary>
    public const double DefaultSilenceSeconds = 60.0;

    /// <summary>
    /// Inputs the runner hands the detector each tick. Pure data so the
    /// detector can be unit-tested without spinning up a CLI process.
    /// </summary>
    public readonly record struct DetectionInputs(
        string CliType,
        RunPhase Phase,
        double SilenceSinceLastEventSeconds,
        int? LastCommandExecutionExitCode,
        string? LastCommandExecutionCommand,
        string? LastCommandExecutionOutputTail,
        bool AlreadyTripped);

    /// <summary>
    /// Pure verdict from <see cref="Decide"/>. When <see cref="ShouldTrip"/>
    /// is true the runner writes the synthetic <c>[codex-silent-completion]</c>
    /// marker, emits the bus observation, tags the job, and stops the process
    /// with <c>RunStopReason.SilentCompletion</c>.
    /// </summary>
    public readonly record struct DetectionVerdict(bool ShouldTrip, string Diagnosis);

    /// <summary>
    /// Apply the trigger contract. Same shape as <see cref="PhaseAwareWatchdog.DecideState"/>:
    /// inputs in, decision out, no side effects.
    /// </summary>
    /// <param name="inputs">Per-tick observation snapshot.</param>
    /// <param name="silenceThresholdSeconds">
    /// Override the silence cap (tests; per-CLI calibration).
    /// Defaults to <see cref="DefaultSilenceSeconds"/>.
    /// </param>
    public static DetectionVerdict Decide(
        DetectionInputs inputs,
        double silenceThresholdSeconds = DefaultSilenceSeconds)
    {
        if (inputs.AlreadyTripped) return new DetectionVerdict(false, "");
        if (!string.Equals(inputs.CliType, CliTypes.Codex, StringComparison.OrdinalIgnoreCase))
            return new DetectionVerdict(false, "");
        if (inputs.LastCommandExecutionExitCode is not 0) return new DetectionVerdict(false, "");
        if (!IsCandidatePhase(inputs.Phase)) return new DetectionVerdict(false, "");
        if (inputs.SilenceSinceLastEventSeconds < silenceThresholdSeconds)
            return new DetectionVerdict(false, "");

        return new DetectionVerdict(true, BuildDiagnosis(inputs, silenceThresholdSeconds));
    }

    /// <summary>
    /// Phases compatible with the silent-completion shape: the model should
    /// be either producing output or kicking off the next tool, but is doing
    /// neither. Terminal phases never qualify (the runner is finalizing
    /// anyway), and <see cref="RunPhase.Spawning"/> / pre-prompt phases mean
    /// the trigger frame can't have happened yet.
    /// </summary>
    private static bool IsCandidatePhase(RunPhase phase) => phase switch
    {
        RunPhase.TurnInProgress => true,
        RunPhase.OutputDelta    => true,
        RunPhase.ToolExecuting  => true,
        _                       => false
    };

    /// <summary>
    /// One-line evidence string that lands in the synthetic marker line +
    /// the orchestrator chat. Keeps the last tool's command + a short output
    /// tail so a reviewer can see what Codex thought it had finished.
    /// </summary>
    private static string BuildDiagnosis(DetectionInputs inputs, double threshold)
    {
        var cmd = string.IsNullOrWhiteSpace(inputs.LastCommandExecutionCommand)
            ? "<unknown command>"
            : TruncateForChat(inputs.LastCommandExecutionCommand!, 120);
        var tail = string.IsNullOrWhiteSpace(inputs.LastCommandExecutionOutputTail)
            ? "(no output)"
            : TruncateForChat(inputs.LastCommandExecutionOutputTail!, 200);
        return
            $"Codex stopped after final tool call without a closing sentinel " +
            $"(silence={inputs.SilenceSinceLastEventSeconds:F0}s >= {threshold:F0}s, " +
            $"phase={inputs.Phase}). Last command: {cmd} | output tail: {tail}";
    }

    private static string TruncateForChat(string value, int max)
    {
        var collapsed = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return collapsed.Length <= max ? collapsed : collapsed[..max] + "…";
    }
}
