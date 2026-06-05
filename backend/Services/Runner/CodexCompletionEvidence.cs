namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Evidence-based completion evaluation for Codex runs that ended without a
/// terminal sentinel ("silent finish").
///
/// <para>
/// <b>Why this exists.</b> Codex silent-finishes the large majority of its
/// runs (it ends the turn on a <c>turn.completed</c>/exit, or stalls
/// mid-investigation, without emitting <c>[[TASK_DONE]]</c>), whereas Claude
/// almost always signs off. Treating every Codex silent finish as
/// missing-terminal-sentinel forces a reissue even when the work is plainly
/// done (real commits + a clean self-reported status), which churns the run
/// and depresses the accept rate. This evaluator lets the orchestrator trust
/// the <em>evidence on disk</em> for Codex instead of insisting on the
/// sentinel:
/// <list type="bullet">
///   <item><b>Accept as done</b> when the run produced commits and the
///   agent's own close-out reports success with no open items / build|test
///   failures and was not a mid-task timeout.</item>
///   <item><b>Continue</b> (a bounded <c>codex exec resume</c> loop) when the
///   run left open items or timed out mid-task - drive it to a clean finish
///   before reissuing or escalating to a human.</item>
///   <item><b>Inconclusive</b> otherwise - the caller keeps its existing
///   routing.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Codex-only.</b> Claude stays sentinel-based: <see cref="Inputs.IsCodex"/>
/// gates the whole evaluation so a non-Codex run always returns
/// <see cref="CompletionAction.Inconclusive"/> and the existing
/// terminal-sentinel contract is untouched.
/// </para>
///
/// <para>
/// <b>Boundedness.</b> The continuation loop is capped by
/// <see cref="DefaultContinuationBudget"/>: once
/// <see cref="Inputs.ContinuationAttemptsUsed"/> reaches the budget the
/// evaluator stops returning <see cref="CompletionAction.Continue"/> and
/// falls through to <see cref="CompletionAction.Inconclusive"/>, so the loop
/// cannot run unbounded - it converges to the caller's terminal routing.
/// </para>
///
/// <para>Pure (ADR-0032): inputs in, verdict out, no side effects.</para>
/// </summary>
public static class CodexCompletionEvidence
{
    /// <summary>
    /// Maximum number of automatic <c>codex exec resume</c> continuations for a
    /// silent finish with open work before the evaluator gives up and lets the
    /// caller's existing routing (reissue / escalate) take over.
    /// </summary>
    public const int DefaultContinuationBudget = 2;

    public enum CompletionAction
    {
        /// <summary>Not Codex, or not enough signal - caller keeps its existing behavior.</summary>
        Inconclusive,
        /// <summary>Clean finished-work evidence - accept without insisting on the sentinel.</summary>
        AcceptAsDone,
        /// <summary>Open items / mid-task timeout - run a bounded continuation (codex exec resume).</summary>
        Continue,
    }

    /// <summary>
    /// Pure observation snapshot for one finished Codex run.
    /// </summary>
    /// <param name="IsCodex">Whether the run was a Codex run (false short-circuits to Inconclusive).</param>
    /// <param name="HasCommits">Whether the run produced at least one commit in its SHA range.</param>
    /// <param name="StatusResultToken">The agent's self-reported <c>Result:</c> token, if any.</param>
    /// <param name="OpenFindingsCount">Count of unfinished-work findings from <see cref="CompletionGate.ExtractFindings"/>.</param>
    /// <param name="TimedOutMidTask">Whether the run was killed by the watchdog mid-task.</param>
    /// <param name="ContinuationAttemptsUsed">How many continuations have already run for this job.</param>
    public readonly record struct Inputs(
        bool IsCodex,
        bool HasCommits,
        string? StatusResultToken,
        int OpenFindingsCount,
        bool TimedOutMidTask,
        int ContinuationAttemptsUsed);

    public readonly record struct Verdict(CompletionAction Action, string Reason);

    public static Verdict Decide(Inputs inputs, int continuationBudget = DefaultContinuationBudget)
    {
        if (!inputs.IsCodex)
            return new Verdict(CompletionAction.Inconclusive,
                "Evidence-based completion is Codex-only; non-Codex runs stay sentinel-based.");

        var cleanStatus = CompletionGate.IsSuccessResultToken(inputs.StatusResultToken);
        var hasOpenWork = inputs.OpenFindingsCount > 0 || inputs.TimedOutMidTask;

        // 1) Finished-work evidence: real commits + a success status + nothing
        //    open and not a mid-task timeout. Trust the disk state over the
        //    missing sign-off.
        if (inputs.HasCommits && cleanStatus && !hasOpenWork)
            return new Verdict(CompletionAction.AcceptAsDone,
                $"Codex silent-finish with commits and a clean status (Result:{inputs.StatusResultToken}); accepting as done without a sentinel.");

        // 2) Open work / mid-task timeout: drive to completion with a bounded
        //    continuation loop before falling back to reissue/escalate.
        if (hasOpenWork)
        {
            if (inputs.ContinuationAttemptsUsed < continuationBudget)
            {
                var topic = inputs.TimedOutMidTask
                    ? "timed out mid-task"
                    : $"{inputs.OpenFindingsCount} open item(s)";
                return new Verdict(CompletionAction.Continue,
                    $"Codex silent-finish with open work ({topic}); continuation {inputs.ContinuationAttemptsUsed + 1}/{continuationBudget}.");
            }

            return new Verdict(CompletionAction.Inconclusive,
                $"Codex continuation budget exhausted ({inputs.ContinuationAttemptsUsed}/{continuationBudget}); falling through to existing routing.");
        }

        // 3) No commit evidence and nothing clearly open: cannot assert the
        //    work is finished. Let the existing routing handle it.
        return new Verdict(CompletionAction.Inconclusive,
            "Codex silent-finish without commit evidence and without an open-item signal; cannot assert completion.");
    }
}
