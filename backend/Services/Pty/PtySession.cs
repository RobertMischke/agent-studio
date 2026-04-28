using System.Text;
using System.Text.RegularExpressions;
using Porta.Pty;

namespace OrchestratorApi.Services.Pty;

/// <summary>
/// Thin async wrapper around <see cref="IPtyConnection"/> tailored for driving
/// interactive console programs (Copilot CLI, REPLs, …) headless.
///
/// Captures the raw ANSI stream into a rolling buffer, lets callers
///   • wait until output settles (heuristic for "TUI is ready"),
///   • wait for a specific regex to appear,
///   • send keystrokes (with symbolic mapping like <c>&lt;Esc&gt;</c>, <c>&lt;Down&gt;</c>),
///   • take an ANSI-stripped snapshot of what would currently be on screen.
/// </summary>
public sealed class PtySession : IAsyncDisposable
{
    private static readonly Regex CsiRegex = new(@"\x1b\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly Regex OscRegex = new(@"\x1b\][^\x07\x1b]*(\x07|\x1b\\)", RegexOptions.Compiled);
    private static readonly Regex EscRegex = new(@"\x1b[@-Z\\-_]", RegexOptions.Compiled);

    private static readonly Dictionary<string, string> KeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["<Esc>"]   = "\x1b",
        ["<Enter>"] = "\r",
        ["<CR>"]    = "\r",
        ["<Tab>"]   = "\t",
        ["<Up>"]    = "\x1b[A",
        ["<Down>"]  = "\x1b[B",
        ["<Right>"] = "\x1b[C",
        ["<Left>"]  = "\x1b[D",
        ["<BS>"]    = "\x7f",
        ["<Space>"] = " ",
    };

    private readonly IPtyConnection _conn;
    private readonly StringBuilder _buffer = new();
    private readonly object _bufferLock = new();
    private readonly Task _readLoop;
    private readonly CancellationTokenSource _cts = new();
    private DateTime _lastByteAt = DateTime.UtcNow;
    private volatile bool _disposed;

    public int Pid => _conn.Pid;

    private PtySession(IPtyConnection conn)
    {
        _conn = conn;
        _readLoop = Task.Run(ReadLoopAsync);
    }

    public static async Task<PtySession> SpawnAsync(
        string app,
        IEnumerable<string>? args = null,
        string? cwd = null,
        IDictionary<string, string>? extraEnv = null,
        int cols = 160,
        int rows = 40,
        bool verbatimCommandLine = false,
        CancellationToken ct = default)
    {
        var env = new Dictionary<string, string>
        {
            ["TERM"] = "xterm-256color",
            ["COLORTERM"] = "truecolor",
            ["FORCE_COLOR"] = "0",
            ["NO_COLOR"] = "1",
        };
        if (extraEnv != null)
        {
            foreach (var (k, v) in extraEnv) env[k] = v;
        }

        var options = new PtyOptions
        {
            Name = "xterm-256color",
            Cols = cols,
            Rows = rows,
            Cwd = cwd ?? Environment.CurrentDirectory,
            App = app,
            CommandLine = (args ?? Array.Empty<string>()).ToArray(),
            VerbatimCommandLine = verbatimCommandLine,
            Environment = env,
        };
        var conn = await PtyProvider.SpawnAsync(options, ct);
        return new PtySession(conn);
    }

    private async Task ReadLoopAsync()
    {
        var buf = new byte[8192];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int n;
                try { n = await _conn.ReaderStream.ReadAsync(buf, _cts.Token); }
                catch (OperationCanceledException) { break; }
                catch { break; }
                if (n <= 0) break;
                lock (_bufferLock)
                {
                    _buffer.Append(Encoding.UTF8.GetString(buf, 0, n));
                    _lastByteAt = DateTime.UtcNow;
                }
            }
        }
        catch { /* swallow — connection torn down */ }
    }

    /// <summary>
    /// Wait until no new bytes arrive for at least <paramref name="idleMs"/>
    /// milliseconds, or <paramref name="timeoutMs"/> elapses.
    /// Returns true if idle was reached, false on timeout.
    /// </summary>
    public async Task<bool> WaitForIdleAsync(int idleMs = 500, int timeoutMs = 5000, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            DateTime last;
            lock (_bufferLock) last = _lastByteAt;
            if ((DateTime.UtcNow - last).TotalMilliseconds >= idleMs) return true;
            await Task.Delay(75, ct);
        }
        return false;
    }

    /// <summary>
    /// Wait until <paramref name="pattern"/> matches the ANSI-stripped snapshot,
    /// or timeout. Returns the matching <see cref="Match"/> or null.
    /// </summary>
    public async Task<Match?> WaitForPatternAsync(Regex pattern, int timeoutMs = 5000, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var snap = SnapshotStripped();
            var m = pattern.Match(snap);
            if (m.Success) return m;
            await Task.Delay(100, ct);
        }
        return null;
    }

    /// <summary>
    /// Send keys. Symbolic tokens like <c>&lt;Esc&gt;</c> are mapped to control sequences.
    /// </summary>
    public async Task SendKeysAsync(string keys, CancellationToken ct = default)
    {
        var expanded = ExpandKeys(keys);
        var bytes = Encoding.UTF8.GetBytes(expanded);
        await _conn.WriterStream.WriteAsync(bytes, ct);
        await _conn.WriterStream.FlushAsync(ct);
    }

    private static string ExpandKeys(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        return Regex.Replace(input, @"<[A-Za-z]+>", m =>
            KeyMap.TryGetValue(m.Value, out var v) ? v : m.Value);
    }

    /// <summary>Raw ANSI buffer (everything received so far).</summary>
    public string SnapshotRaw()
    {
        lock (_bufferLock) return _buffer.ToString();
    }

    /// <summary>ANSI-stripped buffer — what a human would (roughly) read.</summary>
    public string SnapshotStripped()
    {
        var raw = SnapshotRaw();
        raw = CsiRegex.Replace(raw, "");
        raw = OscRegex.Replace(raw, "");
        raw = EscRegex.Replace(raw, "");
        return raw;
    }

    public void ClearBuffer()
    {
        lock (_bufferLock) _buffer.Clear();
    }

    public void Resize(int cols, int rows)
    {
        try { _conn.Resize(cols, rows); } catch { /* best-effort */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _conn.Kill(); } catch { }
        try { await _readLoop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        try { _conn.Dispose(); } catch { }
        _cts.Dispose();
    }
}
