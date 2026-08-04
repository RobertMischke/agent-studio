namespace AgentRunner;

/// <summary>
/// Independent liveness guard for a daemon that has no work to protect. A
/// blocked startup call or claim-loop operation cannot service an in-loop
/// timer, so this guard owns a separate timer and cancellation source.
/// </summary>
internal sealed class DaemonIdleWatchdog : IAsyncDisposable
{
    private readonly Action<string> _log;
    private readonly TimeSpan _stallAfter;
    private readonly TimeSpan _checkEvery;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _abort = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _watchTask;
    private long _lastPollTimestamp;
    private int _activeSlots;
    private int _tripped;

    public DaemonIdleWatchdog(
        Action<string> log,
        TimeSpan stallAfter,
        TimeProvider? timeProvider = null,
        TimeSpan? checkEvery = null)
    {
        if (stallAfter <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(stallAfter));

        _log = log;
        _stallAfter = stallAfter;
        _checkEvery = checkEvery ?? TimeSpan.FromSeconds(Math.Clamp(stallAfter.TotalSeconds / 6, 1, 30));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lastPollTimestamp = _timeProvider.GetTimestamp();
        _watchTask = WatchAsync();
    }

    public CancellationToken AbortToken => _abort.Token;

    public bool Tripped => Volatile.Read(ref _tripped) != 0;

    public void RecordPollStarted()
        => Interlocked.Exchange(ref _lastPollTimestamp, _timeProvider.GetTimestamp());

    public void RecordActiveSlots(int activeSlots)
        => Volatile.Write(ref _activeSlots, Math.Max(0, activeSlots));

    private async Task WatchAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await Task.Delay(_checkEvery, _timeProvider, _lifetime.Token);
                if (Volatile.Read(ref _activeSlots) != 0)
                    continue;

                var lastPoll = Interlocked.Read(ref _lastPollTimestamp);
                var stalledFor = _timeProvider.GetElapsedTime(lastPoll, _timeProvider.GetTimestamp());
                if (stalledFor < _stallAfter
                    || Interlocked.Exchange(ref _tripped, 1) != 0)
                    continue;

                _log(
                    "daemon-idle-watchdog status=fatal " +
                    $"noPollSeconds={Math.Max(0, stalledFor.TotalSeconds):0} activeSlots=0 " +
                    "action=exit-for-service-restart");
                _abort.Cancel();
                return;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try { await _watchTask; }
        catch (OperationCanceledException) { }
        _abort.Dispose();
        _lifetime.Dispose();
    }
}
