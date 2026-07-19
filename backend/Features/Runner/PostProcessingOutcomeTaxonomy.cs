namespace AgentStudio.Runner;

/// <summary>
/// The five post-processing outcome buckets from the outcome-taxonomy concept
/// (post-processing-immediacy-and-parallelism §2.2/§3, AGT-1944). Every finished
/// CLI run that did not sign off cleanly is classified into exactly one bucket
/// at the gate / escalation boundary, and the bucket - not the raw process exit
/// code - drives what happens next:
/// <list type="bullet">
///   <item><see cref="Success"/> is accepted.</item>
///   <item><see cref="CodeDefect"/> flows through the normal reissue / human
///   review path as a problem the agent can fix.</item>
///   <item><see cref="Environmental"/> is NOT the code's fault: the transient
///   members (host file lock, network glitch, CLI launch/resume failure) retry
///   with exponential backoff before escalating; every member that does escalate
///   is flagged <c>environmental</c> so a reviewer does not read an infra blip as
///   a failed change.</item>
///   <item><see cref="InconclusiveWithResults"/> could not be mapped to a
///   terminal verdict but left files in <c>results/</c> - so it is routed to
///   human review WITH a "there is work to inspect" hint instead of a bare park.</item>
///   <item><see cref="InconclusiveEmpty"/> could not be concluded and left
///   nothing behind - the bare 5e escalation.</item>
/// </list>
/// This type is a pure classifier + retry decider; the routing side effects live
/// in <see cref="RunOutcomePolicy"/> and the runner.
/// </summary>
public enum PostProcessingOutcome
{
    /// <summary>The run reached a clean terminal verdict (DONE / NOOP / heuristic-done / codex silent-finish).</summary>
    Success,
    /// <summary>The run failed on a defect in the change itself - a self-reported
    /// build / compile / test failure, or a process violation the agent caused.
    /// The agent can fix it, so it is a code problem, not an infra fault.</summary>
    CodeDefect,
    /// <summary>The run failed on the environment, not the change: a host file
    /// lock, a network glitch, a CLI launch/resume failure, an exhausted quota, a
    /// wrong model, a blown context window, or a sandbox/permission blocker.</summary>
    Environmental,
    /// <summary>The run could not be mapped to a terminal verdict but left files
    /// in <c>results/</c>: there is partial work for a human to inspect.</summary>
    InconclusiveWithResults,
    /// <summary>The run could not be mapped to a terminal verdict and left nothing
    /// in <c>results/</c>.</summary>
    InconclusiveEmpty
}

/// <summary>What to do with an environmental-fault run at the gate.</summary>
public enum EnvironmentalRetryAction
{
    /// <summary>Retry the task after <see cref="EnvironmentalRetryDecision.Backoff"/> - the fault is transient and clears on its own.</summary>
    RetryWithBackoff,
    /// <summary>Escalate to human review flagged <c>environmental</c> - the fault is not retryable, or the retry budget is spent.</summary>
    Escalate
}

/// <summary>Pure decision for an environmental-fault run: retry-with-backoff or escalate.</summary>
public sealed record EnvironmentalRetryDecision(
    EnvironmentalRetryAction Action,
    TimeSpan Backoff,
    int Attempt,
    string Reason);

/// <summary>
/// Pure, side-effect-free classifier + retry decider for the post-processing
/// outcome taxonomy. See <see cref="PostProcessingOutcome"/> for the buckets.
/// </summary>
public static class PostProcessingOutcomeTaxonomy
{
    /// <summary>Bounded retry budget for a transient host file lock / network glitch.</summary>
    public const int DefaultMaxEnvironmentalRetries = 2;

    /// <summary>
    /// Bounded retry budget for a CLI launch / resume failure. One automatic
    /// fresh-start retry (rebuild from disk), then escalate - the
    /// "Fresh-Start-Retry (1x), erst danach eskalieren" rule (AGT-1944; belege
    /// AGT-1945/1929/1930 backend-restart resume losses).
    /// </summary>
    public const int MaxCliLaunchRetries = 1;

    /// <summary>
    /// Bounded retry budget for a POST-STEP verdict that came back missing /
    /// corrupt / unparseable because the reviewing CLI call itself died (the
    /// backend cut that killed the aspect runner mid-run, AGT-2021; belege the
    /// AGT-1996 hard-cut). A dead reviewer is an INFRASTRUCTURE fault, never the
    /// card's unfinished work, so the affected step reruns exactly ONCE with the
    /// environmental backoff and only records an <see cref="RunIssueKind.InfraCrash"/>
    /// (flagged <c>environmental</c>, no reissue-budget burn) when the retry again
    /// yields no verdict. Reuses the AGT-1944 taxonomy - same backoff, same
    /// "environmental never counts as a failed change" contract.
    /// </summary>
    public const int MaxPostStepVerdictRetries = 1;

    /// <summary>
    /// True when an issue kind is a TRANSIENT environmental fault that clears on
    /// its own, so re-running the same task after a short backoff usually
    /// succeeds. These are the only members that retry; the other environmental
    /// members (quota, model, context overflow, sandbox blocker) escalate
    /// immediately because re-running would hit the same wall.
    /// </summary>
    public static bool IsRetryableEnvironmental(RunIssueKind issueKind) => issueKind switch
    {
        RunIssueKind.EnvironmentalTransient => true,
        RunIssueKind.CliLaunchFailed        => true,
        _                                   => false
    };

    /// <summary>
    /// True when an issue kind is environmental in the taxonomy sense: the run
    /// failed on the host / provider / CLI, not on the change. Members escalate
    /// flagged <c>environmental</c> (the transient ones only after their retry
    /// budget is spent). Implemented as an explicit include list so a future,
    /// unrelated issue kind is treated as non-environmental by default.
    /// </summary>
    public static bool IsEnvironmental(RunIssueKind issueKind) => issueKind switch
    {
        RunIssueKind.EnvironmentalTransient => true,
        RunIssueKind.CliLaunchFailed        => true,
        RunIssueKind.EnvironmentBlocker     => true,
        RunIssueKind.EmptyFastExit          => true,
        RunIssueKind.QuotaExhausted         => true,
        RunIssueKind.ModelInvalid           => true,
        // A failed OAuth-session refresh is a shared credential/host fault, not
        // the change: escalate flagged environmental so a reviewer reads it as a
        // re-auth chore, not a code defect (AGT-2066).
        RunIssueKind.AuthRefreshFailed      => true,
        RunIssueKind.ContextOverflow        => true,
        RunIssueKind.PermissionBlocked      => true,
        RunIssueKind.WatchdogTimeout        => true,
        _                                   => false
    };

    /// <summary>The retry budget for a given retryable environmental kind.</summary>
    public static int MaxRetriesFor(RunIssueKind issueKind) => issueKind switch
    {
        RunIssueKind.CliLaunchFailed        => MaxCliLaunchRetries,
        RunIssueKind.EnvironmentalTransient => DefaultMaxEnvironmentalRetries,
        _                                   => 0
    };

    /// <summary>
    /// Exponential backoff for the Nth (1-based) environmental retry: 30s, 120s,
    /// then capped at 5 minutes. Longer than the rapid-crash backoff because an
    /// environmental fault (a file lock releasing, a network recovering) needs
    /// real wall-clock time to clear, not just host relief. Returns
    /// <see cref="TimeSpan.Zero"/> for non-positive input.
    /// </summary>
    public static TimeSpan RetryBackoff(int attempt)
    {
        if (attempt < 1) return TimeSpan.Zero;
        var exp = Math.Min(attempt - 1, 3);
        var seconds = Math.Min(30.0 * Math.Pow(4, exp), 300.0);
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Decide what to do with an environmental-fault run. Retryable kinds retry
    /// with backoff until their per-kind budget (<see cref="MaxRetriesFor"/>) is
    /// spent, then escalate; non-retryable environmental kinds escalate on first
    /// detection. <paramref name="priorRetryAttempt"/> is how many environmental
    /// retries this run chain has already spent (0 on first detection).
    /// </summary>
    public static EnvironmentalRetryDecision DecideEnvironmentalRetry(RunIssueKind issueKind, int priorRetryAttempt)
    {
        if (!IsRetryableEnvironmental(issueKind))
        {
            return new EnvironmentalRetryDecision(
                EnvironmentalRetryAction.Escalate,
                TimeSpan.Zero,
                priorRetryAttempt,
                $"{issueKind} is a non-retryable environmental fault; escalating flagged environmental.");
        }

        var max = MaxRetriesFor(issueKind);
        if (priorRetryAttempt < max)
        {
            var attempt = priorRetryAttempt + 1;
            return new EnvironmentalRetryDecision(
                EnvironmentalRetryAction.RetryWithBackoff,
                RetryBackoff(attempt),
                attempt,
                $"Transient environmental fault ({issueKind}); retry {attempt}/{max} after backoff.");
        }

        return new EnvironmentalRetryDecision(
            EnvironmentalRetryAction.Escalate,
            TimeSpan.Zero,
            priorRetryAttempt,
            $"Transient environmental fault ({issueKind}) persisted after {priorRetryAttempt} retr{(priorRetryAttempt == 1 ? "y" : "ies")}; escalating flagged environmental.");
    }

    /// <summary>
    /// Decide what to do with a post-step (aspect / code-review) whose verdict
    /// came back missing / unparseable because the reviewing CLI call died - an
    /// environmental infra fault, not the card's work. Reruns the step once with
    /// the environmental backoff (<see cref="RetryBackoff"/>), then escalates as
    /// an <see cref="RunIssueKind.InfraCrash"/> flagged <c>environmental</c>.
    /// <paramref name="priorRetryAttempt"/> is how many environmental retries this
    /// step has already spent (0 on first detection). Same shape as
    /// <see cref="DecideEnvironmentalRetry"/> so the AGT-1944 contract is reused
    /// verbatim - only the per-kind budget (<see cref="MaxPostStepVerdictRetries"/>)
    /// differs.
    /// </summary>
    public static EnvironmentalRetryDecision DecidePostStepVerdictRetry(int priorRetryAttempt)
    {
        if (priorRetryAttempt < MaxPostStepVerdictRetries)
        {
            var attempt = priorRetryAttempt + 1;
            return new EnvironmentalRetryDecision(
                EnvironmentalRetryAction.RetryWithBackoff,
                RetryBackoff(attempt),
                attempt,
                $"Post-step produced no parseable verdict (environmental infra fault); retry {attempt}/{MaxPostStepVerdictRetries} after backoff.");
        }

        return new EnvironmentalRetryDecision(
            EnvironmentalRetryAction.Escalate,
            TimeSpan.Zero,
            priorRetryAttempt,
            $"Post-step still produced no parseable verdict after {priorRetryAttempt} environmental retr{(priorRetryAttempt == 1 ? "y" : "ies")}; recording InfraCrash flagged environmental (no reissue-budget burn).");
    }

    /// <summary>
    /// Classify a finished run into one of the five taxonomy buckets.
    /// </summary>
    /// <param name="issueKind">The typed issue from <see cref="AgentOutcomeAnalyzer"/>.</param>
    /// <param name="terminalKind">The <see cref="TerminalRunOutcomeKinds"/> wire value, when known.</param>
    /// <param name="hasResults">Whether the task's <c>results/</c> dir holds at least one file.</param>
    /// <param name="hasCodeDefectEvidence">
    /// Whether a completeness gate found a self-reported build / compile / test
    /// failure in the run's own close-out. Drives <see cref="PostProcessingOutcome.CodeDefect"/>
    /// even when the raw issue kind is a generic soft failure.
    /// </param>
    public static PostProcessingOutcome Classify(
        RunIssueKind issueKind,
        string? terminalKind,
        bool hasResults,
        bool hasCodeDefectEvidence = false)
    {
        // Environmental wins first: an infra fault must never be mislabelled as a
        // code failure or an inconclusive verdict, whatever the process exit shape.
        if (IsEnvironmental(issueKind))
            return PostProcessingOutcome.Environmental;

        // A self-reported build/test failure or an agent process violation is a
        // defect in the change itself.
        if (hasCodeDefectEvidence || issueKind == RunIssueKind.AgentGitViolation)
            return PostProcessingOutcome.CodeDefect;

        // A run that could not be mapped to a terminal verdict splits on whether
        // it left anything to inspect.
        if (IsInconclusive(issueKind))
            return hasResults ? PostProcessingOutcome.InconclusiveWithResults : PostProcessingOutcome.InconclusiveEmpty;

        // A clean terminal verdict (or a heuristic/codex-silent done) is success.
        if (IsSuccessTerminal(terminalKind) || issueKind is RunIssueKind.HeuristicDone or RunIssueKind.SilentCompletion)
            return PostProcessingOutcome.Success;

        // Residual: no typed reason and no clean terminal. Treat as inconclusive,
        // split by results, so it never silently reads as success.
        return hasResults ? PostProcessingOutcome.InconclusiveWithResults : PostProcessingOutcome.InconclusiveEmpty;
    }

    private static bool IsInconclusive(RunIssueKind issueKind) => issueKind switch
    {
        RunIssueKind.MissingTerminalSentinel  => true,
        RunIssueKind.InfraCrash               => true,
        RunIssueKind.OrchestratorInconclusive => true,
        RunIssueKind.NoAgentOutput            => true,
        RunIssueKind.Quarantined              => true,
        _                                     => false
    };

    private static bool IsSuccessTerminal(string? terminalKind)
        => string.Equals(terminalKind, TerminalRunOutcomeKinds.Success, StringComparison.OrdinalIgnoreCase)
        || string.Equals(terminalKind, TerminalRunOutcomeKinds.NoOp, StringComparison.OrdinalIgnoreCase);
}
