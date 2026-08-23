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
        foreach (var entry in (pathVariable ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = entry.Trim().TrimEnd('\\', '/');
            if (dir.Length == 0) continue;
            var leaf = System.IO.Path.GetFileName(dir);
            if (!leaf.Equals("cmd", StringComparison.OrdinalIgnoreCase)
                && !leaf.Equals("bin", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var root = System.IO.Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(root)) continue;
            var rootLeaf = System.IO.Path.GetFileName(root);
            if (rootLeaf.Equals("mingw64", StringComparison.OrdinalIgnoreCase)
                || rootLeaf.Equals("usr", StringComparison.OrdinalIgnoreCase))
            {
                root = System.IO.Path.GetDirectoryName(root);
                if (string.IsNullOrEmpty(root)) continue;
            }
            yield return System.IO.Path.Combine(root, "bin", "bash.exe");
            yield return System.IO.Path.Combine(root, "usr", "bin", "bash.exe");
        }

        // 2. Standard per-machine and per-user install locations.
        foreach (var baseDir in new[] { programFiles, programFilesX86 })
        {
            if (string.IsNullOrWhiteSpace(baseDir)) continue;
            yield return System.IO.Path.Combine(baseDir, "Git", "bin", "bash.exe");
            yield return System.IO.Path.Combine(baseDir, "Git", "usr", "bin", "bash.exe");
        }
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return System.IO.Path.Combine(localAppData, "Programs", "Git", "bin", "bash.exe");
            yield return System.IO.Path.Combine(localAppData, "Programs", "Git", "usr", "bin", "bash.exe");
        }
    }
}
