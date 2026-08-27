using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using AgentStudio.Persistence;

namespace AgentStudio.Cli;

public static class LocalCliInstallStates
{
    public const string Available = "available";
    public const string MissingShimPackagePresent = "missing-shim-package-present";
    public const string TrulyUninstalled = "truly-uninstalled";
    public const string Unsupported = "unsupported";
}

public static class LocalCliRepairOutcomes
{
    public const string Started = "repair-started";
    public const string Repaired = "repaired";
    public const string Failed = "repair-failed";
    public const string RateLimited = "rate-limited";
    public const string NotApplicable = "not-applicable";
}

public sealed record LocalCliInstallClassification(
    string CliType,
    string PackageName,
    string PackagePath,
    string InstallState,
    IReadOnlyList<string> ExpectedShims);

public sealed record LocalCliRepairEvent(
    DateTimeOffset DetectedAt,
    DateTimeOffset CompletedAt,
    string CliType,
    string PackageName,
    string InstallState,
    string Outcome,
    string? CliVersionBefore,
    string? CliVersionAfter,
    DateTimeOffset? PackageModifiedAt,
    int? NpmExitCode,
    string Detail,
    IReadOnlyList<string> NpmActivity,
    string? NpmStdOutTail = null,
    string? NpmStdErrTail = null);

public sealed record LocalCliRepairSnapshot(
    DateTimeOffset At,
    LocalCliRepairEvent? LatestRepair,
    LocalCliRepairEvent? ActiveFailure,
    string JournalPath);

public sealed record LocalCliRepairResult(
    string Outcome,
    string Detail,
    LocalCliRepairEvent? Event = null);

internal sealed record LocalCliRepairProcessResult(int ExitCode, string StdOut, string StdErr);

/// <summary>
/// Repairs the Windows npm failure in which a global CLI package is still
/// present but every executable shim has disappeared. Detection is separate
/// from repair so a genuinely uninstalled CLI never triggers an install.
/// Repair attempts are shared by capability and pre-spawn probes, persisted to
/// one journal, and bounded to one attempt per CLI per hour.
/// </summary>
public sealed class LocalCliRepairService
{
    public static readonly TimeSpan RepairAttemptInterval = TimeSpan.FromHours(1);
    public const string JournalFileName = "cli-repairs.jsonl";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IConfiguration _configuration;
    private readonly ILogger<LocalCliRepairService> _logger;
    private readonly IJsonlAppender _appender;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, string> _lastKnownVersions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LocalCliRepairEvent> _lastAttempts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<string, string, CancellationToken, Task<LocalCliRepairProcessResult>> _installer;
    private readonly bool _isWindows;
    private LocalCliRepairEvent? _latestRepair;
    private LocalCliRepairEvent? _activeFailure;

    public LocalCliRepairService(
        IConfiguration configuration,
        ILogger<LocalCliRepairService> logger,
        IJsonlAppender appender)
        : this(
            configuration,
            logger,
            appender,
            () => DateTimeOffset.UtcNow,
            RunNpmInstallAsync,
            OperatingSystem.IsWindows())
    {
    }

    internal LocalCliRepairService(
        IConfiguration configuration,
        ILogger<LocalCliRepairService> logger,
        IJsonlAppender appender,
        Func<DateTimeOffset> clock,
        Func<string, string, CancellationToken, Task<LocalCliRepairProcessResult>> installer,
        bool isWindows)
    {
        _configuration = configuration;
        _logger = logger;
        _appender = appender;
        _clock = clock;
        _installer = installer;
        _isWindows = isWindows;
        LoadJournalState();
    }

    public string JournalPath
    {
        get
        {
            var taskRepository = _configuration["TaskRepository"];
            if (!string.IsNullOrWhiteSpace(taskRepository))
                return Path.Combine(taskRepository, "logs", JournalFileName);

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local)) local = Path.GetTempPath();
            return Path.Combine(local, "agent-taskboard", JournalFileName);
        }
    }

    public LocalCliRepairSnapshot Snapshot()
        => new(_clock(), _latestRepair, _activeFailure, JournalPath);

    public void ObserveAvailable(string cliType, string? version)
    {
        if (!string.IsNullOrWhiteSpace(version))
            _lastKnownVersions[CliTypes.Normalize(cliType)] = version.Trim();

        if (_activeFailure is not null
            && string.Equals(_activeFailure.CliType, cliType, StringComparison.OrdinalIgnoreCase))
        {
            _activeFailure = null;
        }
    }

    public async Task<LocalCliRepairResult> TryRepairMissingShimAsync(
        string cliType,
        Func<(bool Available, string? Version, string Path)> probe,
        CancellationToken ct)
    {
        var normalized = CliTypes.Normalize(cliType);
        var initial = probe();
        if (initial.Available)
        {
            ObserveAvailable(normalized, initial.Version);
            return new LocalCliRepairResult(LocalCliRepairOutcomes.NotApplicable, "CLI probe is healthy.");
        }
        if (!_isWindows)
            return new LocalCliRepairResult(LocalCliRepairOutcomes.NotApplicable, "Windows npm shim repair does not apply on this host.");

        var npmBin = ResolveNpmBin();
        var classification = ClassifyInstall(normalized, npmBin, File.Exists, Directory.Exists);
        if (classification.InstallState != LocalCliInstallStates.MissingShimPackagePresent)
        {
            return new LocalCliRepairResult(
                LocalCliRepairOutcomes.NotApplicable,
                classification.InstallState == LocalCliInstallStates.TrulyUninstalled
                    ? $"{normalized} is truly uninstalled; package '{classification.PackageName}' is absent."
                    : $"No supported missing-shim repair applies to {normalized}.");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            initial = probe();
            if (initial.Available)
            {
                ObserveAvailable(normalized, initial.Version);
                return new LocalCliRepairResult(LocalCliRepairOutcomes.NotApplicable, "CLI recovered before the repair lock was acquired.");
            }

            classification = ClassifyInstall(normalized, npmBin, File.Exists, Directory.Exists);
            if (classification.InstallState != LocalCliInstallStates.MissingShimPackagePresent)
                return new LocalCliRepairResult(LocalCliRepairOutcomes.NotApplicable, "Install state changed before repair.");

            var now = _clock();
            var lastAttempt = LatestAttempt(normalized);
            if (!ShouldAttemptRepair(now, lastAttempt?.DetectedAt, RepairAttemptInterval))
            {
                var next = lastAttempt!.DetectedAt + RepairAttemptInterval;
                return new LocalCliRepairResult(
                    LocalCliRepairOutcomes.RateLimited,
                    $"Repair already attempted within one hour; next attempt is allowed at {next:O}.",
                    lastAttempt);
            }

            var packageVersion = ReadPackageVersion(classification.PackagePath);
            var beforeVersion = _lastKnownVersions.TryGetValue(normalized, out var known)
                ? known
                : packageVersion;
            var activity = CaptureNpmActivity(classification, now);
            var packageModifiedAt = SafeModifiedAt(classification.PackagePath);

            _logger.LogInformation(
                "local-cli-repair-started cli={CliType} package={Package} beforeVersion={BeforeVersion} journal={Journal}",
                normalized, classification.PackageName, beforeVersion ?? "unknown", JournalPath);

            // Persist intent before starting npm. If the backend or host dies
            // during install, the next process still observes the hourly bound
            // and promotes the unterminated row to an operator-visible failure.
            var started = new LocalCliRepairEvent(
                now,
                now,
                normalized,
                classification.PackageName,
                classification.InstallState,
                LocalCliRepairOutcomes.Started,
                beforeVersion,
                null,
                packageModifiedAt,
                null,
                $"Started bounded npm reinstall for {classification.PackageName}.",
                activity);
            await AppendAsync(started, ct).ConfigureAwait(false);
            _lastAttempts[normalized] = started;

            LocalCliRepairProcessResult install;
            try
            {
                install = await _installer(classification.PackageName, npmBin, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                install = new LocalCliRepairProcessResult(-1, string.Empty, $"{ex.GetType().Name}: {ex.Message}");
            }

            var verification = probe();
            var repaired = install.ExitCode == 0 && verification.Available;
            var completedAt = _clock();
            var detail = repaired
                ? $"Reinstalled {classification.PackageName} and restored the {normalized} shim."
                : $"npm install -g exited {install.ExitCode}; post-repair --version available={verification.Available}.";
            var entry = new LocalCliRepairEvent(
                now,
                completedAt,
                normalized,
                classification.PackageName,
                classification.InstallState,
                repaired ? LocalCliRepairOutcomes.Repaired : LocalCliRepairOutcomes.Failed,
                beforeVersion,
                verification.Version,
                packageModifiedAt,
                install.ExitCode,
                detail,
                activity,
                Tail(install.StdOut),
                Tail(install.StdErr));

            await AppendAsync(entry, ct).ConfigureAwait(false);
            _lastAttempts[normalized] = entry;
            if (repaired)
            {
                _latestRepair = entry;
                _activeFailure = null;
                ObserveAvailable(normalized, verification.Version);
                _logger.LogInformation(
                    "local-cli-repaired cli={CliType} at={CompletedAt} beforeVersion={BeforeVersion} afterVersion={AfterVersion}",
                    normalized, completedAt, beforeVersion ?? "unknown", verification.Version ?? "unknown");
                return new LocalCliRepairResult(LocalCliRepairOutcomes.Repaired, detail, entry);
            }

            _activeFailure = entry;
            _logger.LogError(
                "local-cli-repair-failed cli={CliType} exitCode={ExitCode} detail={Detail} journal={Journal}",
                normalized, install.ExitCode, detail, JournalPath);
            return new LocalCliRepairResult(LocalCliRepairOutcomes.Failed, detail, entry);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static LocalCliInstallClassification ClassifyInstall(
        string cliType,
        string npmBin,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists)
    {
        var normalized = CliTypes.Normalize(cliType);
        var spec = normalized switch
        {
            CliTypes.Claude => (Package: "@anthropic-ai/claude-code", Scope: "@anthropic-ai", Folder: "claude-code", Shim: "claude"),
            CliTypes.Codex => (Package: "@openai/codex", Scope: "@openai", Folder: "codex", Shim: "codex"),
            _ => (Package: string.Empty, Scope: string.Empty, Folder: string.Empty, Shim: string.Empty),
        };
        if (string.IsNullOrEmpty(spec.Package))
            return new LocalCliInstallClassification(normalized, string.Empty, string.Empty, LocalCliInstallStates.Unsupported, []);

        var packagePath = Path.Combine(npmBin, "node_modules", spec.Scope, spec.Folder);
        var shims = new[]
        {
            Path.Combine(npmBin, spec.Shim),
            Path.Combine(npmBin, spec.Shim + ".cmd"),
            Path.Combine(npmBin, spec.Shim + ".ps1"),
        };
        // The backend uses CreateProcess + PATHEXT, so the npm .cmd launcher is
        // the canonical Windows execution shim. A leftover POSIX or PowerShell
        // shim does not make local task execution available.
        if (fileExists(shims[1]))
            return new LocalCliInstallClassification(normalized, spec.Package, packagePath, LocalCliInstallStates.Available, shims);
        return new LocalCliInstallClassification(
            normalized,
            spec.Package,
            packagePath,
            directoryExists(packagePath)
                ? LocalCliInstallStates.MissingShimPackagePresent
                : LocalCliInstallStates.TrulyUninstalled,
            shims);
    }

    internal static bool ShouldAttemptRepair(
        DateTimeOffset now,
        DateTimeOffset? lastAttemptAt,
        TimeSpan interval)
        => lastAttemptAt is null || now - lastAttemptAt.Value >= interval;

    private string ResolveNpmBin()
    {
        var configured = _configuration["CliRepair:NpmBin"];
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        return string.IsNullOrWhiteSpace(appData)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm")
            : Path.Combine(appData, "npm");
    }

    private LocalCliRepairEvent? LatestAttempt(string cliType)
        => _lastAttempts.TryGetValue(cliType, out var attempt) ? attempt : null;

    private async Task AppendAsync(LocalCliRepairEvent entry, CancellationToken ct)
    {
        try
        {
            await _appender.AppendAsync(JournalPath, entry, Json, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to append local CLI repair journal at {Journal}", JournalPath);
        }
    }

    private void LoadJournalState()
    {
        try
        {
            if (!File.Exists(JournalPath)) return;
            foreach (var line in File.ReadLines(JournalPath).TakeLast(200))
            {
                LocalCliRepairEvent? entry;
                try { entry = JsonSerializer.Deserialize<LocalCliRepairEvent>(line, Json); }
                catch (JsonException) { continue; }
                if (entry is null) continue;
                if (!_lastAttempts.TryGetValue(entry.CliType, out var lastAttempt)
                    || entry.CompletedAt > lastAttempt.CompletedAt
                    || (entry.CompletedAt == lastAttempt.CompletedAt
                        && entry.Outcome != LocalCliRepairOutcomes.Started))
                    _lastAttempts[entry.CliType] = entry;
                if (entry.Outcome == LocalCliRepairOutcomes.Repaired)
                {
                    if (_latestRepair is null || entry.CompletedAt > _latestRepair.CompletedAt)
                        _latestRepair = entry;
                    if (!string.IsNullOrWhiteSpace(entry.CliVersionAfter))
                        _lastKnownVersions[entry.CliType] = entry.CliVersionAfter;
                }
                else if (entry.Outcome == LocalCliRepairOutcomes.Failed
                         && (_activeFailure is null || entry.CompletedAt > _activeFailure.CompletedAt))
                {
                    _activeFailure = entry;
                }
            }
            if (_activeFailure is not null && _latestRepair is not null
                && _latestRepair.CompletedAt > _activeFailure.CompletedAt)
                _activeFailure = null;

            foreach (var attempt in _lastAttempts.Values
                         .Where(item => item.Outcome == LocalCliRepairOutcomes.Started))
            {
                var interrupted = attempt with
                {
                    Outcome = LocalCliRepairOutcomes.Failed,
                    Detail = "The previous repair attempt did not record a terminal outcome; the backend or npm process was interrupted.",
                };
                if (_activeFailure is null || interrupted.CompletedAt > _activeFailure.CompletedAt)
                    _activeFailure = interrupted;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read local CLI repair journal at {Journal}", JournalPath);
        }
    }

    private static string? ReadPackageVersion(string packagePath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(packagePath, "package.json")));
            return document.RootElement.TryGetProperty("version", out var version) ? version.GetString() : null;
        }
        catch { return null; }
    }

    private static DateTimeOffset? SafeModifiedAt(string path)
    {
        try { return Directory.GetLastWriteTimeUtc(path); }
        catch { return null; }
    }

    private static IReadOnlyList<string> CaptureNpmActivity(
        LocalCliInstallClassification classification,
        DateTimeOffset detectedAt)
    {
        var activity = new List<string>();
        try
        {
            var packageFiles = Directory.EnumerateFiles(classification.PackagePath, "*", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .Take(12);
            activity.AddRange(packageFiles.Select(info =>
                $"package-file {info.LastWriteTimeUtc:O} {Path.GetRelativePath(classification.PackagePath, info.FullName)} {info.Length}b"));
        }
        catch (Exception ex) { SilentCatch.Note(ex, "LocalCliRepairService: package activity capture"); }

        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var logs = Path.Combine(local, "npm-cache", "_logs");
            if (Directory.Exists(logs))
            {
                activity.AddRange(Directory.EnumerateFiles(logs, "*-debug-*.log")
                    .Select(path => new FileInfo(path))
                    .Where(info => Math.Abs((detectedAt - info.LastWriteTimeUtc).TotalHours) <= 24)
                    .OrderByDescending(info => info.LastWriteTimeUtc)
                    .Take(8)
                    .Select(info => $"npm-log {info.LastWriteTimeUtc:O} {info.FullName} {info.Length}b"));
            }
        }
        catch (Exception ex) { SilentCatch.Note(ex, "LocalCliRepairService: npm log activity capture"); }
        return activity;
    }

    private static async Task<LocalCliRepairProcessResult> RunNpmInstallAsync(
        string packageName,
        string npmBin,
        CancellationToken ct)
    {
        var command = $"npm install -g {packageName} --no-audit --no-fund";
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                WorkingDirectory = Directory.Exists(npmBin) ? npmBin : Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("/d");
        process.StartInfo.ArgumentList.Add("/s");
        process.StartInfo.ArgumentList.Add("/c");
        process.StartInfo.ArgumentList.Add(command);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) { SilentCatch.Note(ex, "LocalCliRepairService: timed-out npm process cleanup"); }
            return new LocalCliRepairProcessResult(-1, await stdout.ConfigureAwait(false), "npm install timed out");
        }
        return new LocalCliRepairProcessResult(
            process.ExitCode,
            await stdout.ConfigureAwait(false),
            await stderr.ConfigureAwait(false));
    }

    private static string? Tail(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var normalized = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 2000 ? normalized : normalized[^2000..];
    }
}

/// <summary>Re-runs the local Claude/Codex capability probe once per minute.</summary>
public sealed class LocalCliCapabilityMonitor : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private readonly CliRouter _router;
    private readonly LocalCliRepairService _repairs;
    private readonly ILogger<LocalCliCapabilityMonitor> _logger;

    public LocalCliCapabilityMonitor(
        CliRouter router,
        LocalCliRepairService repairs,
        ILogger<LocalCliCapabilityMonitor> logger)
    {
        _router = router;
        _repairs = repairs;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var cliType in new[] { CliTypes.Claude, CliTypes.Codex })
            {
                try
                {
                    var cli = _router.Get(cliType);
                    var probe = cli.TestCliPath();
                    if (probe.Available)
                        _repairs.ObserveAvailable(cliType, probe.Version);
                    else
                        await _repairs.TryRepairMissingShimAsync(cliType, () => cli.TestCliPath(), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Local CLI capability probe failed for {CliType}", cliType);
                }
            }

            try { await Task.Delay(Interval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}
