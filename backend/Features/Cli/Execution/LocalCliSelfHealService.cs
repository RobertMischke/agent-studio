using System.Diagnostics;
using System.Text.Json;

namespace AgentStudio.Cli;

public delegate Task<LocalCliCommandResult> LocalCliCommandLauncher(
    string command,
    IReadOnlyList<string> arguments,
    TimeSpan timeout,
    CancellationToken ct);

/// <summary>
/// Repairs a missing global Claude or Codex npm shim only when the package is
/// still installed. Attempts are serialized and durably limited to one per CLI
/// per hour. Every classification and attempt is appended to a host-local JSONL
/// journal with package, version, shim, and nearby npm/update activity evidence.
/// </summary>
public sealed class LocalCliSelfHealService
{
    public static readonly TimeSpan RepairCooldown = TimeSpan.FromHours(1);
    private static readonly TimeSpan NpmQueryTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan NpmInstallTimeout = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ILogger<LocalCliSelfHealService> _logger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly LocalCliCommandLauncher _launcher;
    private readonly bool _isWindows;
    private readonly string _journalPath;
    private readonly SemaphoreSlim _repairLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly Dictionary<string, DateTimeOffset> _lastAttempts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lastHealthyVersions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LocalCliRepairStatus> _statuses =
        new(StringComparer.OrdinalIgnoreCase);

    public LocalCliSelfHealService(
        ILogger<LocalCliSelfHealService> logger,
        IConfiguration configuration)
        : this(
            logger,
            ResolveJournalPath(configuration),
            () => DateTimeOffset.UtcNow,
            RunCommandAsync,
            OperatingSystem.IsWindows())
    {
    }

    internal LocalCliSelfHealService(
        ILogger<LocalCliSelfHealService> logger,
        string journalPath,
        Func<DateTimeOffset> clock,
        LocalCliCommandLauncher launcher,
        bool isWindows)
    {
        _logger = logger;
        _journalPath = Path.GetFullPath(journalPath);
        _clock = clock;
        _launcher = launcher;
        _isWindows = isWindows;
        LoadJournal();
    }

    public string JournalPath => _journalPath;

    public IReadOnlyList<LocalCliRepairStatus> Snapshot()
    {
        lock (_stateLock)
            return _statuses.Values.OrderBy(status => status.CliType, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Probe one configured CLI and repair only the package-present shim case.
    /// A truly absent package and an explicit path override remain operator-owned.
    /// </summary>
    public async Task<LocalCliSelfHealOutcome> ProbeAndRepairAsync(
        string cliType,
        string configuredPath,
        Func<(bool Available, string? Version, string Path)> probe,
        CancellationToken ct)
    {
        var initial = probe();
        if (initial.Available)
        {
            RecordHealthyVersion(cliType, initial.Version);
            return LocalCliSelfHealOutcome.AvailableNow();
        }

        if (!_isWindows)
        {
            return new LocalCliSelfHealOutcome(
                Handled: false,
                Available: false,
                Repaired: false,
                Throttled: false,
                Error: "Global npm shim self-heal is enabled only on Windows hosts.",
                Inspection: null);
        }

        var layout = await ResolveNpmLayoutAsync(ct);
        var inspection = NpmCliShimInspectionPolicy.Inspect(
            cliType,
            configuredPath,
            commandAvailable: false,
            layout.Prefix,
            layout.Root);
        if (inspection.State != NpmCliInstallState.MissingOrBrokenShimWithPackagePresent)
        {
            if (inspection.State == NpmCliInstallState.TrulyUninstalled)
            {
                _logger.LogInformation(
                    "local-cli-probe cli={Cli} classification=truly-uninstalled detail={Detail}",
                    cliType,
                    inspection.Detail);
            }
            return new LocalCliSelfHealOutcome(
                Handled: false,
                Available: false,
                Repaired: false,
                Throttled: false,
                Error: inspection.Detail,
                Inspection: inspection);
        }

        await _repairLock.WaitAsync(ct);
        try
        {
            var racedProbe = probe();
            if (racedProbe.Available)
            {
                RecordHealthyVersion(cliType, racedProbe.Version);
                return LocalCliSelfHealOutcome.AvailableNow();
            }

            var now = _clock();
            DateTimeOffset? lastAttempt;
            lock (_stateLock) lastAttempt = _lastAttempts.GetValueOrDefault(cliType);
            if (lastAttempt is not null && now - lastAttempt < RepairCooldown)
            {
                var next = lastAttempt.Value + RepairCooldown;
                var cooldownDetail = $"The missing {cliType} shim is still inside the one-hour repair cooldown. Next attempt after {next:o}.";
                return new LocalCliSelfHealOutcome(true, false, false, true, cooldownDetail, inspection);
            }

            var versionBefore = LastHealthyVersion(cliType);
            var activityBefore = CollectActivity(inspection, now);
            lock (_stateLock) _lastAttempts[cliType] = now;
            Append(new LocalCliRepairJournalEvent(
                now,
                cliType,
                "repair-attempt",
                "missing-shim-with-package-present",
                inspection.PackageName,
                configuredPath,
                inspection.NpmPrefix,
                inspection.NpmRoot,
                inspection.PackagePath,
                versionBefore,
                inspection.PackageVersion,
                null,
                null,
                inspection.PackageModifiedAt,
                null,
                activityBefore,
                [],
                null,
                null,
                inspection.Detail));

            _logger.LogWarning(
                "local-cli-repair-started cli={Cli} package={Package} versionBefore={VersionBefore} packageVersionBefore={PackageVersion} journal={Journal}",
                cliType,
                inspection.PackageName,
                versionBefore ?? "unknown",
                inspection.PackageVersion ?? "unknown",
                _journalPath);
            var install = await _launcher(
                "npm",
                ["install", "-g", inspection.PackageName, "--no-audit", "--no-fund"],
                NpmInstallTimeout,
                ct);
            var verified = probe();
            var afterInspection = NpmCliShimInspectionPolicy.Inspect(
                cliType,
                configuredPath,
                verified.Available,
                layout.Prefix,
                layout.Root);
            var completedAt = _clock();
            var activityAfter = CollectActivity(afterInspection, completedAt);
            var succeeded = install.ExitCode == 0 && verified.Available;
            var eventName = succeeded ? "repair-succeeded" : "repair-failed";
            var detail = succeeded
                ? $"CLI repaired at {completedAt:O}. {cliType} {verified.Version ?? afterInspection.PackageVersion ?? "unknown version"} is available."
                : $"CLI repair failed at {completedAt:O}: npm exit {install.ExitCode?.ToString() ?? "unavailable"}; "
                  + (verified.Available ? "the command probe recovered unexpectedly." : "the command probe is still unavailable.")
                  + (string.IsNullOrWhiteSpace(install.Error) ? "" : $" {install.Error}");
            var journalEvent = new LocalCliRepairJournalEvent(
                completedAt,
                cliType,
                eventName,
                "missing-shim-with-package-present",
                inspection.PackageName,
                configuredPath,
                inspection.NpmPrefix,
                inspection.NpmRoot,
                inspection.PackagePath,
                versionBefore,
                inspection.PackageVersion,
                verified.Version,
                afterInspection.PackageVersion,
                inspection.PackageModifiedAt,
                afterInspection.PackageModifiedAt,
                activityBefore,
                activityAfter,
                install.ExitCode,
                Tail(install.Output),
                detail);
            Append(journalEvent);
            var status = StatusFrom(journalEvent, succeeded ? "repaired" : "failed");
            lock (_stateLock)
            {
                _statuses[cliType] = status;
                if (succeeded && !string.IsNullOrWhiteSpace(verified.Version))
                    _lastHealthyVersions[cliType] = verified.Version!;
            }

            if (succeeded)
            {
                _logger.LogInformation(
                    "local-cli-repair-succeeded cli={Cli} versionBefore={VersionBefore} versionAfter={VersionAfter} packageVersionBefore={PackageBefore} packageVersionAfter={PackageAfter} journal={Journal}",
                    cliType,
                    versionBefore ?? "unknown",
                    verified.Version ?? "unknown",
                    inspection.PackageVersion ?? "unknown",
                    afterInspection.PackageVersion ?? "unknown",
                    _journalPath);
                return new LocalCliSelfHealOutcome(true, true, true, false, null, afterInspection);
            }

            _logger.LogError(
                "local-cli-repair-failed cli={Cli} npmExit={ExitCode} detail={Detail} journal={Journal}",
                cliType,
                install.ExitCode,
                detail,
                _journalPath);
            return new LocalCliSelfHealOutcome(true, false, false, false, detail, afterInspection);
        }
        finally
        {
            _repairLock.Release();
        }
    }

    private async Task<(string Prefix, string Root)> ResolveNpmLayoutAsync(CancellationToken ct)
    {
        var prefixResult = await _launcher("npm", ["prefix", "-g"], NpmQueryTimeout, ct);
        var rootResult = await _launcher("npm", ["root", "-g"], NpmQueryTimeout, ct);
        var prefix = prefixResult.ExitCode == 0 ? LastLine(prefixResult.Output) : null;
        if (string.IsNullOrWhiteSpace(prefix))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            prefix = Path.Combine(appData, "npm");
        }
        var root = rootResult.ExitCode == 0 ? LastLine(rootResult.Output) : null;
        if (string.IsNullOrWhiteSpace(root)) root = Path.Combine(prefix, "node_modules");
        return (Path.GetFullPath(prefix), Path.GetFullPath(root));
    }

    private void RecordHealthyVersion(string cliType, string? version)
    {
        var recoveredAfterFailure = false;
        var versionChanged = false;
        lock (_stateLock)
        {
            if (_statuses.TryGetValue(cliType, out var status) && status.State == "failed")
            {
                _statuses.Remove(cliType);
                recoveredAfterFailure = true;
            }
            if (!string.IsNullOrWhiteSpace(version)
                && (!_lastHealthyVersions.TryGetValue(cliType, out var previous)
                    || !string.Equals(previous, version, StringComparison.Ordinal)))
            {
                _lastHealthyVersions[cliType] = version;
                versionChanged = true;
            }
        }
        if (!recoveredAfterFailure && !versionChanged) return;
        var eventName = recoveredAfterFailure ? "capability-restored" : "healthy-version";
        Append(new LocalCliRepairJournalEvent(
            _clock(), cliType, eventName, "available", "", "", null, null, null,
            version, null, version, null, null, null, [], [], null, null,
            recoveredAfterFailure
                ? $"Observed {cliType} available after a failed automatic repair. The active alarm was cleared."
                : $"Observed healthy {cliType} CLI version {version}."));
    }

    private string? LastHealthyVersion(string cliType)
    {
        lock (_stateLock) return _lastHealthyVersions.GetValueOrDefault(cliType);
    }

    private IReadOnlyList<LocalCliActivityEvidence> CollectActivity(
        NpmCliShimInspection inspection,
        DateTimeOffset observedAt)
    {
        var evidence = new List<LocalCliActivityEvidence>();
        void Add(string kind, string path, bool includeMissing = false)
        {
            try
            {
                var exists = File.Exists(path) || Directory.Exists(path);
                if (!exists && !includeMissing) return;
                var modified = exists ? File.GetLastWriteTimeUtc(path) : (DateTime?)null;
                evidence.Add(new LocalCliActivityEvidence(kind, path, exists, modified, null));
            }
            catch (Exception ex)
            {
                SilentCatch.Note(ex, "LocalCliSelfHealService activity evidence");
            }
        }

        if (!string.IsNullOrWhiteSpace(inspection.PackagePath))
        {
            Add("npm-package", inspection.PackagePath!);
            Add("npm-package-json", Path.Combine(inspection.PackagePath!, "package.json"));
        }
        foreach (var shim in inspection.ExpectedShims) Add("npm-shim", shim, includeMissing: true);

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var cache = Environment.GetEnvironmentVariable("NPM_CONFIG_CACHE")
                    ?? Path.Combine(local, "npm-cache");
        AddRecentFiles(evidence, "npm-log", Path.Combine(cache, "_logs"), "*.log", observedAt, 12, summarizeNpmLog: true);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cliState = inspection.CliType.Equals(CliTypes.Claude, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(home, ".claude")
            : Path.Combine(home, ".codex");
        AddRecentFiles(evidence, "cli-update-activity", cliState, "*update*", observedAt, 8);
        AddRecentFiles(evidence, "cli-debug-log", Path.Combine(cliState, "debug"), "*.txt", observedAt, 8);
        AddRecentFiles(evidence, "cli-log", Path.Combine(cliState, "log"), "*.log", observedAt, 8);
        return evidence
            .OrderByDescending(item => item.ModifiedAt)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .ToArray();
    }

    private static void AddRecentFiles(
        ICollection<LocalCliActivityEvidence> target,
        string kind,
        string directory,
        string pattern,
        DateTimeOffset observedAt,
        int limit,
        bool summarizeNpmLog = false)
    {
        if (!Directory.Exists(directory)) return;
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MaxRecursionDepth = 4,
            };
            foreach (var file in Directory.EnumerateFiles(directory, pattern, options)
                         .Select(path => new FileInfo(path))
                         .Where(file => file.LastWriteTimeUtc >= observedAt.UtcDateTime - TimeSpan.FromDays(7)
                                        && file.LastWriteTimeUtc <= observedAt.UtcDateTime + TimeSpan.FromMinutes(5))
                         .OrderByDescending(file => file.LastWriteTimeUtc)
                         .Take(limit))
            {
                target.Add(new LocalCliActivityEvidence(
                    kind,
                    file.FullName,
                    true,
                    file.LastWriteTimeUtc,
                    summarizeNpmLog ? ReadNpmActivitySummary(file.FullName) : null));
            }
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "LocalCliSelfHealService recent activity enumeration");
        }
    }

    private static string? ReadNpmActivitySummary(string path)
    {
        try
        {
            var lines = File.ReadLines(path)
                .Select(line => line.Trim())
                .Where(line => line.Contains(" verbose title ", StringComparison.Ordinal)
                               || line.Contains(" info using npm@", StringComparison.Ordinal)
                               || line.Contains(" info using node@", StringComparison.Ordinal)
                               || line.Contains(" verbose exit ", StringComparison.Ordinal)
                               || line.Contains(" verbose code ", StringComparison.Ordinal))
                .TakeLast(8)
                .ToArray();
            if (lines.Length == 0) return null;
            var summary = string.Join(" | ", lines);
            return summary.Length <= 1000 ? summary : summary[^1000..];
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "LocalCliSelfHealService npm activity summary");
            return null;
        }
    }

    private void Append(LocalCliRepairJournalEvent entry)
    {
        try
        {
            var directory = Path.GetDirectoryName(_journalPath)
                            ?? throw new InvalidOperationException("CLI repair journal has no parent directory.");
            Directory.CreateDirectory(directory);
            lock (_stateLock)
                File.AppendAllText(_journalPath, JsonSerializer.Serialize(entry, Json) + Environment.NewLine);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "local-cli-repair-journal-write-failed path={Path}", _journalPath);
        }
    }

    private void LoadJournal()
    {
        if (!File.Exists(_journalPath)) return;
        try
        {
            foreach (var line in File.ReadLines(_journalPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                LocalCliRepairJournalEvent? entry;
                try { entry = JsonSerializer.Deserialize<LocalCliRepairJournalEvent>(line, Json); }
                catch (JsonException) { continue; }
                if (entry is null || string.IsNullOrWhiteSpace(entry.CliType)) continue;
                if (entry.Event == "repair-attempt") _lastAttempts[entry.CliType] = entry.OccurredAt;
                if (entry.Event is "healthy-version" or "capability-restored"
                    && !string.IsNullOrWhiteSpace(entry.CliVersionAfter))
                    _lastHealthyVersions[entry.CliType] = entry.CliVersionAfter!;
                if (entry.Event is "repair-succeeded" or "repair-failed")
                    _statuses[entry.CliType] = StatusFrom(entry, entry.Event == "repair-succeeded" ? "repaired" : "failed");
                if (entry.Event == "capability-restored") _statuses.Remove(entry.CliType);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "local-cli-repair-journal-read-failed path={Path}", _journalPath);
        }
    }

    private LocalCliRepairStatus StatusFrom(LocalCliRepairJournalEvent entry, string state)
        => new(
            entry.CliType,
            state,
            entry.OccurredAt,
            entry.CliVersionBefore,
            entry.CliVersionAfter,
            entry.PackageVersionBefore,
            entry.PackageVersionAfter,
            entry.Detail);

    private static string ResolveJournalPath(IConfiguration configuration)
    {
        var configured = configuration["CliRepair:JournalPath"];
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) local = Path.GetTempPath();
        return Path.Combine(local, "agent-taskboard", "cli-repair-journal.jsonl");
    }

    private static string? LastLine(string? text)
        => text?.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .LastOrDefault(line => line.Length > 0);

    private static string? Tail(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        return trimmed.Length <= 4000 ? trimmed : trimmed[^4000..];
    }

    private static async Task<LocalCliCommandResult> RunCommandAsync(
        string command,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var start = new ProcessStartInfo
        {
            FileName = command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (OperatingSystem.IsWindows() && command.Equals("npm", StringComparison.OrdinalIgnoreCase))
        {
            var npm = ResolveOnPath("npm.cmd") ?? "npm.cmd";
            start.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            start.ArgumentList.Clear();
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/s");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add($"\"{npm}\" {string.Join(' ', arguments.Select(QuoteWindowsArgument))}");
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null) return new LocalCliCommandResult(null, null, "Process.Start returned null.");
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(timeout);
            try { await process.WaitForExitAsync(bounded.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) { SilentCatch.Note(ex, "LocalCliSelfHealService timed-out process cleanup"); }
                return new LocalCliCommandResult(null, await stdout, $"Command timed out after {timeout}.");
            }
            var output = string.Join(Environment.NewLine, new[] { await stdout, await stderr }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            return new LocalCliCommandResult(process.ExitCode, output, process.ExitCode == 0 ? null : Tail(output));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new LocalCliCommandResult(null, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string? ResolveOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, fileName))
            .FirstOrDefault(File.Exists);
    }

    private static string QuoteWindowsArgument(string value)
        => value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"")}\"" : value;
}

public sealed record LocalCliCommandResult(int? ExitCode, string? Output, string? Error);

public sealed record LocalCliSelfHealOutcome(
    bool Handled,
    bool Available,
    bool Repaired,
    bool Throttled,
    string? Error,
    NpmCliShimInspection? Inspection)
{
    public static LocalCliSelfHealOutcome AvailableNow()
        => new(false, true, false, false, null, null);
}

public sealed record LocalCliRepairStatus(
    string CliType,
    string State,
    DateTimeOffset OccurredAt,
    string? CliVersionBefore,
    string? CliVersionAfter,
    string? PackageVersionBefore,
    string? PackageVersionAfter,
    string Detail);

public sealed record LocalCliActivityEvidence(
    string Kind,
    string Path,
    bool Exists,
    DateTimeOffset? ModifiedAt,
    string? Summary);

public sealed record LocalCliRepairJournalEvent(
    DateTimeOffset OccurredAt,
    string CliType,
    string Event,
    string Classification,
    string PackageName,
    string ConfiguredPath,
    string? NpmPrefix,
    string? NpmRoot,
    string? PackagePath,
    string? CliVersionBefore,
    string? PackageVersionBefore,
    string? CliVersionAfter,
    string? PackageVersionAfter,
    DateTimeOffset? PackageModifiedAtBefore,
    DateTimeOffset? PackageModifiedAtAfter,
    IReadOnlyList<LocalCliActivityEvidence> ActivityBefore,
    IReadOnlyList<LocalCliActivityEvidence> ActivityAfter,
    int? NpmExitCode,
    string? NpmOutputTail,
    string Detail);
