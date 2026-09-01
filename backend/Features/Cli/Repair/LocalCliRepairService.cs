using System.Text.Json;
using AgentStudio.Diagnostics;
using AgentStudio.Shared;

namespace AgentStudio.Cli;

public enum NpmCliInstallState
{
    Unsupported,
    TrulyUninstalled,
    PackagePresentWithShim,
    MissingShimWithPackagePresent,
}

public sealed record NpmCliInstallInspection(
    NpmCliInstallState State,
    string CliType,
    string PackageName,
    string PackageDirectory,
    string? PackageVersion,
    DateTimeOffset? PackageModifiedAt,
    string RequiredCommandShim,
    IReadOnlyList<string> ExpectedShims);

internal sealed record NpmCliRepairPlan(
    NpmGlobalInstallMode InstallMode,
    string Detection,
    string PackageState,
    string RepairAction);

/// <summary>
/// Detects and repairs Windows global-npm failures where the configured package
/// is absent or its required command shim disappears. Custom CLI paths and
/// present-but-broken command shims remain outside this bounded repair.
/// </summary>
public sealed class LocalCliRepairService
{
    public static readonly TimeSpan AttemptWindow = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions JournalJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly NpmGlobalInstaller _installer;
    private readonly ILogger<LocalCliRepairService> _logger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<bool> _isWindows;
    private readonly Func<string?> _appData;
    private readonly Func<string?> _localAppData;
    private readonly string _journalPath;
    private readonly object _sync = new();
    private readonly HashSet<string> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LocalCliRepairStatus> _latest =
        new(StringComparer.OrdinalIgnoreCase);

    public LocalCliRepairService(
        NpmGlobalInstaller installer,
        IConfiguration configuration,
        ILogger<LocalCliRepairService> logger)
        : this(
            installer,
            logger,
            () => DateTimeOffset.UtcNow,
            OperatingSystem.IsWindows,
            () => Environment.GetEnvironmentVariable("APPDATA"),
            () => Environment.GetEnvironmentVariable("LOCALAPPDATA"),
            ResolveJournalPath(configuration))
    {
    }

    internal LocalCliRepairService(
        NpmGlobalInstaller installer,
        ILogger<LocalCliRepairService> logger,
        Func<DateTimeOffset> clock,
        Func<bool> isWindows,
        Func<string?> appData,
        Func<string?> localAppData,
        string journalPath)
    {
        _installer = installer;
        _logger = logger;
        _clock = clock;
        _isWindows = isWindows;
        _appData = appData;
        _localAppData = localAppData;
        _journalPath = journalPath;
        foreach (var entry in ReadJournal())
        {
            if (entry.Outcome == "failed")
                _latest[entry.CliType] = ToStatus(entry);
            else if (entry.Outcome is "repaired" or "healthy")
                _latest.Remove(entry.CliType);
        }
    }

    public IReadOnlyList<LocalCliRepairStatus> Current()
    {
        lock (_sync)
        {
            return _latest.Values
                .OrderByDescending(item => item.OccurredAt)
                .ToArray();
        }
    }

    /// <summary>
    /// Runs the ordinary availability probe and performs one bounded global
    /// npm install or relink for a recognized package/shim state. The returned
    /// probe is always the final observed state.
    /// </summary>
    public async Task<(bool Available, string? Version, string Path)> ProbeAndRepairAsync(
        string cliType,
        string? lastObservedVersion,
        Func<(bool Available, string? Version, string Path)> probe,
        CancellationToken ct)
    {
        var before = probe();
        if (before.Available)
        {
            ReconcileHealthy(cliType, before);
            return before;
        }
        if (!_isWindows()) return before;

        var appData = _appData();
        if (string.IsNullOrWhiteSpace(appData)) return before;
        var npmBin = Path.Combine(appData, "npm");
        var inspection = Inspect(cliType, before.Path, npmBin);
        var repairPlan = SelectRepairPlan(inspection.State);
        if (repairPlan is null)
        {
            _logger.LogDebug(
                "Local CLI probe unavailable cli={Cli} installState={InstallState}; no automatic repair",
                cliType,
                inspection.State);
            return before;
        }
        var shimStateBefore = File.Exists(inspection.RequiredCommandShim) ? "present" : "absent";

        var detectedAt = _clock();
        if (!TryBeginAttempt(cliType, detectedAt)) return before;

        try
        {
            var evidence = CaptureNpmActivity(inspection.PackageName, detectedAt);
            var beforeVersion = lastObservedVersion ?? inspection.PackageVersion;
            AppendJournal(new LocalCliRepairJournalEntry(
                detectedAt,
                cliType,
                "attempting",
                repairPlan.Detection,
                inspection.PackageName,
                inspection.PackageDirectory,
                inspection.PackageModifiedAt,
                before.Path,
                inspection.ExpectedShims,
                beforeVersion,
                null,
                null,
                $"Starting bounded {cliType} CLI repair: package {repairPlan.PackageState}, command shim {shimStateBefore}, npm action {repairPlan.RepairAction}.",
                "",
                "",
                evidence));
            _logger.LogInformation(
                "Starting bounded local CLI repair cli={Cli} package={Package} packageVersion={Version} packageState={PackageState} shimStateBefore={ShimStateBefore} repairAction={RepairAction}",
                cliType,
                inspection.PackageName,
                inspection.PackageVersion ?? lastObservedVersion ?? "unknown",
                repairPlan.PackageState,
                shimStateBefore,
                repairPlan.RepairAction);

            var install = await _installer.InstallAsync(
                inspection.PackageName,
                repairPlan.InstallMode,
                ct);
            var after = probe();
            var afterInspection = Inspect(cliType, before.Path, npmBin);
            var packagePresentAfter = Directory.Exists(inspection.PackageDirectory);
            var commandShimRestored = File.Exists(inspection.RequiredCommandShim);
            var succeeded = install.Succeeded
                            && packagePresentAfter
                            && commandShimRestored
                            && after.Available;
            var occurredAt = _clock();
            var detail = succeeded
                ? $"{cliType} CLI repaired: package {repairPlan.PackageState}, command shim {shimStateBefore}, npm action {repairPlan.RepairAction} restored '{inspection.RequiredCommandShim}'; version {beforeVersion ?? "unknown"} -> {after.Version ?? "unknown"}."
                : BuildFailureDetail(
                    cliType,
                    install,
                    after,
                    inspection.RequiredCommandShim,
                    packagePresentAfter,
                    commandShimRestored,
                    repairPlan,
                    shimStateBefore);
            var entry = new LocalCliRepairJournalEntry(
                occurredAt,
                cliType,
                succeeded ? "repaired" : "failed",
                repairPlan.Detection,
                inspection.PackageName,
                inspection.PackageDirectory,
                inspection.PackageModifiedAt,
                before.Path,
                inspection.ExpectedShims,
                beforeVersion,
                after.Version,
                install.ExitCode,
                detail,
                Truncate(LogRedactor.Scrub(install.StandardOutput), 4000),
                Truncate(LogRedactor.Scrub(install.StandardError), 4000),
                evidence,
                NpmOutcomeLabel(install.Outcome));
            AppendJournal(entry);

            if (succeeded)
            {
                lock (_sync) _latest.Remove(cliType);
                _logger.LogInformation(
                    "Local CLI repaired cli={Cli} packageStateBefore={PackageStateBefore} packageStateAfter=present shimStateBefore={ShimStateBefore} shimStateAfter=present repairAction={RepairAction} repairedAt={RepairedAt:o} previousVersion={PreviousVersion} currentVersion={CurrentVersion}",
                    cliType,
                    repairPlan.PackageState,
                    shimStateBefore,
                    repairPlan.RepairAction,
                    occurredAt,
                    beforeVersion ?? "unknown",
                    after.Version ?? "unknown");
            }
            else
            {
                var status = ToStatus(entry);
                lock (_sync) _latest[cliType] = status;
                _logger.LogError(
                    "Local CLI repair failed cli={Cli} packageStateBefore={PackageStateBefore} packageStateAfter={PackageStateAfter} shimStateBefore={ShimStateBefore} shimStateAfter={ShimStateAfter} repairAction={RepairAction} postInstallState={PostInstallState} attemptedAt={AttemptedAt:o} npmOutcome={NpmOutcome} npmExitCode={ExitCode} detail={Detail}",
                    cliType,
                    repairPlan.PackageState,
                    packagePresentAfter ? "present" : "absent",
                    shimStateBefore,
                    commandShimRestored ? "present" : "absent",
                    repairPlan.RepairAction,
                    afterInspection.State,
                    occurredAt,
                    NpmOutcomeLabel(install.Outcome),
                    install.ExitCode,
                    detail);
            }
            return after;
        }
        finally
        {
            lock (_sync) _inFlight.Remove(cliType);
        }
    }

    public static NpmCliInstallInspection Inspect(
        string cliType,
        string probedPath,
        string npmBin)
    {
        var definition = Definition(cliType);
        if (definition is null || !IsGlobalCommandPath(cliType, probedPath, npmBin))
        {
            return new NpmCliInstallInspection(
                NpmCliInstallState.Unsupported,
                cliType,
                definition?.PackageName ?? "",
                "",
                null,
                null,
                "",
                []);
        }

        var resolvedDefinition = definition.Value;
        var packageDirectory = Path.Combine(
            npmBin,
            "node_modules",
            resolvedDefinition.Scope,
            resolvedDefinition.Package);
        var expectedShims = new[]
        {
            Path.Combine(npmBin, cliType),
            Path.Combine(npmBin, cliType + ".cmd"),
            Path.Combine(npmBin, cliType + ".ps1"),
            Path.Combine(npmBin, cliType + ".exe"),
        };
        var requiredCommandShim = Path.Combine(npmBin, cliType + ".cmd");
        var packagePresent = Directory.Exists(packageDirectory);
        var shimPresent = File.Exists(requiredCommandShim);
        var state = !packagePresent
            ? NpmCliInstallState.TrulyUninstalled
            : shimPresent
                ? NpmCliInstallState.PackagePresentWithShim
                : NpmCliInstallState.MissingShimWithPackagePresent;
        var packageJson = Path.Combine(packageDirectory, "package.json");
        return new NpmCliInstallInspection(
            state,
            cliType,
            resolvedDefinition.PackageName,
            packageDirectory,
            ReadPackageVersion(packageJson),
            File.Exists(packageJson) ? SafeLastWrite(packageJson) : null,
            requiredCommandShim,
            expectedShims);
    }

    public static bool AttemptAllowed(
        DateTimeOffset now,
        DateTimeOffset? previousAttempt,
        TimeSpan? window = null)
        => previousAttempt is null || now - previousAttempt.Value >= (window ?? AttemptWindow);

    internal static NpmCliRepairPlan? SelectRepairPlan(NpmCliInstallState state)
        => state switch
        {
            NpmCliInstallState.TrulyUninstalled => new NpmCliRepairPlan(
                NpmGlobalInstallMode.Install,
                "package-missing",
                "absent",
                "install"),
            NpmCliInstallState.MissingShimWithPackagePresent => new NpmCliRepairPlan(
                NpmGlobalInstallMode.ForceRelink,
                "missing-shim-with-package-present",
                "present",
                "force-relink"),
            _ => null,
        };

    private bool TryBeginAttempt(string cliType, DateTimeOffset now)
    {
        lock (_sync)
        {
            if (!_inFlight.Add(cliType)) return false;
            var previous = ReadJournal()
                .Where(entry => string.Equals(entry.CliType, cliType, StringComparison.OrdinalIgnoreCase))
                .Select(entry => (DateTimeOffset?)entry.Timestamp)
                .LastOrDefault();
            if (AttemptAllowed(now, previous)) return true;
            _inFlight.Remove(cliType);
            _logger.LogInformation(
                "Local CLI repair suppressed by one-hour attempt budget cli={Cli} previousAttempt={PreviousAttempt:o}",
                cliType,
                previous);
            return false;
        }
    }

    private IReadOnlyList<NpmLogEvidence> CaptureNpmActivity(string packageName, DateTimeOffset detectedAt)
    {
        var roots = new[] { _localAppData(), _appData() }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Path.Combine(value!, "npm-cache", "_logs"))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var result = new List<NpmLogEvidence>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*-debug-*.log"); }
            catch (Exception ex)
            {
                SilentCatch.Note(ex, "LocalCliRepairService: npm debug-log enumeration failed");
                continue;
            }
            foreach (var file in files
                         .Select(path => (Path: path, Modified: SafeLastWrite(path)))
                         .Where(item => item.Modified >= detectedAt - TimeSpan.FromHours(2)
                                        && item.Modified <= detectedAt + TimeSpan.FromMinutes(5))
                         .OrderByDescending(item => item.Modified)
                         .Take(6))
            {
                var matchingLines = ReadNpmEvidenceLines(file.Path, packageName);
                result.Add(new NpmLogEvidence(
                    Path.GetFileName(file.Path),
                    file.Modified,
                    matchingLines));
            }
        }
        return result;
    }

    private static IReadOnlyList<string> ReadNpmEvidenceLines(string path, string packageName)
    {
        try
        {
            return File.ReadLines(path)
                .Where(line => line.Contains(packageName, StringComparison.OrdinalIgnoreCase)
                               || line.Contains(" verbose argv ", StringComparison.OrdinalIgnoreCase)
                               || line.Contains(" command ", StringComparison.OrdinalIgnoreCase)
                               || line.Contains(" update ", StringComparison.OrdinalIgnoreCase))
                .Take(16)
                .Select(line => Truncate(LogRedactor.Scrub(line.Trim()), 1000))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private void AppendJournal(LocalCliRepairJournalEntry entry)
    {
        try
        {
            var directory = Path.GetDirectoryName(_journalPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var line = JsonSerializer.Serialize(entry, JournalJson);
            lock (_sync) File.AppendAllText(_journalPath, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append local CLI repair journal at {Path}", _journalPath);
        }
    }

    private IReadOnlyList<LocalCliRepairJournalEntry> ReadJournal()
    {
        if (!File.Exists(_journalPath)) return [];
        try
        {
            var entries = new List<LocalCliRepairJournalEntry>();
            foreach (var line in File.ReadLines(_journalPath))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<LocalCliRepairJournalEntry>(line,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (entry is not null) entries.Add(entry);
                }
                catch (Exception ex)
                {
                    SilentCatch.Note(ex, "LocalCliRepairService: skipping torn CLI repair journal row");
                }
            }
            return entries;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read local CLI repair journal at {Path}", _journalPath);
            return [];
        }
    }

    private static string ResolveJournalPath(IConfiguration configuration)
    {
        var taskRepository = configuration["TaskRepository"];
        return !string.IsNullOrWhiteSpace(taskRepository)
            ? Path.Combine(taskRepository, "logs", "cli-self-heal.jsonl")
            : Path.Combine(AppContext.BaseDirectory, "runtime", "cli-self-heal.jsonl");
    }

    private static string BuildFailureDetail(
        string cliType,
        NpmGlobalInstallResult install,
        (bool Available, string? Version, string Path) after,
        string requiredCommandShim,
        bool packagePresentAfter,
        bool commandShimRestored,
        NpmCliRepairPlan repairPlan,
        string shimStateBefore)
    {
        if (install.Outcome == NpmGlobalInstallOutcome.NpmUnavailable)
            return $"{cliType} CLI repair failed: {install.StandardError}";
        if (!install.Succeeded)
            return $"{cliType} CLI repair failed: package {repairPlan.PackageState}, command shim {shimStateBefore}, npm action {repairPlan.RepairAction} attempted, but npm exited {install.ExitCode?.ToString() ?? "without an exit code"}.";
        if (!packagePresentAfter)
            return $"{cliType} CLI repair failed: package {repairPlan.PackageState}, command shim {shimStateBefore}, npm action {repairPlan.RepairAction} attempted, but the package is still absent.";
        if (!commandShimRestored)
            return $"{cliType} CLI repair failed: package {repairPlan.PackageState}, command shim {shimStateBefore}, npm action {repairPlan.RepairAction} attempted, but required shim '{requiredCommandShim}' is still absent.";
        return $"{cliType} CLI repair failed: package {repairPlan.PackageState}, npm action {repairPlan.RepairAction} restored the command shim, but --version still failed at '{after.Path}'.";
    }

    private static LocalCliRepairStatus ToStatus(LocalCliRepairJournalEntry entry)
        => new()
        {
            CliType = entry.CliType,
            Outcome = entry.Outcome,
            OccurredAt = entry.Timestamp,
            VersionBefore = entry.VersionBefore,
            VersionAfter = entry.VersionAfter,
            Detail = entry.Detail,
        };

    private static string NpmOutcomeLabel(NpmGlobalInstallOutcome outcome)
        => outcome switch
        {
            NpmGlobalInstallOutcome.Installed => "installed",
            NpmGlobalInstallOutcome.NpmUnavailable => "npm-unavailable",
            NpmGlobalInstallOutcome.InstallFailed => "install-failed",
            NpmGlobalInstallOutcome.TimedOut => "timed-out",
            _ => "unknown",
        };

    private void ReconcileHealthy(
        string cliType,
        (bool Available, string? Version, string Path) probe)
    {
        LocalCliRepairStatus? stale;
        lock (_sync) _latest.TryGetValue(cliType, out stale);
        if (stale is null) return;

        var occurredAt = _clock();
        AppendJournal(new LocalCliRepairJournalEntry(
            occurredAt,
            cliType,
            "healthy",
            "healthy-probe",
            "",
            "",
            null,
            probe.Path,
            [],
            stale.VersionAfter ?? stale.VersionBefore,
            probe.Version,
            null,
            $"{cliType} CLI repair status cleared after a successful availability probe.",
            "",
            "",
            []));
        lock (_sync) _latest.Remove(cliType);
        _logger.LogInformation(
            "Cleared stale local CLI repair status cli={Cli} previousOutcome={PreviousOutcome} healthyAt={HealthyAt:o} currentVersion={CurrentVersion}",
            cliType,
            stale.Outcome,
            occurredAt,
            probe.Version ?? "unknown");
    }

    private static bool IsGlobalCommandPath(string cliType, string probedPath, string npmBin)
    {
        if (string.IsNullOrWhiteSpace(probedPath)) return false;
        var name = Path.GetFileNameWithoutExtension(probedPath);
        if (!string.Equals(name, cliType, StringComparison.OrdinalIgnoreCase)) return false;
        if (!Path.IsPathRooted(probedPath)) return true;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(probedPath) ?? ""),
            Path.TrimEndingDirectorySeparator(npmBin),
            StringComparison.OrdinalIgnoreCase);
    }

    private static (string Scope, string Package, string PackageName)? Definition(string cliType)
        => cliType.ToLowerInvariant() switch
        {
            CliTypes.Claude => ("@anthropic-ai", "claude-code", "@anthropic-ai/claude-code"),
            CliTypes.Codex => ("@openai", "codex", "@openai/codex"),
            _ => null,
        };

    private static string? ReadPackageVersion(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("version", out var version)
                ? version.GetString()
                : null;
        }
        catch { return null; }
    }

    private static DateTimeOffset SafeLastWrite(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTimeOffset.MinValue; }
    }

    private static string Truncate(string value, int limit)
        => value.Length <= limit ? value : value[..limit] + "...";

}

public sealed record NpmLogEvidence(
    string FileName,
    DateTimeOffset ModifiedAt,
    IReadOnlyList<string> RelevantLines);

public sealed record LocalCliRepairJournalEntry(
    DateTimeOffset Timestamp,
    string CliType,
    string Outcome,
    string Detection,
    string PackageName,
    string PackageDirectory,
    DateTimeOffset? PackageModifiedAt,
    string ProbedPath,
    IReadOnlyList<string> ExpectedShims,
    string? VersionBefore,
    string? VersionAfter,
    int? NpmExitCode,
    string Detail,
    string NpmStandardOutput,
    string NpmStandardError,
    IReadOnlyList<NpmLogEvidence> RecentNpmActivity,
    string? NpmOutcome = null);
