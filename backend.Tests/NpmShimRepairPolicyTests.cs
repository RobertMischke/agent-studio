using AgentStudio.Cli;
using Xunit;

namespace AgentStudio.Tests;

public sealed class NpmShimRepairPolicyTests
{
    [Theory]
    [InlineData("claude", "@anthropic-ai", "claude-code")]
    [InlineData("codex", "@openai", "codex")]
    public void Package_present_with_missing_cmd_shim_is_repairable(
        string cliType,
        string scope,
        string package)
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "node_modules", scope, package));
        File.WriteAllText(
            Path.Combine(temp.Path, "node_modules", scope, package, "package.json"),
            "{\"version\":\"2.1.234\"}");

        var inspection = NpmShimRepairPolicy.Inspect(
            cliType,
            cliType,
            temp.Path,
            executableAvailable: false);

        Assert.Equal(NpmShimInstallState.MissingShimPackagePresent, inspection.State);
        Assert.Equal("2.1.234", inspection.PackageVersion);
        Assert.Empty(inspection.PresentShims);
    }

    [Fact]
    public void Missing_package_is_a_true_uninstall_and_never_auto_repairs()
    {
        using var temp = new TempDirectory();

        var inspection = NpmShimRepairPolicy.Inspect(
            CliTypes.Claude,
            "claude",
            temp.Path,
            executableAvailable: false);

        Assert.Equal(NpmShimInstallState.TrulyUninstalled, inspection.State);
    }

    [Fact]
    public void Explicit_missing_binary_does_not_redirect_into_global_npm_repair()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(
            temp.Path, "node_modules", "@anthropic-ai", "claude-code"));

        var inspection = NpmShimRepairPolicy.Inspect(
            CliTypes.Claude,
            Path.Combine(temp.Path, "pinned", "claude.exe"),
            temp.Path,
            executableAvailable: false);

        Assert.Equal(NpmShimInstallState.ExplicitPath, inspection.State);
    }

    [Fact]
    public void Repair_attempt_is_bounded_to_one_per_hour()
    {
        var attemptedAt = new DateTimeOffset(2026, 8, 18, 10, 0, 0, TimeSpan.Zero);

        Assert.False(NpmShimRepairPolicy.AttemptAllowed(
            attemptedAt,
            attemptedAt.AddMinutes(59).AddSeconds(59)));
        Assert.True(NpmShimRepairPolicy.AttemptAllowed(
            attemptedAt,
            attemptedAt.AddHours(1)));
    }

    [Fact]
    public void Npm_activity_capture_keeps_only_sanitized_diagnostic_summary()
    {
        using var temp = new TempDirectory();
        var packagePath = Path.Combine(
            temp.Path, "node_modules", "@anthropic-ai", "claude-code");
        Directory.CreateDirectory(packagePath);
        File.WriteAllText(Path.Combine(packagePath, "package.json"), "{\"version\":\"2.1.234\"}");
        var logs = Path.Combine(temp.Path, "cache", "_logs");
        Directory.CreateDirectory(logs);
        var logPath = Path.Combine(logs, "2026-08-18-debug-0.log");
        File.WriteAllLines(logPath,
        [
            "1 info using npm@11.0.0",
            "2 info using node@v22.0.0",
            "3 verbose title npm install https://registry.example.test/secret",
            "4 silly ignored bearer-value",
            "5 verbose exit 0",
        ]);
        var activity = NpmShimHealer.CaptureInstallActivity(
            packagePath,
            temp.Path,
            DateTimeOffset.UtcNow,
            [Path.Combine(temp.Path, "cache")]);

        var npmLog = Assert.Single(activity, item => item.Path == logPath);
        Assert.Contains("npm@11.0.0", npmLog.Summary);
        Assert.Contains("verbose exit 0", npmLog.Summary);
        Assert.Contains("[url]", npmLog.Summary);
        Assert.DoesNotContain("bearer-value", npmLog.Summary);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"npm-shim-policy-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
