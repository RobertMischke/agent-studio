namespace AgentStudio.Shared;

/// <summary>
/// Resolves the <c>bash</c> executable behind every local <c>bash -lc</c>
/// spawn (build-profile dry-runs, build/test gates, dev-tools scripts).
///
/// <para>On Linux/macOS this is plain <c>bash</c> from PATH. On Windows the
/// shell contract is Git Bash, but a Git for Windows install only puts
/// <c>Git\cmd</c> (git.exe) on PATH - <c>bash.exe</c> lives in <c>Git\bin</c>
/// and is not resolvable by name, so a bare <c>Process.Start("bash")</c>
/// fails with "The system cannot find the file specified" and every declared
/// build command reads as a gate failure. Git Bash is therefore located from
/// the <c>git</c> that IS on PATH (<c>&lt;root&gt;\cmd\git.exe</c> ->
/// <c>&lt;root&gt;\bin\bash.exe</c>), then from the standard install
/// locations, and only then falls back to whatever <c>bash</c> PATH offers
/// (which may be WSL's <c>System32\bash.exe</c> - a different operating
/// system, hence last).</para>
/// </summary>
internal static class BashExecutable
{
    private static readonly Lazy<string> Resolved = new(
        () => Resolve(
            OperatingSystem.IsWindows(),
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("ProgramFiles"),
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            Environment.GetEnvironmentVariable("LocalAppData"),
            File.Exists),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The file name to hand to <see cref="System.Diagnostics.ProcessStartInfo"/>.</summary>
    internal static string Path => Resolved.Value;

    /// <summary>
    /// Pure resolution over the supplied environment so the order of
    /// preference is unit-testable without a real file system.
    /// </summary>
    internal static string Resolve(
        bool isWindows,
        string? pathVariable,
        string? programFiles,
        string? programFilesX86,
        string? localAppData,
        Func<string, bool> fileExists)
    {
        if (!isWindows) return "bash";

        foreach (var candidate in GitBashCandidates(pathVariable, programFiles, programFilesX86, localAppData))
        {
            if (fileExists(candidate)) return candidate;
        }
        return "bash";
    }

    private static IEnumerable<string> GitBashCandidates(
        string? pathVariable,
        string? programFiles,
        string? programFilesX86,
        string? localAppData)
    {
        // 1. The Git installation that owns the git.exe on PATH. Git for
        //    Windows exposes git.exe from <root>\cmd (the default PATH entry),
        //    <root>\bin, <root>\mingw64\bin or <root>\usr\bin; bash.exe sits
        //    in <root>\bin and <root>\usr\bin.
        // Windows layouts are parsed with Windows semantics regardless of the
        // host OS: the build/test gate runs on the Linux runner but resolves a
        // Windows Git install when isWindows is true, so System.IO.Path (whose
        // separator follows the HOST) must not touch these paths - a backslash
        // is a separator here by contract.
        foreach (var entry in (pathVariable ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = entry.Trim().TrimEnd('\\', '/');
            if (dir.Length == 0) continue;
            var leaf = WinLeaf(dir);
            if (!leaf.Equals("cmd", StringComparison.OrdinalIgnoreCase)
                && !leaf.Equals("bin", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var root = WinParent(dir);
            if (string.IsNullOrEmpty(root)) continue;
            var rootLeaf = WinLeaf(root);
            if (rootLeaf.Equals("mingw64", StringComparison.OrdinalIgnoreCase)
                || rootLeaf.Equals("usr", StringComparison.OrdinalIgnoreCase))
            {
                root = WinParent(root);
                if (string.IsNullOrEmpty(root)) continue;
            }
            yield return WinJoin(root, "bin", "bash.exe");
            yield return WinJoin(root, "usr", "bin", "bash.exe");
        }

        // 2. Standard per-machine and per-user install locations.
        foreach (var baseDir in new[] { programFiles, programFilesX86 })
        {
            if (string.IsNullOrWhiteSpace(baseDir)) continue;
            yield return WinJoin(baseDir, "Git", "bin", "bash.exe");
            yield return WinJoin(baseDir, "Git", "usr", "bin", "bash.exe");
        }
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return WinJoin(localAppData, "Programs", "Git", "bin", "bash.exe");
            yield return WinJoin(localAppData, "Programs", "Git", "usr", "bin", "bash.exe");
        }
    }

    // Windows-semantics path helpers: a backslash or forward slash is always a
    // separator here, independent of the host OS this process runs on.
    private static readonly char[] WinSeparators = { '\\', '/' };

    private static string WinLeaf(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var idx = trimmed.LastIndexOfAny(WinSeparators);
        return idx < 0 ? trimmed : trimmed[(idx + 1)..];
    }

    private static string WinParent(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var idx = trimmed.LastIndexOfAny(WinSeparators);
        return idx < 0 ? string.Empty : trimmed[..idx];
    }

    private static string WinJoin(string root, params string[] parts)
        => root.TrimEnd('\\', '/') + "\\" + string.Join("\\", parts);
}
