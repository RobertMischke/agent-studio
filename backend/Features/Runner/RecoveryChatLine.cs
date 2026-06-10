namespace AgentStudio.Runner;

/// <summary>
/// Single source of truth for the one compact recovery notice a task gets in
/// its chat / activity log whenever the platform recovers a run (crash
/// requeue, watchdog reissue, host-restart resume, and the
/// <c>system-sleep</c> reason ASS-1729 introduces).
///
/// <para>
/// <b>Why this exists.</b> Recovery used to surface as multi-sentence blocks
/// in the chat (lock pids, sweep timings, retry rationale). The operator asked
/// for the opposite: one calm, informative line per recovery and nothing more.
/// The long form still belongs somewhere — it stays in the run / lifecycle
/// artifacts (recovery.jsonl, orphan-recoveries.jsonl, the orchestrator
/// decision journal) — but the chat gets exactly this shape:
/// </para>
///
/// <code>
/// &lt;reason&gt;: &lt;what happened&gt; -> &lt;action&gt; (attempt N/M, session resumed|new)
/// </code>
///
/// <para>
/// The trailing parenthetical is optional: a crash requeue has no retry
/// counter and no session signal, so it renders clean without it. The arrow is
/// ASCII <c>-&gt;</c> (AGENTS.md forbids em dashes / glyph arrows). The leading
/// <c>[recovery]</c> tag is added by the emit site — either by
/// <see cref="OrchestratorChatLog.Append"/> via
/// <see cref="OrchestratorMessageKind.Recovery"/>, or by
/// <see cref="PersistedLine"/> for boot-time services that write
/// <c>cli-output.log</c> directly and have no chat-log dependency.
/// </para>
/// </summary>
public static class RecoveryChatLine
{
    /// <summary>Backend died (crash / restart) while a run was in flight.</summary>
    public const string ReasonCrash = "crash";

    /// <summary>Watchdog killed a silent run and the completion loop reissued it.</summary>
    public const string ReasonWatchdog = "watchdog";

    /// <summary>A stale run was found after a host / stable restart and requeued.</summary>
    public const string ReasonHostRestart = "host-restart";

    /// <summary>Process ended after the host woke from standby (ASS-1729).</summary>
    public const string ReasonSystemSleep = "system-sleep";

    /// <summary>
    /// Build the recovery line body (everything after the <c>[recovery]</c>
    /// tag). Pass <paramref name="attempt"/>/<paramref name="maxAttempts"/>
    /// only when a real retry counter exists, and
    /// <paramref name="sessionResumed"/> only when the session continuity is
    /// known; the parenthetical is omitted entirely when neither is supplied.
    /// </summary>
    public static string Format(
        string reason,
        string what,
        string action,
        int? attempt = null,
        int? maxAttempts = null,
        bool? sessionResumed = null)
    {
        var head = $"{Clean(reason)}: {Clean(what)} -> {Clean(action)}";
        var parenthetical = BuildParenthetical(attempt, maxAttempts, sessionResumed);
        return parenthetical.Length == 0 ? head : $"{head} {parenthetical}";
    }

    /// <summary>
    /// Compose a fully persisted <c>cli-output.log</c> line for callers that
    /// append to the log directly (boot-time recovery services that run before
    /// the chat log is wired). Matches the exact shape
    /// <see cref="OrchestratorChatLog"/> writes so the activity-log parser and
    /// the frontend treat both emit paths identically.
    /// </summary>
    public static string PersistedLine(
        DateTime utc,
        string reason,
        string what,
        string action,
        int? attempt = null,
        int? maxAttempts = null,
        bool? sessionResumed = null)
        => $"[{utc:HH:mm:ss.fff}] [orchestrator] [{RecoveryTag}] " +
           Format(reason, what, action, attempt, maxAttempts, sessionResumed);

    /// <summary>The bracketed tag the frontend keys recovery rendering off.</summary>
    public const string RecoveryTag = "recovery";

    private static string BuildParenthetical(int? attempt, int? maxAttempts, bool? sessionResumed)
    {
        var parts = new List<string>(2);
        if (attempt is > 0 && maxAttempts is > 0)
            parts.Add($"attempt {attempt}/{maxAttempts}");
        if (sessionResumed.HasValue)
            parts.Add(sessionResumed.Value ? "session resumed" : "session new");
        return parts.Count == 0 ? string.Empty : $"({string.Join(", ", parts)})";
    }

    private static string Clean(string? value)
        => (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
}
