using System.Text;
using System.Text.Json;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Tasks;

/// <summary>
/// Everything that records "what happened to this job's CLI session":
/// the persisted <c>sessionName</c> and full <c>sessionChain</c> in
/// <c>task.json</c>, the JSONL event log under <c>logs/session-events.jsonl</c>
/// that drives the "session continued / lost" chip, and the related
/// usage snapshot writeback. Reads of the same data live in
/// <see cref="TaskScannerService"/> alongside the rest of the read
/// surface; this service owns the writes plus the JSONL append-line
/// reader that the protocol pane needs.
/// </summary>
public class TaskSessionLog
{
    private readonly TaskScannerService _scanner;
    private readonly ILogger<TaskSessionLog> _logger;

    private static readonly JsonSerializerOptions SessionEventJsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };

    public TaskSessionLog(TaskScannerService scanner, ILogger<TaskSessionLog> logger)
    {
        _scanner = scanner;
        _logger = logger;
    }

    public bool SetJobSessionName(string jobId, string? sessionName, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        TaskJsonFile.UpdateField(info.FolderPath, "sessionName", sessionName ?? "", _logger);
        return true;
    }

    /// <summary>
    /// Appends <paramref name="sessionId"/> to the job's <c>sessionChain</c>
    /// (no-op if the id is already the chain's tail) and updates
    /// <c>sessionName</c> in lockstep so the existing single-id consumers keep
    /// working. Used after a CLI emits its session UUID so a later
    /// <c>--resume</c> can still see the latest fork as the tail.
    /// </summary>
    public bool AppendSessionToChain(string jobId, string sessionId, string? watchPath = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        var chain = new List<string>(info.SessionChain);
        if (chain.Count == 0 || !string.Equals(chain[^1], sessionId, StringComparison.Ordinal))
        {
            chain.Add(sessionId);
            TaskJsonFile.UpdateField(info.FolderPath, "sessionChain", chain, _logger);
        }
        TaskJsonFile.UpdateField(info.FolderPath, "sessionName", sessionId, _logger);
        return true;
    }

    /// <summary>
    /// Resets the chain when a recovery-continue is performed. The previous
    /// session ids are preserved as history, but a sentinel <c>"(recovery)"</c>
    /// marker is appended so consumers can spot chain breaks. The next captured
    /// session id will start the new chain segment.
    /// </summary>
    public bool MarkSessionChainRecovery(string jobId, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        var chain = new List<string>(info.SessionChain);
        if (chain.Count > 0 && chain[^1] != "(recovery)")
        {
            chain.Add("(recovery)");
            TaskJsonFile.UpdateField(info.FolderPath, "sessionChain", chain, _logger);
        }
        TaskJsonFile.UpdateField(info.FolderPath, "sessionName", "", _logger);
        return true;
    }

    /// <summary>
    /// Captures the exact context string handed to the CLI for one run into
    /// its own file under <c>logs/run-context/</c> and returns the relative,
    /// forward-slashed path to store on the run's <see cref="SessionEvent.ContextRef"/>.
    /// Kept out of <c>session-events.jsonl</c> (and therefore the polled runs
    /// list) on purpose: a rendered prompt is multi-KB, so inlining it would
    /// bloat every 5 s poll. The file is served on demand by the per-run
    /// context endpoint. Best-effort: a write failure returns null and the run
    /// is recorded with no context ref rather than failing the spawn.
    /// </summary>
    public string? PersistRunContext(string jobFolder, string context)
    {
        try
        {
            var dir = TaskPaths.RunContextDir(jobFolder);
            Directory.CreateDirectory(dir);
            var fileName = $"run-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.md";
            File.WriteAllText(Path.Combine(dir, fileName), context ?? string.Empty, Encoding.UTF8);
            return $"{TaskPaths.LogsDirName}/{TaskPaths.RunContextDirName}/{fileName}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist run context under {JobFolder}", jobFolder);
            return null;
        }
    }

    /// <summary>
    /// Append-one-line writer for <c>logs/session-events.jsonl</c>. JSONL keeps
    /// the file cheap to tail and tolerant to interrupted writes — a torn line
    /// is one lost event, not a corrupted document.
    /// </summary>
    public bool AppendSessionEvent(string jobId, SessionEvent evt, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        try
        {
            Directory.CreateDirectory(TaskPaths.LogsDir(info.FolderPath));
            var path = TaskPaths.SessionEventsLog(info.FolderPath);
            var line = JsonSerializer.Serialize(evt, SessionEventJsonOpts) + Environment.NewLine;
            File.AppendAllText(path, line, Encoding.UTF8);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append session event for job {JobId}", jobId);
            return false;
        }
    }

    /// <summary>
    /// Rewrites the last row in <c>session-events.jsonl</c> with the
    /// <paramref name="capturedSessionId"/>. Called from <c>OnCliFinished</c>
    /// after a CLI emits its session UUID. Best-effort: a missing or
    /// unparseable last line is ignored. Used to fill in the
    /// <c>capturedSessionId</c> that the start-of-run event couldn't know yet.
    /// </summary>
    public bool BackfillLatestSessionEventCapturedId(string jobId, string capturedSessionId, string? watchPath = null)
    {
        if (string.IsNullOrWhiteSpace(capturedSessionId)) return false;
        return MutateLatestSessionEvent(jobId, watchPath, evt => evt with { CapturedSessionId = capturedSessionId });
    }

    /// <summary>
    /// Records the post-run HEAD SHA on the most recent session event so
    /// downstream readers (the run timeline, the per-run commits endpoint)
    /// can derive "commits made during this run" via the deterministic
    /// SHA range <c>HeadShaBefore..HeadShaAfter</c> instead of falling
    /// back to the wall-clock window.
    /// </summary>
    public bool BackfillLatestSessionEventHeadShaAfter(string jobId, string? sha, string? watchPath = null)
    {
        if (string.IsNullOrWhiteSpace(sha)) return false;
        return MutateLatestSessionEvent(jobId, watchPath, evt => evt with { HeadShaAfter = sha });
    }

    private bool MutateLatestSessionEvent(string jobId, string? watchPath, Func<SessionEvent, SessionEvent> mutate)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        var path = TaskPaths.SessionEventsLog(info.FolderPath);
        if (!File.Exists(path)) return false;
        try
        {
            var lines = File.ReadAllLines(path).ToList();
            var idx = lines.FindLastIndex(l => !string.IsNullOrWhiteSpace(l));
            if (idx < 0) return false;
            SessionEvent? evt;
            try { evt = JsonSerializer.Deserialize<SessionEvent>(lines[idx], TaskJsonFile.ReadOpts); }
            catch { return false; }
            if (evt == null) return false;
            lines[idx] = JsonSerializer.Serialize(mutate(evt), SessionEventJsonOpts);
            File.WriteAllLines(path, lines, Encoding.UTF8);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to mutate latest session event for job {JobId}", jobId);
            return false;
        }
    }

    /// <summary>
    /// Reads <c>logs/session-events.jsonl</c> into a list. Tolerant to torn
    /// trailing lines: a line that fails to parse is skipped. Lives next to
    /// the writers because the JSONL shape and parse rules need to evolve in
    /// lockstep.
    /// </summary>
    public List<SessionEvent> ReadSessionEvents(string jobId, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return [];
        var path = TaskPaths.SessionEventsLog(info.FolderPath);
        if (!File.Exists(path)) return [];
        var result = new List<SessionEvent>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var evt = JsonSerializer.Deserialize<SessionEvent>(line, TaskJsonFile.ReadOpts);
                if (evt != null) result.Add(evt);
            }
            catch
            {
                // Best-effort: skip torn / malformed lines.
            }
        }
        return result;
    }

    public bool UpdateLastUsage(string jobId, SessionUsage usage, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        TaskJsonFile.UpdateField(info.FolderPath, "lastUsage", usage, _logger);
        return true;
    }
}
