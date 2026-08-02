namespace AgentStudio.TestSupport;

/// <summary>
/// PATH lookup for test fixtures that shell out to real tools.
///
/// <see cref="System.Diagnostics.ProcessStartInfo.FileName"/> does resolve a
/// bare name against the PATH, but a fixture usually needs the resolved path
/// itself: to decide whether the tool exists at all (skip vs. fail), and to log
/// which one it picked when several are installed.
/// </summary>
public static class Executables
{
    /// <summary>
    /// First match for <paramref name="exe"/> on the PATH, or <c>null</c>.
    /// The Windows <c>.exe</c> suffix is appended automatically.
    /// </summary>
    public static string? FindOnPath(string exe)
    {
        var separator = OperatingSystem.IsWindows() ? ';' : ':';
        var extension = OperatingSystem.IsWindows() ? ".exe" : "";
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in pathEnv.Split(
                     separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, exe + extension);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry must not take the whole lookup down.
            }
        }
        return null;
    }
}
