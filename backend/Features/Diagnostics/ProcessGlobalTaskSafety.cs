namespace AgentStudio.Diagnostics;

/// <summary>
/// Process-global safety net that marks every
/// <see cref="TaskScheduler.UnobservedTaskException"/> as observed so the
/// finalizer thread can never rethrow it.
///
/// <para>
/// A fire-and-forget task whose exception is never awaited raises this event
/// when the GC finalizes the faulted <see cref="Task"/>. When the runtime is
/// configured to throw unobserved task exceptions - dev and CI hosts commonly
/// set <c>DOTNET_ThrowUnobservedTaskExceptions=1</c> to surface fire-and-forget
/// bugs - the finalizer <b>rethrows</b> the exception, which is an uncatchable,
/// fatal host death. That is never an acceptable outcome for the backend: a
/// stray fire-and-forget exception must be <i>surfaced</i> (the per-run recorder
/// in <c>Program.cs</c> logs it and writes a crash marker), not allowed to kill
/// the process.
/// </para>
///
/// <para>
/// This is deliberately separate from that per-run recording handler. Under a
/// test host, <c>WebApplicationFactory&lt;Program&gt;</c> detaches the per-run
/// handlers when each host stops (so per-boot recorders bound to since-deleted
/// temp directories don't accumulate), which opens a window with no subscriber
/// to call <see cref="UnobservedTaskExceptionEventArgs.SetObserved"/>. A task
/// that finalizes in that window - the crash-surface test's
/// <c>GC.WaitForPendingFinalizers()</c> makes it happen deterministically -
/// would rethrow and abort the whole test run. This handler is registered once
/// and <b>never</b> detached, so it closes that window and the equivalent one in
/// production.
/// </para>
/// </summary>
public static class ProcessGlobalTaskSafety
{
    private static int _installed;

    /// <summary>
    /// Idempotently install the never-detached safety handler. Safe to call on
    /// every host boot; only the first call subscribes.
    /// </summary>
    public static void EnsureUnobservedTaskExceptionsAreObserved()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1) return;
        TaskScheduler.UnobservedTaskException += static (_, e) =>
        {
            // Observing is the whole job. It only flips a flag on the args and
            // does not throw; the guard is pure belt-and-suspenders because this
            // runs on the finalizer thread where an escaping exception is fatal.
            try { e.SetObserved(); }
            catch { return; }
        };
    }
}
