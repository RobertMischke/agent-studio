namespace AgentRunner;

/// <summary>
/// Best-effort delivery of capability-failure diagnostics.
///
/// <para>
/// A capability failure report is telemetry about work that has <b>already</b>
/// failed. Its delivery must never decide the fate of the real work: the fenced
/// review report, the run classification, and daemon startup all continue when
/// the report cannot be delivered. Servers that do not mount the capability
/// plane answer 404, an older server may answer 409 - both are diagnostic gaps,
/// never run failures, so they are logged and swallowed here rather than thrown
/// out of the caller.
/// </para>
/// </summary>
internal static class CapabilityFailureReporter
{
    /// <summary>
    /// Reports one capability failure. Returns whether the server accepted it;
    /// a real shutdown still propagates as <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async Task<bool> TryReportAsync(
        TaskServerClient client,
        Action<string> log,
        string capabilityKey,
        string classification,
        string reason,
        string idempotencyKey,
        string? claimKind,
        string? claimId,
        long? fence,
        CancellationToken ct)
    {
        try
        {
            await client.ReportCapabilityFailureAsync(
                capabilityKey,
                classification,
                reason,
                idempotencyKey,
                claimKind,
                claimId,
                fence,
                ct);
            return true;
        }
        // Only a real shutdown propagates. An HttpClient timeout also surfaces as
        // a TaskCanceledException even though nothing asked this runner to stop -
        // filtering on the exception type alone let exactly that crash through the
        // best-effort reporter it was built to be. The caller's token is the only
        // authority on "we are shutting down".
        catch (Exception exception)
            when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log($"capability-failure report deferred capability={capabilityKey} "
                + $"classification={classification}: {exception.Message}");
            return false;
        }
    }
}
