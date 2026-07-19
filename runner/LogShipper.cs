using System.Collections.Concurrent;

namespace AgentRunner;

/// <summary>
/// Buffers consolidated CLI output lines and ships them to the server's
/// log-ingestion endpoint in batches. During a live run the server's own live
/// view is read locally on the runner host; this ingestion is what makes the
/// output durable and visible on the local board after the fact. Failures to
/// ship never abort the run - the console still shows the output.
/// </summary>
public sealed class LogShipper
{
    private readonly TaskServerClient _client;
    private readonly string _taskKey;
    private readonly Action<string> _diag;
    private readonly ConcurrentQueue<CliOutputLine> _pending = new();

    public LogShipper(TaskServerClient client, string taskKey, Action<string> diag)
    {
        _client = client;
        _taskKey = taskKey;
        _diag = diag;
    }

    public void Add(string stream, string text)
        => _pending.Enqueue(new CliOutputLine(DateTime.UtcNow, stream, text));

    /// <summary>Drain the buffer and post it. Safe to call repeatedly; a no-op when empty.</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        var batch = new List<CliOutputLine>();
        while (_pending.TryDequeue(out var line)) batch.Add(line);
        if (batch.Count == 0) return;

        try
        {
            await _client.IngestLogsAsync(new LogIngestRequest(_taskKey, batch), ct);
        }
        catch (Exception ex)
        {
            // Re-queue so the next flush retries; the run must not fail because a
            // log batch could not be shipped.
            foreach (var line in batch) _pending.Enqueue(line);
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
