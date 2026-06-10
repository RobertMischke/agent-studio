using System.Collections.Concurrent;

namespace AgentStudio.Cli;

/// <summary>
/// Per-run output log keyed by <b>stream</b>. Each logical stream of a run
/// (<c>stdout</c>, <c>stderr</c>, and any synthetic/system stream) gets its
/// own append-only file and its own lock, under a per-run directory:
///
/// <code>.runtime/cli-output/&lt;cli&gt;-&lt;taskKey&gt;/&lt;stream&gt;.jsonl</code>
///
/// <para>
/// This is the Step 5b hardening from
/// <c>docs/concepts/task-execution-and-log-architecture.md</c>: "one
/// append-only file + one lock per stream, never shared." The motivation is
/// concrete even for a single CLI process — the process's stdout and stderr
/// are pumped by two <b>separate reader threads</b> that both append at once.
/// With a single shared file they serialise on one lock (and an interrupted
/// writer can leave the shared handle open → the Windows "file in use" failure
/// that produced empty Activity Logs). One writer owning one file removes that
/// contention by construction; an orphaned writer can only ever affect its own
/// stream, never poison the whole run.
/// </para>
/// <para>
/// The merged, timestamp-interleaved view is computed <b>on read</b>
/// (<see cref="ReadMerged"/>), not by everyone writing the same file. The
/// public surface mirrors <see cref="CliOutputLogStore"/> so it is a drop-in
/// for the single-file store the executor used before.
/// </para>
/// </summary>
public sealed class RunLogStore : IDisposable
{
    private readonly string _runDir;
    private readonly ConcurrentDictionary<string, CliOutputLogStore> _byStream = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;
    private volatile string? _lastAppendError;

    /// <summary>The per-run directory this store writes its stream files into.</summary>
    public string Path => _runDir;

    /// <summary>
    /// Reason of the most recent failed <see cref="Append"/> (propagated from
    /// the per-stream <see cref="CliOutputLogStore.LastErrorMessage"/>), or
    /// null when the last append succeeded. The read loop logs this once per
    /// failure window instead of swallowing a bare false.
    /// </summary>
    public string? LastAppendError => _lastAppendError;

    public RunLogStore(string runDir)
    {
        _runDir = runDir ?? throw new ArgumentNullException(nameof(runDir));
    }

    /// <summary>
    /// Clear the run directory so a fresh run does not accumulate stale lines
    /// from a previous attempt. Mirrors <see cref="CliOutputLogStore.Reset"/>.
    /// </summary>
    public void Reset()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RunLogStore));
        // Drop any open stream writers first so their handles are released
        // before we delete the directory (Windows refuses to delete a file
        // with an open handle).
        foreach (var store in _byStream.Values)
        {
            try { store.Dispose(); } catch (Exception __ex) { SilentCatch.Note(__ex, "RunLogStore: already broken"); /* already broken */ }
        }
        _byStream.Clear();

        try
        {
            if (Directory.Exists(_runDir)) Directory.Delete(_runDir, recursive: true);
        }
        catch (Exception __ex) { SilentCatch.Note(__ex, "RunLogStore: best-effort; the per-stream Reset below still truncates each file"); /* best-effort; the per-stream Reset below still truncates each file */ }

        Directory.CreateDirectory(_runDir);
    }

    /// <summary>
    /// Append one line to the file for its <see cref="CliOutputLine.Stream"/>.
    /// Each stream's writes are serialised against other writes <i>for that
    /// same stream only</i> — distinct streams never share a lock or a handle.
    /// Returns false on I/O failure (same contract as
    /// <see cref="CliOutputLogStore.Append"/>).
    /// </summary>
    public bool Append(CliOutputLine line)
    {
        if (line is null || _disposed) return false;
        var store = StoreFor(line.Stream);
        if (store.Append(line)) return true;
        _lastAppendError = store.LastErrorMessage;
        return false;
    }

    private CliOutputLogStore StoreFor(string? stream)
    {
        var key = SanitizeStream(stream);
        return _byStream.GetOrAdd(key, k =>
            new CliOutputLogStore(System.IO.Path.Combine(_runDir, $"{k}.jsonl")));
    }

    /// <summary>
    /// Read every stream file under <paramref name="runDir"/> and merge them
    /// into a single timestamp-ordered list. Falls back to the legacy
    /// single-file layout (<c>&lt;runDir&gt;.jsonl</c>) written before per-stream
    /// files existed, so output from a run that started under the old layout is
    /// still recoverable after the upgrade. Static so it works after the owning
    /// store is disposed (run finished, backend restarted, reviewer reads later).
    /// </summary>
    public static List<CliOutputLine> ReadMerged(string? runDir)
    {
        if (string.IsNullOrEmpty(runDir)) return new List<CliOutputLine>();

        var merged = new List<CliOutputLine>();
        if (Directory.Exists(runDir))
        {
            foreach (var file in Directory.EnumerateFiles(runDir, "*.jsonl"))
            {
                merged.AddRange(CliOutputLogStore.ReadAll(file));
            }
        }

        // Backward compatibility: the pre-5b layout wrote one file at
        // "<runDir>.jsonl" instead of a directory of per-stream files.
        var legacy = runDir + ".jsonl";
        if (merged.Count == 0 && File.Exists(legacy))
        {
            merged.AddRange(CliOutputLogStore.ReadAll(legacy));
        }

        // Stable order by timestamp; OrderBy is a stable sort in .NET, so lines
        // sharing a millisecond keep their per-file append order.
        return merged.OrderBy(l => l.Timestamp).ToList();
    }

    /// <summary>
    /// Delete a run's stream directory (and any legacy single file). Best-effort:
    /// open handles are dropped first by <see cref="Dispose"/> at the call site.
    /// </summary>
    public static void DeleteRun(string? runDir)
    {
        if (string.IsNullOrEmpty(runDir)) return;
        try { if (Directory.Exists(runDir)) Directory.Delete(runDir, recursive: true); } catch (Exception __ex) { SilentCatch.Note(__ex, "RunLogStore:144"); }
        try { var legacy = runDir + ".jsonl"; if (File.Exists(legacy)) File.Delete(legacy); } catch (Exception __ex) { SilentCatch.Note(__ex, "RunLogStore:145"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var store in _byStream.Values)
        {
            try { store.Dispose(); } catch (Exception __ex) { SilentCatch.Note(__ex, "RunLogStore: already broken"); /* already broken */ }
        }
        _byStream.Clear();
    }

    private static string SanitizeStream(string? stream)
    {
        if (string.IsNullOrWhiteSpace(stream)) return "stdout";
        var s = stream.Trim().ToLowerInvariant();
        // Keep file names tame; the stream label is a short token (stdout /
        // stderr / system), but guard against anything odd a caller passes.
        foreach (var c in System.IO.Path.GetInvalidFileNameChars())
        {
            s = s.Replace(c, '_');
        }
        return s.Length == 0 ? "stdout" : s;
    }
}
