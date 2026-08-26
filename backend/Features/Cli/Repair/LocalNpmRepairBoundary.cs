using System.Diagnostics;
using System.Text.Json;

namespace AgentStudio.Cli;

public interface ILocalNpmRepairBoundary
{
    bool SupportsRepair { get; }
    Task<LocalNpmInstallationSnapshot> CaptureAsync(
        string cliType,
        string npmPackage,
        string configuredExecutable,
        CancellationToken ct);
    Task<LocalNpmInstallResult> InstallGlobalAsync(string npmPackage, CancellationToken ct);
}

/// <summary>
/// Windows npm filesystem/process boundary for local capability repair. It
/// records metadata only: paths, mtimes, package versions, and npm command
/// output tails. From recent npm debug logs it copies only bounded
/// <c>title</c>/<c>info run</c> command metadata that names the
/// affected package, never the full log.
/// </summary>
public sealed class LocalNpmRepairBoundary : ILocalNpmRepairBoundary
{
    private readonly ILogger<LocalNpmRepairBoundary> _logger;
    private readonly bool _repairEnabled;

    public LocalNpmRepairBoundary(
        ILogger<LocalNpmRepairBoundary> logger,
        bool repairEnabled = true)
    {
        _logger = logger;
        _repairEnabled = repairEnabled;
    }

    public bool SupportsRepair => _repairEnabled && OperatingSystem.IsWindows();

    public Task<LocalNpmInstallationSnapshot> CaptureAsync(
        string cliType,
        string npmPackage,
        string configuredExecutable,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var prefix = ResolvePrefix(cliType, npmPackage, configuredExecutable);
        var packageDirectory = Path.Combine(
            prefix,
            "node_modules",
            npmPackage.Replace('/', Path.DirectorySeparatorChar));
        var packageJson = Path.Combine(packageDirectory, "package.json");
        var packagePresent = File.Exists(packageJson);
        var version = ReadPackageVersion(packageJson);
        var callableShimPresent = CallableShimCandidates(prefix, cliType, configuredExecutable)
            .Any(File.Exists);
        var activity = CaptureRecentActivity(prefix, packageDirectory, cliType, npmPackage);
        return Task.FromResult(new LocalNpmInstallationSnapshot(
            prefix,
            packageDirectory,
            packagePresent,
            callableShimPresent,
            version,
            activity));
    }

    public async Task<LocalNpmInstallResult> InstallGlobalAsync(
        string npmPackage,
        CancellationToken ct)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = FindNpmLauncher(),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            process.StartInfo.ArgumentList.Add("install");
            process.StartInfo.ArgumentList.Add("--global");
            process.StartInfo.ArgumentList.Add(npmPackage);
            if (!process.Start())
                return new LocalNpmInstallResult(false, null, "", "", "npm did not start");

            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) { _logger.LogDebug(ex, "Failed to stop timed-out npm repair"); }
                return new LocalNpmInstallResult(false, null, Tail(await stdout), Tail(await stderr),
                    "npm install timed out after five minutes");
            }

            var outText = Tail(await stdout);
            var errText = Tail(await stderr);
            return new LocalNpmInstallResult(
                process.ExitCode == 0,
                process.ExitCode,
                outText,
                errText,
                process.ExitCode == 0 ? null : $"npm install exited {process.ExitCode}");
        }
        catch (Exception ex)
        {
            return new LocalNpmInstallResult(false, null, "", "", ex.Message);
        }
    }

    private static string ResolvePrefix(
        string cliType,
        string npmPackage,
        string configuredExecutable)
    {
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        var defaultPrefix = string.IsNullOrWhiteSpace(appData)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm")
            : Path.Combine(appData, "npm");

        var candidates = new List<string> { defaultPrefix };
        if (Path.IsPathRooted(configuredExecutable))
        {
            var configuredDirectory = Path.GetDirectoryName(configuredExecutable);
            if (!string.IsNullOrWhiteSpace(configuredDirectory)) candidates.Add(configuredDirectory);
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            candidates.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(candidate => candidate.Trim().Trim('"'))
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate)));
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(candidate =>
                File.Exists(Path.Combine(
                    candidate,
                    "node_modules",
                    npmPackage.Replace('/', Path.DirectorySeparatorChar),
                    "package.json"))
                || CallableShimCandidates(candidate, cliType, configuredExecutable).Any(File.Exists))
            ?? defaultPrefix;
    }

    private static string FindNpmLauncher()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                         .Select(candidate => candidate.Trim().Trim('"')))
            {
                foreach (var name in new[] { "npm.cmd", "npm.exe", "npm.bat" })
                {
                    var candidate = Path.Combine(directory, name);
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }

        return "npm";
    }

    private static IEnumerable<string> CallableShimCandidates(
        string prefix,
        string cliType,
        string configuredExecutable)
    {
        if (Path.IsPathRooted(configuredExecutable))
        {
            yield return configuredExecutable;
            yield return configuredExecutable + ".cmd";
            yield return configuredExecutable + ".exe";
        }
        yield return Path.Combine(prefix, cliType + ".cmd");
        yield return Path.Combine(prefix, cliType + ".exe");
        yield return Path.Combine(prefix, cliType + ".bat");
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

    private static IReadOnlyList<string> CaptureRecentActivity(
        string prefix,
        string packageDirectory,
        string cliType,
        string npmPackage)
    {
        var rows = new List<(DateTime At, string Text)>();
        AddPath(rows, "package", Path.Combine(packageDirectory, "package.json"));
        foreach (var candidate in CallableShimCandidates(prefix, cliType, cliType))
            AddPath(rows, "shim", candidate);

        AddMatches(rows, "shim-orphan", prefix, $".{cliType}*-*", SearchOption.TopDirectoryOnly, 12);
        AddMatches(rows, "package-update", packageDirectory, "*update*", SearchOption.AllDirectories, 12);

        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            AddNpmLogActivity(
                rows,
                Path.Combine(localAppData, "npm-cache", "_logs"),
                npmPackage);
        }

        return rows
            .OrderByDescending(row => row.At)
            .Take(20)
            .Select(row => row.Text)
            .ToArray();
    }

    private static void AddNpmLogActivity(
        ICollection<(DateTime At, string Text)> rows,
        string logDirectory,
        string npmPackage)
    {
        if (!Directory.Exists(logDirectory)) return;
        try
        {
            var logs = Directory.EnumerateFiles(logDirectory, "*-debug-*.log")
                .Select(path => (Path: path, At: File.GetLastWriteTimeUtc(path)))
                .OrderByDescending(item => item.At)
                .Take(8);
            foreach (var log in logs)
            {
                var commandLines = File.ReadLines(log.Path)
                    .Take(100)
                    .Where(line => line.Contains(npmPackage, StringComparison.OrdinalIgnoreCase))
                    .Where(line => line.Contains(" verbose title ", StringComparison.Ordinal)
                                   || line.Contains(" info run ", StringComparison.Ordinal))
                    .Take(4)
                    .Select(SanitizeActivityLine)
                    .ToArray();
                if (commandLines.Length == 0) continue;
                rows.Add((log.At,
                    $"npm-command:{Path.GetFileName(log.Path)}@{log.At:o}:{string.Join(" | ", commandLines)}"));
            }
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "LocalNpmRepairBoundary: npm activity log changed during capture");
        }
    }

    private static string SanitizeActivityLine(string line)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var sanitized = string.IsNullOrWhiteSpace(home)
            ? line
            : line.Replace(home, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        sanitized = sanitized.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return sanitized.Length <= 400 ? sanitized : sanitized[..400];
    }

    private static void AddMatches(
        ICollection<(DateTime At, string Text)> rows,
        string kind,
        string directory,
        string pattern,
        SearchOption option,
        int limit)
    {
        if (!Directory.Exists(directory)) return;
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory, pattern, option).Take(limit))
                AddPath(rows, kind, path);
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "LocalNpmRepairBoundary: activity directory changed during capture");
            // Activity capture is forensic enrichment. It must never block the
            // repair decision when a directory is concurrently being replaced.
        }
    }

    private static void AddPath(ICollection<(DateTime At, string Text)> rows, string kind, string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return;
            var at = File.GetLastWriteTimeUtc(path);
            rows.Add((at, $"{kind}:{Path.GetFileName(path)}@{at:o}"));
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "LocalNpmRepairBoundary: activity path changed during capture");
            // Best-effort metadata only.
        }
    }

    private static string Tail(string value)
    {
        const int limit = 4096;
        if (string.IsNullOrEmpty(value) || value.Length <= limit) return value;
        return value[^limit..];
    }
}
