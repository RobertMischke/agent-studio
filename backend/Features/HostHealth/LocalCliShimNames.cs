namespace AgentStudio.HostHealth;

/// <summary>
/// Which file names in the npm global bin directory count as a launchable
/// shim, and which count as an atomic-rename orphan. Pure string work, split
/// out from the filesystem reads so the platform matrix is testable on any
/// host.
/// </summary>
public static class LocalCliShimNames
{
    /// <summary>
    /// Names that, if any one of them exists, mean the OS can launch this CLI
    /// from the npm global bin directory.
    ///
    /// <para>
    /// On Windows npm writes three files per binary: a bare Bash shim, a
    /// <c>.cmd</c> launcher and a <c>.ps1</c> launcher. Only the first two are
    /// reachable through <c>PATHEXT</c> from a .NET <c>Process.Start</c>, so a
    /// lone surviving <c>.ps1</c> is deliberately not treated as launchable.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> LaunchableShims(string command, bool isWindows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        return isWindows
            ? [command, command + ".cmd", command + ".exe"]
            : [command];
    }

    /// <summary>
    /// True for the leftovers of npm's write-then-rename pattern:
    /// <c>.claude-2shlnT4k</c>, <c>.claude.cmd-A8DH7lDq</c>,
    /// <c>.claude.ps1-Phb6s52t</c>. Their presence means an install was
    /// interrupted mid-rename, which is a different defect (and a different
    /// repair) from shims that are simply gone.
    /// </summary>
    public static bool IsOrphanShim(string fileName, string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (string.IsNullOrEmpty(fileName)) return false;

        var prefix = "." + command;
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal)) return false;

        // Everything after the command name has to be an optional extension
        // followed by "-<random>". Requiring the dash keeps a hypothetical
        // ".claudeconfig" from being read as a broken shim.
        var tail = fileName[prefix.Length..];
        var dash = tail.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0 || dash == tail.Length - 1) return false;

        var extension = tail[..dash];
        return extension.Length == 0 || extension is ".cmd" or ".ps1" or ".exe";
    }
}
