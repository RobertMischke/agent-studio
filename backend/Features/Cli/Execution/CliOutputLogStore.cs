using System.Text;
using System.Text.Json;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Cli;

/// <summary>
/// Durable per-job persistence for the raw CLI activity stream
/// (<c>.runtime/cli-output/&lt;cli&gt;-&lt;jobKey&gt;.jsonl</c>).
///
/// The file is the source-of-truth backup for the in-memory
/// <c>OutputBuffer</c> while a CLI is running — the buffer can be lost on a
/// backend restart, crash, or the 30-minute post-exit cleanup, and reviewers
/// have reported "the Activity Log disappeared" specifically in those windows.
/// To make the log survive those events we:
///
/// <list type="bullet">
/// <item>open one long-lived <see cref="FileStream"/> per job with
///   <see cref="FileShare.ReadWrite"/> so concurrent reads from API
///   handlers see a coherent file while the writer is appending,</item>
/// <item>serialise concurrent stdout / stderr / system writers through a
///   per-instance lock (no global cross-job contention),</item>
/// <item>call <c>Flush(true)</c> after every line so an OS-level kill of
///   the host process still leaves every acknowledged line on disk.</item>
/// </list>
///
/// Read paths must tolerate a partially-written trailing line — a crash mid-
/// write can leave one. <see cref="ReadAll"/> drops malformed lines instead
/// of throwing.
/// </summary>
public sealed class CliOutputLogStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };
    private static readonly byte[] Newline = Encoding.UTF8.GetBytes("\n");

    // Circuit breaker for a target that has gone unwritable mid-stream
    // (job folder moved/deleted, antivirus quarantine, disk full). Each
    // failed Append otherwise costs a Directory.CreateDirectory + FileStream
    // open + fsync; when every CLI line hits that, the syscalls plus the
    // per-line warning at the call site flooded the log and contributed to a
    // host crash (the "Failed to persist CLI output line" storm). After a
    // short burst of consecutive failures we stop touching the filesystem for
    // a cooldown window and return false cheaply; the caller still has the
    // in-memory OutputBuffer, so no acknowledged line is lost from the live
    // view. One attempt per window then probes whether the path recovered.
    private const int FailuresBeforeBackoff = 5;
    private static readonly TimeSpan BackoffWindow = TimeSpan.FromSeconds(2);

    private readonly object _lock = new();
    private FileStream? _stream;
    private bool _disposed;
    private int _consecutiveFailures;
    private long _totalFailures;
    private DateTime _backoffUntilUtc = DateTime.MinValue;
    private string? _lastErrorMessage;

    public string Path { get; }

    /// <summary>
    /// Reason (<c>ExceptionType: message</c>) of the most recent failed
    /// <see cref="Append"/>, or null when the last append succeeded. Lets the
    /// call site surface the cause instead of swallowing it - the previous
    /// behaviour returned a bare <c>false</c> and the operator never learned
    /// <i>why</i> the line could not be persisted.
    /// </summary>
    public string? LastErrorMessage { get { lock (_lock) return _lastErrorMessage; } }

    /// <summary>
    /// Total number of <see cref="Append"/> calls that actually attempted a
    /// filesystem write and threw, over this store's lifetime. Calls skipped
    /// during a backoff window are not counted, so a value far below the
    /// number of Append calls is the signal that the breaker is bounding the
    /// I/O storm.
    /// </summary>
    public long TotalFailures { get { lock (_lock) return _totalFailures; } }

    public CliOutputLogStore(string path)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
    }

    /// <summary>
    /// Truncate the file. Called once at the start of each fresh run so a
    /// re-run doesn't accumulate stale lines from the previous attempt.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CliOutputLogStore));
            EnsureDirectory();
            CloseStream();
            // Truncate atomically. Open with WriteThrough so the truncation
            // itself reaches disk before we hand the file back for appending.
            using (var fs = new FileStream(
                Path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096, FileOptions.WriteThrough))
            {
                fs.Flush(flushToDisk: true);
            }
        }
    }

    /// <summary>
    /// Append a single line. The write is serialised against other appenders
    /// for the same store and flushed all the way to disk before returning.
    /// Returns false on I/O failure so callers can surface it instead of
    /// silently losing data — the previous behaviour swallowed every
    /// exception and is exactly how the user noticed lines going missing.
    /// </summary>
    public bool Append(CliOutputLine line)
    {
        if (line is null) return false;

        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(line, JsonOpts));

        lock (_lock)
        {
            if (_disposed) return false;

            // Breaker open: a recent burst of failures put us in a cooldown.
            // Skip the filesystem entirely so a vanished target cannot turn
            // every streamed line into a failed open + fsync.
            if (DateTime.UtcNow < _backoffUntilUtc) return false;

            try
            {
                EnsureDirectory();
                _stream ??= OpenAppend();
                _stream.Write(payload, 0, payload.Length);
                _stream.Write(Newline, 0, Newline.Length);
                _stream.Flush(flushToDisk: true);
                if (_consecutiveFailures != 0)
                {
                    // Path recovered (or the breaker's probe succeeded): clear
                    // the failure latch so the caller can log a recovery.
                    _consecutiveFailures = 0;
                    _backoffUntilUtc = DateTime.MinValue;
                    _lastErrorMessage = null;
                }
                return true;
            }
            catch (Exception ex)
            {
                // The handle may have been invalidated (file deleted out from
                // under us, antivirus quarantine, etc). Capture the cause so
                // the caller can surface it once, drop the (possibly dead)
                // handle so the next attempt re-opens cleanly, and arm the
                // breaker once failures pile up so we stop tight-looping.
                _lastErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
                _totalFailures++;
                _consecutiveFailures++;
                CloseStream();
                if (_consecutiveFailures >= FailuresBeforeBackoff)
                    _backoffUntilUtc = DateTime.UtcNow + BackoffWindow;
                return false;
            }
        }
    }

    /// <summary>
    /// Read the entire log from disk. Safe to call concurrently with
    /// <see cref="Append"/> on the same store and from another process.
    /// </summary>
    public List<CliOutputLine> ReadAll() => ReadAll(Path);

    /// <summary>
    /// Static read so the API surface still works after the owning store
    /// has been disposed (e.g. job ran to completion, backend restarted,
    /// reviewer is reading the log later).
    /// </summary>
    public static List<CliOutputLine> ReadAll(string? path)
    {
        var result = new List<CliOutputLine>();
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return result;

        try
        {
            // FileShare.ReadWrite is essential — without it, opening for read
            // while a sibling process / thread holds the writer's handle would
            // throw IOException and the Activity Log endpoint would 500.
            using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                CliOutputLine? entry = null;
                try { entry = JsonSerializer.Deserialize<CliOutputLine>(line); }
                catch (Exception __ex) { SilentCatch.Note(__ex, "CliOutputLogStore: trailing partial line from a crash mid-write — skip"); /* trailing partial line from a crash mid-write — skip */ }
                if (entry != null) result.Add(entry);
            }
        }
        catch (Exception __ex)
        {
            SilentCatch.Note(__ex, "CliOutputLogStore: Read is best-effort on top of best-effort: returning what we");
            // Read is best-effort on top of best-effort: returning what we
            // managed to parse is strictly better than failing the request.
        }

        return result;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            CloseStream();
        }
    }

    private FileStream OpenAppend()
    {
        // FileMode.Append positions at end and refuses seeks — exactly what
        // we want to keep concurrent appenders honest.
        return new FileStream(
            Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096, FileOptions.None);
    }

    private void EnsureDirectory()
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    private void CloseStream()
    {
        try { _stream?.Dispose(); } catch (Exception __ex) { SilentCatch.Note(__ex, "CliOutputLogStore: already broken"); /* already broken */ }
        _stream = null;
    }
}
