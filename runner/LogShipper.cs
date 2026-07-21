using System.Collections.Concurrent;

namespace AgentRunner;

/// <summary>
/// Buffers consolidated CLI output lines and ships them to the server's
/// log-ingestion endpoint in batches. During a live run the server's own live
/// view is read locally on the runner host; this ingestion is what makes the
/// output durable and visible on the local board after the fact. Failures to
/// ship never abort the run - the console still shows the output.
///
/// <para>
/// The pending queue is bounded. A task server outage is exactly when this
/// buffer is at risk of unbounded growth (every failed flush re-queues its
/// batch while new output keeps arriving), and the daemon lives for days, so a
/// hard cap on retained lines is what keeps a backlog from turning a transient
/// outage into a memory blow-up. When the cap is exceeded the oldest lines are
/// dropped and counted; the live console output already carried them.
/// </para>
/// </summary>
public sealed class LogShipper
{
    // A backlog beyond this many lines is dropped oldest-first. At the 5 s flush
    // cadence this is minutes of very chatty output; the durable copy is best
    // effort, so bounding memory wins over retaining an unbounded backlog.
    private const int MaxPendingLines = 20_000;

    // Cap a single flush so a large backlog drains over several cadences instead
    // of building one giant request (and, on the v1 path, one giant burst of
    // per-line POSTs) that would hold the whole batch live at once.
    private const int MaxBatchLines = 2_000;

    private readonly TaskServerClient _client;
    private readonly string _taskKey;
    private readonly RunLeaseInfoDto _lease;
    private readonly Action<string> _diag;
    private readonly ConcurrentQueue<CliOutputLine> _pending = new();
    private int _pendingCount;
    private long _dropped;
    private long _reportedDropped;

    public LogShipper(TaskServerClient client, string taskKey, RunLeaseInfoDto lease, Action<string> diag)
    {
        _client = client;
        _taskKey = taskKey;
        _lease = lease;
        _diag = diag;
    }

    /// <summary>Approximate number of lines currently buffered (test/diagnostic seam).</summary>
    internal int PendingCount => Volatile.Read(ref _pendingCount);

    /// <summary>Total lines dropped to honour the cap (test/diagnostic seam).</summary>
    internal long DroppedCount => Volatile.Read(ref _dropped);

    public void Add(string stream, string text)
    {
        _pending.Enqueue(new CliOutputLine(DateTime.UtcNow, stream, text));
        if (Interlocked.Increment(ref _pendingCount) > MaxPendingLines)
            TrimToCap();
    }

    private void TrimToCap()
    {
        while (Volatile.Read(ref _pendingCount) > MaxPendingLines && _pending.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _pendingCount);
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>Drain the buffer and post it. Safe to call repeatedly; a no-op when empty.</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        var batch = new List<CliOutputLine>();
        while (batch.Count < MaxBatchLines && _pending.TryDequeue(out var line))
        {
            Interlocked.Decrement(ref _pendingCount);
            batch.Add(line);
        }
        if (batch.Count == 0) return;

        try
        {
            await _client.IngestLogsAsync(new LogIngestRequest(
                _taskKey, batch, _lease.RunnerId, _lease.LeaseId, _lease.FencingToken), ct);

            var dropped = Volatile.Read(ref _dropped);
            var reported = Volatile.Read(ref _reportedDropped);
            if (dropped > reported)
            {
                _diag($"log buffer capped: dropped {dropped - reported} backlogged line(s) to bound memory");
                Volatile.Write(ref _reportedDropped, dropped);
            }
        }
        catch (Exception ex)
        {
            // Re-queue so the next flush retries; the run must not fail because a
            // log batch could not be shipped. Re-queueing counts against the cap,
            // so a persistent outage sheds the oldest backlog instead of growing.
            foreach (var line in batch)
            {
                _pending.Enqueue(line);
                Interlocked.Increment(ref _pendingCount);
            }
            TrimToCap();
            _diag($"log ingest failed, will retry: {ex.Message}");
        }
    }

    /// <summary>Flush on a fixed cadence until cancelled, then flush once more.</summary>
    public async Task RunAsync(TimeSpan interval, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct);
                await FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        await FlushAsync(CancellationToken.None);
    }
}
