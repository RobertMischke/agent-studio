namespace AgentStudio.TestSupport;

/// <summary>
/// Single place that answers "which POSIX shell can this test host actually
/// execute, and how do I hand it a path?".
///
/// Several suites drive real shell scripts (the agent-host resource policy, the
/// faked review commands, the faked CLI behind the durable worker). Hard-coding
/// <c>/bin/sh</c> makes those suites permanently red on the Windows development
/// baseline even though Git for Windows ships a fully capable bash. The tests
/// are not Linux-bound - only the literal interpreter path was.
///
/// Two things are needed to run such a script from .NET on Windows:
///
///   1. a real interpreter path, because <c>ProcessStartInfo.FileName</c> does
///      not consult the MSYS root and Git's bash is frequently absent from the
///      PATH of the process that hosts the test run;
///   2. MSYS-style arguments, because a script that validates
///      <c>[[ "$p" == /* ]]</c> rejects <c>C:\Users\...</c>. .NET passes
///      arguments verbatim, so no automatic conversion happens.
///
/// Genuinely Linux-bound behaviour (<c>/proc</c>, systemd, cgroups, Unix file
/// modes) does not belong here - mark it with <see cref="PlatformGate"/>.
/// </summary>
public static class PosixShell
{
    private static readonly Lazy<string?> Resolved = new(Resolve);

    /// <summary>
    /// Full path of a usable POSIX shell, or <c>null</c> when the host has none.
    /// </summary>
    public static string? Path => Resolved.Value;

    public static bool IsAvailable => Resolved.Value is not null;

    /// <summary>
    /// Full path of a usable POSIX shell. Throws with an actionable message when
    /// the host has none, so a misconfigured machine fails loudly instead of
    /// silently degrading into a confusing assertion error.
    /// </summary>
    public static string RequirePath()
        => Resolved.Value ?? throw new InvalidOperationException(
            "No POSIX shell found. Install Git for Windows (which ships bash) or "
            + "put bash on PATH. See docs/operations/testing-on-windows.md.");

    /// <summary>
    /// Translate a path into the form the resolved shell understands.
    ///
    /// On Linux/macOS this is the identity. On Windows a rooted DOS path is
    /// converted to its MSYS equivalent (<c>C:\Users\x</c> -&gt; <c>/c/Users/x</c>),
    /// which Git bash resolves back to the very same file - so the .NET side of a
    /// test can keep using the Windows path for its own assertions.
    /// </summary>
    public static string ToShellPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (!OperatingSystem.IsWindows()) return path;

        var normalized = path.Replace('\\', '/');

        // UNC paths map onto the MSYS //server/share form.
        if (normalized.StartsWith("//", StringComparison.Ordinal)) return normalized;

        // Drive-rooted: C:/Users/x -> /c/Users/x
        if (normalized.Length >= 2 && normalized[1] == ':' && char.IsLetter(normalized[0]))
        {
            var rest = normalized.Length > 2 ? normalized[2..] : "/";
            if (!rest.StartsWith('/')) rest = "/" + rest;
            return "/" + char.ToLowerInvariant(normalized[0]) + rest;
        }

        return normalized;
    }

    private static string? Resolve()
    {
        if (!OperatingSystem.IsWindows())
            return File.Exists("/bin/sh") ? "/bin/sh" : FindOnPath("sh") ?? FindOnPath("bash");

        // Prefer an explicitly configured shell so an unusual install still works.
        var configured = Environment.GetEnvironmentVariable("AGENT_STUDIO_TEST_SHELL");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        return FindOnPath("bash") ?? FindWindowsGitBash();
    }

    private static string? FindOnPath(string exe) => Executables.FindOnPath(exe);

    private static string? FindWindowsGitBash()
    {
        if (!OperatingSystem.IsWindows()) return null;

        var roots = new List<string>();
        foreach (var variable in new[] { "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "LOCALAPPDATA" })
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value)) roots.Add(value);
        }
        roots.Add(@"C:\Program Files");
        roots.Add(@"C:\Program Files (x86)");

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // bin\bash.exe is the launcher, usr\bin\bash.exe the real interpreter;
            // both accept a script plus MSYS-style arguments.
            foreach (var relative in new[] { @"Git\bin\bash.exe", @"Git\usr\bin\bash.exe" })
            {
                try
                {
                    var candidate = System.IO.Path.Combine(root, relative);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                    // Ignore an unusable root and keep probing the remaining ones.
                }
            }
        }
        return null;
    }
}
