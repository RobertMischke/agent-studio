using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.Persistence;

namespace AgentStudio.Cli;

public enum NpmCliInstallDisposition
{
    Available,
    MissingShimWithPackagePresent,
    TrulyUninstalled,
    BrokenExecutable,
}

public sealed record NpmCliInstallSnapshot(
    bool ExecutableAvailable,
    bool PackagePresent,
    bool LaunchShimPresent);

/// <summary>
/// Pure policy for the Windows npm failure shape. A package directory without
/// its launch shim is repairable; an absent package is an ordinary uninstalled
/// CLI and must not trigger a network mutation.
/// </summary>
public static class NpmCliInstallPolicy
{
    public static NpmCliInstallDisposition Classify(NpmCliInstallSnapshot snapshot)
    {
        if (snapshot.ExecutableAvailable) return NpmCliInstallDisposition.Available;
        if (!snapshot.PackagePresent) return NpmCliInstallDisposition.TrulyUninstalled;
        return snapshot.LaunchShimPresent
            ? NpmCliInstallDisposition.BrokenExecutable
            : NpmCliInstallDisposition.MissingShimWithPackagePresent;
    }
}

public sealed record LocalCliRepairStatus(
    string CliType,
    string State,
    DateTime AttemptedAt,
    DateTime CompletedAt,
    string? VersionBefore,
    string? VersionAfter,
    string Message);

internal sealed record LocalCliRepairActivity(
    IReadOnlyList<LocalCliRepairFileEvidence> Files,
    IReadOnlyList<LocalCliRepairProcessEvidence> Processes,
    IReadOnlyList<LocalCliRepairFileEvidence> RecentNpmLogs);

internal sealed record LocalCliRepairFileEvidence(
    string Path,
    DateTime? LastWriteAt,
    long? SizeBytes);

internal sealed record LocalCliRepairProcessEvidence(
    int ProcessId,
    string Name,
    DateTime? StartedAt);

internal sealed record LocalCliRepairJournalEntry(
    DateTime AttemptedAt,
    DateTime CompletedAt,
    string Kind,
    string CliType,
    string PackageName,
    string PackagePath,
    string ShimPath,
    string? VersionBefore,
    string? VersionAfter,
    string? PackageVersionBefore,
    string? PackageVersionAfter,
    int? NpmExitCode,
    string? NpmOutputTail,
    string? Error,
    LocalCliRepairActivity Before,
    LocalCliRepairActivity After);

/// <summary>
/// Repairs the recurring Windows control-plane failure where npm leaves a
/// globally installed Claude or Codex package in node_modules but removes its
/// command shim. Repair is deliberately narrower than installation: only the
/// package-present, shim-absent shape may run <c>npm install -g</c>.
/// </summary>
public sealed class LocalCliSelfHealService : BackgroundService
{
    internal static readonly TimeSpan RepairCooldown = TimeSpan.FromHours(1);
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan NpmTimeout = TimeSpan.FromMinutes(10);
    private static readonly string[] SupportedCliTypes = [CliTypes.Claude, CliTypes.Codex];
    private static readonly IReadOnlyDictionary<string, string> PackageByCli =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [CliTypes.Claude] = "@anthropic-ai/claude-code",
            [CliTypes.Codex] = "@openai/codex",
        };

    private readonly CliRouter _router;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LocalCliSelfHealService> _logger;
    private readonly IJsonlAppender _appender;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, DateTime> _lastAttemptByCli =
        new(StringComparer.OrdinalIgnoreCase);
    private LocalCliRepairStatus? _latestStatus;

    public LocalCliSelfHealService(
        CliRouter router,
        IConfiguration configuration,
        ILogger<LocalCliSelfHealService> logger,
        IJsonlAppender appender)
    {
        _router = router;
        _configuration = configuration;
        _logger = logger;
        _appender = appender;
    }

    public LocalCliRepairStatus? LatestStatus => Volatile.Read(ref _latestStatus);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows()) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var cliType in SupportedCliTypes)
            {
                if (stoppingToken.IsCancellationRequested) break;
                try { await EnsureAvailableAsync(cliType, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Local CLI capability probe failed for {Cli}", cliType);
                }
            }

            try { await Task.Delay(ProbeInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    /// <summary>
    /// Re-probes one CLI and runs at most one package repair per hour. Returns
    /// the final executable availability so manual task admission can retry in
    /// the same request that discovered the vanished shim.
    /// </summary>
    public async Task<bool> EnsureAvailableAsync(string cliType, CancellationToken ct)
    {
        var normalized = CliTypes.Normalize(cliType);
        var cli = _router.Get(normalized);
        var probe = cli.TestCliPath();
        if (probe.Available)
        {
            var latest = LatestStatus;
            if (latest?.State == "failed"
                && string.Equals(latest.CliType, normalized, StringComparison.OrdinalIgnoreCase))
            {
                Volatile.Write(ref _latestStatus, null);
            }
            return true;
        }
        if (!OperatingSystem.IsWindows()) return false;
        var packageName = PackageFor(normalized);
        if (packageName is null) return false;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            probe = cli.TestCliPath();
            if (probe.Available) return true;

            var install = LocateInstall(normalized, packageName, probe.Path);
            var disposition = NpmCliInstallPolicy.Classify(new NpmCliInstallSnapshot(
                probe.Available,
                Directory.Exists(install.PackagePath),
                File.Exists(install.ShimPath)));
            if (disposition != NpmCliInstallDisposition.MissingShimWithPackagePresent)
            {
                _logger.LogDebug(
                    "Local CLI capability remains unavailable cli={Cli} disposition={Disposition} path={Path}",
                    normalized, disposition, probe.Path);
                return false;
            }

            using var crossProcessLock = TryAcquireCrossProcessLock(normalized);
            if (crossProcessLock is null)
            {
                _logger.LogDebug("CLI shim repair already owned by another backend process cli={Cli}", normalized);
                return false;
            }

            var now = DateTime.UtcNow;
            LoadAttemptHistory();
            if (_lastAttemptByCli.TryGetValue(normalized, out var lastAttempt)
                && now - lastAttempt < RepairCooldown)
            {
                _logger.LogDebug(
                    "CLI shim repair suppressed by one-hour bound cli={Cli} lastAttempt={LastAttempt:o}",
                    normalized, lastAttempt);
                return false;
            }
            _lastAttemptByCli[normalized] = now;

            var before = CaptureActivity(install);
            var packageVersionBefore = ReadPackageVersion(install.PackagePath);
            await AppendJournalAsync(new LocalCliRepairJournalEntry(
                now,
                now,
                "cli-repair-started",
                normalized,
                packageName,
                install.PackagePath,
                install.ShimPath,
                probe.Version ?? packageVersionBefore,
                null,
                packageVersionBefore,
                packageVersionBefore,
                null,
                null,
                null,
                before,
                before), ct).ConfigureAwait(false);
            var npmResult = await RunNpmRepairAsync(packageName, ct).ConfigureAwait(false);
            var verify = cli.TestCliPath();
            var completedAt = DateTime.UtcNow;
            var after = CaptureActivity(install);
            var packageVersionAfter = ReadPackageVersion(install.PackagePath);
            var repaired = npmResult.ExitCode == 0 && verify.Available;
            var error = repaired
                ? null
                : npmResult.Error ?? (npmResult.ExitCode == 0
                    ? $"{normalized} --version was still unavailable after npm repair"
                    : $"npm install -g exited {npmResult.ExitCode}");
            var entry = new LocalCliRepairJournalEntry(
                now,
                completedAt,
                repaired ? "cli-repair-succeeded" : "cli-repair-failed",
                normalized,
                packageName,
                install.PackagePath,
                install.ShimPath,
                probe.Version ?? packageVersionBefore,
                verify.Version,
                packageVersionBefore,
                packageVersionAfter,
                npmResult.ExitCode,
                npmResult.OutputTail,
                error,
                before,
                after);
            await AppendJournalAsync(entry, ct).ConfigureAwait(false);

            var message = repaired
                ? $"{normalized} CLI repaired at {completedAt:O}"
                : $"{normalized} CLI repair failed at {completedAt:O}: {error}";
            Volatile.Write(ref _latestStatus, new LocalCliRepairStatus(
                normalized,
                repaired ? "repaired" : "failed",
                now,
                completedAt,
                entry.VersionBefore,
                entry.VersionAfter,
                message));

            if (repaired)
            {
                _logger.LogInformation(
                    "cli_repair_succeeded cli={Cli} attempted_at={AttemptedAt:o} completed_at={CompletedAt:o} version_before={VersionBefore} version_after={VersionAfter} journal={Journal}",
                    normalized, now, completedAt, entry.VersionBefore, entry.VersionAfter, JournalPath());
            }
            else
            {
                _logger.LogError(
                    "cli_repair_failed cli={Cli} attempted_at={AttemptedAt:o} completed_at={CompletedAt:o} version_before={VersionBefore} version_after={VersionAfter} error={Error} journal={Journal}",
                    normalized, now, completedAt, entry.VersionBefore, entry.VersionAfter, error, JournalPath());
            }
            return repaired;
        }
        finally
        {
            _gate.Release();
        }
    }

    private (string NpmBin, string PackagePath, string ShimPath) LocateInstall(
        string cliType,
        string packageName,
        string probedPath)
    {
        var npmBin = ResolveNpmBin(probedPath);
        var packagePath = Path.Combine(
            npmBin,
            "node_modules",
            packageName.Replace('/', Path.DirectorySeparatorChar));
        return (npmBin, packagePath, Path.Combine(npmBin, cliType + ".cmd"));
    }

    internal static string? PackageFor(string cliType)
        => PackageByCli.GetValueOrDefault(cliType);

    private static string ResolveNpmBin(string probedPath)
    {
        if (Path.IsPathRooted(probedPath))
        {
            var directory = Path.GetDirectoryName(probedPath);
            if (!string.IsNullOrWhiteSpace(directory)) return directory;
        }

        var prefix = Environment.GetEnvironmentVariable("NPM_CONFIG_PREFIX");
        if (!string.IsNullOrWhiteSpace(prefix)) return prefix;
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        return string.IsNullOrWhiteSpace(appData)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm")
            : Path.Combine(appData, "npm");
    }

    private static string? ReadPackageVersion(string packagePath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(packagePath, "package.json")));
            return document.RootElement.TryGetProperty("version", out var version)
                ? version.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static LocalCliRepairActivity CaptureActivity(
        (string NpmBin, string PackagePath, string ShimPath) install)
    {
        var files = new[]
        {
            Evidence(install.ShimPath),
            Evidence(Path.Combine(install.PackagePath, "package.json")),
            Evidence(install.PackagePath),
        };
        var processes = new List<LocalCliRepairProcessEvidence>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var name = process.ProcessName;
                    if (name.Contains("npm", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("node", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("claude", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("codex", StringComparison.OrdinalIgnoreCase))
                    {
                        DateTime? startedAt = null;
                        try { startedAt = process.StartTime.ToUniversalTime(); }
                        catch (Exception ex) { SilentCatch.Note(ex, "LocalCliSelfHealService: process start time unavailable"); }
                        processes.Add(new LocalCliRepairProcessEvidence(process.Id, name, startedAt));
                    }
                }
                catch (Exception ex) { SilentCatch.Note(ex, "LocalCliSelfHealService: process evidence unavailable"); }
            }
        }

        var npmLogs = new List<LocalCliRepairFileEvidence>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var npmLogDir = Path.Combine(localAppData, "npm-cache", "_logs");
        try
        {
            npmLogs.AddRange(Directory.GetFiles(npmLogDir, "*.log")
                .Select(Evidence)
                .Where(item => item.LastWriteAt >= DateTime.UtcNow.Subtract(TimeSpan.FromHours(2)))
                .OrderByDescending(item => item.LastWriteAt)
                .Take(10));
        }
        catch (Exception ex) { SilentCatch.Note(ex, "LocalCliSelfHealService: npm log inventory unavailable"); }

        return new LocalCliRepairActivity(files, processes, npmLogs);
    }

    private static LocalCliRepairFileEvidence Evidence(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists) return new LocalCliRepairFileEvidence(path, info.LastWriteTimeUtc, info.Length);
            var directory = new DirectoryInfo(path);
            return directory.Exists
                ? new LocalCliRepairFileEvidence(path, directory.LastWriteTimeUtc, null)
                : new LocalCliRepairFileEvidence(path, null, null);
        }
        catch
        {
            return new LocalCliRepairFileEvidence(path, null, null);
        }
    }

    private static async Task<(int? ExitCode, string? OutputTail, string? Error)> RunNpmRepairAsync(
        string packageName,
        CancellationToken ct)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = GenericCliExecutionService.ResolveExecutable("npm"),
                Arguments = $"install -g {packageName} --no-audit --no-fund",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null) return (null, null, "npm process did not start");

            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(NpmTimeout);
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) { SilentCatch.Note(ex, "LocalCliSelfHealService: timed-out npm process already exited"); }
                return (null, null, "npm install -g timed out after 10 minutes");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) { SilentCatch.Note(ex, "LocalCliSelfHealService: cancelled npm process already exited"); }
                throw;
            }
            var output = $"{await stdout.ConfigureAwait(false)}\n{await stderr.ConfigureAwait(false)}".Trim();
            return (process.ExitCode, Tail(output, 4_000), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, null, ex.Message);
        }
    }

    private async Task AppendJournalAsync(LocalCliRepairJournalEntry entry, CancellationToken ct)
    {
        try
        {
            await _appender.AppendAsync(JournalPath(), entry, JournalJson, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append local CLI repair journal");
        }
    }

    private void LoadAttemptHistory()
    {
        try
        {
            var path = JournalPath();
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (!root.TryGetProperty("cliType", out var cliElement)
                        || !root.TryGetProperty("attemptedAt", out var atElement)
                        || !DateTime.TryParse(atElement.GetString(), out var attemptedAt))
                    {
                        continue;
                    }
                    var cliType = cliElement.GetString();
                    if (string.IsNullOrWhiteSpace(cliType)) continue;
                    var utc = attemptedAt.ToUniversalTime();
                    if (!_lastAttemptByCli.TryGetValue(cliType, out var current) || utc > current)
                        _lastAttemptByCli[cliType] = utc;
                }
                catch (JsonException ex)
                {
                    // A torn final JSONL row must not discard older cooldown evidence.
                    SilentCatch.Note(ex, "LocalCliSelfHealService: skipped torn journal row");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to recover CLI repair cooldown from {Journal}", JournalPath());
        }
    }

    private FileStream? TryAcquireCrossProcessLock(string cliType)
    {
        try
        {
            var directory = Path.GetDirectoryName(JournalPath());
            if (string.IsNullOrWhiteSpace(directory)) return null;
            Directory.CreateDirectory(directory);
            return new FileStream(
                Path.Combine(directory, $"cli-repair-{cliType}.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "CLI repair lock is not writable for {Cli}", cliType);
            return null;
        }
    }

    private string JournalPath()
    {
        var root = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "agent-taskboard");
        }
        return Path.Combine(root, "logs", "cli-repairs.jsonl");
    }

    private static string Tail(string value, int maxLength)
        => value.Length <= maxLength ? value : value[^maxLength..];

    private static readonly JsonSerializerOptions JournalJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
