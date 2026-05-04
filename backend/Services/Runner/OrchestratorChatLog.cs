using OrchestratorApi.Models;

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

    public OrchestratorChatLog(ILogger<OrchestratorChatLog> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Append one orchestrator meta message to the job's <c>cli-output.log</c>
    /// and the runtime in-memory buffer (when one is supplied). The kind
    /// (<paramref name="kind"/>) becomes a leading tag on the persisted line
    /// (e.g. <c>[reissue]</c>) so future parsers can pick out structured
    /// classes without re-deriving them from the prose.
    /// </summary>
    public bool Append(JobInfo info, OrchestratorMessageKind kind, string text, ICollection<CliOutputLine>? liveBuffer = null)
    {
        return AppendWithStream(info, "orchestrator", $"[{kind.ToTag()}] {text}", liveBuffer);
    }

    /// <summary>
    /// Append a meta message attributed to the supervisor participant. Same
    /// persistence shape as the orchestrator stream, but with the
    /// <c>[supervisor]</c> stream tag so the activity-log parser renders it
    /// as a separate participant alongside <c>You</c>, the agent, and
    /// <c>Orchestrator</c>.
    /// </summary>
    public bool AppendSupervisor(JobInfo info, string tag, string text, ICollection<CliOutputLine>? liveBuffer = null)
    {
        return AppendWithStream(info, "supervisor", $"[{tag}] {text}", liveBuffer);
    }

    private bool AppendWithStream(JobInfo info, string streamTag, string body, ICollection<CliOutputLine>? liveBuffer)
    {
        if (info == null) return false;
        try
        {
            Directory.CreateDirectory(JobPaths.LogsDir(info.FolderPath));
            var logPath = JobPaths.CliOutputLog(info.FolderPath);
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
    /// <summary>The orchestrator gave up after a retry budget; user attention required.</summary>
    GiveUp
}

internal static class OrchestratorMessageKindExtensions
{
    public static string ToTag(this OrchestratorMessageKind kind) => kind switch
    {
        OrchestratorMessageKind.Decision          => "decision",
        OrchestratorMessageKind.Reissue           => "reissue",
        OrchestratorMessageKind.HeuristicFallback => "heuristic",
        OrchestratorMessageKind.GiveUp            => "giveup",
        _ => "info"
    };
}
