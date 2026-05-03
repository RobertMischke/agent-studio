using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OrchestratorApi.Services.Cli.Win;

/// <summary>
/// Win32-native child-process spawn that curates which parent handles
/// are inheritable. The default <see cref="Process"/> API on Windows
/// sets <c>bInheritHandles=TRUE</c> and inherits ALL inheritable
/// handles in the parent's table - including SignalR sockets, file
/// watchers, ConPTY consoles, ETW listeners, and any random library's
/// global state. A Node-based CLI (Claude / Codex / Gemini) inherits
/// those handles and may stat / read them during init, blocking on a
/// handle it has no business owning.
///
/// <para>
/// Survey § R5 / ADR-0014 follow-up: spawn via
/// <c>CreateProcessW</c> + <c>STARTUPINFOEX</c> +
/// <c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c>, passing exactly the three
/// pipe handles the CLI expects (stdin, stdout, stderr) and nothing
/// else. The OSS reference for the same shape is in
/// <c>openai-codex/codex-rs/windows-sandbox-rs/src/proc_thread_attr.rs</c>;
/// the .NET adaptation lives here.
/// </para>
/// <para>
/// Windows-only by attribute. Callers should fall through to the
/// standard <see cref="Process.Start()"/> path on non-Windows.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsHandleScrubSpawner
{
    /// <summary>
    /// Result of a curated spawn. Stdin / Stdout / Stderr are owned by
    /// the caller and must be disposed; <see cref="Process"/> wraps the
    /// child by PID for the runner's existing watchdog / kill flow.
    /// </summary>
    internal sealed record Result(
        Process Process,
        FileStream? Stdin,
        FileStream Stdout,
        FileStream Stderr,
        Action KillTree);

    /// <summary>
    /// Spawn <paramref name="exePath"/> with <paramref name="argList"/> as
    /// argv (escaped per CommandLineToArgvW), <paramref name="cwd"/> as
    /// the working directory, and <paramref name="envBlock"/> as the
    /// child's environment. Only the child-side ends of the stdout +
    /// stderr (and optional stdin) pipes are marked inheritable; nothing
    /// else from the parent leaks in.
    /// </summary>
    internal static Result Spawn(
        string exePath,
        IReadOnlyList<string> argList,
        string cwd,
        IReadOnlyDictionary<string, string?> envBlock,
        bool wantStdin)
    {
        // 1. Create stdout / stderr / (optional) stdin anonymous pipes.
        //    The PARENT keeps the read ends (stdout/stderr) and the
        //    write end (stdin); the CHILD gets the write ends
        //    (stdout/stderr) and read end (stdin). Only the child-side
        //    handles are flagged inheritable; the parent-side handles
        //    are NOT inheritable so other inadvertent fork-spawns
        //    elsewhere in the process don't leak them.
        var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = 1 };
        IntPtr stdoutRead = IntPtr.Zero, stdoutWrite = IntPtr.Zero;
        IntPtr stderrRead = IntPtr.Zero, stderrWrite = IntPtr.Zero;
        IntPtr stdinRead = IntPtr.Zero, stdinWrite = IntPtr.Zero;
        if (!CreatePipe(out stdoutRead, out stdoutWrite, ref sa, 0)) ThrowLastError("CreatePipe(stdout)");
        if (!SetHandleInformation(stdoutRead, HANDLE_FLAG_INHERIT, 0)) ThrowLastError("SetHandleInformation(stdoutRead)");
        if (!CreatePipe(out stderrRead, out stderrWrite, ref sa, 0)) ThrowLastError("CreatePipe(stderr)");
        if (!SetHandleInformation(stderrRead, HANDLE_FLAG_INHERIT, 0)) ThrowLastError("SetHandleInformation(stderrRead)");
        if (wantStdin)
        {
            if (!CreatePipe(out stdinRead, out stdinWrite, ref sa, 0)) ThrowLastError("CreatePipe(stdin)");
            if (!SetHandleInformation(stdinWrite, HANDLE_FLAG_INHERIT, 0)) ThrowLastError("SetHandleInformation(stdinWrite)");
        }

        // 2. Build the PROC_THREAD_ATTRIBUTE_LIST holding ONLY our pipe
        //    handles. InitializeProcThreadAttributeList is called twice:
        //    once with NULL to size the buffer, once with the buffer.
        IntPtr lpAttributeList = IntPtr.Zero;
        IntPtr handleListPtr = IntPtr.Zero;
        try
        {
            UIntPtr size = UIntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            lpAttributeList = Marshal.AllocHGlobal((int)size);
            if (!InitializeProcThreadAttributeList(lpAttributeList, 1, 0, ref size))
                ThrowLastError("InitializeProcThreadAttributeList");

            // Pack the inheritable handles into a heap buffer.
            var handles = wantStdin
                ? new[] { stdinRead, stdoutWrite, stderrWrite }
                : new[] { stdoutWrite, stderrWrite };
            var bytes = handles.Length * IntPtr.Size;
            handleListPtr = Marshal.AllocHGlobal(bytes);
            for (int i = 0; i < handles.Length; i++)
                Marshal.WriteIntPtr(handleListPtr, i * IntPtr.Size, handles[i]);

            if (!UpdateProcThreadAttribute(
                    lpAttributeList,
                    0,
                    (UIntPtr)PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
                    handleListPtr,
                    (UIntPtr)bytes,
                    IntPtr.Zero,
                    IntPtr.Zero))
                ThrowLastError("UpdateProcThreadAttribute");

            // 3. Build the command line. CreateProcessW wants a single
            //    string parsed by CommandLineToArgvW; we apply standard
            //    Win32 escaping (same rules ProcessStartInfo.ArgumentList
            //    uses).
            var cmdLine = BuildCommandLine(exePath, argList);

            // 4. Build the environment block. Win32 wants a sorted
            //    null-terminated list of VAR=VALUE entries followed by
            //    a final null. The CREATE_UNICODE_ENVIRONMENT flag is
            //    required when we pass UTF-16.
            var envPtr = BuildEnvironmentBlock(envBlock);

            // 5. STARTUPINFOEX wraps the regular STARTUPINFO and adds
            //    the attribute list. STARTF_USESTDHANDLES makes
            //    CreateProcess use our hStdInput/Output/Error.
            var siEx = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO
                {
                    cb = (uint)Marshal.SizeOf<STARTUPINFOEX>(),
                    dwFlags = STARTF_USESTDHANDLES,
                    hStdInput = wantStdin ? stdinRead : IntPtr.Zero,
                    hStdOutput = stdoutWrite,
                    hStdError = stderrWrite
                },
                lpAttributeList = lpAttributeList
            };

            // 6. CreateProcessW. EXTENDED_STARTUPINFO_PRESENT is the
            //    flag that tells CreateProcess to honour the attribute
            //    list. bInheritHandles=TRUE is REQUIRED for the
            //    handle-list attribute to take effect (Win32 docs).
            //    Without the attribute list, that would inherit
            //    everything; with the attribute list, it only inherits
            //    the handles we listed.
            var pi = new PROCESS_INFORMATION();
            const uint creationFlags = CREATE_UNICODE_ENVIRONMENT | EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW;
            if (!CreateProcessW(
                    null,
                    cmdLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    bInheritHandles: true,
                    creationFlags,
                    envPtr,
                    cwd,
                    ref siEx,
                    out pi))
                ThrowLastError($"CreateProcessW({exePath})");

            // We don't need the thread handle.
            CloseHandle(pi.hThread);

            // Close the child-side pipe ends in the parent so EOF
            // propagates correctly when the child closes its end.
            CloseHandle(stdoutWrite);
            CloseHandle(stderrWrite);
            if (wantStdin) CloseHandle(stdinRead);

            // Wrap the parent-side pipe ends as FileStreams.
            var stdoutStream = new FileStream(new SafeFileHandle(stdoutRead, ownsHandle: true), FileAccess.Read);
            var stderrStream = new FileStream(new SafeFileHandle(stderrRead, ownsHandle: true), FileAccess.Read);
            FileStream? stdinStream = wantStdin
                ? new FileStream(new SafeFileHandle(stdinWrite, ownsHandle: true), FileAccess.Write)
                : null;

            // Wrap the process by PID. Process.GetProcessById gives us a
            // managed Process for HasExited / WaitForExitAsync; the raw
            // handle stays with our PROCESS_INFORMATION until we close
            // it via Kill.
            var managed = Process.GetProcessById((int)pi.dwProcessId);
            var rawHandle = pi.hProcess;
            var rawPid = (int)pi.dwProcessId;
            Action killTree = () =>
            {
                try { TerminateProcessTree(rawPid); }
                catch { /* best-effort */ }
                finally { CloseHandle(rawHandle); }
            };

            return new Result(managed, stdinStream, stdoutStream, stderrStream, killTree);
        }
        finally
        {
            if (lpAttributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(lpAttributeList);
                Marshal.FreeHGlobal(lpAttributeList);
            }
            if (handleListPtr != IntPtr.Zero) Marshal.FreeHGlobal(handleListPtr);
        }
    }

    /// <summary>Best-effort kill of the spawned process and its children via taskkill /T /F.</summary>
    private static void TerminateProcessTree(int pid)
    {
        try
        {
            using var killer = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = $"/PID {pid} /T /F",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            killer?.WaitForExit(2000);
        }
        catch { /* swallow */ }
    }

    /// <summary>Build a command line string with Win32 argv quoting rules (same as ProcessStartInfo.ArgumentList).</summary>
    internal static string BuildCommandLine(string exe, IReadOnlyList<string> args)
    {
        var sb = new StringBuilder();
        AppendArg(sb, exe);
        foreach (var a in args)
        {
            sb.Append(' ');
            AppendArg(sb, a);
        }
        return sb.ToString();
    }

    /// <summary>Apply Microsoft's documented argv-quoting algorithm. Public for testing.</summary>
    internal static void AppendArg(StringBuilder sb, string arg)
    {
        // From Raymond Chen's "Everyone quotes command line arguments the wrong way".
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
        {
            sb.Append(arg);
            return;
        }
        sb.Append('"');
        for (int i = 0; ; i++)
        {
            int backslashes = 0;
            while (i < arg.Length && arg[i] == '\\') { backslashes++; i++; }
            if (i == arg.Length)
            {
                sb.Append('\\', backslashes * 2);
                break;
            }
            if (arg[i] == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
            }
            else
            {
                sb.Append('\\', backslashes);
                sb.Append(arg[i]);
            }
        }
        sb.Append('"');
    }

    /// <summary>Convert a dictionary to a Win32 Unicode environment block (sorted, null-terminated, double-null-terminated).</summary>
    private static IntPtr BuildEnvironmentBlock(IReadOnlyDictionary<string, string?> env)
    {
        // CreateProcess wants entries sorted by key (case-insensitive on Windows)
        // followed by an empty string. Each entry is "KEY=VALUE\0", terminated
        // by an additional "\0" at the end.
        var sb = new StringBuilder();
        foreach (var kv in env.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(kv.Key)) continue;
            sb.Append(kv.Key).Append('=').Append(kv.Value ?? string.Empty).Append('\0');
        }
        sb.Append('\0');
        var bytes = Encoding.Unicode.GetBytes(sb.ToString());
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return ptr;
        // Note: we do NOT free this; CreateProcess copies it. (Per docs.)
    }

    private static void ThrowLastError(string what)
    {
        var err = Marshal.GetLastWin32Error();
        throw new System.ComponentModel.Win32Exception(err, $"{what} failed: Win32 error {err}");
    }

    // ── Win32 P/Invoke surface ──────────────────────────────────────────

    private const int HANDLE_FLAG_INHERIT = 0x00000001;
    private const uint STARTF_USESTDHANDLES = 0x00000100;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const ulong PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x00020002;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public uint cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow, cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, uint dwFlags, ref UIntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, UIntPtr Attribute, IntPtr lpValue, UIntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
