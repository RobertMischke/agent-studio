namespace AgentStudio.Cli;

/// <summary>
/// Probes local Claude/Codex capability and repairs only the narrow state where
/// npm still owns the package but the callable Windows shim has disappeared.
/// Each CLI has one persisted attempt per hour. A genuine uninstall and a
/// present-but-broken executable remain observation-only states.
/// </summary>
public sealed class LocalCliSelfRepairService
{
    public static readonly TimeSpan RepairCooldown = TimeSpan.FromHours(1);

    private static readonly IReadOnlyDictionary<string, string> Packages =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [CliTypes.Claude] = "@anthropic-ai/claude-code",
            [CliTypes.Codex] = "@openai/codex",
        };

    private readonly CliRouter _router;
    private readonly ILocalNpmRepairBoundary _boundary;
    private readonly LocalCliRepairJournal _journal;
    private readonly ILogger<LocalCliSelfRepairService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, DateTime> _lastAttempts = new(StringComparer.OrdinalIgnoreCase);
    private LocalCliRepairReceipt? _latestRepair;

    public LocalCliSelfRepairService(
        CliRouter router,
        ILocalNpmRepairBoundary boundary,
        LocalCliRepairJournal journal,
        ILogger<LocalCliSelfRepairService> logger,
        TimeProvider? timeProvider = null)
    {
        _router = router;
        _boundary = boundary;
        _journal = journal;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;

        foreach (var record in journal.Read().OrderBy(record => record.At))
        {
            _lastAttempts[record.CliType] = record.At;
            if (record.Outcome is "succeeded" or "failed")
                _latestRepair = record.ToReceipt();
        }
    }

    public async Task<LocalCliCapabilityReport> ProbeAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var capabilities = new List<LocalCliCapability>();
            foreach (var (cliType, npmPackage) in Packages)
                capabilities.Add(await ProbeOneAsync(cliType, npmPackage, ct));
            return new LocalCliCapabilityReport(
                _timeProvider.GetUtcNow().UtcDateTime,
                capabilities,
                _latestRepair);
        }
        finally
        {
            _gate.Release();
        }
    }

    public LocalCliRepairReceipt? LatestRepair => _latestRepair;

    private async Task<LocalCliCapability> ProbeOneAsync(
        string cliType,
        string npmPackage,
        CancellationToken ct)
    {
        var cli = _router.Get(cliType);
        var beforeProbe = cli.TestCliPath();
        if (!_boundary.SupportsRepair)
        {
            return Capability(cliType, beforeProbe.Available ? LocalCliInstallationState.Available : LocalCliInstallationState.Uninstalled,
                beforeProbe.Available, beforeProbe.Version, null, beforeProbe.Path);
        }

        var before = await _boundary.CaptureAsync(cliType, npmPackage, cli.GetCliPath(), ct);
        var state = LocalCliInstallationPolicy.Classify(
            beforeProbe.Available,
            before.PackagePresent,
            before.CallableShimPresent);
        if (state != LocalCliInstallationState.MissingShimWithPackagePresent)
            return Capability(cliType, state, beforeProbe.Available, beforeProbe.Version, before.PackageVersion, beforeProbe.Path);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        _lastAttempts.TryGetValue(cliType, out var lastAttempt);
        if (!LocalCliInstallationPolicy.MayAttemptRepair(
                lastAttempt == default ? null : lastAttempt,
                now,
                RepairCooldown))
        {
            _logger.LogDebug(
                "Local CLI repair suppressed by hourly budget for {Cli}; last attempt {LastAttempt:o}",
                cliType,
                lastAttempt);
            return Capability(cliType, state, false, null, before.PackageVersion, beforeProbe.Path);
        }

        _lastAttempts[cliType] = now;
        _logger.LogInformation(
            "Local CLI missing shim detected for {Cli}; package {Package} version {Version} remains at {Directory}. Running bounded npm repair",
            cliType,
            npmPackage,
            before.PackageVersion ?? "unknown",
            before.PackageDirectory);

        var attemptRecord = new LocalCliRepairJournalRecord
        {
            At = now,
            CliType = cliType,
            NpmPackage = npmPackage,
            Outcome = "started",
            Command = $"npm install --global {npmPackage}",
            ExecutablePath = beforeProbe.Path,
            NpmPrefix = before.Prefix,
            PackageDirectory = before.PackageDirectory,
            CliVersionBefore = beforeProbe.Version,
            PackageVersionBefore = before.PackageVersion,
            ActivityBefore = before.RecentActivity,
        };
        try
        {
            // Persist the budget before npm starts. A host crash during the
            // install must not buy another attempt immediately after restart.
            await _journal.AppendAsync(attemptRecord, ct);
        }
        catch (Exception ex)
        {
            var journalError = $"repair journal unavailable; npm mutation was not attempted: {ex.Message}";
            _latestRepair = new LocalCliRepairReceipt(
                now, cliType, "failed", beforeProbe.Version, before.PackageVersion, null, null, journalError);
            _logger.LogError(ex,
                "Local CLI repair refused for {Cli} because the attempt budget could not be journalled",
                cliType);
            return Capability(cliType, state, false, null, before.PackageVersion, beforeProbe.Path);
        }

        var install = await _boundary.InstallGlobalAsync(npmPackage, ct);
        var afterProbe = cli.TestCliPath();
        var after = await _boundary.CaptureAsync(cliType, npmPackage, cli.GetCliPath(), ct);
        var succeeded = install.Succeeded && afterProbe.Available;
        var completedAt = _timeProvider.GetUtcNow().UtcDateTime;
        _lastAttempts[cliType] = completedAt;
        var error = succeeded
            ? null
            : install.Error ?? $"{cliType} --version remained unavailable after npm install";
        var record = new LocalCliRepairJournalRecord
        {
            At = completedAt,
            CliType = cliType,
            NpmPackage = npmPackage,
            Outcome = succeeded ? "succeeded" : "failed",
            Command = $"npm install --global {npmPackage}",
            ExecutablePath = beforeProbe.Path,
            NpmPrefix = before.Prefix,
            PackageDirectory = before.PackageDirectory,
            CliVersionBefore = beforeProbe.Version,
            PackageVersionBefore = before.PackageVersion,
            CliVersionAfter = afterProbe.Version,
            PackageVersionAfter = after.PackageVersion,
            ActivityBefore = before.RecentActivity,
            ActivityAfter = after.RecentActivity,
            NpmExitCode = install.ExitCode,
            NpmStdoutTail = install.StdoutTail,
            NpmStderrTail = install.StderrTail,
            Error = error,
        };
        _latestRepair = record.ToReceipt();
        try
        {
            await _journal.AppendAsync(record, ct);
        }
        catch (Exception ex)
        {
            // The started row already preserves the hourly budget. Keep the
            // actual repair result visible in memory and make the missing final
            // receipt explicit in the backend log.
            _logger.LogError(ex, "Failed to append final local CLI repair receipt for {Cli}", cliType);
        }

        if (succeeded)
        {
            _logger.LogInformation(
                "Local CLI repaired for {Cli}; CLI {CliBefore} -> {CliAfter}, package {PackageBefore} -> {PackageAfter}",
                cliType,
                beforeProbe.Version ?? "not-found",
                afterProbe.Version ?? "unknown",
                before.PackageVersion ?? "unknown",
                after.PackageVersion ?? "unknown");
        }
        else
        {
            _logger.LogError(
                "Local CLI repair failed for {Cli}; package remained {PackageVersion}. {Error}",
                cliType,
                after.PackageVersion ?? "unknown",
                error);
        }

        var afterState = LocalCliInstallationPolicy.Classify(
            afterProbe.Available,
            after.PackagePresent,
            after.CallableShimPresent);
        return Capability(cliType, afterState, afterProbe.Available, afterProbe.Version,
            after.PackageVersion, afterProbe.Path);
    }

    private static LocalCliCapability Capability(
        string cliType,
        LocalCliInstallationState state,
        bool available,
        string? cliVersion,
        string? packageVersion,
        string executablePath)
        => new(cliType, StateName(state), available, cliVersion, packageVersion, executablePath);

    private static string StateName(LocalCliInstallationState state) => state switch
    {
        LocalCliInstallationState.Available => "available",
        LocalCliInstallationState.MissingShimWithPackagePresent => "missing-shim-with-package-present",
        LocalCliInstallationState.Uninstalled => "uninstalled",
        _ => "broken-install",
    };
}
