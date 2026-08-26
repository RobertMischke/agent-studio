using System.Text.Json;

namespace AgentStudio.Cli;

/// <summary>
/// Pure classification for the npm failure shape where a globally installed
/// package remains on disk but the executable shim has disappeared. Keeping
/// this separate from process execution makes the Windows-specific incident
/// reproducible on every test platform.
/// </summary>
public static class NpmShimRepairPolicy
{
    public static readonly TimeSpan AttemptCooldown = TimeSpan.FromHours(1);

    private static readonly IReadOnlyDictionary<string, string> Packages =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [CliTypes.Claude] = "@anthropic-ai/claude-code",
            [CliTypes.Codex] = "@openai/codex",
        };

    public static NpmShimInstallInspection Inspect(
        string cliType,
        string configuredBinary,
        string npmBin,
        bool executableAvailable)
    {
        var normalized = CliTypes.Normalize(cliType);
        if (!Packages.TryGetValue(normalized, out var packageName))
        {
            return new NpmShimInstallInspection(
                NpmShimInstallState.Unsupported,
                normalized,
                null,
                null,
                null,
                Array.Empty<string>());
        }

        if (Path.IsPathRooted(configuredBinary))
        {
            var configuredFullPath = Path.GetFullPath(configuredBinary);
            var canonicalShims = new[]
            {
                Path.Combine(npmBin, normalized),
                Path.Combine(npmBin, normalized + ".cmd"),
                Path.Combine(npmBin, normalized + ".ps1"),
            }.Select(Path.GetFullPath);
            if (!canonicalShims.Contains(configuredFullPath, StringComparer.OrdinalIgnoreCase))
            {
                return new NpmShimInstallInspection(
                    NpmShimInstallState.ExplicitPath,
                    normalized,
                    packageName,
                    null,
                    null,
                    Array.Empty<string>());
            }
        }

        var binaryName = Path.GetFileNameWithoutExtension(configuredBinary).Trim();
        if (!string.Equals(binaryName, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return new NpmShimInstallInspection(
                NpmShimInstallState.ExplicitPath,
                normalized,
                packageName,
                null,
                null,
                Array.Empty<string>());
        }

        var packagePath = Path.Combine(
            npmBin,
            "node_modules",
            packageName.Replace('/', Path.DirectorySeparatorChar));
        var shims = new[]
        {
            Path.Combine(npmBin, normalized),
            Path.Combine(npmBin, normalized + ".cmd"),
            Path.Combine(npmBin, normalized + ".ps1"),
        };
        var packagePresent = Directory.Exists(packagePath);
        var cmdShimPresent = File.Exists(Path.Combine(npmBin, normalized + ".cmd"));

        var state = executableAvailable
            ? NpmShimInstallState.Available
            : packagePresent && !cmdShimPresent
                ? NpmShimInstallState.MissingShimPackagePresent
                : packagePresent
                    ? NpmShimInstallState.BrokenInstall
                    : NpmShimInstallState.TrulyUninstalled;

        return new NpmShimInstallInspection(
            state,
            normalized,
            packageName,
            packagePath,
            ReadPackageVersion(packagePath),
            shims.Where(File.Exists).ToArray());
    }

    public static bool AttemptAllowed(DateTimeOffset? lastAttemptAt, DateTimeOffset now)
        => lastAttemptAt is null || now - lastAttemptAt.Value >= AttemptCooldown;

    private static string? ReadPackageVersion(string packagePath)
    {
        try
        {
            var path = Path.Combine(packagePath, "package.json");
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("version", out var version)
                ? version.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }
}

public enum NpmShimInstallState
{
    Available,
    MissingShimPackagePresent,
    TrulyUninstalled,
    BrokenInstall,
    ExplicitPath,
    Unsupported,
}

public sealed record NpmShimInstallInspection(
    NpmShimInstallState State,
    string CliType,
    string? PackageName,
    string? PackagePath,
    string? PackageVersion,
    IReadOnlyList<string> PresentShims);
