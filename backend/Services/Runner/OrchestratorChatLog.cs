using OrchestratorApi.Models;
using OrchestratorApi.Services.Bus;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Lets the orchestrator speak directly into the chat transcript as a
/// first-class participant.
///
/// <para>
/// <b>Why this exists.</b> The product treats orchestrator-to-CLI
/// communication as a core capability, not a side-effect. When the
/// orchestrator decides to re-issue a follow-up, accept a heuristic
/// verdict, or warn that the deterministic contract did not match, the
/// user has to see that decision next to the agent's own messages.
/// Hiding it in the backend log file would defeat the point. The
/// activity log already pulls from <c>logs/cli-output.log</c>, so the
/// cheapest reliable channel is to append <c>[orchestrator]</c>-stream
/// lines there.
/// </para>
///
/// <para>
/// Lines written here use the same persisted shape the CLI output
/// parser already understands (timestamp + bracketed stream tag), so
/// the disk-backed activity-log fallback continues to work and the
/// frontend can pick the messages up by stream alone.
/// </para>
/// </summary>
public class OrchestratorChatLog
{
    private readonly ILogger<OrchestratorChatLog> _logger;
    private readonly AgentMessageBusBridge? _bus;

    public OrchestratorChatLog(ILogger<OrchestratorChatLog> logger, AgentMessageBusBridge? bus = null)
    {
        _logger = logger;
        _bus = bus;
    }

    /// <summary>
    /// Append one orchestrator meta message to the job's <c>cli-output.log</c>
    /// and the runtime in-memory buffer (when one is supplied). The kind
    /// (<paramref name="kind"/>) becomes a leading tag on the persisted line
    /// (e.g. <c>[reissue]</c>) so future parsers can pick out structured
    /// classes without re-deriving them from the prose.
    /// </summary>
    public virtual bool Append(TaskInfo info, OrchestratorMessageKind kind, string text, ICollection<CliOutputLine>? liveBuffer = null)
    {
        var ok = AppendWithStream(info, "orchestrator", $"[{kind.ToTag()}] {text}", liveBuffer);
        if (ok)
        {
            // Bridge to the Agent Message Bus. Best-effort; the chat log is the
            // canonical record (the activity-log parser reads it). The bus
            // mirrors typed entries so future tooling can query without
            // reparsing prose. See docs/agent-message-bus.md section 9.
            try { _ = _bus?.EmitOrchestratorChatAsync(info, kind, text); }
            catch (Exception ex) { _logger.LogDebug(ex, "Bus mirror of orchestrator chat failed for {JobId}", info?.Id); }
        }
        return ok;
    }

    /// <summary>
    /// Append a meta message attributed to the supervisor participant. Same
    /// persistence shape as the orchestrator stream, but with the
    /// <c>[supervisor]</c> stream tag so the activity-log parser renders it
    /// as a separate participant alongside <c>You</c>, the agent, and
    /// <c>Orchestrator</c>.
    /// </summary>
    public bool AppendSupervisor(TaskInfo info, string tag, string text, ICollection<CliOutputLine>? liveBuffer = null)
    {
        var ok = AppendWithStream(info, "supervisor", $"[{tag}] {text}", liveBuffer);
        if (ok)
        {
            try { _ = _bus?.EmitSupervisorChatAsync(info, tag, text); }
            catch (Exception ex) { _logger.LogDebug(ex, "Bus mirror of supervisor chat failed for {JobId}", info?.Id); }
        }
        return ok;
    }

    private bool AppendWithStream(TaskInfo info, string streamTag, string body, ICollection<CliOutputLine>? liveBuffer)
    {
        if (info == null) return false;
        // If the job folder no longer exists, the job was moved (or deleted)
        // between the caller's lookup and this append. Recreating the folder
        // here would resurrect the source lane as a one-line skeleton —
        // exactly the residue that was littering 4-auto-review after every
        // accept-as-done. Refuse the write and let the caller treat it as
        // best-effort; the canonical record (decision journal, bus event)
        // still goes out.
        if (!Directory.Exists(info.FolderPath))
        {
            _logger.LogWarning(
                "OrchestratorChatLog: refusing to append {Stream} for {JobId}; folder gone at {Path}",
                streamTag, info.Id, info.FolderPath);
            return false;
        }
        try
        {
            Directory.CreateDirectory(TaskPaths.LogsDir(info.FolderPath));
            var logPath = TaskPaths.CliOutputLog(info.FolderPath);
            var ts = DateTime.UtcNow;
            var oneLine = (body ?? string.Empty).Replace("\r", " ").Replace("\n", " ").TrimEnd();
            var persistLine = $"[{ts:HH:mm:ss.fff}] [{streamTag}] {oneLine}";
            var prefix = File.Exists(logPath) && new FileInfo(logPath).Length > 0
                ? Environment.NewLine
                : string.Empty;
            File.AppendAllText(logPath, prefix + persistLine + Environment.NewLine, System.Text.Encoding.UTF8);

            if (liveBuffer != null)
            {
                liveBuffer.Add(new CliOutputLine
                {
                    Timestamp = ts,
                    Stream = streamTag,
                    Text = oneLine
                });
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append {Stream} message for {JobId}", streamTag, info.Id);
            return false;
        }
    }
}

/// <summary>
/// Kinds of orchestrator meta messages. Each maps to a short tag on the
/// persisted line so the frontend can render different glyphs / colors
/// without parsing the prose. Kept narrow so we are not tempted to use
/// the meta channel for general logging.
/// </summary>
public enum OrchestratorMessageKind
{
    /// <summary>The orchestrator made a decision about how to proceed (informational).</summary>
    Decision,
    /// <summary>The orchestrator is re-issuing a follow-up because the agent did not honor it.</summary>
    Reissue,
    /// <summary>The deterministic contract did not match; classification is a heuristic best-effort.</summary>
    HeuristicFallback,
    /// <summary>The orchestrator is intervening once to repair a recoverable protocol or tool-boundary issue.</summary>
    SoftIntervention,
    /// <summary>The agent hit tool permission boundaries and exhausted the one soft intervention.</summary>
    PermissionBlocked,
    /// <summary>The watchdog killed the run after a silence timeout.</summary>
    WatchdogTimeout,
    /// <summary>
    /// The watchdog noticed a silence gap but has not killed the run yet
    /// (Quiet or Suspicious states). Operator-facing copy is informational
    /// ("no action needed unless this repeats"); the topic is distinct from
    /// <see cref="Decision"/> so the workspace banner does not misread a
    /// silence advisory as a review verdict.
    /// </summary>
    WatchdogWarning,
    /// <summary>The agent did not emit the required terminal sentinel after one prompt repair.</summary>
    MissingTerminalSentinel,
    /// <summary>The agent reported done without a structured sentinel; kept as visible legacy heuristic.</summary>
    HeuristicDone,
    /// <summary>The classifier could not map the agent text to a known outcome.</summary>
    ClassifierUnknown,
    /// <summary>
    /// The agent CLI failed to launch or its <c>--resume</c> target was
    /// rejected before any agent turn happened (exit != 0, ~0s, only a CLI
    /// error fragment). The orchestrator treats this as a recoverable
    /// host/CLI condition and rebuilds from disk via Recovery on the next
    /// attempt, rather than surfacing a terminal classifier-unknown FAILURE.
    /// </summary>
    CliLaunchFailed,
    /// <summary>The orchestrator gave up after a retry budget; user attention required.</summary>
    GiveUp,
    /// <summary>The orchestrator could not pick a path on its own but identified a concrete unblocking ask the user can resolve. Renders distinctly so the user sees a productive escalation, not a silent deferral.</summary>
    Steer,
    /// <summary>
    /// An OS / sandbox / host-permission blocker was detected in-stream
    /// by <see cref="AgentEnvironmentDetector"/>. The run was killed
    /// before the silence budget elapsed; the job is escalated to human
    /// review with a typed diagnosis instead of a generic
    /// missing-terminal-sentinel verdict.
    /// </summary>
    EnvironmentBlocker,
    /// <summary>
    /// Codex stopped emitting frames after a successful tool call but
    /// never sent a closing <c>turn.completed</c> or sentinel
    /// (<see cref="CodexSilentCompletionDetector"/>). The runner finalized
    /// the run as Completed with the <c>outcome:silent-finish</c> tag so
    /// the auto-review aspect calls still run and the user sees why no
    /// sentinel landed.
    /// </summary>
    SilentCompletion,
    /// <summary>
    /// The run exceeded the model's input window (prompt too long / context
    /// length). Non-retryable: the orchestrator routes it straight to human
    /// review instead of re-issuing into the same overflow.
    /// </summary>
    ContextOverflow,
    /// <summary>
    /// The per-task circuit breaker tripped after N consecutive failed runs
    /// without progress; the task was parked in human review to stop an
    /// endless reissue loop.
    /// </summary>
    Quarantined
}

internal static class OrchestratorMessageKindExtensions
{
    public static string ToTag(this OrchestratorMessageKind kind) => kind switch
    {
        OrchestratorMessageKind.Decision          => "decision",
        OrchestratorMessageKind.Reissue           => "reissue",
        OrchestratorMessageKind.HeuristicFallback => "heuristic",
        OrchestratorMessageKind.SoftIntervention  => "intervention",
        OrchestratorMessageKind.PermissionBlocked => "permission-blocked",
        OrchestratorMessageKind.WatchdogTimeout   => "watchdog-timeout",
        OrchestratorMessageKind.WatchdogWarning   => "watchdog",
        OrchestratorMessageKind.MissingTerminalSentinel => "missing-terminal-sentinel",
        OrchestratorMessageKind.HeuristicDone     => "heuristic-done",
        OrchestratorMessageKind.ClassifierUnknown => "classifier-unknown",
        OrchestratorMessageKind.CliLaunchFailed   => "cli-launch-failed",
        OrchestratorMessageKind.GiveUp            => "giveup",
        OrchestratorMessageKind.Steer             => "steer",
        OrchestratorMessageKind.EnvironmentBlocker => "environment-blocker",
        OrchestratorMessageKind.SilentCompletion  => "codex-silent-completion",
        OrchestratorMessageKind.ContextOverflow   => "context-overflow",
        OrchestratorMessageKind.Quarantined       => "quarantined",
        _ => "info"
    };

    public static string ToBusTopic(this OrchestratorMessageKind kind) => kind switch
    {
        OrchestratorMessageKind.HeuristicFallback => "heuristicfallback",
        OrchestratorMessageKind.SoftIntervention  => "soft-intervention",
        OrchestratorMessageKind.PermissionBlocked => "permission-blocked",
        OrchestratorMessageKind.WatchdogTimeout   => "watchdog-timeout",
        OrchestratorMessageKind.WatchdogWarning   => "watchdog-warning",
        OrchestratorMessageKind.MissingTerminalSentinel => "missing-terminal-sentinel",
        OrchestratorMessageKind.HeuristicDone     => "heuristic-done",
        OrchestratorMessageKind.ClassifierUnknown => "classifier-unknown",
        OrchestratorMessageKind.CliLaunchFailed   => "cli-launch-failed",
        OrchestratorMessageKind.EnvironmentBlocker => "environment-blocker",
        OrchestratorMessageKind.SilentCompletion  => "codex-silent-completion",
        OrchestratorMessageKind.ContextOverflow   => "context-overflow",
        OrchestratorMessageKind.Quarantined       => "quarantined",
        _ => kind.ToString().ToLowerInvariant()
    };
}
