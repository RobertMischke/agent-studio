using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Bus;
using AgentStudio.Persistence;

namespace AgentStudio.Cli;

internal enum NpmCliRepairDecision
{
    Available,
    TrulyUninstalled,
    MissingShimRepair,
    RateLimited,
    Unsupported,
}

internal sealed record NpmCliInstallFacts(
    string CliType,
    bool ExecutableAvailable,
    bool PackageDirectoryPresent,
    bool ShimPresent,
    DateTime ObservedAt,
    DateTime? LastAttemptAt);

/// <summary>
/// Pure classification for the Windows npm failure shape. Package presence is
/// deliberately independent from PATH presence: a missing launcher beside an
/// intact package is repairable, while a missing package is an operator-owned
/// installation decision.
/// </summary>
internal static class NpmCliShimRepairPolicy
{
    public static readonly TimeSpan AttemptInterval = TimeSpan.FromHours(1);

    public static NpmCliRepairDecision Decide(NpmCliInstallFacts facts)
    {
        if (facts.CliType is not (CliTypes.Claude or CliTypes.Codex))
            return NpmCliRepairDecision.Unsupported;
        if (facts.ExecutableAvailable) return NpmCliRepairDecision.Available;
        if (!facts.PackageDirectoryPresent) return NpmCliRepairDecision.TrulyUninstalled;
        if (facts.ShimPresent) return NpmCliRepairDecision.Unsupported;
        if (facts.LastAttemptAt is { } last
            && facts.ObservedAt - last < AttemptInterval)
            return NpmCliRepairDecision.RateLimited;
        return NpmCliRepairDecision.MissingShimRepair;
    }
}

internal sealed record CliRepairFileEvidence(
    string Path,
    bool Exists,
    long? SizeBytes,
    DateTime? LastWriteUtc);

internal sealed record CliRepairNpmLogEvidence(
    string FileName,
    DateTime LastWriteUtc,
    long SizeBytes,
    IReadOnlyList<string> RelevantLines);

internal sealed record CliRepairProcessEvidence(
    string Name,
    int Id,
    DateTime? StartedAtUtc);

internal sealed record CliRepairJournalRecord
{
    public int SchemaVersion { get; init; } = 1;
    public required string CliType { get; init; }
    public required string PackageName { get; init; }
    public required string Outcome { get; init; }
    public required DateTime ObservedAt { get; init; }
    public DateTime? AttemptedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public DateTime? RepairedAt { get; init; }
    public string? VersionBefore { get; init; }
    public string? VersionAfter { get; init; }
    public required string Detail { get; init; }
    public required string PackageDirectory { get; init; }
    public required IReadOnlyList<CliRepairFileEvidence> FilesBefore { get; init; }
    public IReadOnlyList<CliRepairFileEvidence>? FilesAfter { get; init; }
    public required IReadOnlyList<CliRepairNpmLogEvidence> RecentNpmLogs { get; init; }
    public required IReadOnlyList<CliRepairFileEvidence> UpdateArtifacts { get; init; }
    public required IReadOnlyList<CliRepairProcessEvidence> RelevantProcesses { get; init; }
    public string? NpmExecutable { get; init; }
    public int? NpmExitCode { get; init; }
    public string? NpmStdoutTail { get; init; }
    public string? NpmStderrTail { get; init; }

    public CliRepairStatus ToStatus() => new(
        CliType,
        Outcome,
        ObservedAt,
        AttemptedAt,
        RepairedAt,
        VersionBefore,
        VersionAfter,
        Detail);
}

/// <summary>
/// Windows control-plane self-heal for globally installed Claude and Codex npm
/// packages whose launcher shims disappeared. The service probes once per
/// minute, permits at most one reinstall per CLI per hour (including across a
/// backend restart), and writes the complete before/after evidence to an
/// append-only journal.
/// </summary>
public sealed partial class LocalCliRepairService : BackgroundService
{
    public const string JournalFileName = "cli-repairs.jsonl";
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JournalJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };
    private static readonly IReadOnlyDictionary<string, string> Packages =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [CliTypes.Claude] = "@anthropic-ai/claude-code",
            [CliTypes.Codex] = "@openai/codex",
        };

    private readonly CliRouter _router;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LocalCliRepairService> _logger;
    private readonly IJsonlAppender _appender;
    private readonly AgentMessageBusBridge? _bus;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _repairLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly Dictionary<string, DateTime> _lastAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CliRepairStatus> _latest = new(StringComparer.OrdinalIgnoreCase);

    public LocalCliRepairService(
        CliRouter router,
        IConfiguration configuration,
        ILogger<LocalCliRepairService> logger,
        IJsonlAppender appender,
        AgentMessageBusBridge? bus = null,
        TimeProvider? time = null)
    {
        _router = router;
        _configuration = configuration;
        _logger = logger;
        _appender = appender;
        _bus = bus;
        _time = time ?? TimeProvider.System;
        LoadJournalState();
    }

    public IReadOnlyList<CliRepairStatus> Snapshot()
    {
        lock (_stateLock)
        {
            return _latest.Values
                .OrderBy(item => item.CliType, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows()) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var cliType in Packages.Keys)
            {
                try
                {
                    await ProbeAndRepairAsync(cliType, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Local CLI repair probe failed unexpectedly for {Cli}", cliType);
                }
            }

            await Task.Delay(ProbeInterval, _time, stoppingToken).ConfigureAwait(false);
        }
    }

    internal async Task<NpmCliRepairDecision> ProbeAndRepairAsync(
        string cliType,
        CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() || !Packages.TryGetValue(cliType, out var packageName))
            return NpmCliRepairDecision.Unsupported;

        var cli = _router.Get(cliType);
        var initialProbe = cli.TestCliPath();
        if (initialProbe.Available) return NpmCliRepairDecision.Available;

        var observedAt = UtcNow();
        var npmBin = ResolveNpmBin();
        var packageDirectory = Path.Combine(npmBin, "node_modules", PackagePath(packageName));
        var shimPaths = ExpectedShimPaths(npmBin, cliType);
        DateTime? lastAttempt = null;
        lock (_stateLock)
        {
            if (_lastAttempts.TryGetValue(cliType, out var recordedAttempt))
                lastAttempt = recordedAttempt;
        }

        var facts = new NpmCliInstallFacts(
            cliType,
            ExecutableAvailable: false,
            PackageDirectoryPresent: Directory.Exists(packageDirectory),
            ShimPresent: shimPaths.Any(File.Exists),
            observedAt,
            lastAttempt);
        var decision = NpmCliShimRepairPolicy.Decide(facts);
        if (decision != NpmCliRepairDecision.MissingShimRepair) return decision;

        await _repairLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Recheck both the executable and durable attempt budget after the
            // lock. Two simultaneous capability consumers must not run npm.
            var lockedProbe = cli.TestCliPath();
            if (lockedProbe.Available) return NpmCliRepairDecision.Available;
            lock (_stateLock)
            {
                lastAttempt = _lastAttempts.TryGetValue(cliType, out var recordedAttempt)
                    ? recordedAttempt
                    : null;
            }
            var lockedFacts = facts with { ObservedAt = UtcNow(), LastAttemptAt = lastAttempt };
            decision = NpmCliShimRepairPolicy.Decide(lockedFacts);
            if (decision != NpmCliRepairDecision.MissingShimRepair) return decision;

            await RepairAsync(cli, cliType, packageName, packageDirectory, shimPaths, ct)
                .ConfigureAwait(false);
            return NpmCliRepairDecision.MissingShimRepair;
        }
        finally
        {
            _repairLock.Release();
        }
    }

    private async Task RepairAsync(
        ICliExecutionService cli,
        string cliType,
        string packageName,
        string packageDirectory,
        IReadOnlyList<string> shimPaths,
        CancellationToken ct)
    {
        var attemptedAt = UtcNow();
        lock (_stateLock) _lastAttempts[cliType] = attemptedAt;

        var versionBefore = ReadPackageVersion(packageDirectory);
        var filesBefore = CaptureFiles(packageDirectory, shimPaths);
        var activityAnchor = filesBefore
            .Where(item => item.LastWriteUtc.HasValue)
            .Select(item => item.LastWriteUtc!.Value)
            .DefaultIfEmpty(attemptedAt)
            .Max();
        var npmLogs = CaptureRecentNpmLogs(activityAnchor, attemptedAt);
        var updateArtifacts = CaptureUpdateArtifacts(cliType);
        var processes = CaptureRelevantProcesses();
        var npmExecutable = ResolveNpmExecutable();

        _logger.LogInformation(
            "Local CLI missing-shim repair starting for {Cli}: package {Package} remains at {PackageDirectory}; version before {VersionBefore}",
            cliType, packageName, packageDirectory, versionBefore ?? "unknown");

        var command = await RunNpmInstallAsync(npmExecutable, packageName, ct).ConfigureAwait(false);
        var completedAt = UtcNow();
        var afterProbe = cli.TestCliPath();
        var succeeded = command.ExitCode == 0 && afterProbe.Available;
        var versionAfter = afterProbe.Version ?? ReadPackageVersion(packageDirectory);
        var detail = succeeded
            ? $"Restored the missing {cliType} npm launcher with npm install -g."
            : $"npm install -g did not restore the {cliType} launcher: "
              + (command.Error ?? $"npm exited {command.ExitCode}; version probe remained unavailable.");
        var record = new CliRepairJournalRecord
        {
            CliType = cliType,
            PackageName = packageName,
            Outcome = succeeded ? "repaired" : "failed",
            ObservedAt = attemptedAt,
            AttemptedAt = attemptedAt,
            CompletedAt = completedAt,
            RepairedAt = succeeded ? completedAt : null,
            VersionBefore = versionBefore,
            VersionAfter = versionAfter,
            Detail = detail,
            PackageDirectory = packageDirectory,
            FilesBefore = filesBefore,
            FilesAfter = CaptureFiles(packageDirectory, shimPaths),
            RecentNpmLogs = npmLogs,
            UpdateArtifacts = updateArtifacts,
            RelevantProcesses = processes,
            NpmExecutable = npmExecutable,
            NpmExitCode = command.ExitCode,
            NpmStdoutTail = command.StdoutTail,
            NpmStderrTail = command.StderrTail,
        };

        try
        {
            await _appender.AppendAsync(JournalPath(), record, JournalJson, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append local CLI repair journal for {Cli}", cliType);
        }

        lock (_stateLock) _latest[cliType] = record.ToStatus();
        if (_bus is not null)
        {
            _ = _bus.EmitCliRepairAsync(
                cliType,
                succeeded,
                record.RepairedAt,
                versionBefore,
                versionAfter,
                detail,
                JournalPath(),
                CancellationToken.None);
        }

        if (succeeded)
        {
            _logger.LogInformation(
                "Local CLI repaired: {Cli} at {RepairedAt}; version {VersionBefore} -> {VersionAfter}",
                cliType, completedAt, versionBefore ?? "unknown", versionAfter ?? "unknown");
        }
        else
        {
            _logger.LogError(
                "Local CLI repair failed: {Cli} at {CompletedAt}; version {VersionBefore} -> {VersionAfter}; {Detail}",
                cliType, completedAt, versionBefore ?? "unknown", versionAfter ?? "unavailable", detail);
        }
    }

    private void LoadJournalState()
    {
        var path = JournalPath();
        if (!File.Exists(path)) return;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                CliRepairJournalRecord? record;
                try { record = JsonSerializer.Deserialize<CliRepairJournalRecord>(line, JournalJson); }
                catch { continue; }
                if (record is null || !Packages.ContainsKey(record.CliType)) continue;
                lock (_stateLock)
                {
                    if (record.AttemptedAt is { } attempted)
                        _lastAttempts[record.CliType] = attempted;
                    _latest[record.CliType] = record.ToStatus();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load local CLI repair journal at {Path}", path);
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
        return Path.Combine(root, ".runtime", JournalFileName);
    }

    private string ResolveNpmBin()
    {
        var configured = _configuration["CliRepair:NpmBin"];
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var prefix = Environment.GetEnvironmentVariable("NPM_CONFIG_PREFIX");
        if (!string.IsNullOrWhiteSpace(prefix)) return prefix;
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        return string.IsNullOrWhiteSpace(appData)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm")
            : Path.Combine(appData, "npm");
    }

    private string ResolveNpmExecutable()
    {
        var configured = _configuration["CliRepair:NpmExecutable"];
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        return GenericCliExecutionService.ResolveExecutable("npm");
    }

    private static string PackagePath(string packageName)
        => packageName.Replace('/', Path.DirectorySeparatorChar);

    private static IReadOnlyList<string> ExpectedShimPaths(string npmBin, string cliType)
        => [
            Path.Combine(npmBin, cliType),
            Path.Combine(npmBin, cliType + ".cmd"),
            Path.Combine(npmBin, cliType + ".ps1"),
        ];

    private static IReadOnlyList<CliRepairFileEvidence> CaptureFiles(
        string packageDirectory,
        IReadOnlyList<string> shimPaths)
    {
        var paths = shimPaths.Append(Path.Combine(packageDirectory, "package.json"));
        return paths.Select(CaptureFile).ToArray();
    }

    private static CliRepairFileEvidence CaptureFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return new CliRepairFileEvidence(
                path,
                info.Exists,
                info.Exists ? info.Length : null,
                info.Exists ? info.LastWriteTimeUtc : null);
        }
        catch
        {
            return new CliRepairFileEvidence(path, false, null, null);
        }
    }

    private static string? ReadPackageVersion(string packageDirectory)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(packageDirectory, "package.json")));
            return document.RootElement.TryGetProperty("version", out var version)
                ? version.GetString()
                : null;
        }
        catch { return null; }
    }

    private static IReadOnlyList<CliRepairNpmLogEvidence> CaptureRecentNpmLogs(
        DateTime activityAnchor,
        DateTime observedAt)
    {
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA")
                    ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var directory = Path.Combine(local, "npm-cache", "_logs");
        if (!Directory.Exists(directory)) return [];
        try
        {
            var from = activityAnchor.AddHours(-2);
            var until = observedAt.AddMinutes(5);
            return Directory.EnumerateFiles(directory, "*.log")
                .Select(path => new FileInfo(path))
                .Where(info => info.LastWriteTimeUtc >= from && info.LastWriteTimeUtc <= until)
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .Take(5)
                .Select(info => new CliRepairNpmLogEvidence(
                    info.Name,
                    info.LastWriteTimeUtc,
                    info.Length,
                    RelevantNpmLines(info.FullName)))
                .ToArray();
        }
        catch { return []; }
    }

    private static IReadOnlyList<string> RelevantNpmLines(string path)
    {
        try
        {
            return File.ReadLines(path)
                .Where(line => NpmActivityLine().IsMatch(line))
                .TakeLast(12)
                .Select(ScrubNpmLogLine)
                .ToArray();
        }
        catch { return []; }
    }

    private static string ScrubNpmLogLine(string line)
    {
        var scrubbed = NpmToken().Replace(line, "$1[redacted]");
        return scrubbed.Length <= 500 ? scrubbed : scrubbed[..500];
    }

    private static IReadOnlyList<CliRepairFileEvidence> CaptureUpdateArtifacts(string cliType)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, cliType == CliTypes.Claude ? ".claude" : ".codex");
        if (!Directory.Exists(root)) return [];
        try
        {
            return Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
                .Where(path => UpdateArtifactName().IsMatch(Path.GetFileName(path)))
                .Take(20)
                .Select(CaptureFile)
                .ToArray();
        }
        catch { return []; }
    }

    private static IReadOnlyList<CliRepairProcessEvidence> CaptureRelevantProcesses()
    {
        var names = new HashSet<string>(["npm", "node", "claude", "codex"], StringComparer.OrdinalIgnoreCase);
        var result = new List<CliRepairProcessEvidence>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (!names.Contains(process.ProcessName)) continue;
                    DateTime? startedAt = null;
                    try { startedAt = process.StartTime.ToUniversalTime(); }
                    catch (Exception ex) { SilentCatch.Note(ex, "LocalCliRepairService: process start time unavailable"); }
                    result.Add(new CliRepairProcessEvidence(process.ProcessName, process.Id, startedAt));
                }
                catch (Exception ex) { SilentCatch.Note(ex, "LocalCliRepairService: process exited while sampled"); }
            }
        }
        return result;
    }

    private static async Task<NpmInstallResult> RunNpmInstallAsync(
        string npmExecutable,
        string packageName,
        CancellationToken ct)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = npmExecutable,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            process.StartInfo.ArgumentList.Add("install");
            process.StartInfo.ArgumentList.Add("-g");
            process.StartInfo.ArgumentList.Add(packageName);
            if (!process.Start())
                return new NpmInstallResult(null, null, null, "npm process did not start");

            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(InstallTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) { SilentCatch.Note(ex, "LocalCliRepairService: npm timeout kill is best effort"); }
                return new NpmInstallResult(null, Tail(await stdout), Tail(await stderr), "npm install timed out");
            }

            return new NpmInstallResult(
                process.ExitCode,
                Tail(await stdout.ConfigureAwait(false)),
                Tail(await stderr.ConfigureAwait(false)),
                process.ExitCode == 0 ? null : $"npm exited {process.ExitCode}");
        }
        catch (Exception ex)
        {
            return new NpmInstallResult(null, null, null, ex.Message);
        }
    }

    private static string? Tail(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lines = text.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('\n', lines.TakeLast(40));
    }

    private DateTime UtcNow() => _time.GetUtcNow().UtcDateTime;

    private sealed record NpmInstallResult(
        int? ExitCode,
        string? StdoutTail,
        string? StderrTail,
        string? Error);

    [GeneratedRegex("(?:npm|install|update|claude|codex|exit|error|verbose)", RegexOptions.IgnoreCase)]
    private static partial Regex NpmActivityLine();

    [GeneratedRegex("((?:_authToken|token|password)=)[^\\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex NpmToken();

    [GeneratedRegex("(?:update|version|install)", RegexOptions.IgnoreCase)]
    private static partial Regex UpdateArtifactName();
}
