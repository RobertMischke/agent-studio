using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.State;

/// <summary>
/// File-backed, in-memory projection of one append-only JSONL document type
/// per (workspace, project) pair. The pattern matches the Agent Message Bus
/// store (see <c>AgentStudio.Bus.AgentMessageBusStore</c>) and is
/// the shared implementation for everything that lands a typed schema in
/// <c>docs/schemas/</c>.
/// </summary>
/// <remarks>
/// <para>
/// Disk is the source of truth: a single JSONL file under the workspace's
/// <c>logs/</c> directory holds every record, one JSON object per line.
/// External writers that bypass the store will not be visible until
/// <see cref="InvalidateProjection"/> is called for the (workspace, project)
/// pair.
/// </para>
/// <para>
/// Thread-safety: writes serialise per-file via a <see cref="SemaphoreSlim"/>
/// and the projection list is replaced atomically under a per-projection lock.
/// Readers snapshot the underlying list reference so a reader and an appender
/// cannot trip on each other.
/// </para>
/// <para>
/// Optimistic concurrency: every successful append bumps a per-projection
/// monotonic <c>Version</c> counter. Consumers that rely on read-then-write
/// can call <see cref="AppendIfVersionAsync"/>; pure append-only writers
/// ignore the version. Streaming consumers like
/// <c>AutoInterventionHostedService</c> use <see cref="ReadSince"/> with the
/// last seen version as a cursor.
/// </para>
/// </remarks>
public abstract class InMemoryStore<T> where T : class
{
    /// <summary>
    /// Shared serializer options. Web defaults align with the backend's HTTP
    /// JSON setup; <c>WhenWritingNull</c> matches the JSON-schema-first rule
    /// that optional fields are absent rather than null on disk.
    /// </summary>
    protected static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ConcurrentDictionary<ProjectionKey, Projection> _projections = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolve the absolute path of the JSONL file for one (workspace, project).</summary>
    protected abstract string ResolvePath(string workspaceRoot, string project);

    /// <summary>
    /// Stable identifier for one record. Used for <see cref="GetById"/> lookups
    /// and to dedupe the by-id map when the on-disk file already contains a
    /// record with the same id. Records that have no externally meaningful id
    /// (append-only logs of advisories, interventions) can synthesise one from
    /// the fields they care about.
    /// </summary>
    protected abstract string GetId(T item);

    /// <summary>
    /// Validate a record against the schema's value sets and required-field
    /// rules. Returns false on rejection together with a one-line reason.
    /// Strict at append time so new garbage cannot enter; lenient on read so
    /// one bad legacy line does not break the projection.
    /// </summary>
    protected abstract bool TryValidate(T item, out string? error);

    /// <summary>
    /// Parse one JSON line into a record. Default implementation deserialises
    /// with <see cref="JsonOptions"/> and swallows <see cref="JsonException"/>;
    /// override to plug a different parser.
    /// </summary>
    protected virtual T? ParseLine(string line)
    {
        try { return JsonSerializer.Deserialize<T>(line, JsonOptions); }
        catch (JsonException) { return null; }
    }

    public IReadOnlyList<T> Snapshot(string workspaceRoot, string project)
        => GetOrLoad(workspaceRoot, project).Snapshot();

    public T? GetById(string workspaceRoot, string project, string id)
        => GetOrLoad(workspaceRoot, project).GetById(id);

    public IReadOnlyList<T> Where(string workspaceRoot, string project, Func<T, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return GetOrLoad(workspaceRoot, project).Snapshot().Where(predicate).ToList();
    }

    public int Count(string workspaceRoot, string project)
        => GetOrLoad(workspaceRoot, project).Snapshot().Count;

    /// <summary>
    /// Monotonic projection version. Equal to the number of successful appends
    /// since the projection was loaded plus any rows present on disk at load
    /// time. Use as a cursor for <see cref="ReadSince"/> or as the expected
    /// version for <see cref="AppendIfVersionAsync"/>.
    /// </summary>
    public long GetVersion(string workspaceRoot, string project)
        => GetOrLoad(workspaceRoot, project).Version;

    /// <summary>
    /// Drop the cached projection for one (workspace, project). The next read
    /// reloads from disk. Call this after an out-of-band write that bypassed
    /// the store (legacy code paths, manual edits, tests).
    /// </summary>
    public void InvalidateProjection(string workspaceRoot, string project)
        => _projections.TryRemove(new ProjectionKey(workspaceRoot, project), out _);

    /// <summary>
    /// Append one record to the file and update the in-memory projection.
    /// Atomic at the line level: a single serialise-and-write under a per-file
    /// semaphore. If validation fails the file is not touched and an
    /// <see cref="InvalidOperationException"/> is thrown.
    /// </summary>
    /// <returns>The new monotonic projection version.</returns>
    public async Task<long> AppendAsync(string workspaceRoot, string project, T record, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentNullException.ThrowIfNull(record);
        if (!TryValidate(record, out var error))
            throw new InvalidOperationException($"{typeof(T).Name} rejected: {error}");

        var path = ResolvePath(workspaceRoot, project);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Load the projection BEFORE writing so the disk-load below does not
        // pick up the line we are about to append (which would double-count it
        // when we then mirror the append into the in-memory list).
        var projection = GetOrLoad(workspaceRoot, project);

        var line = JsonSerializer.Serialize(record, JsonOptions);
        // JSONL contract: one record per line, no embedded newlines.
        if (line.Contains('\n')) line = line.Replace("\r", "").Replace("\n", " ");
        var bytes = Encoding.UTF8.GetBytes(line + "\n");

        var sem = _fileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
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

        return projection.Append(record, GetId(record));
    }

    /// <summary>
    /// Append only when the projection is still at <paramref name="expectedVersion"/>.
    /// Throws <see cref="OptimisticConcurrencyException"/> when another writer
    /// has advanced the version. Routine append-only writers should call
    /// <see cref="AppendAsync"/>; this overload is for read-modify-write paths
    /// that need to detect concurrent appends.
    /// </summary>
    public Task<long> AppendIfVersionAsync(string workspaceRoot, string project, T record, long expectedVersion, CancellationToken ct = default)
    {
        var current = GetOrLoad(workspaceRoot, project).Version;
        if (current != expectedVersion)
        {
            throw new OptimisticConcurrencyException(
                $"{typeof(T).Name}: expected projection version {expectedVersion} but found {current}.");
        }
        return AppendAsync(workspaceRoot, project, record, ct);
    }

    /// <summary>
    /// Returns records appended after <paramref name="cursor"/> together with
    /// the new cursor to pass next time. Pass <c>0</c> on the first call to
    /// receive everything that is on disk. Cursor-based reads are how
    /// streaming consumers replace ad-hoc <c>FileStream.Seek</c> bookkeeping.
    /// </summary>
    public (IReadOnlyList<T> Records, long NewCursor) ReadSince(string workspaceRoot, string project, long cursor)
        => GetOrLoad(workspaceRoot, project).ReadSince(cursor);

    private Projection GetOrLoad(string workspaceRoot, string project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        return _projections.GetOrAdd(new ProjectionKey(workspaceRoot, project), LoadFromDisk);
    }

    private Projection LoadFromDisk(ProjectionKey key)
    {
        var projection = new Projection();
        var path = ResolvePath(key.WorkspaceRoot, key.Project);
        if (!File.Exists(path)) return projection;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var item = ParseLine(line);
            if (item is null) continue;
            // Lenient on read: a single bad line never breaks the projection.
            // Strict-mode validation runs at append time.
            if (!TryValidate(item, out _)) continue;
            projection.Append(item, GetId(item));
        }
        return projection;
    }

    private readonly record struct ProjectionKey(string WorkspaceRoot, string Project);

    private sealed class Projection
    {
        private readonly object _lock = new();
        private List<T> _items = new();
        private Dictionary<string, T> _byId = new(StringComparer.Ordinal);
        private long _version;

        public long Version
        {
            get { lock (_lock) return _version; }
        }

        public IReadOnlyList<T> Snapshot()
        {
            lock (_lock) return _items;
        }

        public T? GetById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (_lock) return _byId.TryGetValue(id, out var v) ? v : null;
        }

        public long Append(T item, string id)
        {
            lock (_lock)
            {
                var next = new List<T>(_items.Count + 1);
                next.AddRange(_items);
                next.Add(item);
                _items = next;

                // First-write-wins for the by-id map; the list keeps every
                // record so repeated appends with the same synthetic id do
                // not lose data.
                if (!string.IsNullOrEmpty(id) && !_byId.ContainsKey(id))
                {
                    var byId = new Dictionary<string, T>(_byId, StringComparer.Ordinal) { [id] = item };
                    _byId = byId;
                }

                return ++_version;
            }
        }

        public (IReadOnlyList<T>, long) ReadSince(long cursor)
        {
            lock (_lock)
            {
                if (cursor >= _version) return (Array.Empty<T>(), _version);
                var delta = (int)Math.Min(_items.Count, _version - cursor);
                var startIndex = _items.Count - delta;
                if (startIndex < 0) startIndex = 0;
                var slice = _items.GetRange(startIndex, _items.Count - startIndex);
                return (slice, _version);
            }
        }
    }
}

/// <summary>
/// Raised by <see cref="InMemoryStore{T}.AppendIfVersionAsync"/> when the
/// projection has advanced past the expected version. Read-modify-write
/// callers should re-read and retry; pure append-only writers do not encounter
/// this exception.
/// </summary>
public sealed class OptimisticConcurrencyException : Exception
{
    public OptimisticConcurrencyException(string message) : base(message) { }
}
