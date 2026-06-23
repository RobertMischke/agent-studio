using Serilog;

namespace AgentStudio.Diagnostics;

/// <summary>
/// TEMPORARY forensic instrument (bug #2: orchestrator-spawned claude/codex dies
/// exit=-1 mid-run with StopReason=None and no "Reaped" log). exit=-1 is the
/// TerminateProcess(-1) signature of a .NET <c>Process.Kill()</c>, so SOME kill
/// site is hitting the run's process without going through Stop()/the reaper.
/// Every <c>Process.Kill()</c> call site routes its target PID + a stack trace
/// through here so the death can be matched to its caller by PID. Writes both to
/// Serilog (Warning) and to a flat file for trivial grepping. Remove once the
/// killer is identified (companion to the [crash-diag] log line).
/// </summary>
public static class CliKillAudit
{
    private static readonly string LogPath =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "atp-kill-audit.log");

    private static int SafePid(System.Diagnostics.Process? p)
    {
        try { return p?.Id ?? -1; } catch { return -1; }
    }

    public static void Trace(System.Diagnostics.Process? target, string site)
    {
        Trace(SafePid(target), site);
    }

    public static void Trace(int targetPid, string site)
    {
        try
        {
            var stamp = DateTime.UtcNow.ToString("HH:mm:ss.fff");
            Log.Warning("[KILL-AUDIT] site={Site} targetPid={Pid} t={Stamp}", site, targetPid, stamp);
            var line = $"[KILL-AUDIT] {stamp} site={site} targetPid={targetPid}\n{Environment.StackTrace}\n";
            System.IO.File.AppendAllText(LogPath, line);
        }
        catch (Exception ex) { SilentCatch.Note(ex, "CliKillAudit (forensic best-effort; never throw from a kill path)"); }
    }
}
