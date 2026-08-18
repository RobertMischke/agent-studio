namespace AgentStudio.HostHealth;

/// <summary>The environment values that decide where a global npm install lives.</summary>
public sealed record NpmEnvironment
{
    /// <summary>Explicit operator override (<c>HostHealth:NpmGlobalBin</c>); wins over everything else.</summary>
    public string? ConfiguredBin { get; init; }

    /// <summary>True on Windows, where npm's global prefix is the bin directory itself.</summary>
    public bool IsWindows { get; init; }

    /// <summary><c>%APPDATA%</c>; only meaningful on Windows.</summary>
    public string? AppData { get; init; }

    /// <summary><c>%LOCALAPPDATA%</c>; only meaningful on Windows.</summary>
    public string? LocalAppData { get; init; }

    /// <summary><c>$NPM_CONFIG_PREFIX</c> when the operator moved the global prefix.</summary>
    public string? NpmConfigPrefix { get; init; }

    /// <summary>User home directory.</summary>
    public string? Home { get; init; }
}

/// <summary>
/// Where the three directories this feature reads live on one host. All three
/// may be null: a container image with no global npm install is a legitimate
/// host, and <see cref="LocalCliInstallDiagnosis"/> treats an unresolved
/// layout as "cannot tell" rather than "broken".
/// </summary>
public sealed record NpmGlobalLayout(string? BinDirectory, string? NodeModulesDirectory, string? LogsDirectory)
{
    public static readonly NpmGlobalLayout Unresolved = new(null, null, null);

    public bool Resolved => !string.IsNullOrEmpty(BinDirectory) && !string.IsNullOrEmpty(NodeModulesDirectory);
}

/// <summary>
/// Pure resolution of the npm global layout from environment values. Kept
/// separate from the filesystem reads so the platform matrix (Windows
/// <c>%APPDATA%\npm</c> vs POSIX <c>&lt;prefix&gt;/lib/node_modules</c>) is
/// testable on any host, which is what makes the shim detection portable.
/// </summary>
public static class NpmGlobalLayoutResolver
{
    /// <summary>
    /// <paramref name="directoryExists"/> is injected so the resolver can pick
    /// between candidate POSIX prefixes without touching a real filesystem in
    /// tests.
    /// </summary>
    public static NpmGlobalLayout Resolve(NpmEnvironment environment, Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(directoryExists);

        var logs = ResolveLogsDirectory(environment);

        if (!string.IsNullOrWhiteSpace(environment.ConfiguredBin))
        {
            var bin = environment.ConfiguredBin!.TrimEnd('/', '\\');
            return new(bin, Path.Combine(bin, "node_modules"), logs);
        }

        if (environment.IsWindows)
        {
            if (string.IsNullOrWhiteSpace(environment.AppData)) return NpmGlobalLayout.Unresolved with { LogsDirectory = logs };
            var bin = Path.Combine(environment.AppData!, "npm");
            return new(bin, Path.Combine(bin, "node_modules"), logs);
        }

        var prefix = ResolvePosixPrefix(environment, directoryExists);
        if (prefix is null) return NpmGlobalLayout.Unresolved with { LogsDirectory = logs };

        return new(
            Path.Combine(prefix, "bin"),
            Path.Combine(prefix, "lib", "node_modules"),
            logs);
    }

    private static string? ResolvePosixPrefix(NpmEnvironment environment, Func<string, bool> directoryExists)
    {
        if (!string.IsNullOrWhiteSpace(environment.NpmConfigPrefix))
            return environment.NpmConfigPrefix!.TrimEnd('/');

        // Ordered by how specific the evidence is: a per-user prefix that
        // actually exists beats the system default, which we only fall back to
        // when it is really there.
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(environment.Home))
        {
            candidates.Add(Path.Combine(environment.Home!, ".npm-global"));
            candidates.Add(Path.Combine(environment.Home!, ".local"));
        }
        candidates.Add("/usr/local");

        foreach (var candidate in candidates)
        {
            if (directoryExists(Path.Combine(candidate, "lib", "node_modules"))) return candidate;
        }
        return null;
    }

    private static string? ResolveLogsDirectory(NpmEnvironment environment)
    {
        if (environment.IsWindows)
        {
            return string.IsNullOrWhiteSpace(environment.LocalAppData)
                ? null
                : Path.Combine(environment.LocalAppData!, "npm-cache", "_logs");
        }
        return string.IsNullOrWhiteSpace(environment.Home)
            ? null
            : Path.Combine(environment.Home!, ".npm", "_logs");
    }
}
