namespace AgentRunner;

/// <summary>
/// Keeps capability publication alive across Task Server restarts. The
/// compatibility registry is intentionally process-local in the monolith, so a
/// 409 from advertisement means the daemon must repeat its idempotent PUT
/// registration before advertising the same generation again.
/// </summary>
internal static class CapabilityAdvertisementRecovery
{
    public static async Task ExecuteAsync(
        string operation,
        Func<CancellationToken, Task> advertise,
        Func<CancellationToken, Task> register,
        TaskServerConnectivityMonitor connectivity,
        Func<int> activeSlots,
        int pollSeconds,
        TimeSpan requestTimeout,
        Action<string> log,
        CancellationToken shutdown)
    {
        var consecutiveFaults = 0;
        var registrationLosses = 0;
        while (true)
        {
            shutdown.ThrowIfCancellationRequested();
            try
            {
                using var request = RequestDeadline(shutdown, requestTimeout);
                await advertise(request.Token);
                connectivity.RecordSuccess(DateTime.UtcNow, operation);
                return;
            }
            catch (TaskServerException exception) when (
                exception.StatusCode == 409
                && !shutdown.IsCancellationRequested)
            {
                registrationLosses++;
                log(
                    $"capability-advertisement registration=lost operation={Token(operation)} " +
                    $"recovery=reregister attempt={registrationLosses} error={Token(exception.Message)}");
                await RegisterWithRetryAsync(
                    register,
                    connectivity,
                    activeSlots,
                    pollSeconds,
                    requestTimeout,
                    shutdown);

                // A backend can restart again between the PUT and POST. Avoid a
                // hot 409 loop while preserving indefinite self-recovery.
                if (registrationLosses > 1)
                    await Task.Delay(
                        TaskServerConnectivityMonitor.RetryDelay(pollSeconds, registrationLosses),
                        shutdown);
            }
            catch (Exception exception) when (
                RemoteRunnerDaemon.IsTransientServerFault(exception)
                && !shutdown.IsCancellationRequested)
            {
                var delay = TaskServerConnectivityMonitor.RetryDelay(
                    pollSeconds,
                    ++consecutiveFaults);
                connectivity.RecordFailure(
                    DateTime.UtcNow,
                    operation,
                    exception,
                    delay,
                    activeSlots());
                await Task.Delay(delay, shutdown);
            }
        }
    }

    private static async Task RegisterWithRetryAsync(
        Func<CancellationToken, Task> register,
        TaskServerConnectivityMonitor connectivity,
        Func<int> activeSlots,
        int pollSeconds,
        TimeSpan requestTimeout,
        CancellationToken shutdown)
    {
        for (var attempt = 1; ; attempt++)
        {
            shutdown.ThrowIfCancellationRequested();
            try
            {
                using var request = RequestDeadline(shutdown, requestTimeout);
                await register(request.Token);
                connectivity.RecordSuccess(DateTime.UtcNow, "capability re-registration");
                return;
            }
            catch (Exception exception) when (
                RemoteRunnerDaemon.IsTransientServerFault(exception)
                && !shutdown.IsCancellationRequested)
            {
                var delay = TaskServerConnectivityMonitor.RetryDelay(pollSeconds, attempt);
                connectivity.RecordFailure(
                    DateTime.UtcNow,
                    "capability re-registration",
                    exception,
                    delay,
                    activeSlots());
                await Task.Delay(delay, shutdown);
            }
        }
    }

    private static CancellationTokenSource RequestDeadline(
        CancellationToken shutdown,
        TimeSpan requestTimeout)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        deadline.CancelAfter(requestTimeout);
        return deadline;
    }

    private static string Token(string value)
        => string.Concat(value.Select(character => char.IsWhiteSpace(character) ? '_' : character));
}
