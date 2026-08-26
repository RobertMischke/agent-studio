using System.Diagnostics;
using System.Text.Json;
using AgentStudio.Persistence;
using AgentStudio.Shared;

namespace AgentStudio.Cli;

/// <summary>
/// Windows-local recovery for the npm failure shape where a global CLI package
/// is still installed but npm's command shims have disappeared. The service
/// extends the normal <c>--version</c> capability probe and deliberately does
/// not install a package that is genuinely absent.
/// </summary>
public sealed class LocalCliSelfHealService : IHostedService, IDisposable
{
    internal static readonly TimeSpan RepairCooldown = TimeSpan.FromHours(1);
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions JournalJson = new(JsonSerializerDefaults.Web);
    private static readonly NpmCliPackage[] Packages =
    [
        new(CliTypes.Claude, "claude", "@anthropic-ai/claude-code", "@anthropic-ai", "claude-code"),
        new(CliTypes.Codex, "codex", "@openai/codex", "@openai", "codex"),
    ];

    private readonly CliRouter _router;
    private readonly IConfiguration _configuration;
    private readonly IJsonlAppender _appender;
    private readonly ILogger<LocalCliSelfHealService> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _probeLock = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _lastAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LocalCliRepairStatus> _latest = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private bool _journalLoaded;

    public LocalCliSelfHealService(
        CliRouter router,
        IConfiguration configuration,
        IJsonlAppender appender,
        ILogger<LocalCliSelfHealService> logger)
        : this(router, configuration, appender, logger, TimeProvider.System)
    {
    }

    internal LocalCliSelfHealService(
        CliRouter router,
        IConfiguration configuration,
        IJsonlAppender appender,
        ILogger<LocalCliSelfHealService> logger,
        TimeProvider time)
    {
        _router = router;
        _configuration = configuration;
        _appender = appender;
        _logger = logger;
        _time = time;
    }

    /// <summary>Latest visible repair outcome across Claude and Codex.</summary>
    public LocalCliRepairStatus? Latest
    {
        get
        {
            lock (_latest)
                return _latest.Values.OrderByDescending(item => item.OccurredAt).FirstOrDefault();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return;

        // The initial pass finishes before TaskRunnerService performs its boot
        // capability check, so a repair can keep the local runner available.
        await ProbeAllAsync(cancellationToken).ConfigureAwait(false);
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = RunLoopAsync(_loopCts.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_loopCts is null || _loop is null) return;
        await _loopCts.CancelAsync().ConfigureAwait(false);
        try { await _loop.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException ex) when (_loopCts.IsCancellationRequested)
        {
            SilentCatch.Note(ex, "LocalCliSelfHealService: expected hosted-loop cancellation");
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(ProbeInterval, _time);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try { await ProbeAllAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // A monitoring fault is diagnostic, not a CLI repair failure.
                _logger.LogInformation(ex, "local-cli-self-heal probe iteration could not complete");
            }
        }
    }

    internal async Task ProbeAllAsync(CancellationToken ct)
    {
        await _probeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            LoadJournalState();
            var npmBin = WindowsNpmGlobalInstallInspector.ResolveNpmBin();
            if (npmBin is null) return;

            foreach (var package in Packages)
                await ProbeOneAsync(package, npmBin, ct).ConfigureAwait(false);
        }
        finally
        {
            _probeLock.Release();
        }
    }

    private async Task ProbeOneAsync(NpmCliPackage package, string npmBin, CancellationToken ct)
    {
        var probe = _router.Get(package.CliType).TestCliPath();
        if (probe.Available)
        {
            ClearRecoveredFailure(package, probe.Version);
            return;
        }

        var inspection = WindowsNpmGlobalInstallInspector.Inspect(
            npmBin, package.CommandName, package.PackageScope, package.PackageDirectoryName);
        if (inspection.State != NpmGlobalCliInstallState.PackagePresentShimMissing)
        {
            // Truly uninstalled and unrelated executable failures remain normal
            // capability results. Only a failed attempted repair is an alarm.
            return;
        }
        if (!LocalCliRepairPolicy.ShouldRepairPath(probe.Path, npmBin)) return;

        var now = _time.GetUtcNow();
        _lastAttempts.TryGetValue(package.CliType, out var previousAttempt);
        if (!LocalCliRepairPolicy.CanAttempt(
                previousAttempt == default ? null : previousAttempt, now, RepairCooldown))
            return;

        _lastAttempts[package.CliType] = now;
        var activity = WindowsNpmGlobalInstallInspector.CaptureActivity(inspection, now);
        var started = new LocalCliRepairJournalEntry
        {
            Timestamp = now,
            Event = "repair-started",
            CliType = package.CliType,
            PackageName = package.PackageName,
            Trigger = "cli-not-found-package-present-shim-missing",
            BeforeVersion = inspection.PackageVersion,
            PackagePath = inspection.PackageDirectory,
            PackageModifiedAt = inspection.PackageModifiedAt,
            MissingShims = inspection.MissingShimPaths,
            NpmActivity = activity,
            Command = $"npm install -g {package.PackageName}",
            NextAllowedAttemptAt = now + RepairCooldown,
        };
        await AppendJournalAsync(started, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "local-cli-repair-started cli={CliType} package={Package} beforeVersion={BeforeVersion} nextAllowed={NextAllowed:O}",
            package.CliType, package.PackageName, inspection.PackageVersion ?? "unknown", now + RepairCooldown);

        var command = await RunNpmInstallAsync(package.PackageName, ct).ConfigureAwait(false);
        var afterProbe = _router.Get(package.CliType).TestCliPath();
        var success = command.ExitCode == 0 && afterProbe.Available;
        var error = success
            ? null
            : command.Error ?? (command.ExitCode == 0
                ? $"{package.CommandName} --version still failed after npm install"
                : $"npm install exited {command.ExitCode?.ToString() ?? "without an exit code"}");
        var completedAt = _time.GetUtcNow();
        var completed = started with
        {
            Timestamp = completedAt,
            Event = success ? "repair-succeeded" : "repair-failed",
            AfterVersion = afterProbe.Version,
            NpmActivity = WindowsNpmGlobalInstallInspector.CaptureActivity(inspection, completedAt),
            ExitCode = command.ExitCode,
            OutputTail = command.OutputTail,
            Error = error,
        };
        await AppendJournalAsync(completed, ct).ConfigureAwait(false);

        var status = ToStatus(completed);
        lock (_latest) _latest[package.CliType] = status;
        if (success)
        {
            _logger.LogInformation(
                "local-cli-repair-succeeded cli={CliType} beforeVersion={BeforeVersion} afterVersion={AfterVersion} repairedAt={RepairedAt:O}",
                package.CliType, inspection.PackageVersion ?? "unknown", afterProbe.Version ?? "unknown", completedAt);
        }
        else
        {
            _logger.LogError(
                "local-cli-repair-failed cli={CliType} beforeVersion={BeforeVersion} exitCode={ExitCode} error={Error} nextAllowed={NextAllowed:O}",
                package.CliType, inspection.PackageVersion ?? "unknown", command.ExitCode, error, now + RepairCooldown);
        }
    }

    private void ClearRecoveredFailure(NpmCliPackage package, string? version)
    {
        LocalCliRepairStatus? prior;
        lock (_latest) _latest.TryGetValue(package.CliType, out prior);
        if (prior?.Outcome != "failed") return;

        var recovered = new LocalCliRepairStatus
        {
            CliType = package.CliType,
            Outcome = "recovered",
            OccurredAt = _time.GetUtcNow(),
            BeforeVersion = prior.BeforeVersion,
            AfterVersion = version,
            Error = null,
        };
        lock (_latest) _latest[package.CliType] = recovered;
        _logger.LogInformation(
            "local-cli-repair-alarm-cleared cli={CliType} version={Version} recoveredAt={RecoveredAt:O}",
            package.CliType, version ?? "unknown", recovered.OccurredAt);
    }

    private async Task AppendJournalAsync(LocalCliRepairJournalEntry entry, CancellationToken ct)
    {
        try { await _appender.AppendAsync(JournalPath, entry, JournalJson, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Repair must still proceed when observability persistence is
            // temporarily unavailable. The structured process log retains it.
            _logger.LogInformation(ex, "local-cli-repair journal append failed path={Path}", JournalPath);
        }
    }

    private void LoadJournalState()
    {
        if (_journalLoaded) return;
        _journalLoaded = true;
        if (!File.Exists(JournalPath)) return;

        try
        {
            foreach (var line in File.ReadLines(JournalPath))
            {
                LocalCliRepairJournalEntry? entry;
                try { entry = JsonSerializer.Deserialize<LocalCliRepairJournalEntry>(line, JournalJson); }
                catch (JsonException) { continue; }
                if (entry is null || string.IsNullOrWhiteSpace(entry.CliType)) continue;
                if (entry.Event == "repair-started")
                    _lastAttempts[entry.CliType] = entry.Timestamp;
                if (entry.Event is "repair-succeeded" or "repair-failed")
                    lock (_latest) _latest[entry.CliType] = ToStatus(entry);
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "local-cli-repair journal could not be read path={Path}", JournalPath);
        }
    }

    private string JournalPath
    {
        get
        {
            var root = _configuration["TaskRepository"];
            if (string.IsNullOrWhiteSpace(root)) root = Path.Combine(AppContext.BaseDirectory, "workspace");
            return Path.Combine(root, "logs", "local-cli-repairs.jsonl");
        }
    }

    private static LocalCliRepairStatus ToStatus(LocalCliRepairJournalEntry entry) => new()
    {
        CliType = entry.CliType,
        Outcome = entry.Event == "repair-failed" ? "failed" : "repaired",
        OccurredAt = entry.Timestamp,
        BeforeVersion = entry.BeforeVersion,
        AfterVersion = entry.AfterVersion,
        Error = entry.Error,
    };

    private static async Task<NpmInstallResult> RunNpmInstallAsync(string packageName, CancellationToken ct)
    {
        try
        {
            var npm = GenericCliExecutionService.ResolveExecutable("npm");
            var start = new ProcessStartInfo
            {
                FileName = npm,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("install");
            start.ArgumentList.Add("-g");
            start.ArgumentList.Add(packageName);

            // CreateProcess cannot execute a .cmd file directly with
            // UseShellExecute=false on every supported Windows runtime. Keep
            // the package argument fixed by the internal descriptor table and
            // route only the npm batch dispatcher through cmd.exe.
            if (npm.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                || npm.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            {
                start.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                start.ArgumentList.Clear();
                start.ArgumentList.Add("/d");
                start.ArgumentList.Add("/s");
                start.ArgumentList.Add("/c");
                start.ArgumentList.Add($"\"{npm}\" install -g {packageName}");
            }

            using var process = Process.Start(start);
            if (process is null) return new NpmInstallResult(null, null, "npm process did not start");
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) { SilentCatch.Note(ex, "LocalCliSelfHealService: best-effort timeout kill"); }
                return new NpmInstallResult(null, null, "npm install timed out after 10 minutes");
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) { SilentCatch.Note(ex, "LocalCliSelfHealService: best-effort cancellation kill"); }
                throw;
            }

            var output = (await stdout.ConfigureAwait(false)) + Environment.NewLine
                + (await stderr.ConfigureAwait(false));
            return new NpmInstallResult(process.ExitCode, Tail(output, 4096), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new NpmInstallResult(null, null, $"npm install could not start: {ex.Message}");
        }
    }

    private static string? Tail(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return normalized.Length <= maxLength ? normalized : normalized[^maxLength..];
    }

    public void Dispose()
    {
        _loopCts?.Dispose();
        _probeLock.Dispose();
    }
}

internal sealed record NpmCliPackage(
    string CliType,
    string CommandName,
    string PackageName,
    string PackageScope,
    string PackageDirectoryName);

internal sealed record NpmInstallResult(int? ExitCode, string? OutputTail, string? Error);

public enum NpmGlobalCliInstallState
{
    PackageAbsent,
    PackagePresentShimMissing,
    ShimPresentOrDifferentFailure,
}

public sealed record NpmGlobalCliInstallInspection
{
    public NpmGlobalCliInstallState State { get; init; }
    public string PackageDirectory { get; init; } = "";
    public string? PackageVersion { get; init; }
    public DateTimeOffset? PackageModifiedAt { get; init; }
    public IReadOnlyList<string> MissingShimPaths { get; init; } = [];
}

public sealed record NpmActivityEvidence
{
    public string Path { get; init; } = "";
    public DateTimeOffset ModifiedAt { get; init; }
    public long SizeBytes { get; init; }
    public IReadOnlyList<string> Summary { get; init; } = [];
}

/// <summary>Pure, portable classifier for the Windows npm layout.</summary>
public static class WindowsNpmGlobalInstallInspector
{
    public static string? ResolveNpmBin()
    {
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        if (string.IsNullOrWhiteSpace(appData)) return null;
        var path = Path.Combine(appData, "npm");
        return Directory.Exists(path) ? path : null;
    }

    public static NpmGlobalCliInstallInspection Inspect(
        string npmBin,
        string commandName,
        string packageScope,
        string packageDirectoryName)
    {
        var packageDirectory = Path.Combine(npmBin, "node_modules", packageScope, packageDirectoryName);
        var packageJson = Path.Combine(packageDirectory, "package.json");
        var packagePresent = Directory.Exists(packageDirectory) && File.Exists(packageJson);
        var executableShims = new[]
        {
            Path.Combine(npmBin, commandName + ".cmd"),
            Path.Combine(npmBin, commandName + ".exe"),
        };
        var missingShims = new[]
        {
            Path.Combine(npmBin, commandName),
            Path.Combine(npmBin, commandName + ".cmd"),
            Path.Combine(npmBin, commandName + ".ps1"),
        }.Where(path => !File.Exists(path)).ToArray();
        var hasExecutableShim = executableShims.Any(File.Exists);

        return new NpmGlobalCliInstallInspection
        {
            State = !packagePresent
                ? NpmGlobalCliInstallState.PackageAbsent
                : !hasExecutableShim
                    ? NpmGlobalCliInstallState.PackagePresentShimMissing
                    : NpmGlobalCliInstallState.ShimPresentOrDifferentFailure,
            PackageDirectory = packageDirectory,
            PackageVersion = packagePresent ? ReadPackageVersion(packageJson) : null,
            PackageModifiedAt = packagePresent ? SafeModifiedAt(packageJson) : null,
            MissingShimPaths = missingShims,
        };
    }

    public static IReadOnlyList<NpmActivityEvidence> CaptureActivity(
        NpmGlobalCliInstallInspection inspection,
        DateTimeOffset detectedAt)
    {
        var candidates = new List<string>();
        var cache = Environment.GetEnvironmentVariable("NPM_CONFIG_CACHE");
        if (!string.IsNullOrWhiteSpace(cache)) candidates.Add(Path.Combine(cache, "_logs"));
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrWhiteSpace(localAppData)) candidates.Add(Path.Combine(localAppData, "npm-cache", "_logs"));

        var evidence = new List<NpmActivityEvidence>();
        if (inspection.PackageModifiedAt is { } packageAt)
        {
            evidence.Add(new NpmActivityEvidence
            {
                Path = Path.Combine(inspection.PackageDirectory, "package.json"),
                ModifiedAt = packageAt,
                SizeBytes = SafeLength(Path.Combine(inspection.PackageDirectory, "package.json")),
            });
        }

        var windowStart = detectedAt - TimeSpan.FromHours(4);
        var windowEnd = detectedAt + TimeSpan.FromMinutes(5);
        foreach (var directory in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(directory)) continue;
            try
            {
                evidence.AddRange(Directory.EnumerateFiles(directory, "*.log")
                    .Select(path => new NpmActivityEvidence
                    {
                        Path = path,
                        ModifiedAt = SafeModifiedAt(path) ?? DateTimeOffset.MinValue,
                        SizeBytes = SafeLength(path),
                        Summary = SummarizeNpmLog(path),
                    })
                    .Where(item => item.ModifiedAt >= windowStart && item.ModifiedAt <= windowEnd)
                    .OrderByDescending(item => item.ModifiedAt)
                    .Take(5));
            }
            catch (Exception ex) { SilentCatch.Note(ex, "WindowsNpmGlobalInstallInspector: activity scan"); }
        }
        return evidence.OrderByDescending(item => item.ModifiedAt).Take(6).ToArray();
    }

    /// <summary>
    /// Retains only npm version, invocation, and exit markers. Authentication
    /// configuration and arbitrary error text are intentionally excluded.
    /// </summary>
    public static IReadOnlyList<string> SummarizeNpmLog(string path)
    {
        try
        {
            return File.ReadLines(path)
                .Where(IsSafeActivityLine)
                .Select(line => line.Length <= 300 ? line : line[..300])
                .Take(12)
                .ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static bool IsSafeActivityLine(string line)
    {
        if (line.Contains("token", StringComparison.OrdinalIgnoreCase)
            || line.Contains("password", StringComparison.OrdinalIgnoreCase)
            || line.Contains("credential", StringComparison.OrdinalIgnoreCase))
            return false;

        return line.Contains(" verbose title ", StringComparison.OrdinalIgnoreCase)
            || line.Contains(" verbose argv ", StringComparison.OrdinalIgnoreCase)
            || line.Contains(" info using npm@", StringComparison.OrdinalIgnoreCase)
            || line.Contains(" info using node@", StringComparison.OrdinalIgnoreCase)
            || line.Contains(" verbose exit ", StringComparison.OrdinalIgnoreCase)
            || line.Contains(" verbose code ", StringComparison.OrdinalIgnoreCase);
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
        catch (Exception) { return null; }
    }

    private static DateTimeOffset? SafeModifiedAt(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch (Exception) { return null; }
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception) { return 0; }
    }
}

/// <summary>Side-effect-free rate and path policy used by portable tests.</summary>
public static class LocalCliRepairPolicy
{
    public static bool CanAttempt(DateTimeOffset? lastAttempt, DateTimeOffset now, TimeSpan cooldown)
        => lastAttempt is null || now - lastAttempt.Value >= cooldown;

    public static bool ShouldRepairPath(string? failedPath, string npmBin)
    {
        if (string.IsNullOrWhiteSpace(failedPath)) return true;
        if (!Path.IsPathRooted(failedPath)) return true;
        var relative = Path.GetRelativePath(npmBin, failedPath);
        return relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }
}

public sealed record LocalCliRepairJournalEntry
{
    public DateTimeOffset Timestamp { get; init; }
    public string Event { get; init; } = "";
    public string CliType { get; init; } = "";
    public string PackageName { get; init; } = "";
    public string Trigger { get; init; } = "";
    public string? BeforeVersion { get; init; }
    public string? AfterVersion { get; init; }
    public string? PackagePath { get; init; }
    public DateTimeOffset? PackageModifiedAt { get; init; }
    public IReadOnlyList<string> MissingShims { get; init; } = [];
    public IReadOnlyList<NpmActivityEvidence> NpmActivity { get; init; } = [];
    public string Command { get; init; } = "";
    public int? ExitCode { get; init; }
    public string? OutputTail { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset? NextAllowedAttemptAt { get; init; }
}
