using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Cli;

/// <summary>
/// Contains a task-run's CLI process and <b>every</b> process it later spawns
/// — including ones that break away from the parent→child PID chain (the
/// agent's Playwright capture server, a detached <c>node serve.cjs</c>, an
/// <c>ng serve</c>) — and reaps the whole group at run-end.
///
/// <para>
/// Implemented on top of a Win32 <i>Job Object</i> (the OS primitive whose
/// API names — <c>CreateJobObject</c> / <c>AssignProcessToJobObject</c> —
/// the P/Invoke layer below keeps verbatim). The wrapper is named after the
/// task-run it scopes, NOT the OS primitive, so it does not collide with the
/// domain's <c>Job</c>→<c>Task</c> naming.
/// </para>
/// <para>
/// <b>Why this exists.</b> The runner's tree-kill
/// (<c>Process.Kill(entireProcessTree)</c> / <c>taskkill /T</c>) walks the
/// live parent→child chain. A grandchild that re-parents or daemonises is no
/// longer in that chain, so tree-kill misses it and it keeps running —
/// holding the run's worktree directory open. The post-run
/// <c>git worktree remove</c> then fails "Device or resource busy", the
/// worktree directory is orphaned, and every later re-pick collides with it
/// (<c>git worktree add</c> on an existing dir) → <c>pick-reverted-no-run</c>
/// loop (AGT-1791). The observed leaker is the agent's Playwright capture
/// server, which outlives the CLI run by design.
/// </para>
/// <para>
/// Job-object membership is inherited by everything a member spawns and is
/// NOT severed by detaching, so <see cref="Terminate"/> at run-end reaps the
/// whole subtree regardless of re-parenting. The group is created with
/// <c>KILL_ON_JOB_CLOSE</c> so even if <see cref="Terminate"/> is never
/// reached, disposing the handle still kills any stragglers.
/// </para>
/// <para>
/// <b>Best-effort, Windows-only, zero-regression.</b>
/// <see cref="CreateForProcess"/> returns <c>null</c> on non-Windows or if the
/// OS refuses the assignment (already exited / access denied / nested-job
/// limit), and the caller keeps its existing tree-kill behaviour. The group
/// only ever contains the run's own process subtree, so terminating it can
/// never touch the backend or another run.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class TaskProcessReaper : IDisposable
{
    private readonly object _gate = new();
    private IntPtr _handle;
    private bool _terminated;

    private TaskProcessReaper(IntPtr handle) => _handle = handle;

    /// <summary>
    /// Create a kill-on-close process group and assign <paramref name="process"/>
    /// to it. Returns null (and leaves the caller on its tree-kill fallback) on
    /// non-Windows or any failure — assignment must happen right after spawn,
    /// before the CLI has a chance to spawn the helpers we want to contain.
    /// </summary>
    public static TaskProcessReaper? CreateForProcess(Process process, ILogger? logger = null)
    {
        if (!OperatingSystem.IsWindows()) return null;

        IntPtr job = IntPtr.Zero;
        try
        {
            job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                logger?.LogDebug("TaskProcessReaper: CreateJobObject failed (Win32 {Err}); falling back to tree-kill", Marshal.GetLastWin32Error());
                return null;
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                }
            };
            var len = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var infoPtr = Marshal.AllocHGlobal(len);
            try
            {
                Marshal.StructureToPtr(info, infoPtr, fDeleteOld: false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, infoPtr, (uint)len))
                {
                    logger?.LogDebug("TaskProcessReaper: SetInformationJobObject failed (Win32 {Err}); falling back to tree-kill", Marshal.GetLastWin32Error());
                    CloseHandle(job);
                    return null;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(infoPtr);
            }

            if (!AssignProcessToJobObject(job, process.Handle))
            {
                // Process already exited, access denied, or an OS nested-job
                // limit — nothing to contain or nothing we can do. Drop the
                // group; the caller's tree-kill stays in force.
                logger?.LogDebug("TaskProcessReaper: AssignProcessToJobObject failed (Win32 {Err}); falling back to tree-kill", Marshal.GetLastWin32Error());
                CloseHandle(job);
                return null;
            }

            return new TaskProcessReaper(job);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "TaskProcessReaper.CreateForProcess: best-effort, falling back to tree-kill");
            if (job != IntPtr.Zero)
            {
                try { CloseHandle(job); } catch (Exception __ex) { SilentCatch.Note(__ex, "TaskProcessReaper: handle close best-effort"); }
            }
            return null;
        }
    }

    /// <summary>
    /// Kill every process in the group <b>now</b> — the direct CLI child and all
    /// descendants, including detached ones tree-kill would miss. Idempotent.
    /// </summary>
    public void Terminate()
    {
        lock (_gate)
        {
            if (_terminated || _handle == IntPtr.Zero) return;
            try { TerminateJobObject(_handle, 1); }
            catch (Exception __ex) { SilentCatch.Note(__ex, "TaskProcessReaper.Terminate: best-effort"); }
            finally { _terminated = true; }
        }
    }

    /// <summary>
    /// Close the group handle. With <c>KILL_ON_JOB_CLOSE</c> this also kills any
    /// member still alive that <see cref="Terminate"/> did not already reap.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero) return;
            try { CloseHandle(_handle); }
            catch (Exception __ex) { SilentCatch.Note(__ex, "TaskProcessReaper.Dispose: best-effort"); }
            finally { _handle = IntPtr.Zero; }
        }
    }

    // ── Win32 P/Invoke surface (OS API names kept verbatim) ─────────────

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInformationClass, IntPtr lpJobObjectInformation, uint cbJobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
