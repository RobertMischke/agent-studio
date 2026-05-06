using System.Globalization;

namespace OrchestratorApi.Services.Cli;

/// <summary>
/// Side-channel liveness watcher for the Claude CLI. Claude writes one
/// JSONL line per protocol frame to
/// <c>~/.claude/projects/&lt;encoded-cwd&gt;/&lt;session-uuid&gt;.jsonl</c>
/// independently of the stdout pipe the orchestrator captures. When the
/// stdout pipe is block-buffered (the standard symptom of Node piping
/// to a non-TTY parent) we may see no bytes for tens of seconds even
/// though the agent is mid-conversation; the session file does not
/// suffer from that buffering and is the most reliable "is the agent
/// still alive" signal.
///
/// <para>
/// Contract: caller hands us the captured session id and the working
/// directory the CLI ran in. We compute the per-cwd encoded folder name
/// claude uses, point a <see cref="FileSystemWatcher"/> at the resulting
/// session file, and invoke <paramref name="onActivity"/> every time
/// the file mtime changes. The watcher is disposed when this object is.
/// </para>
/// <para>
/// We do not parse the JSONL frames here. The runner's adapter consumes
/// the stdout side; this helper exists to feed the watchdog a heartbeat
/// signal that does not depend on a live stdout pipe. The
/// <see cref="CliRunEvent.Heartbeat"/> the runner raises in response is
/// already an activity signal in
/// <see cref="RunPhaseTransitions.IsActivitySignal"/>.
/// </para>
/// </summary>
public sealed class ClaudeSessionHeartbeat : IDisposable
{
    private readonly FileSystemWatcher? _watcher;
    private readonly string? _path;
    private readonly Action _onActivity;
    private readonly object _gate = new();
    private DateTime _lastSeenWriteAt = DateTime.MinValue;

    public string? WatchedPath => _path;

    public ClaudeSessionHeartbeat(string? sessionId, string workingDirectory, Action onActivity, ILogger? logger = null)
    {
        _onActivity = onActivity;

        if (string.IsNullOrWhiteSpace(sessionId)) return;

        var path = ResolveSessionFile(sessionId!, workingDirectory);
        if (path == null) return;

        _path = path;
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            logger?.LogDebug("ClaudeSessionHeartbeat: directory does not exist yet ({Dir}); watcher idle.", dir);
            return;
        }

        var fileName = Path.GetFileName(path);
        try
        {
            _watcher = new FileSystemWatcher(dir, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnFsEvent;
            _watcher.Created += OnFsEvent;
            _watcher.Renamed += OnFsRename;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "ClaudeSessionHeartbeat: FileSystemWatcher could not start for {Path}", path);
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    /// <summary>
    /// Path inside <c>~/.claude/projects/&lt;encoded-cwd&gt;/</c> where
    /// claude stores the per-session JSONL log. The encoding rule is
    /// observed empirically: each path segment separator (drive colon,
    /// backslash, forward slash) is replaced with two hyphens for the
    /// drive (e.g. <c>C:</c> → <c>C-</c>), then segments join with one
    /// hyphen. Case is preserved. The actual encoder is internal to
    /// claude-code; we replicate the visible-on-disk shape only.
    /// </summary>
    public static string? ResolveSessionFile(string sessionId, string workingDirectory)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return null;

        var encoded = EncodeProjectFolder(workingDirectory);
        if (string.IsNullOrEmpty(encoded)) return null;

        return Path.Combine(home, ".claude", "projects", encoded, sessionId + ".jsonl");
    }

    private static string EncodeProjectFolder(string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return "";
        // Empirical mapping observed in ~/.claude/projects/:
        //   C:\Projects\agent-taskboard-devspace\agent-taskboard-dev
        //     ->  C--Projects-agent-taskboard-devspace-agent-taskboard-dev
        // Each `:` becomes a single hyphen; each `\` or `/` also becomes
        // a single hyphen. The visible double `--` after the drive letter
        // is just `:` + `\` collapsing to `-` + `-`.
        var sb = new System.Text.StringBuilder(cwd.Length + 4);
        foreach (var ch in cwd)
        {
            sb.Append(ch == ':' || ch == '\\' || ch == '/' ? '-' : ch);
        }
        return sb.ToString();
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e) => Pulse();
    private void OnFsRename(object sender, RenamedEventArgs e) => Pulse();

    private void Pulse()
    {
        // Coalesce bursts: FileSystemWatcher commonly fires twice per
        // append (one for size, one for mtime). One heartbeat per second
        // is plenty for the watchdog and avoids inflating the typed
        // event stream with noise.
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if ((now - _lastSeenWriteAt).TotalMilliseconds < 1000) return;
            _lastSeenWriteAt = now;
        }
        try { _onActivity(); }
        catch { /* best-effort: never let a heartbeat callback crash the watcher */ }
    }

    public void Dispose()
    {
        try
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Changed -= OnFsEvent;
                _watcher.Created -= OnFsEvent;
                _watcher.Renamed -= OnFsRename;
                _watcher.Dispose();
            }
        }
        catch { /* dispose path swallows */ }
    }
}
