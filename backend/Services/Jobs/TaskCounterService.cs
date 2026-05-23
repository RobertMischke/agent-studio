using System.Collections.Concurrent;
using System.Text.Json;

namespace OrchestratorApi.Services.Jobs;

/// <summary>
/// F33: per-project monotonic counter that mints the numeric tail of a
/// reference key (<c>ATP-<u>130</u></c>). Persisted as
/// <c>&lt;watchPath&gt;/.task-counter.json</c>; in-memory writes are
/// serialised through a per-project <see cref="SemaphoreSlim"/> so two
/// concurrent <c>CreateJob</c> calls cannot draw the same number.
///
/// <para>The persisted shape is intentionally tiny:
/// <c>{ "next": 131, "lastIssuedUtc": "..." }</c>. <c>next</c> is the
/// value that will be handed out on the next call to
/// <see cref="Issue"/>. The counter never goes backwards;
/// <see cref="EnsureAtLeast"/> raises it to a floor without lowering it,
/// which is what the boot-time migration uses to align the counter with
/// the highest key already on disk.</para>
/// </summary>
public sealed class TaskCounterService
{
    private const string FileName = ".task-counter.json";
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _mutexes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<TaskCounterService> _logger;

    public TaskCounterService(ILogger<TaskCounterService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Atomically reserves the next integer for <paramref name="watchPath"/>
    /// and persists the counter file before returning. Concurrent callers
    /// are serialised so each one observes a distinct value.
    /// </summary>
    public int Issue(string watchPath)
    {
        if (string.IsNullOrWhiteSpace(watchPath))
            throw new ArgumentException("watchPath is required", nameof(watchPath));

        var sem = MutexFor(watchPath);
        sem.Wait();
        try
        {
            var state = Read(watchPath);
            var issued = state.Next;
            var nextState = new CounterState(issued + 1, DateTime.UtcNow);
            Write(watchPath, nextState);
            return issued;
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>
    /// Raises the per-project counter to at least <paramref name="floor"/>
    /// (i.e. the next <see cref="Issue"/> call returns &gt;= <paramref name="floor"/>).
    /// Used by the boot-time migration after stamping existing jobs with
    /// keys so the counter cannot mint a duplicate. No-op when the
    /// in-memory value is already at or above the floor.
    /// </summary>
    public void EnsureAtLeast(string watchPath, int floor)
    {
        if (string.IsNullOrWhiteSpace(watchPath)) return;
        if (floor < 1) return;
        var sem = MutexFor(watchPath);
        sem.Wait();
        try
        {
            var state = Read(watchPath);
            if (state.Next >= floor) return;
            Write(watchPath, new CounterState(floor, DateTime.UtcNow));
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>
    /// Reads the persisted counter without mutating it. Returns
    /// <c>(Next=1, LastIssuedUtc=null)</c> when the file is missing or
    /// unreadable; the service is permissive on read so a corrupt file
    /// cannot wedge job creation.
    /// </summary>
    public CounterState Peek(string watchPath) => Read(watchPath);

    private SemaphoreSlim MutexFor(string watchPath) =>
        _mutexes.GetOrAdd(Normalise(watchPath), _ => new SemaphoreSlim(1, 1));

    private CounterState Read(string watchPath)
    {
        var path = Path.Combine(watchPath, FileName);
        if (!File.Exists(path)) return new CounterState(1, null);
        try
        {
            var json = File.ReadAllText(path);
            var doc = JsonSerializer.Deserialize<CounterStateOnDisk>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var next = doc?.Next ?? 1;
            if (next < 1) next = 1;
            return new CounterState(next, doc?.LastIssuedUtc);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "TaskCounterService: failed to read {Path}; falling back to next=1. " +
                "The file will be rewritten on the next Issue() call.", path);
            return new CounterState(1, null);
        }
    }

    private void Write(string watchPath, CounterState state)
    {
        var path = Path.Combine(watchPath, FileName);
        try
        {
            Directory.CreateDirectory(watchPath);
            var json = JsonSerializer.Serialize(new CounterStateOnDisk
            {
                Next = state.Next,
                LastIssuedUtc = state.LastIssuedUtc
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            // A failure to persist is bad - the next boot will re-issue
            // the same number and produce a duplicate key. Surface the
            // exception so the caller can fail the request rather than
            // silently mint a colliding key.
            _logger.LogError(ex, "TaskCounterService: failed to write {Path}", path);
            throw;
        }
    }

    private static string Normalise(string watchPath)
    {
        var trimmed = watchPath.Replace('\\', '/').TrimEnd('/');
        return trimmed.ToLowerInvariant();
    }

    public readonly record struct CounterState(int Next, DateTime? LastIssuedUtc);

    private sealed class CounterStateOnDisk
    {
        public int Next { get; set; } = 1;
        public DateTime? LastIssuedUtc { get; set; }
    }
}
