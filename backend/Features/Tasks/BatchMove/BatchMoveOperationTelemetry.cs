namespace AgentStudio.Tasks;

/// <summary>
/// Async-local measurements for one background batch. Task-index invalidation,
/// cache refresh and lane-lock code live below the job coordinator, so this
/// scope records their contribution without counting unrelated application
/// traffic that overlaps the batch.
/// </summary>
internal static class BatchMoveOperationTelemetry
{
    private static readonly AsyncLocal<Scope?> Current = new();

    public static IDisposable Begin()
    {
        var scope = new Scope(Current.Value);
        Current.Value = scope;
        return scope;
    }

    public static void RecordScannerInvalidation() => Current.Value?.RecordScannerInvalidation();
    public static void RecordScannerRefresh(TimeSpan elapsed) => Current.Value?.RecordScannerRefresh(elapsed);
    public static void RecordLaneLockWait(TimeSpan elapsed) => Current.Value?.RecordLaneLockWait(elapsed);
    public static void RecordLaneLockHeld(TimeSpan elapsed) => Current.Value?.RecordLaneLockHeld(elapsed);

    public static BatchMoveOperationTally? CurrentTally() => Current.Value?.Snapshot();

    private sealed class Scope(Scope? parent) : IDisposable
    {
        private readonly object _gate = new();
        private readonly Scope? _parent = parent;
        private int _laneLockAcquisitions;
        private long _laneLockWaitTicks;
        private long _laneLockHeldTicks;
        private int _scannerInvalidations;
        private int _scannerRefreshes;
        private long _scannerRefreshTicks;

        public void RecordScannerInvalidation()
        {
            lock (_gate) _scannerInvalidations++;
        }

        public void RecordScannerRefresh(TimeSpan elapsed)
        {
            lock (_gate)
            {
                _scannerRefreshes++;
                _scannerRefreshTicks += elapsed.Ticks;
            }
        }

        public void RecordLaneLockWait(TimeSpan elapsed)
        {
            lock (_gate)
            {
                _laneLockAcquisitions++;
                _laneLockWaitTicks += elapsed.Ticks;
            }
        }

        public void RecordLaneLockHeld(TimeSpan elapsed)
        {
            lock (_gate) _laneLockHeldTicks += elapsed.Ticks;
        }

        public BatchMoveOperationTally Snapshot()
        {
            lock (_gate)
            {
                return new BatchMoveOperationTally(
                    _laneLockAcquisitions,
                    TimeSpan.FromTicks(_laneLockWaitTicks).TotalMilliseconds,
                    TimeSpan.FromTicks(_laneLockHeldTicks).TotalMilliseconds,
                    _scannerInvalidations,
                    _scannerRefreshes,
                    TimeSpan.FromTicks(_scannerRefreshTicks).TotalMilliseconds);
            }
        }

        public void Dispose()
        {
            if (ReferenceEquals(Current.Value, this)) Current.Value = _parent;
        }
    }
}

internal sealed record BatchMoveOperationTally(
    int LaneLockAcquisitions,
    double LaneLockWaitMs,
    double LaneLockHeldMs,
    int ScannerInvalidations,
    int ScannerRefreshes,
    double ScannerRefreshMs);
