using System.Text.Json;

namespace AgentStudio.Cli;

internal sealed record NpmCliInstallInspection(
    string CliType,
    string PackageName,
    string NpmBin,
    string PackagePath,
    bool PackagePresent,
    string? PackageVersion,
    DateTimeOffset? PackageManifestModifiedAt,
    IReadOnlyList<string> MissingShims,
    bool InvocableShimPresent)
{
    public bool IsMissingShimWithPackagePresent => PackagePresent && !InvocableShimPresent;
}

/// <summary>
/// Pure filesystem classification for the Windows npm failure where a global
/// package survives under node_modules but npm's command shims disappear.
/// The caller decides whether and when a reinstall is allowed.
/// </summary>
internal static class NpmCliShimInspection
{
    internal static NpmCliInstallInspection Inspect(string cliType, string npmBin)
    {
        var descriptor = Descriptor(cliType)
            ?? throw new ArgumentOutOfRangeException(nameof(cliType), cliType, "Unsupported npm CLI");
        var packagePath = descriptor.Scope is null
            ? Path.Combine(npmBin, "node_modules", descriptor.Package)
            : Path.Combine(npmBin, "node_modules", descriptor.Scope, descriptor.Package);
        var manifestPath = Path.Combine(packagePath, "package.json");
        var npmShims = new[]
        {
            Path.Combine(npmBin, descriptor.Command),
            Path.Combine(npmBin, descriptor.Command + ".cmd"),
        };
        var missing = npmShims
            .Where(path => !File.Exists(path))
            .Select(Path.GetFileName)
            .OfType<string>()
            .ToArray();

        return new NpmCliInstallInspection(
            cliType,
            descriptor.PackageName,
            npmBin,
            packagePath,
            Directory.Exists(packagePath),
            ReadPackageVersion(manifestPath),
            SafeLastWriteTime(manifestPath),
            missing,
            File.Exists(Path.Combine(npmBin, descriptor.Command + ".cmd")));
    }

    internal static bool CanAttempt(DateTimeOffset? lastAttemptAt, DateTimeOffset now)
        => lastAttemptAt is null || now - lastAttemptAt.Value >= TimeSpan.FromHours(1);

    private static (string? Scope, string Package, string PackageName, string Command)? Descriptor(string cliType)
        => cliType.ToLowerInvariant() switch
        {
            "claude" => ("@anthropic-ai", "claude-code", "@anthropic-ai/claude-code", "claude"),
            "codex" => ("@openai", "codex", "@openai/codex", "codex"),
            _ => null,
        };

    private static string? ReadPackageVersion(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return document.RootElement.TryGetProperty("version", out var version)
                ? version.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset? SafeLastWriteTime(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null; }
        catch { return null; }
    }
}
