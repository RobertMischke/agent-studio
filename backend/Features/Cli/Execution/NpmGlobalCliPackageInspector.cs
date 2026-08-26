using System.Text.Json;

namespace AgentStudio.Cli;

/// <summary>
/// Pure filesystem classification for npm-installed coding CLIs. A failed
/// <c>--version</c> probe alone cannot tell an absent installation from the
/// Windows failure seen on 2026-08-13 and 2026-08-18, where the package stayed
/// under <c>node_modules</c> while npm's launch shims disappeared.
/// </summary>
internal static class NpmGlobalCliPackageInspector
{
    private static readonly IReadOnlyDictionary<string, NpmCliPackage> Packages =
        new Dictionary<string, NpmCliPackage>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"] = new("@anthropic-ai/claude-code", ["@anthropic-ai", "claude-code"]),
            ["codex"] = new("@openai/codex", ["@openai", "codex"]),
        };

    internal static NpmCliInstallInspection Inspect(string cliType, string npmBin)
    {
        var normalizedCliType = cliType.Trim();
        if (!Packages.TryGetValue(normalizedCliType, out var package))
            return NpmCliInstallInspection.Unsupported(cliType);

        var packagePath = package.PathSegments.Aggregate(
            Path.Combine(npmBin, "node_modules"),
            (current, segment) => Path.Combine(current, segment));
        var manifestPath = Path.Combine(packagePath, "package.json");
        var packagePresent = Directory.Exists(packagePath);
        var commandShim = Path.Combine(npmBin, normalizedCliType + ".cmd");
        var shellShim = Path.Combine(npmBin, normalizedCliType);
        var powershellShim = Path.Combine(npmBin, normalizedCliType + ".ps1");
        var launchShimPresent = File.Exists(commandShim);

        return new NpmCliInstallInspection(
            CliType: normalizedCliType,
            PackageName: package.Name,
            PackagePath: packagePath,
            PackagePresent: packagePresent,
            PackageVersion: packagePresent ? ReadVersion(manifestPath) : null,
            PackageModifiedAt: SafeLastWriteTime(packagePath),
            CommandShimPath: commandShim,
            ShellShimPath: shellShim,
            PowerShellShimPath: powershellShim,
            CommandShimPresent: launchShimPresent,
            ShellShimPresent: File.Exists(shellShim),
            PowerShellShimPresent: File.Exists(powershellShim));
    }

    private static string? ReadVersion(string manifestPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return document.RootElement.TryGetProperty("version", out var version)
                   && version.ValueKind == JsonValueKind.String
                ? version.GetString()
                : null;
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "NpmGlobalCliPackageInspector: unreadable package manifest");
            return null;
        }
    }

    private static DateTime? SafeLastWriteTime(string path)
    {
        try { return Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : null; }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "NpmGlobalCliPackageInspector: package timestamp unavailable");
            return null;
        }
    }

    private sealed record NpmCliPackage(string Name, string[] PathSegments);
}

internal sealed record NpmCliInstallInspection(
    string CliType,
    string? PackageName,
    string? PackagePath,
    bool PackagePresent,
    string? PackageVersion,
    DateTime? PackageModifiedAt,
    string? CommandShimPath,
    string? ShellShimPath,
    string? PowerShellShimPath,
    bool CommandShimPresent,
    bool ShellShimPresent,
    bool PowerShellShimPresent)
{
    internal bool MissingShimWithPackagePresent =>
        PackagePresent && !CommandShimPresent;

    internal static NpmCliInstallInspection Unsupported(string? cliType) => new(
        cliType ?? string.Empty,
        null,
        null,
        false,
        null,
        null,
        null,
        null,
        null,
        false,
        false,
        false);
}
