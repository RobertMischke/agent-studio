using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace OrchestratorApi.Services.Persistence;

/// <summary>
/// Single point of code for "append one line to a JSONL file" — the
/// pattern that previously lived inline in 20+ services. Holds a
/// per-path <see cref="SemaphoreSlim"/> so concurrent appenders cannot
/// interleave bytes; guarantees newline termination; ensures parent
/// directories exist; serializes via shared JSON options so every JSONL
/// file in the workspace uses the same casing and null-handling rules.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> Before this helper existed, 22 backend services rolled
/// their own <c>new FileStream(path, FileMode.Append, ...)</c> blocks;
/// only 4 wrapped the write in a semaphore. The other 18 are at risk
/// of byte-interleaving when two threads append simultaneously — the
/// .NET docs only guarantee atomicity for a single &lt;4 KB write under
/// <see cref="FileMode.Append"/> on NTFS, and many of our writes
/// exceed that (token-usage records, observation payloads).
/// </para>
/// <para>
/// <b>Best-effort contract.</b> All append methods throw on IO failure
/// so the caller can decide between log-and-continue (the bus / runtime
/// log path) and propagate (test fixtures). The caller chooses.
/// </para>
/// </remarks>
public interface IJsonlAppender
{
    /// <summary>Serialise <paramref name="record"/> as a single JSONL line
    /// and append it to <paramref name="path"/>. Newlines inside the JSON
    /// representation are flattened to single spaces so each on-disk line
    /// stays parseable on its own.</summary>
    Task AppendAsync<T>(string path, T record, JsonSerializerOptions? options = null, CancellationToken ct = default);

    /// <summary>Append a pre-formatted line. The helper adds the trailing
    /// newline if missing and flattens any embedded newlines. Use when the
    /// caller has already serialised (e.g. legacy paths writing pre-built
    /// strings).</summary>
    Task AppendLineAsync(string path, string line, CancellationToken ct = default);
}

/// <summary>
/// Default implementation. Holds one <see cref="SemaphoreSlim"/> per
/// distinct path; the cache grows by at most one entry per JSONL file
/// the process appends to (small bounded set in practice).
/// </summary>
public sealed class JsonlAppender : IJsonlAppender
{
    private static readonly JsonSerializerOptions DefaultOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks
        = new(StringComparer.OrdinalIgnoreCase);

    public async Task AppendAsync<T>(string path, T record, JsonSerializerOptions? options = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var json = JsonSerializer.Serialize(record, options ?? DefaultOptions);
        await AppendLineAsync(path, json, ct).ConfigureAwait(false);
    }

    public async Task AppendLineAsync(string path, string line, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(line);

        // Single-line guarantee: collapse any embedded newlines so each
        // record stays parseable on its own. Callers should not depend on
        // multiline payloads in JSONL; if they do, they need a different
        // container format.
        if (line.Contains('\n')) line = line.Replace("\r", "").Replace("\n", " ");
        var bytes = Encoding.UTF8.GetBytes(line + "\n");

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var sem = _locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Append, FileAccess.Write, FileShare.Read,
                bufferSize: 4096, useAsync: true);
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            sem.Release();
        }
    }
}
