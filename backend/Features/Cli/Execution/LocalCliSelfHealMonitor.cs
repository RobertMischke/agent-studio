using System.Collections.Concurrent;
using System.Text.Json;
using AgentStudio.Persistence;

namespace AgentStudio.Cli;

/// <summary>
/// Periodically extends the local CLI capability probe with one narrowly
/// scoped repair: when Claude or Codex is absent from PATH while its npm
/// package is still installed, reinstall that same global package. A genuine
/// uninstall and an explicitly configured missing path remain operator-owned.
/// </summary>
public sealed class LocalCliSelfHealMonitor : BackgroundService
{
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromMinutes(1);
    public const string JournalFileName = "cli-repairs.jsonl";

    private readonly CliRouter _router;
    private readonly IConfiguration _configuration;
    private readonly IJsonlAppender _appender;
    private readonly ILogger<LocalCliSelfHealMonitor> _logger;
    private readonly SemaphoreSlim _probeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, CliRepairSnapshot> _latest =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastAttempts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<string, CancellationToken, Task<NpmGlobalInstallResult>> _installer;
    private int _journalLoaded;
    private static readonly JsonSerializerOptions JournalJson =
        new(JsonSerializerDefaults.Web);

    public LocalCliSelfHealMonitor(
        CliRouter router,
        IConfiguration configuration,
        IJsonlAppender appender,
        ILogger<LocalCliSelfHealMonitor> logger)
        : this(router, configuration, appender, logger, null, null)
    {
    }

    internal LocalCliSelfHealMonitor(
        CliRouter router,
        IConfiguration configuration,
        IJsonlAppender appender,
        ILogger<LocalCliSelfHealMonitor> logger,
        Func<DateTimeOffset>? clock,
        Func<string, CancellationToken, Task<NpmGlobalInstallResult>>? installer)
    {
        _router = router;
        _configuration = configuration;
        _appender = appender;
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _installer = installer ?? ((packageName, ct) =>
            NpmShimHealer.InstallGlobalPackageAsync(packageName, logger, ct));
    }

    public CliRepairSnapshot? Snapshot(string cliType)
        => _latest.GetValueOrDefault(CliTypes.Normalize(cliType));

    public IReadOnlyList<CliRepairSnapshot> Snapshots()
        => _latest.Values
            .OrderByDescending(snapshot => snapshot.CompletedAt)
            .ToArray();

    public async Task ProbeAllAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()
            && !_configuration.GetValue("CliSelfHeal:AllowNonWindows", false))
        {
            return;
        }
        if (!_configuration.GetValue("CliSelfHeal:Enabled", true)) return;

        await _probeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            LoadJournalOnce();
            foreach (var cliType in new[] { CliTypes.Claude, CliTypes.Codex })
            {
                await ProbeOneAsync(cliType, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _probeLock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProbeAllAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Local CLI self-heal probe failed");
            }

            try
            {
                await Task.Delay(ProbeInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ProbeOneAsync(string cliType, CancellationToken ct)
    {
        var cli = _router.Get(cliType);
        var probe = cli.TestCliPath();
        if (probe.Available)
        {
            if (_latest.TryGetValue(cliType, out var previous)
                && previous.Status == "failed")
            {
                _latest.TryRemove(cliType, out _);
            }
            return;
        }

        var npmBin = ResolveNpmBin();
        if (string.IsNullOrWhiteSpace(npmBin)) return;
        var inspection = NpmShimRepairPolicy.Inspect(
            cliType,
            cli.GetCliPath(),
            npmBin,
            executableAvailable: false);
        if (inspection.State != NpmShimInstallState.MissingShimPackagePresent) return;

        var now = _clock();
        var lastAttempt = _lastAttempts.GetValueOrDefault(cliType);
        if (!NpmShimRepairPolicy.AttemptAllowed(
                lastAttempt == default ? null : lastAttempt,
                now))
        {
            return;
        }

        _lastAttempts[cliType] = now;
        var activityBefore = NpmShimHealer.CaptureInstallActivity(
            inspection.PackagePath!, npmBin, now);
        var attempt = new CliRepairJournalEntry
        {
            CliType = cliType,
            PackageName = inspection.PackageName!,
            Status = "attempting",
            AttemptedAt = now,
            VersionBefore = inspection.PackageVersion,
            ExecutableBefore = probe.Path,
            ActivityBefore = activityBefore,
        };
        await AppendAsync(attempt, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "CLI shim missing while npm package remains present; repair starting cli={CliType} package={Package} versionBefore={VersionBefore}",
            cliType,
            inspection.PackageName,
            inspection.PackageVersion ?? "unknown");

        var install = await _installer(inspection.PackageName!, ct).ConfigureAwait(false);
        var verify = cli.TestCliPath();
        var completedAt = _clock();
        var succeeded = install.ExitCode == 0 && verify.Available;
        var status = succeeded ? "repaired" : "failed";
        var note = succeeded
            ? $"CLI repaired at {completedAt:O}"
            : $"CLI repair failed at {completedAt:O}";
        var completed = attempt with
        {
            Status = status,
            CompletedAt = completedAt,
            VersionAfter = verify.Version,
            ExecutableAfter = verify.Path,
            InstallExitCode = install.ExitCode,
            InstallDetail = install.Detail,
            ActivityAfter = NpmShimHealer.CaptureInstallActivity(
                inspection.PackagePath!, npmBin, completedAt),
            Note = note,
        };
        await AppendAsync(completed, ct).ConfigureAwait(false);

        var snapshot = new CliRepairSnapshot
        {
            CliType = cliType,
            Status = status,
            AttemptedAt = now,
            CompletedAt = completedAt,
            VersionBefore = inspection.PackageVersion,
            VersionAfter = verify.Version,
            Note = note,
            Detail = succeeded
                ? $"npm global reinstall restored {Path.GetFileName(verify.Path)}; {inspection.PackageVersion ?? "unknown"} -> {verify.Version ?? "unknown"}."
                : $"npm global reinstall exited {install.ExitCode}; {install.Detail}",
        };
        _latest[cliType] = snapshot;

        if (succeeded)
        {
            _logger.LogInformation(
                "CLI repair succeeded cli={CliType} repairedAt={RepairedAt} versionBefore={VersionBefore} versionAfter={VersionAfter}",
                cliType,
                completedAt,
                inspection.PackageVersion ?? "unknown",
                verify.Version ?? "unknown");
        }
        else
        {
            _logger.LogError(
                "CLI repair failed cli={CliType} attemptedAt={AttemptedAt} exitCode={ExitCode} detail={Detail}",
                cliType,
                now,
                install.ExitCode,
                install.Detail);
        }
    }

    private string? ResolveNpmBin()
    {
        var configured = _configuration["CliSelfHeal:NpmBin"];
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        return string.IsNullOrWhiteSpace(appData) ? null : Path.Combine(appData, "npm");
    }

    private string JournalPath()
    {
        var workspace = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace)) workspace = AppContext.BaseDirectory;
        return Path.Combine(workspace, "logs", JournalFileName);
    }

    private async Task AppendAsync(CliRepairJournalEntry entry, CancellationToken ct)
    {
        try
        {
            await _appender.AppendAsync(JournalPath(), entry, ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to append local CLI repair journal at {Path}", JournalPath());
        }
    }

    private void LoadJournalOnce()
    {
        if (Interlocked.Exchange(ref _journalLoaded, 1) != 0) return;
        var path = JournalPath();
        if (!File.Exists(path)) return;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                CliRepairJournalEntry? entry;
                try { entry = JsonSerializer.Deserialize<CliRepairJournalEntry>(line, JournalJson); }
                catch { continue; }
                if (entry is null) continue;
                if (entry.Status == "attempting")
                    _lastAttempts.AddOrUpdate(
                        entry.CliType,
                        entry.AttemptedAt,
                        (_, current) => current > entry.AttemptedAt ? current : entry.AttemptedAt);
                if (entry.Status is "repaired" or "failed" && entry.CompletedAt is not null)
                {
                    _latest[entry.CliType] = new CliRepairSnapshot
                    {
                        CliType = entry.CliType,
                        Status = entry.Status,
                        AttemptedAt = entry.AttemptedAt,
                        CompletedAt = entry.CompletedAt.Value,
                        VersionBefore = entry.VersionBefore,
                        VersionAfter = entry.VersionAfter,
                        Note = entry.Note ?? $"CLI repair {entry.Status} at {entry.CompletedAt:O}",
                        Detail = entry.InstallDetail,
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read local CLI repair journal at {Path}", path);
        }
    }
}

public sealed record CliRepairSnapshot
{
    public required string CliType { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset AttemptedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public string? VersionBefore { get; init; }
    public string? VersionAfter { get; init; }
    public required string Note { get; init; }
    public string? Detail { get; init; }
}

public sealed record CliRepairJournalEntry
{
    public int SchemaVersion { get; init; } = 1;
    public required string CliType { get; init; }
    public required string PackageName { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset AttemptedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? VersionBefore { get; init; }
    public string? VersionAfter { get; init; }
    public string? ExecutableBefore { get; init; }
    public string? ExecutableAfter { get; init; }
    public int? InstallExitCode { get; init; }
    public string? InstallDetail { get; init; }
    public string? Note { get; init; }
    public IReadOnlyList<NpmInstallActivity> ActivityBefore { get; init; } = [];
    public IReadOnlyList<NpmInstallActivity> ActivityAfter { get; init; } = [];
}
