using Serilog;
using Serilog.Events;

namespace OrchestratorApi.Diagnostics;

/// <summary>
/// Project-wide standard for catch blocks that used to swallow their exception
/// silently. Every previously-silent catch now routes through here so the error
/// is never lost, while the log level keeps benign control flow from flooding
/// the console.
///
/// <para>Uses the static Serilog <see cref="Log"/> logger so it works in
/// DI-less / static contexts (path + parser helpers, <c>TryReadEnteredLaneAt</c>,
/// dispose paths) exactly where the silent catches live. The level reflects
/// intent, never the absence of logging:</para>
/// <list type="bullet">
///   <item><see cref="Note"/> - expected, benign control flow (torn/optional
///   file, graceful shutdown, idempotent cleanup). Emitted at <c>Debug</c>:
///   captured if you lower the level, invisible at the default level so it
///   cannot flood.</item>
///   <item><see cref="Warn"/> - an unexpected swallow that should be looked at.
///   Emitted at <c>Warning</c> with the exception.</item>
/// </list>
/// </summary>
public static class SilentCatch
{
    /// <summary>
    /// Records an expected, benign swallowed exception at <c>Debug</c>.
    /// <paramref name="context"/> says what was being attempted.
    /// </summary>
    public static void Note(Exception ex, string context)
    {
        if (Log.IsEnabled(LogEventLevel.Debug))
            Log.ForContext("SilentCatch", context).Debug(ex, "Swallowed (best-effort): {Context}", context);
    }

    /// <summary>
    /// Records an unexpected swallowed exception at <c>Warning</c> with the
    /// exception. <paramref name="context"/> says what was being attempted.
    /// </summary>
    public static void Warn(Exception ex, string context)
    {
        Log.ForContext("SilentCatch", context).Warning(ex, "Swallowed unexpected error: {Context}", context);
    }
}
