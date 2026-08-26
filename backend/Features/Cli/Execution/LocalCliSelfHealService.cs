using System.Text.Json;

namespace AgentStudio.Cli;

public enum LocalCliInstallState
{
    Ready,
    TrulyUninstalled,
    MissingShimWithPackagePresent,
    Unavailable,
}

public sealed record LocalCliInstallInspection(
    LocalCliInstallState State,
    string CliType,
    string ConfiguredPath,
    string PackageName,
    string PackagePath,
    string ExpectedShimPath,
    bool PackagePresent,
    bool ExpectedShimPresent,
    string? PackageVersion);

/// <summary>
/// Pure classification for a failed local CLI probe. The repair side effect is
/// allowed only when a global npm package is still present but its platform
/// shim is gone. A custom executable path and a present-but-broken shim never
/// enter the automatic repair path.
/// </summary>
public static class LocalCliSelfHealPolicy
{
    public static bool RepairAttemptAllowed(
        DateTimeOffset? lastAttempt,
        DateTimeOffset observedAt,
        TimeSpan attemptWindow)
        => lastAttempt is null || observedAt - lastAttempt.Value >= attemptWindow;

    public static LocalCliInstallInspection Inspect(
        string cliType,
        string configuredPath,
        string packageName,
        string npmBinPath,
        bool isWindows)
    {
        var packagePath = Path.Combine(
            npmBinPath,
            "node_modules",
            packageName.Replace('/', Path.DirectorySeparatorChar));
        var expectedShim = Path.Combine(npmBinPath, cliType + (isWindows ? ".cmd" : string.Empty));
        var packagePresent = Directory.Exists(packagePath)
                             && File.Exists(Path.Combine(packagePath, "package.json"));
        var shimPresent = File.Exists(expectedShim);
        var isBareGlobalReference = !Path.IsPathRooted(configuredPath)
                                    && string.Equals(
                                        Path.GetFileNameWithoutExtension(configuredPath),
                                        cliType,
                                        StringComparison.OrdinalIgnoreCase);

        var state = !packagePresent
            ? LocalCliInstallState.TrulyUninstalled
            : isBareGlobalReference && !shimPresent
                ? LocalCliInstallState.MissingShimWithPackagePresent
                : LocalCliInstallState.Unavailable;

        return new LocalCliInstallInspection(
            state,
            cliType,
            configuredPath,
            packageName,
            packagePath,
            expectedShim,
            packagePresent,
            shimPresent,
            NpmShimHealer.TryReadPackageVersion(packagePath));
    }
}

public sealed record LocalCliCapability(
    string CliType,
    string Status,
    string InstallState,
    string ConfiguredPath,
    string? ResolvedPath,
    string? Version,
    string Detail,
    DateTimeOffset ObservedAt);

public sealed record LocalCliRepairEvent(
    string CliType,
    string Outcome,
    DateTimeOffset OccurredAt,
    string? VersionBefore,
    string? VersionAfter,
    string Detail);

public sealed record LocalCliCapabilitySnapshot(
    DateTimeOffset ObservedAt,
    IReadOnlyList<LocalCliCapability> Capabilities,
    LocalCliRepairEvent? LatestRepair,
    bool RepairAlarm);

public sealed record LocalCliRepairJournalEntry(
    DateTimeOffset Timestamp,
    string CliType,
    string PackageName,
    string Outcome,
    string Trigger,
    string ConfiguredPath,
    string ExpectedShimPath,
    string? VersionBefore,
    string? VersionAfter,
    int? NpmExitCode,
    string Detail,
    IReadOnlyList<NpmActivityEvidence> NpmActivity);

/// <summary>
/// Periodically probes the monolith's local Claude and Codex binaries. A
/// missing Windows npm shim is repaired by reinstalling the package at most
/// once per CLI per hour. Every attempt is journaled before its result is
/// exposed to the local host and status-bar projections.
/// </summary>
public sealed class LocalCliSelfHealService : BackgroundService
{
    public static readonly TimeSpan RepairAttemptWindow = TimeSpan.FromHours(1);
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromMinutes(1);

    private static readonly IReadOnlyDictionary<string, string> Packages =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [CliTypes.Claude] = "@anthropic-ai/claude-code",
            [CliTypes.Codex] = "@openai/codex",
        };

    private readonly CliRouter _router;
    private readonly ILogger<LocalCliSelfHealService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly string _journalPath;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private static readonly JsonSerializerOptions JournalJson = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, DateTimeOffset> _lastAttempts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LocalCliRepairEvent> _repairEventsByCli =
        new(StringComparer.OrdinalIgnoreCase);
    private LocalCliCapabilitySnapshot _snapshot;

    public LocalCliSelfHealService(
        CliRouter router,
        IConfiguration configuration,
        ILogger<LocalCliSelfHealService> logger,
        TimeProvider? timeProvider = null)
    {
        _router = router;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        var taskRepository = configuration["TaskRepository"]
                             ?? Path.Combine(AppContext.BaseDirectory, "workspace");
        _journalPath = Path.Combine(taskRepository, "logs", "local-cli-repairs.jsonl");
        RestoreJournalState();
        _snapshot = new LocalCliCapabilitySnapshot(
            _timeProvider.GetUtcNow(),
            Array.Empty<LocalCliCapability>(),
            LatestSuccessfulRepair(),
            false);
    }

    public LocalCliCapabilitySnapshot Current => Volatile.Read(ref _snapshot);

    public async Task<LocalCliCapabilitySnapshot> RefreshAsync(CancellationToken ct = default)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
            var capabilities = new List<LocalCliCapability>();
            foreach (var (cliType, packageName) in Packages)
            {
                var capability = await ProbeOneAsync(cliType, packageName, ct);
                capabilities.Add(capability);
            }

            var activeFailure = capabilities
                .Where(capability => capability.Status != "ready")
                .Select(capability => _repairEventsByCli.GetValueOrDefault(capability.CliType))
                .Where(repair => repair?.Outcome == "failed")
                .OrderByDescending(repair => repair!.OccurredAt)
                .FirstOrDefault();
            var repairAlarm = activeFailure is not null;

            var next = new LocalCliCapabilitySnapshot(
                _timeProvider.GetUtcNow(),
                capabilities,
                activeFailure ?? LatestSuccessfulRepair(),
                repairAlarm);
            Volatile.Write(ref _snapshot, next);
            return next;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "local-cli-capability-probe-failed");
            }

            try
            {
                await Task.Delay(ProbeInterval, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<LocalCliCapability> ProbeOneAsync(
        string cliType,
        string packageName,
        CancellationToken ct)
    {
        var observedAt = _timeProvider.GetUtcNow();
        var cli = _router.Get(cliType);
        var probe = cli.TestCliPath();
        if (probe.Available)
        {
            return new LocalCliCapability(
                cliType,
                "ready",
                LocalCliInstallState.Ready.ToString(),
                cli.GetCliPath(),
                probe.Path,
                probe.Version,
                $"{cliType} CLI is available.",
                observedAt);
        }

        if (!OperatingSystem.IsWindows())
        {
            return Unavailable(
                cliType,
                cli.GetCliPath(),
                probe.Path,
                LocalCliInstallState.Unavailable,
                "Automatic npm-shim repair applies only to the Windows local host.",
                observedAt);
        }

        var appData = Environment.GetEnvironmentVariable("APPDATA");
        if (string.IsNullOrWhiteSpace(appData))
        {
            return Unavailable(
                cliType,
                cli.GetCliPath(),
                probe.Path,
                LocalCliInstallState.Unavailable,
                "APPDATA is unavailable, so the global npm install cannot be inspected.",
                observedAt);
        }

        var npmBin = Path.Combine(appData, "npm");
        var inspection = LocalCliSelfHealPolicy.Inspect(
            cliType,
            cli.GetCliPath(),
            packageName,
            npmBin,
            isWindows: true);
        if (inspection.State != LocalCliInstallState.MissingShimWithPackagePresent)
        {
            var unavailableDetail = inspection.State == LocalCliInstallState.TrulyUninstalled
                ? $"{packageName} is not installed in the global npm package root."
                : $"{cliType} is unavailable, but the missing-global-shim repair condition was not met.";
            return Unavailable(
                cliType,
                inspection.ConfiguredPath,
                probe.Path,
                inspection.State,
                unavailableDetail,
                observedAt,
                inspection.PackageVersion);
        }

        _lastAttempts.TryGetValue(cliType, out var attemptedAt);
        if (!LocalCliSelfHealPolicy.RepairAttemptAllowed(
                _lastAttempts.ContainsKey(cliType) ? attemptedAt : null,
                observedAt,
                RepairAttemptWindow))
        {
            var retryAt = attemptedAt + RepairAttemptWindow;
            return Unavailable(
                cliType,
                inspection.ConfiguredPath,
                probe.Path,
                inspection.State,
                $"Global npm package is present but its shim is missing. Repair is rate-limited until {retryAt:O}.",
                observedAt,
                inspection.PackageVersion);
        }

        _lastAttempts[cliType] = observedAt;
        var npmActivity = NpmShimHealer.CaptureRecentNpmActivity(
            inspection.PackageName,
            cliType,
            observedAt,
            "before-repair");
        var started = new LocalCliRepairJournalEntry(
            observedAt,
            cliType,
            inspection.PackageName,
            "attempting",
            "missing-shim-with-package-present",
            inspection.ConfiguredPath,
            inspection.ExpectedShimPath,
            inspection.PackageVersion,
            null,
            null,
            "Repair attempt started after a missing npm shim was detected.",
            npmActivity);
        if (!await AppendJournalAsync(started, ct))
        {
            var journalFailure = new LocalCliRepairEvent(
                cliType,
                "failed",
                observedAt,
                inspection.PackageVersion,
                null,
                "CLI repair was not started because its rate-limit journal could not be written.");
            _repairEventsByCli[cliType] = journalFailure;
            return new LocalCliCapability(
                cliType,
                "repair-failed",
                inspection.State.ToString(),
                inspection.ConfiguredPath,
                inspection.ExpectedShimPath,
                inspection.PackageVersion,
                journalFailure.Detail,
                observedAt);
        }

        var install = await NpmShimHealer.ReinstallGlobalPackageAsync(
            inspection.PackageName,
            _logger,
            ct);
        var verify = cli.TestCliPath(inspection.ExpectedShimPath);
        var completedAt = _timeProvider.GetUtcNow();
        var afterActivity = NpmShimHealer.CaptureRecentNpmActivity(
            inspection.PackageName,
            cliType,
            completedAt,
            "after-repair");
        var allActivity = npmActivity.Concat(afterActivity).ToArray();
        var outcome = install.ExitCode == 0 && verify.Available ? "repaired" : "failed";
        var detail = outcome == "repaired"
            ? $"{cliType} CLI repaired at {completedAt:O}."
            : $"npm reinstall did not restore a working {cliType} shim: {install.Error ?? "verification failed"}";
        var entry = new LocalCliRepairJournalEntry(
            completedAt,
            cliType,
            inspection.PackageName,
            outcome,
            "missing-shim-with-package-present",
            inspection.ConfiguredPath,
            inspection.ExpectedShimPath,
            inspection.PackageVersion,
            verify.Version ?? NpmShimHealer.TryReadPackageVersion(inspection.PackagePath),
            install.ExitCode,
            detail,
            allActivity);
        await AppendJournalAsync(entry, ct);
        _repairEventsByCli[cliType] = new LocalCliRepairEvent(
            cliType,
            outcome,
            completedAt,
            entry.VersionBefore,
            entry.VersionAfter,
            detail);

        if (outcome == "repaired")
        {
            _logger.LogInformation(
                "local-cli-repaired cliType={CliType} occurredAt={OccurredAt} versionBefore={VersionBefore} versionAfter={VersionAfter} npmEvidenceCount={NpmEvidenceCount}",
                cliType,
                completedAt,
                entry.VersionBefore,
                entry.VersionAfter,
                allActivity.Length);
            return new LocalCliCapability(
                cliType,
                "ready",
                LocalCliInstallState.Ready.ToString(),
                inspection.ConfiguredPath,
                inspection.ExpectedShimPath,
                entry.VersionAfter,
                detail,
                completedAt);
        }

        _logger.LogError(
            "local-cli-repair-failed cliType={CliType} occurredAt={OccurredAt} versionBefore={VersionBefore} npmExitCode={NpmExitCode} detail={Detail}",
            cliType,
            completedAt,
            entry.VersionBefore,
            entry.NpmExitCode,
            detail);
        return new LocalCliCapability(
            cliType,
            "repair-failed",
            inspection.State.ToString(),
            inspection.ConfiguredPath,
            inspection.ExpectedShimPath,
            entry.VersionBefore,
            detail,
            completedAt);
    }

    private static LocalCliCapability Unavailable(
        string cliType,
        string configuredPath,
        string? resolvedPath,
        LocalCliInstallState state,
        string detail,
        DateTimeOffset observedAt,
        string? version = null)
        => new(
            cliType,
            "unavailable",
            state.ToString(),
            configuredPath,
            resolvedPath,
            version,
            detail,
            observedAt);

    private async Task<bool> AppendJournalAsync(LocalCliRepairJournalEntry entry, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
            var line = JsonSerializer.Serialize(entry, JournalJson) + Environment.NewLine;
            await File.AppendAllTextAsync(_journalPath, line, ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "local-cli-repair-journal-write-failed path={Path}", _journalPath);
            return false;
        }
    }

    private void RestoreJournalState()
    {
        if (!File.Exists(_journalPath)) return;
        try
        {
            foreach (var line in File.ReadLines(_journalPath).TakeLast(100))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var entry = JsonSerializer.Deserialize<LocalCliRepairJournalEntry>(line, JournalJson);
                if (entry is null) continue;
                _lastAttempts[entry.CliType] = entry.Timestamp;
                _repairEventsByCli[entry.CliType] = new LocalCliRepairEvent(
                    entry.CliType,
                    entry.Outcome == "attempting" ? "failed" : entry.Outcome,
                    entry.Timestamp,
                    entry.VersionBefore,
                    entry.VersionAfter,
                    entry.Outcome == "attempting"
                        ? "The previous repair attempt did not record a terminal result."
                        : entry.Detail);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "local-cli-repair-journal-read-failed path={Path}", _journalPath);
        }
    }

    private LocalCliRepairEvent? LatestSuccessfulRepair()
        => _repairEventsByCli.Values
            .Where(repair => repair.Outcome == "repaired")
            .OrderByDescending(repair => repair.OccurredAt)
            .FirstOrDefault();
}
