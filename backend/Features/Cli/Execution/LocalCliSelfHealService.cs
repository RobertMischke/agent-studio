using System.Diagnostics;
using System.Text.Json;
using AgentStudio.Diagnostics;
using AgentStudio.Shared;

namespace AgentStudio.Cli;

public enum NpmShimInstallState
{
    Unsupported,
    Available,
    TrulyUninstalled,
    MissingShimWithPackagePresent,
}

public sealed record NpmShimInspection(
    string CliType,
    string PackageName,
    string PackageDirectory,
    NpmShimInstallState State);

/// <summary>
/// Pure classification for the Windows npm failure where the package directory
/// survives but every global launcher shim disappears. A genuinely absent
/// package is deliberately not eligible for automatic installation.
/// </summary>
public static class NpmShimRepairPolicy
{
    public static readonly TimeSpan AttemptInterval = TimeSpan.FromHours(1);

    public static NpmShimInspection Inspect(
        string cliType,
        string configuredPath,
        string npmBin,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists)
    {
        var normalized = CliTypes.Normalize(cliType);
        var packageName = normalized switch
        {
            CliTypes.Claude => "@anthropic-ai/claude-code",
            CliTypes.Codex => "@openai/codex",
            _ => string.Empty,
        };
        if (packageName.Length == 0 || string.IsNullOrWhiteSpace(npmBin))
            return new NpmShimInspection(normalized, packageName, string.Empty, NpmShimInstallState.Unsupported);

        var configuredName = Path.GetFileNameWithoutExtension(configuredPath).Trim();
        var configuredDirectory = Path.GetDirectoryName(configuredPath);
        var usesGlobalNpmBin = UsesGlobalNpmBin(configuredDirectory, npmBin);
        if (!usesGlobalNpmBin
            || !string.Equals(configuredName, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return new NpmShimInspection(
                normalized,
                packageName,
                PackageDirectory(npmBin, packageName),
                NpmShimInstallState.Unsupported);
        }

        // Process.Start on Windows resolves the npm .cmd launcher. A leftover
        // POSIX or PowerShell shim does not make that launcher executable.
        if (fileExists(Path.Combine(npmBin, normalized + ".cmd")))
        {
            return new NpmShimInspection(
                normalized,
                packageName,
                PackageDirectory(npmBin, packageName),
                NpmShimInstallState.Available);
        }

        var packageDirectory = PackageDirectory(npmBin, packageName);
        return new NpmShimInspection(
            normalized,
            packageName,
            packageDirectory,
            directoryExists(packageDirectory)
                ? NpmShimInstallState.MissingShimWithPackagePresent
                : NpmShimInstallState.TrulyUninstalled);
    }

    public static bool CanAttempt(DateTimeOffset? lastAttemptAt, DateTimeOffset now)
        => lastAttemptAt is null || now - lastAttemptAt.Value >= AttemptInterval;

    private static string PackageDirectory(string npmBin, string packageName)
    {
        var segments = packageName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return Path.Combine([npmBin, "node_modules", .. segments]);
    }

    private static bool UsesGlobalNpmBin(string? configuredDirectory, string npmBin)
    {
        if (string.IsNullOrWhiteSpace(configuredDirectory)) return true;
        try
        {
            return string.Equals(
                Path.GetFullPath(configuredDirectory),
                Path.GetFullPath(npmBin),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

internal sealed record CliRepairRootCauseSnapshot(
    string? PackageVersion,
    DateTimeOffset? PackageLastWriteAt,
    IReadOnlyList<string> RecentNpmActivity,
    string AutoUpdatePolicy);

internal sealed record CliRepairJournalEntry(
    DateTimeOffset OccurredAt,
    string CliType,
    string PackageName,
    string Outcome,
    string? CliVersionBefore,
    string? CliVersionAfter,
    string? PackageVersionBefore,
    string? PackageVersionAfter,
    DateTimeOffset? PackageLastWriteAt,
    IReadOnlyList<string> RecentNpmActivity,
    string AutoUpdatePolicy,
    int? NpmExitCode,
    string? Detail);

/// <summary>
/// Periodically extends the local <c>--version</c> capability probe with one
/// narrowly-authorized repair: when a supported global npm package still
/// exists but all of its shims are gone, re-run that package's global install.
/// Attempts are durable and bounded to once per CLI per hour.
/// </summary>
public sealed class LocalCliSelfHealService : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(3);
    private readonly CliRouter _router;
    private readonly ILogger<LocalCliSelfHealService> _logger;
    private readonly string _journalPath;
    private readonly object _sync = new();
    private readonly Dictionary<string, DateTimeOffset> _lastAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _lastKnownVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LocalCliRepairStatus> _latestRepairs = new(StringComparer.OrdinalIgnoreCase);

    public LocalCliSelfHealService(
        CliRouter router,
        ILogger<LocalCliSelfHealService> logger,
        BackendFileLogSink fileLogSink)
    {
        _router = router;
        _logger = logger;
        _journalPath = Path.Combine(fileLogSink.ResolvedDirectory, "cli-repair-journal.jsonl");
        LoadJournal();
    }

    public IReadOnlyList<LocalCliRepairStatus> LatestRepairs()
    {
        lock (_sync)
        {
            return _latestRepairs.Values
                .OrderByDescending(item => item.RepairedAt)
                .ToArray();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProbeAllAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Local CLI self-heal probe failed unexpectedly");
            }

            try { await Task.Delay(ProbeInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task ProbeAllAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return;
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        if (string.IsNullOrWhiteSpace(appData)) return;
        var npmBin = Path.Combine(appData, "npm");

        foreach (var cliType in new[] { CliTypes.Claude, CliTypes.Codex })
        {
            var cli = _router.Get(cliType);
            var probe = cli.TestCliPath();
            if (probe.Available)
            {
                RecordHealthyVersion(cli, cliType, probe.Version, npmBin);
                continue;
            }

            var inspection = NpmShimRepairPolicy.Inspect(
                cliType,
                cli.GetCliPath(),
                npmBin,
                File.Exists,
                Directory.Exists);
            if (inspection.State != NpmShimInstallState.MissingShimWithPackagePresent) continue;

            var now = DateTimeOffset.UtcNow;
            DateTimeOffset? lastAttempt;
            string? cliVersionBefore;
            lock (_sync)
            {
                lastAttempt = _lastAttempts.GetValueOrDefault(cliType);
                cliVersionBefore = _lastKnownVersions.GetValueOrDefault(cliType);
                if (!NpmShimRepairPolicy.CanAttempt(lastAttempt, now)) continue;
                _lastAttempts[cliType] = now;
            }

            await RepairAsync(inspection, cli, cliVersionBefore, now, ct);
        }
    }

    private async Task RepairAsync(
        NpmShimInspection inspection,
        ICliExecutionService cli,
        string? cliVersionBefore,
        DateTimeOffset detectedAt,
        CancellationToken ct)
    {
        var before = CaptureRootCause(inspection.CliType, inspection.PackageDirectory);
        _logger.LogInformation(
            "cli-self-heal-detected cli={CliType} classification=missing-shim-package-present package={Package} packageVersion={PackageVersion} packageLastWriteAt={PackageLastWriteAt} autoUpdatePolicy={AutoUpdatePolicy} npmActivity={NpmActivity}",
            inspection.CliType,
            inspection.PackageName,
            before.PackageVersion ?? "unknown",
            before.PackageLastWriteAt,
            before.AutoUpdatePolicy,
            string.Join(" | ", before.RecentNpmActivity));
        AppendJournal(new CliRepairJournalEntry(
            detectedAt,
            inspection.CliType,
            inspection.PackageName,
            "attempted",
            cliVersionBefore,
            null,
            before.PackageVersion,
            null,
            before.PackageLastWriteAt,
            before.RecentNpmActivity,
            before.AutoUpdatePolicy,
            null,
            "Missing shim with package present; starting bounded npm global reinstall."));

        var install = await NpmShimHealer.InstallGlobalPackageAsync(
            inspection.PackageName,
            InstallTimeout,
            ct);
        var after = CaptureRootCause(inspection.CliType, inspection.PackageDirectory);
        var verify = cli.TestCliPath();
        var completedAt = DateTimeOffset.UtcNow;
        var repaired = install.ExitCode == 0 && verify.Available;
        var detail = repaired
            ? "Global npm install restored the missing CLI shim and the --version probe passed."
            : $"Global npm install exit={install.ExitCode}; --version available={verify.Available}; {Excerpt(install.Output)}";
        var entry = new CliRepairJournalEntry(
            completedAt,
            inspection.CliType,
            inspection.PackageName,
            repaired ? "repaired" : "repair-failed",
            cliVersionBefore,
            verify.Version,
            before.PackageVersion,
            after.PackageVersion,
            before.PackageLastWriteAt,
            before.RecentNpmActivity.Concat(after.RecentNpmActivity).Distinct(StringComparer.Ordinal).ToArray(),
            before.AutoUpdatePolicy,
            install.ExitCode,
            detail);
        AppendJournal(entry);

        if (repaired)
        {
            var status = new LocalCliRepairStatus(
                inspection.CliType,
                completedAt,
                cliVersionBefore,
                verify.Version,
                before.PackageVersion,
                after.PackageVersion);
            lock (_sync)
            {
                _latestRepairs[inspection.CliType] = status;
                _lastKnownVersions[inspection.CliType] = verify.Version;
            }
            _logger.LogInformation(
                "cli-self-heal-repaired cli={CliType} repairedAt={RepairedAt} cliVersionBefore={CliVersionBefore} cliVersionAfter={CliVersionAfter} packageVersionBefore={PackageVersionBefore} packageVersionAfter={PackageVersionAfter} journal={Journal}",
                inspection.CliType,
                completedAt,
                cliVersionBefore ?? "unknown",
                verify.Version ?? "unknown",
                before.PackageVersion ?? "unknown",
                after.PackageVersion ?? "unknown",
                _journalPath);
            return;
        }

        _logger.LogError(
            "cli-self-heal-failed cli={CliType} attemptedAt={AttemptedAt} package={Package} npmExitCode={ExitCode} detail={Detail} journal={Journal}",
            inspection.CliType,
            completedAt,
            inspection.PackageName,
            install.ExitCode,
            detail,
            _journalPath);
    }

    private void RecordHealthyVersion(
        ICliExecutionService cli,
        string cliType,
        string? version,
        string npmBin)
    {
        string? previous;
        lock (_sync)
        {
            previous = _lastKnownVersions.GetValueOrDefault(cliType);
            _lastKnownVersions[cliType] = version;
        }
        if (string.Equals(previous, version, StringComparison.Ordinal)) return;

        var inspection = NpmShimRepairPolicy.Inspect(
            cliType,
            cli.GetCliPath(),
            npmBin,
            File.Exists,
            Directory.Exists);
        if (inspection.PackageName.Length == 0) return;
        var snapshot = CaptureRootCause(cliType, inspection.PackageDirectory);
        AppendJournal(new CliRepairJournalEntry(
            DateTimeOffset.UtcNow,
            cliType,
            inspection.PackageName,
            "version-observed",
            previous,
            version,
            snapshot.PackageVersion,
            snapshot.PackageVersion,
            snapshot.PackageLastWriteAt,
            snapshot.RecentNpmActivity,
            snapshot.AutoUpdatePolicy,
            null,
            "Healthy local --version observation; retained as the pre-break version anchor."));
    }

    private static CliRepairRootCauseSnapshot CaptureRootCause(string cliType, string packageDirectory)
    {
        string? version = null;
        DateTimeOffset? lastWrite = null;
        try
        {
            var packageJson = Path.Combine(packageDirectory, "package.json");
            if (File.Exists(packageJson))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(packageJson));
                if (document.RootElement.TryGetProperty("version", out var value)) version = value.GetString();
                lastWrite = File.GetLastWriteTimeUtc(packageJson);
            }
            else if (Directory.Exists(packageDirectory))
            {
                lastWrite = Directory.GetLastWriteTimeUtc(packageDirectory);
            }
        }
        catch (Exception ex) { SilentCatch.Note(ex, "LocalCliSelfHealService: package root-cause capture"); }

        var activity = new List<string>();
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            var npmLogs = Path.Combine(localAppData, "npm-cache", "_logs");
            try
            {
                activity.AddRange(Directory.EnumerateFiles(npmLogs, "*.log")
                    .Select(path => new FileInfo(path))
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .Take(5)
                    .Select(NpmActivitySummary));
            }
            catch (Exception ex) { SilentCatch.Note(ex, "LocalCliSelfHealService: npm activity capture"); }
        }
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    var name = process.ProcessName;
                    if (!new[] { "npm", "node", "claude", "codex" }
                        .Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    try { activity.Add($"process:{name}:{process.Id}@{process.StartTime.ToUniversalTime():o}"); }
                    catch (Exception ex)
                    {
                        SilentCatch.Note(ex, "LocalCliSelfHealService: process start-time capture");
                        activity.Add($"process:{name}:{process.Id}@unknown");
                    }
                }
            }
        }
        catch (Exception ex) { SilentCatch.Note(ex, "LocalCliSelfHealService: installer process capture"); }
        return new CliRepairRootCauseSnapshot(version, lastWrite, activity, AutoUpdatePolicy(cliType));
    }

    private static string AutoUpdatePolicy(string cliType)
    {
        if (!string.Equals(cliType, CliTypes.Claude, StringComparison.OrdinalIgnoreCase))
            return "not-captured";
        var disabled = Environment.GetEnvironmentVariable("DISABLE_AUTOUPDATER");
        var setting = "unset";
        try
        {
            var settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude",
                "settings.json");
            if (File.Exists(settingsPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
                if (document.RootElement.TryGetProperty("autoUpdates", out var value))
                    setting = value.ToString();
            }
        }
        catch (Exception ex) { SilentCatch.Note(ex, "LocalCliSelfHealService: Claude auto-update policy capture"); }
        return $"DISABLE_AUTOUPDATER={(string.IsNullOrWhiteSpace(disabled) ? "unset" : disabled)};settings.autoUpdates={setting}";
    }

    private static string NpmActivitySummary(FileInfo file)
    {
        var safeMarkers = new[]
        {
            "verbose title ",
            "info using npm@",
            "info using node@",
            "verbose exit ",
            "verbose code ",
        };
        try
        {
            var signals = File.ReadLines(file.FullName)
                .Where(line => safeMarkers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                .Take(12)
                .Select(line => Excerpt(line))
                .ToArray();
            var suffix = signals.Length == 0 ? string.Empty : $":{string.Join(" | ", signals)}";
            return $"{file.Name}@{file.LastWriteTimeUtc:o}:{file.Length}{suffix}";
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "LocalCliSelfHealService: npm log summary capture");
            return $"{file.Name}@{file.LastWriteTimeUtc:o}:{file.Length}:unreadable";
        }
    }

    private void AppendJournal(CliRepairJournalEntry entry)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
            File.AppendAllText(_journalPath, JsonSerializer.Serialize(entry, Json) + Environment.NewLine);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not append CLI repair journal at {Journal}", _journalPath);
        }
    }

    private void LoadJournal()
    {
        if (!File.Exists(_journalPath)) return;
        try
        {
            foreach (var line in File.ReadLines(_journalPath))
            {
                CliRepairJournalEntry? entry;
                try { entry = JsonSerializer.Deserialize<CliRepairJournalEntry>(line, Json); }
                catch (Exception ex)
                {
                    SilentCatch.Note(ex, "LocalCliSelfHealService: skip malformed journal line");
                    continue;
                }
                if (entry is null) continue;
                if (entry.Outcome is "attempted" or "repaired" or "repair-failed")
                    _lastAttempts[entry.CliType] = entry.OccurredAt;
                if (entry.Outcome == "version-observed")
                {
                    _lastKnownVersions[entry.CliType] = entry.CliVersionAfter;
                    continue;
                }
                if (entry.Outcome != "repaired") continue;
                _latestRepairs[entry.CliType] = new LocalCliRepairStatus(
                    entry.CliType,
                    entry.OccurredAt,
                    entry.CliVersionBefore,
                    entry.CliVersionAfter,
                    entry.PackageVersionBefore,
                    entry.PackageVersionAfter);
                _lastKnownVersions[entry.CliType] = entry.CliVersionAfter;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read CLI repair journal at {Journal}", _journalPath);
        }
    }

    private static string Excerpt(string text)
    {
        var compact = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 500 ? compact : compact[..500] + "...";
    }
}
