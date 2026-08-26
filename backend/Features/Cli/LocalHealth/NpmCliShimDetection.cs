using System.Text.Json;

namespace AgentStudio.Cli;

public enum NpmCliInstallState
{
    Healthy,
    TrulyUninstalled,
    MissingShimWithPackagePresent,
    UnavailableWithShimPresent,
}

public sealed record NpmCliFileFact(
    string Name,
    bool Exists,
    long? SizeBytes,
    DateTimeOffset? LastWriteAt);

public sealed record NpmActivityFact(
    string FileName,
    DateTimeOffset LastWriteAt,
    long SizeBytes,
    IReadOnlyList<string> RelevantTail);

public sealed record NpmCliInstallSnapshot(
    string CliType,
    string PackageName,
    string NpmBinPath,
    string PackagePath,
    bool PackagePresent,
    string? PackageVersion,
    NpmCliInstallState State,
    IReadOnlyList<NpmCliFileFact> Shims,
    IReadOnlyList<NpmCliFileFact> PackageArtifacts,
    IReadOnlyList<NpmActivityFact> RecentNpmActivity,
    IReadOnlyList<NpmActivityFact> RecentProviderActivity);

/// <summary>
/// Reads the npm-global install shape without invoking a CLI. Keeping this
/// detector separate makes the missing-shim decision portable and directly
/// testable even though automatic repair only runs on Windows.
/// </summary>
public static class NpmCliShimDetection
{
    private static readonly IReadOnlyDictionary<string, NpmCliDescriptor> Descriptors =
        new Dictionary<string, NpmCliDescriptor>(StringComparer.OrdinalIgnoreCase)
        {
            [CliTypes.Claude] = new(CliTypes.Claude, "@anthropic-ai/claude-code", ["@anthropic-ai", "claude-code"]),
            [CliTypes.Codex] = new(CliTypes.Codex, "@openai/codex", ["@openai", "codex"]),
        };

    public static bool TryGetDescriptor(string cliType, out NpmCliDescriptor descriptor)
        => Descriptors.TryGetValue(cliType, out descriptor!);

    public static NpmCliInstallState Classify(
        bool cliAvailable,
        bool packagePresent,
        bool commandShimPresent)
    {
        if (cliAvailable) return NpmCliInstallState.Healthy;
        if (!packagePresent) return NpmCliInstallState.TrulyUninstalled;
        return commandShimPresent
            ? NpmCliInstallState.UnavailableWithShimPresent
            : NpmCliInstallState.MissingShimWithPackagePresent;
    }

    public static NpmCliInstallSnapshot Inspect(
        string cliType,
        string npmBinPath,
        string? npmCachePath,
        bool cliAvailable,
        DateTimeOffset observedAt,
        string? providerActivityPath = null)
    {
        if (!TryGetDescriptor(cliType, out var descriptor))
            throw new ArgumentException($"CLI '{cliType}' is not an npm self-heal target.", nameof(cliType));

        var packagePath = Path.Combine(
            npmBinPath,
            "node_modules",
            Path.Combine(descriptor.PackageSegments.ToArray()));
        var packageJson = Path.Combine(packagePath, "package.json");
        var packagePresent = Directory.Exists(packagePath);
        var commandShim = Path.Combine(npmBinPath, descriptor.CliType + ".cmd");
        var shims = new[] { "", ".cmd", ".ps1" }
            .Select(extension => FileFact(Path.Combine(npmBinPath, descriptor.CliType + extension), npmBinPath))
            .ToArray();
        var artifacts = new List<NpmCliFileFact>
        {
            FileFact(packageJson, packagePath),
            FileFact(Path.Combine(packagePath, "bin", descriptor.CliType + ".exe"), packagePath),
        };
        foreach (var directory in new[] { packagePath, Path.Combine(packagePath, "bin") })
        {
            foreach (var orphan in SafeEnumerateFiles(directory, descriptor.CliType + ".exe.old.*"))
                artifacts.Add(FileFact(orphan, packagePath));
        }
        if (descriptor.CliType == CliTypes.Claude)
        {
            var platformPath = Path.Combine(
                npmBinPath,
                "node_modules",
                "@anthropic-ai",
                "claude-code-win32-x64");
            artifacts.Add(FileFact(Path.Combine(platformPath, "claude.exe"), packagePath));
            foreach (var orphan in SafeEnumerateFiles(platformPath, "claude.exe.old.*"))
                artifacts.Add(FileFact(orphan, packagePath));
        }

        return new NpmCliInstallSnapshot(
            descriptor.CliType,
            descriptor.PackageName,
            npmBinPath,
            packagePath,
            packagePresent,
            ReadPackageVersion(packageJson),
            Classify(cliAvailable, packagePresent, File.Exists(commandShim)),
            shims,
            artifacts,
            ReadRecentActivity(npmCachePath, "_logs", descriptor, observedAt),
            ReadRecentActivity(providerActivityPath, null, descriptor, observedAt));
    }

    private static NpmCliFileFact FileFact(string path, string relativeTo)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists
                ? new NpmCliFileFact(Path.GetRelativePath(relativeTo, path), true, file.Length, file.LastWriteTimeUtc)
                : new NpmCliFileFact(Path.GetRelativePath(relativeTo, path), false, null, null);
        }
        catch
        {
            return new NpmCliFileFact(Path.GetRelativePath(relativeTo, path), false, null, null);
        }
    }

    private static string? ReadPackageVersion(string packageJson)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packageJson));
            return document.RootElement.TryGetProperty("version", out var version)
                ? version.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<NpmActivityFact> ReadRecentActivity(
        string? activityRoot,
        string? childDirectory,
        NpmCliDescriptor descriptor,
        DateTimeOffset observedAt)
    {
        if (string.IsNullOrWhiteSpace(activityRoot)) return [];
        var logDir = childDirectory is null ? activityRoot : Path.Combine(activityRoot, childDirectory);
        if (!Directory.Exists(logDir)) return [];

        try
        {
            var pattern = childDirectory is null ? "*" : "*.log";
            return Directory.EnumerateFiles(logDir, pattern, SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .Where(file => observedAt - file.LastWriteTimeUtc <= TimeSpan.FromHours(12))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(5)
                .Select(file => new NpmActivityFact(
                    file.Name,
                    file.LastWriteTimeUtc,
                    file.Length,
                    RelevantTail(file.FullName, descriptor)))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<string> RelevantTail(string path, NpmCliDescriptor descriptor)
    {
        try
        {
            return ReadTailLines(path, 64 * 1024)
                .Where(line => line.Contains(descriptor.PackageName, StringComparison.OrdinalIgnoreCase)
                    || line.Contains(descriptor.CliType, StringComparison.OrdinalIgnoreCase)
                    || line.Contains("version", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("install", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("update", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("postinstall", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("rename", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("move", StringComparison.OrdinalIgnoreCase)
                    || line.Contains(".old.", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("error", StringComparison.OrdinalIgnoreCase))
                .TakeLast(24)
                .Select(Redact)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static string Redact(string line)
    {
        var value = line.Length <= 600 ? line : line[..600];
        foreach (var marker in new[] { "_authToken=", "token=", "authorization:" })
        {
            var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) value = value[..(index + marker.Length)] + "[redacted]";
        }
        return value;
    }

    private static IEnumerable<string> SafeEnumerateFiles(string root, string pattern)
    {
        if (!Directory.Exists(root)) return [];
        try { return Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly).Take(20).ToArray(); }
        catch { return []; }
    }

    private static IReadOnlyList<string> ReadTailLines(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > maxBytes) stream.Seek(-maxBytes, SeekOrigin.End);
        using var reader = new StreamReader(stream);
        if (stream.Position > 0) _ = reader.ReadLine();
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }
}

public sealed record NpmCliDescriptor(
    string CliType,
    string PackageName,
    IReadOnlyList<string> PackageSegments);
