using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AgentStudio.Cli;

/// <summary>
/// Coordinates local Claude and Codex capability checks with a narrow repair:
/// only a missing npm shim whose package is still installed may trigger
/// <c>npm install -g</c>. Attempts are serialized, limited to one per CLI per
/// hour across process restarts, and appended to cli-repairs.jsonl.
/// </summary>
public sealed class LocalCliSelfHealService
{
    public const string JournalFileName = "cli-repairs.jsonl";

    private static readonly JsonSerializerOptions JournalJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IConfiguration _configuration;
    private readonly CliVersionTracker _versions;
    private readonly IJsonlAppender _appender;
    private readonly ILogger<LocalCliSelfHealService> _logger;
    private readonly TimeProvider _time;
    private readonly INpmGlobalInstaller _installer;
    private readonly Func<bool> _isWindows;
    private readonly string _journalPath;
    private readonly SemaphoreSlim _repairGate = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastAttempts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LocalCliCapabilityState> _capabilities =
        new(StringComparer.OrdinalIgnoreCase);
    private LocalCliRepairEvent? _latestRepair;

    public LocalCliSelfHealService(
        IConfiguration configuration,
        CliVersionTracker versions,
        IJsonlAppender appender,
        IOptions<BackendFileLoggerOptions> logOptions,
        ILogger<LocalCliSelfHealService> logger)
        : this(
            configuration,
            versions,
            appender,
            logger,
            TimeProvider.System,
            new NpmGlobalInstaller(),
            OperatingSystem.IsWindows,
            Path.Combine(Path.GetFullPath(logOptions.Value.LogDirectory), JournalFileName))
    {
    }

    internal LocalCliSelfHealService(
        IConfiguration configuration,
        CliVersionTracker versions,
        IJsonlAppender appender,
        ILogger<LocalCliSelfHealService> logger,
        TimeProvider time,
        INpmGlobalInstaller installer,
        Func<bool> isWindows,
        string journalPath)
    {
        _configuration = configuration;
        _versions = versions;
        _appender = appender;
        _logger = logger;
        _time = time;
        _installer = installer;
        _isWindows = isWindows;
        _journalPath = journalPath;
        HydrateJournal();
    }

    public async Task ProbeAllAsync(CliRouter router, string source, CancellationToken ct)
    {
        foreach (var cliType in new[] { CliTypes.Claude, CliTypes.Codex })
        {
            try { await ProbeAndRepairAsync(router.Get(cliType), source, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "CLI capability check failed cli={Cli}; the next probe will retry", cliType);
            }
        }
    }

    internal async Task ProbeAndRepairAsync(ICliExecutionService cli, string source, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var probe = SafeProbe(cli);
        if (probe.Available)
        {
            _versions.Observe(cli.CliType, probe.Version, source);
            RememberCapability(cli.CliType, probe, "ready", now);
            return;
        }

        if (!_isWindows() || cli.CliType is not (CliTypes.Claude or CliTypes.Codex))
        {
            RememberCapability(cli.CliType, probe, "not-available", now);
            return;
        }

        var inspection = Inspect(cli.CliType);
        if (!inspection.IsMissingShimWithPackagePresent)
        {
            RememberCapability(
                cli.CliType,
                probe,
                inspection.PackagePresent ? "probe-failed-with-shim-present" : "not-installed",
                now);
            return;
        }

        await _repairGate.WaitAsync(ct);
        try
        {
            now = _time.GetUtcNow();
            probe = SafeProbe(cli);
            if (probe.Available)
            {
                _versions.Observe(cli.CliType, probe.Version, source + "-concurrent-recovery");
                RememberCapability(cli.CliType, probe, "ready", now);
                return;
            }

            inspection = Inspect(cli.CliType);
            if (!inspection.IsMissingShimWithPackagePresent)
            {
                RememberCapability(
                    cli.CliType,
                    probe,
                    inspection.PackagePresent ? "probe-failed-with-shim-present" : "not-installed",
                    now);
                return;
            }

            var lastAttempt = _lastAttempts.TryGetValue(cli.CliType, out var last) ? last : (DateTimeOffset?)null;
            if (!NpmCliShimInspection.CanAttempt(lastAttempt, now))
            {
                RememberCapability(cli.CliType, probe, "repair-throttled", now);
                _logger.LogInformation(
                    "CLI shim repair suppressed cli={Cli} lastAttemptAt={LastAttemptAt} minimumIntervalMinutes=60",
                    cli.CliType, lastAttempt);
                return;
            }

            _lastAttempts[cli.CliType] = now;
            var started = now;
            var npmActivity = RecentNpmActivity(started, inspection.PackageName);
            var beforeVersion = _versions.Current(cli.CliType);
            _logger.LogInformation(
                "CLI missing shim detected cli={Cli} package={Package} packageVersion={PackageVersion}; starting bounded npm repair",
                cli.CliType, inspection.PackageName, inspection.PackageVersion);

            var startedEvent = new LocalCliRepairEvent(
                cli.CliType,
                inspection.PackageName,
                started,
                started,
                "started",
                false,
                source,
                beforeVersion,
                inspection.PackageVersion,
                null,
                inspection.NpmBin,
                inspection.PackagePath,
                inspection.PackageManifestModifiedAt,
                inspection.MissingShims,
                npmActivity,
                null,
                null,
                null,
                "Repair attempt started but no completion was journaled.");
            try
            {
                await _appender.AppendAsync(_journalPath, startedEvent, JournalJson, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CLI repair start journal append failed path={Path}", _journalPath);
            }

            var install = await _installer.InstallAsync(inspection.PackageName, ct);
            var afterProbe = SafeProbe(cli);
            var completed = _time.GetUtcNow();
            var afterInspection = Inspect(cli.CliType);
            var succeeded = afterProbe.Available;
            var error = succeeded
                ? null
                : install.Error ?? $"{cli.CliType} --version still failed after npm install";
            var repairEvent = new LocalCliRepairEvent(
                cli.CliType,
                inspection.PackageName,
                started,
                completed,
                "completed",
                succeeded,
                source,
                beforeVersion,
                inspection.PackageVersion,
                afterProbe.Version ?? afterInspection.PackageVersion,
                inspection.NpmBin,
                inspection.PackagePath,
                inspection.PackageManifestModifiedAt,
                inspection.MissingShims,
                npmActivity,
                install.ExitCode,
                install.OutputTail,
                install.ErrorTail,
                error);

            try
            {
                await _appender.AppendAsync(_journalPath, repairEvent, JournalJson, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CLI repair journal append failed path={Path}", _journalPath);
            }

            Volatile.Write(ref _latestRepair, repairEvent);
            if (succeeded)
            {
                _versions.Observe(cli.CliType, afterProbe.Version, "self-repair");
                RememberCapability(cli.CliType, afterProbe, "repaired", completed);
                _logger.LogInformation(
                    "CLI repaired cli={Cli} at={RepairedAt} previousVersion={PreviousVersion} currentVersion={CurrentVersion}",
                    cli.CliType, completed, beforeVersion ?? inspection.PackageVersion, afterProbe.Version);
            }
            else
            {
                RememberCapability(cli.CliType, afterProbe, "repair-failed", completed);
                _logger.LogError(
                    "CLI repair failed cli={Cli} at={FailedAt} package={Package} error={Error}",
                    cli.CliType, completed, inspection.PackageName, error);
            }
        }
        finally
        {
            _repairGate.Release();
        }
    }

    public LocalCliHealthSnapshot Snapshot()
    {
        var repair = Volatile.Read(ref _latestRepair);
        return new(
            _time.GetUtcNow(),
            _capabilities.Values.OrderBy(item => item.CliType, StringComparer.Ordinal).ToArray(),
            repair is null
                ? null
                : new LocalCliRepairSummary(
                    repair.CliType,
                    repair.PackageName,
                    repair.AttemptedAt,
                    repair.CompletedAt,
                    repair.Succeeded,
                    repair.Trigger,
                    repair.LastObservedVersionBefore,
                    repair.PackageVersionBefore,
                    repair.VersionAfter,
                    repair.Error));
    }

    internal string JournalPath => _journalPath;

    private (bool Available, string? Version, string Path) SafeProbe(ICliExecutionService cli)
    {
        try { return cli.TestCliPath(); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CLI capability probe failed cli={Cli}", cli.CliType);
            return (false, null, cli.GetCliPath());
        }
    }

    private void RememberCapability(
        string cliType,
        (bool Available, string? Version, string Path) probe,
        string classification,
        DateTimeOffset checkedAt)
        => _capabilities[cliType] = new LocalCliCapabilityState(
            cliType, probe.Available, probe.Version, probe.Path, classification, checkedAt);

    private NpmCliInstallInspection Inspect(string cliType)
    {
        var candidates = NpmBinCandidates();
        foreach (var candidate in candidates)
        {
            var inspection = NpmCliShimInspection.Inspect(cliType, candidate);
            if (inspection.PackagePresent) return inspection;
        }
        return NpmCliShimInspection.Inspect(cliType, candidates[0]);
    }

    private IReadOnlyList<string> NpmBinCandidates()
    {
        var values = new List<string?>
        {
            _configuration["CliSelfHeal:NpmBin"],
            Environment.GetEnvironmentVariable("npm_config_prefix"),
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APPDATA"))
                ? null
                : Path.Combine(Environment.GetEnvironmentVariable("APPDATA")!, "npm"),
        };
        values.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        var distinct = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Path.GetFullPath(value!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return distinct.Length > 0 ? distinct : [Path.GetFullPath(".")];
    }

    private IReadOnlyList<NpmActivityEvidence> RecentNpmActivity(
        DateTimeOffset breakageAt,
        string packageName)
    {
        var cacheRoots = new[]
        {
            Environment.GetEnvironmentVariable("npm_config_cache"),
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LOCALAPPDATA"))
                ? null
                : Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA")!, "npm-cache"),
        };
        var earliest = breakageAt - TimeSpan.FromHours(24);
        var evidence = new List<NpmActivityEvidence>();
        foreach (var root in cacheRoots.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var logs = Path.Combine(root!, "_logs");
            try
            {
                if (!Directory.Exists(logs)) continue;
                evidence.AddRange(Directory.EnumerateFiles(logs, "*.log")
                    .Select(path => new FileInfo(path))
                    .Where(file => file.LastWriteTimeUtc >= earliest.UtcDateTime)
                    .Select(file => new NpmActivityEvidence(
                        file.Name,
                        file.LastWriteTimeUtc,
                        file.Length,
                        SafeNpmEvidenceLines(file.FullName, packageName))));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not enumerate npm activity logs at {Path}", logs);
            }
        }
        return evidence
            .OrderByDescending(item => item.LastWriteAt)
            .Take(8)
            .ToArray();
    }

    private static IReadOnlyList<string> SafeNpmEvidenceLines(string path, string packageName)
    {
        try
        {
            return File.ReadLines(path)
                .TakeLast(250)
                .Where(line =>
                    line.Contains(packageName, StringComparison.OrdinalIgnoreCase)
                    || line.Contains(" info using npm@", StringComparison.OrdinalIgnoreCase)
                    || line.Contains(" info using node@", StringComparison.OrdinalIgnoreCase)
                    || line.Contains(" verbose title npm ", StringComparison.OrdinalIgnoreCase)
                    || line.Contains(" verbose argv ", StringComparison.OrdinalIgnoreCase)
                    || line.Contains(" verbose exit ", StringComparison.OrdinalIgnoreCase))
                .Where(line =>
                    !line.Contains("token", StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("auth", StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("password", StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("http", StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("registry", StringComparison.OrdinalIgnoreCase))
                .Select(line => line.Length <= 500 ? line : line[..500])
                .Take(12)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private void HydrateJournal()
    {
        try
        {
            if (!File.Exists(_journalPath)) return;
            foreach (var line in File.ReadLines(_journalPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                LocalCliRepairEvent? repair;
                try { repair = JsonSerializer.Deserialize<LocalCliRepairEvent>(line, JournalJson); }
                catch (JsonException) { continue; }
                if (repair is null) continue;
                _lastAttempts.AddOrUpdate(
                    repair.CliType,
                    repair.AttemptedAt,
                    (_, current) => repair.AttemptedAt > current ? repair.AttemptedAt : current);
                if (_latestRepair is null || repair.CompletedAt >= _latestRepair.CompletedAt)
                    _latestRepair = repair;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CLI repair journal could not be read path={Path}", _journalPath);
        }
    }
}
