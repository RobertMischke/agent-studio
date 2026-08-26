using System.Text.Json;

namespace AgentStudio.Cli;

/// <summary>
/// Pure classification for a global npm CLI whose command is unavailable.
/// The policy does not launch npm and does not depend on the current operating
/// system, so Windows shim layouts can be exercised on every test host.
/// </summary>
public static class NpmCliShimInspectionPolicy
{
    private static readonly IReadOnlyDictionary<string, NpmCliDescriptor> Descriptors =
        new Dictionary<string, NpmCliDescriptor>(StringComparer.OrdinalIgnoreCase)
        {
            [CliTypes.Claude] = new(CliTypes.Claude, "claude", "@anthropic-ai/claude-code"),
            [CliTypes.Codex] = new(CliTypes.Codex, "codex", "@openai/codex"),
        };

    public static NpmCliShimInspection Inspect(
        string cliType,
        string configuredPath,
        bool commandAvailable,
        string npmPrefix,
        string npmRoot,
        Func<string, bool>? fileExists = null,
        Func<string, string>? readAllText = null,
        Func<string, DateTimeOffset?>? lastWriteTime = null)
    {
        fileExists ??= File.Exists;
        readAllText ??= File.ReadAllText;
        lastWriteTime ??= path =>
        {
            try { return File.GetLastWriteTimeUtc(path); }
            catch { return null; }
        };

        if (!Descriptors.TryGetValue(CliTypes.Normalize(cliType), out var descriptor))
        {
            return NpmCliShimInspection.Unsupported(
                cliType,
                configuredPath,
                "No global npm repair descriptor is registered for this CLI.");
        }

        var configuredCommand = Path.GetFileNameWithoutExtension(configuredPath.Trim());
        if (Path.IsPathRooted(configuredPath)
            || configuredPath.IndexOfAny(['/', '\\']) >= 0
            || !string.Equals(configuredCommand, descriptor.Command, StringComparison.OrdinalIgnoreCase))
        {
            return NpmCliShimInspection.Unsupported(
                descriptor.CliType,
                configuredPath,
                $"Configured path '{configuredPath}' is an explicit override, not the global '{descriptor.Command}' command.");
        }

        var packagePath = PackagePath(npmRoot, descriptor.PackageName);
        var packageJson = Path.Combine(packagePath, "package.json");
        var packagePresent = fileExists(packageJson);
        var shims = new[]
        {
            Path.Combine(npmPrefix, descriptor.Command),
            Path.Combine(npmPrefix, descriptor.Command + ".cmd"),
            Path.Combine(npmPrefix, descriptor.Command + ".ps1"),
            Path.Combine(npmPrefix, descriptor.Command + ".exe"),
        };
        var existingShims = shims.Where(fileExists).ToArray();
        var packageVersion = packagePresent ? ReadPackageVersion(packageJson, readAllText) : null;

        if (commandAvailable)
        {
            return new NpmCliShimInspection(
                descriptor.CliType,
                descriptor.Command,
                descriptor.PackageName,
                configuredPath,
                NpmCliInstallState.Available,
                npmPrefix,
                npmRoot,
                packagePath,
                packageVersion,
                lastWriteTime(packageJson),
                shims,
                existingShims,
                "The configured CLI command is available.");
        }

        if (!packagePresent)
        {
            return new NpmCliShimInspection(
                descriptor.CliType,
                descriptor.Command,
                descriptor.PackageName,
                configuredPath,
                NpmCliInstallState.TrulyUninstalled,
                npmPrefix,
                npmRoot,
                packagePath,
                null,
                null,
                shims,
                existingShims,
                $"Global npm package '{descriptor.PackageName}' is not installed under '{npmRoot}'.");
        }

        var detail = existingShims.Length == 0
            ? $"Global npm package '{descriptor.PackageName}' {packageVersion ?? "(unknown version)"} is present, but every '{descriptor.Command}' shim is missing from '{npmPrefix}'."
            : $"Global npm package '{descriptor.PackageName}' {packageVersion ?? "(unknown version)"} and {existingShims.Length} shim file(s) are present, but the command probe failed.";
        return new NpmCliShimInspection(
            descriptor.CliType,
            descriptor.Command,
            descriptor.PackageName,
            configuredPath,
            NpmCliInstallState.MissingOrBrokenShimWithPackagePresent,
            npmPrefix,
            npmRoot,
            packagePath,
            packageVersion,
            lastWriteTime(packageJson),
            shims,
            existingShims,
            detail);
    }

    private static string PackagePath(string npmRoot, string packageName)
        => packageName.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Aggregate(npmRoot, Path.Combine);

    private static string? ReadPackageVersion(string packageJson, Func<string, string> readAllText)
    {
        try
        {
            using var document = JsonDocument.Parse(readAllText(packageJson));
            return document.RootElement.TryGetProperty("version", out var version)
                   && version.ValueKind == JsonValueKind.String
                ? version.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed record NpmCliDescriptor(string CliType, string Command, string PackageName);
}

public enum NpmCliInstallState
{
    Available,
    MissingOrBrokenShimWithPackagePresent,
    TrulyUninstalled,
    Unsupported,
}

public sealed record NpmCliShimInspection(
    string CliType,
    string Command,
    string PackageName,
    string ConfiguredPath,
    NpmCliInstallState State,
    string? NpmPrefix,
    string? NpmRoot,
    string? PackagePath,
    string? PackageVersion,
    DateTimeOffset? PackageModifiedAt,
    IReadOnlyList<string> ExpectedShims,
    IReadOnlyList<string> ExistingShims,
    string Detail)
{
    public static NpmCliShimInspection Unsupported(string cliType, string configuredPath, string detail)
        => new(
            CliTypes.Normalize(cliType),
            Path.GetFileNameWithoutExtension(configuredPath),
            "",
            configuredPath,
            NpmCliInstallState.Unsupported,
            null,
            null,
            null,
            null,
            null,
            [],
            [],
            detail);
}
