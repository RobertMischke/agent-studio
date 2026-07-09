namespace AgentStudio.Runner;

/// <summary>
/// Pure decision library for the per-task anti-endless-reissue circuit breaker.
///
/// <para>
/// <b>Why this exists.</b> A task whose every run failed without making
/// progress (no commit / no diff) could loop forever: an auto-pickup run that
/// failed with a soft, non-routed issue (classifier-unknown,
/// missing-terminal-sentinel, no-agent-output) was either re-issued as a
/// <c>UserContinue</c> run or left in 3-progress for the next auto-pickup tick.
/// Neither path fed the existing auto-pickup failure breaker (the re-issue
/// returns early before the counter; the re-issue itself is not an
/// AutoPickup intent), so the same bad task restarted indefinitely - the
/// observed symptom was 200+ CLI starts on one task in ~40 minutes, burning
/// quota and wedging the queue. This breaker counts those no-progress failures
/// per task across BOTH paths and trips after a small threshold, parking the
/// task in human review instead of re-issuing.
/// </para>
/// </summary>
public static class RunQuarantineBreaker
{
    /// <summary>
    /// Consecutive no-progress failures (counting the current run) at which the
    /// breaker trips. Chosen so a task gets a couple of genuine retries - the
    /// soft-intervention re-issue and one fresh auto-pickup - before it is
    /// quarantined, matching the "&lt;= N attempts" acceptance.
    /// </summary>
    public const int DefaultFailThreshold = 3;

    /// <summary>
    /// True when a finished run's issue kind is one that keeps a failing task
    /// in the run loop (re-issued or left in 3-progress) rather than routing it
    /// somewhere terminal. These are the only outcomes the streak counts.
    /// Excluded kinds either route to human review on their own
    /// (permission / watchdog / environment / context-overflow), have a
    /// dedicated recovery breaker (cli-launch-failed → capture-fail), are
    /// accepted as done (codex silent-completion), or are the breaker's own
    /// terminal verdict (quarantined). Implemented as an exclusion list so a
    /// future soft-failure issue kind is counted by default.
    /// </summary>
    public static bool CountsAsNoProgressFailure(RunIssueKind issueKind) => issueKind switch
    {
        RunIssueKind.PermissionBlocked  => false,
        RunIssueKind.WatchdogTimeout    => false,
        RunIssueKind.EnvironmentBlocker => false,
        RunIssueKind.EmptyFastExit      => false,
        RunIssueKind.ContextOverflow    => false,
        // A wrong/unsupported model and an exhausted quota are not the task's
        // fault: re-running the same task content will not fix them, so they
        // must not accrue toward the per-task no-progress quarantine streak.
        RunIssueKind.ModelInvalid       => false,
        RunIssueKind.QuotaExhausted     => false,
        // A transient host file lock / network glitch is not the task's fault and
        // has its own bounded retry-with-backoff; it must not accrue toward the
        // per-task no-progress quarantine streak (AGT-1944).
        RunIssueKind.EnvironmentalTransient => false,
        RunIssueKind.CliLaunchFailed    => false,
        RunIssueKind.SilentCompletion   => false,
        RunIssueKind.Quarantined        => false,
        RunIssueKind.AgentGitViolation  => false,
        _                               => true
    };

    /// <summary>
    /// The streak trips once the consecutive no-progress failure count
    /// (including the current run) reaches the threshold.
    /// </summary>
    public static bool ShouldQuarantine(int consecutiveFailsIncludingCurrent, int threshold = DefaultFailThreshold)
        => consecutiveFailsIncludingCurrent >= threshold;
}
