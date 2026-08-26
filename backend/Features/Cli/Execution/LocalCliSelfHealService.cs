using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Cli;

/// <summary>
/// Local Windows-host repair coordinator for npm packages whose global package
/// directory still exists but whose executable shim vanished. Detection is
/// read-only and deterministic; the only repair is a bounded
/// <c>npm install --global</c> of the already-present package.
/// </summary>
public sealed class LocalCliSelfHealService
{
    internal static readonly TimeSpan MinimumAttemptInterval = TimeSpan.FromHours(1);
    private static readonly string[] RepairableCliTypes = ["claude", "codex"];
    private static readonly JsonSerializerOptions JournalJson = new(JsonSerializerDefaults.Web);

    private readonly IConfiguration _configuration;
    private readonly ILogger<LocalCliSelfHealService> _logger;
    private readonly IJsonlAppender _appender;
    private readonly INpmGlobalInstaller _installer;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _lastAttempt = new(StringComparer.OrdinalIgnoreCase);
    private CliRepairEvent? _latestCompleted;

    public LocalCliSelfHealService(
        IConfiguration configuration,
        ILogger<LocalCliSelfHealService> logger,
        IJsonlAppender appender,
        INpmGlobalInstaller installer)
        : this(configuration, logger, appender, installer, TimeProvider.System)
    {
    }

    internal LocalCliSelfHealService(
        IConfiguration configuration,
        ILogger<LocalCliSelfHealService> logger,
        IJsonlAppender appender,
        INpmGlobalInstaller installer,
        TimeProvider time)
    {
        _configuration = configuration;
        _logger = logger;
        _appender = appender;
        _installer = installer;
        _time = time;
        LoadJournalState();
    }

    public CliRepairNotice? LatestNotice
    {
        get
        {
            var latest = Volatile.Read(ref _latestCompleted);
            if (latest is null) return null;
            return new CliRepairNotice
            {
                CliType = latest.CliType,
                Status = latest.Status,
                CompletedAt = latest.CompletedAt ?? latest.AttemptedAt,
                VersionBefore = latest.VersionBefore,
                VersionAfter = latest.VersionAfter,
                Message = latest.Message,
            };
        }
    }

    public async Task ProbeKnownAsync(CliRouter router, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var cliType in RepairableCliTypes)
            await ProbeAndRepairAsync(router.Get(cliType), ct);
    }

    public async Task<LocalCliProbeResult> ProbeAndRepairAsync(
        ICliExecutionService cli,
        CancellationToken ct)
    {
        var initial = cli.TestCliPath();
        if (initial.Available || !OperatingSystem.IsWindows())
            return new LocalCliProbeResult(initial.Available, initial.Version, initial.Path, false, null);

        var npmBin = ResolveNpmBin();
        if (npmBin is null)
            return new LocalCliProbeResult(false, null, initial.Path, false, "npm global bin is unavailable");

        var inspection = NpmGlobalCliPackageInspector.Inspect(cli.CliType, npmBin);
        if (!inspection.MissingShimWithPackagePresent || inspection.PackageName is null)
        {
            return new LocalCliProbeResult(
                false,
                null,
                initial.Path,
                false,
                inspection.PackagePresent
                    ? "CLI package is present, but this is not the missing Windows command-shim shape"
                    : "CLI package is not installed in the npm global package directory");
        }

        var gate = _gates.GetOrAdd(cli.CliType, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var recheck = cli.TestCliPath();
            if (recheck.Available)
                return new LocalCliProbeResult(true, recheck.Version, recheck.Path, false, null);

            var now = _time.GetUtcNow().UtcDateTime;
            if (_lastAttempt.TryGetValue(cli.CliType, out var last) && !MayAttempt(last, now))
            {
                return new LocalCliProbeResult(
                    false,
                    null,
                    recheck.Path,
                    false,
                    $"CLI repair is rate-limited until {last.Add(MinimumAttemptInterval):O}");
            }

            _lastAttempt[cli.CliType] = now;
            var attemptId = Guid.NewGuid().ToString("N");
            var activityBefore = CaptureNpmActivity(now);
            var attempting = new CliRepairEvent
            {
                AttemptId = attemptId,
                AttemptedAt = now,
                Status = CliRepairStatuses.Attempting,
                CliType = cli.CliType,
                PackageName = inspection.PackageName,
                PackagePath = inspection.PackagePath,
                VersionBefore = inspection.PackageVersion,
                PackageModifiedAtBefore = inspection.PackageModifiedAt,
                MissingShims = MissingShims(inspection),
                NpmActivityBefore = activityBefore,
                Message = "Package present while the Windows command shim is missing; starting bounded npm global reinstall.",
            };
            await AppendAsync(attempting, ct);

            _logger.LogInformation(
                "local-cli-repair-started cli={Cli} package={Package} versionBefore={VersionBefore} attempt={AttemptId}",
                cli.CliType,
                inspection.PackageName,
                inspection.PackageVersion ?? "<unknown>",
                attemptId);

            var install = await _installer.InstallAsync(inspection.PackageName, ct);
            var verified = cli.TestCliPath();
            var afterInspection = NpmGlobalCliPackageInspector.Inspect(cli.CliType, npmBin);
            var completedAt = _time.GetUtcNow().UtcDateTime;
            var repaired = verified.Available;
            var completed = attempting with
            {
                CompletedAt = completedAt,
                Status = repaired ? CliRepairStatuses.Repaired : CliRepairStatuses.Failed,
                VersionAfter = verified.Version ?? afterInspection.PackageVersion,
                PackageModifiedAtAfter = afterInspection.PackageModifiedAt,
                NpmActivityAfter = CaptureNpmActivity(completedAt),
                NpmExitCode = install.ExitCode,
                NpmStandardOutput = install.StandardOutput,
                NpmStandardError = install.StandardError,
                Error = repaired ? null : install.Error ?? "CLI --version probe still failed after npm reinstall",
                Message = repaired
                    ? "CLI repaired and verified with --version."
                    : "CLI repair failed; operator attention is required.",
            };
            // Once npm has started, persist its terminal state even if the HTTP
            // request or job that triggered the probe was cancelled meanwhile.
            await AppendAsync(completed, CancellationToken.None);
            Volatile.Write(ref _latestCompleted, completed);

            if (repaired)
            {
                _logger.LogInformation(
                    "local-cli-repair-completed cli={Cli} versionBefore={VersionBefore} versionAfter={VersionAfter} attempt={AttemptId}",
                    cli.CliType,
                    completed.VersionBefore ?? "<unknown>",
                    completed.VersionAfter ?? "<unknown>",
                    attemptId);
            }
            else
            {
                _logger.LogWarning(
                    "local-cli-repair-failed cli={Cli} versionBefore={VersionBefore} npmExit={ExitCode} error={Error} attempt={AttemptId}",
                    cli.CliType,
                    completed.VersionBefore ?? "<unknown>",
                    completed.NpmExitCode,
                    completed.Error,
                    attemptId);
            }

            return new LocalCliProbeResult(
                repaired,
                completed.VersionAfter,
                verified.Path,
                true,
                completed.Error);
        }
        finally
        {
            gate.Release();
        }
    }

    internal static bool MayAttempt(DateTime lastAttemptUtc, DateTime nowUtc) =>
        nowUtc - lastAttemptUtc >= MinimumAttemptInterval;

    private string? ResolveNpmBin()
    {
        var configured = _configuration["CliSelfHeal:NpmBin"];
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        return string.IsNullOrWhiteSpace(appData) ? null : Path.Combine(appData, "npm");
    }

    private string JournalPath()
    {
        var workspace = _configuration["TaskRepository"];
        if (!string.IsNullOrWhiteSpace(workspace))
            return Path.Combine(workspace, "logs", "cli-repairs.jsonl");

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrWhiteSpace(local)
            ? Path.Combine(AppContext.BaseDirectory, "runtime")
            : Path.Combine(local, "agent-taskboard");
        return Path.Combine(root, "logs", "cli-repairs.jsonl");
    }

    private async Task AppendAsync(CliRepairEvent evt, CancellationToken ct)
    {
        try { await _appender.AppendAsync(JournalPath(), evt, JournalJson, ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append local CLI repair journal for {Cli}", evt.CliType);
        }
    }

    private void LoadJournalState()
    {
        var path = JournalPath();
        if (!File.Exists(path)) return;
        try
        {
            foreach (var line in File.ReadLines(path).TakeLast(200))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                CliRepairEvent? evt;
                try { evt = JsonSerializer.Deserialize<CliRepairEvent>(line, JournalJson); }
                catch (JsonException ex)
                {
                    SilentCatch.Note(ex, "LocalCliSelfHealService: skip malformed journal row");
                    continue;
                }
                if (evt is null || string.IsNullOrWhiteSpace(evt.CliType)) continue;
                _lastAttempt.AddOrUpdate(evt.CliType, evt.AttemptedAt, (_, old) => evt.AttemptedAt > old ? evt.AttemptedAt : old);
                if (evt.Status is CliRepairStatuses.Repaired or CliRepairStatuses.Failed
                    && (_latestCompleted is null || (evt.CompletedAt ?? evt.AttemptedAt) > (_latestCompleted.CompletedAt ?? _latestCompleted.AttemptedAt)))
                {
                    _latestCompleted = evt;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to restore local CLI repair journal state from {Path}", path);
        }
    }

    private IReadOnlyList<NpmActivityEvidence> CaptureNpmActivity(DateTime observedAt)
    {
        var configured = _configuration["CliSelfHeal:NpmCache"];
        var cache = configured;
        if (string.IsNullOrWhiteSpace(cache))
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            cache = string.IsNullOrWhiteSpace(local) ? null : Path.Combine(local, "npm-cache");
        }
        if (string.IsNullOrWhiteSpace(cache)) return [];

        var logs = Path.Combine(cache, "_logs");
        if (!Directory.Exists(logs)) return [];
        try
        {
            return Directory.EnumerateFiles(logs, "*.log")
                .Select(path => new FileInfo(path))
                .Where(file =>
                {
                    var age = observedAt - file.LastWriteTimeUtc;
                    return age >= TimeSpan.FromMinutes(-5) && age <= TimeSpan.FromHours(24);
                })
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(5)
                .Select(file => new NpmActivityEvidence
                {
                    FileName = file.Name,
                    ModifiedAt = file.LastWriteTimeUtc,
                    SizeBytes = file.Length,
                    RelevantLines = RelevantNpmLines(file.FullName),
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "npm activity capture failed for {Logs}", logs);
            return [];
        }
    }

    private static IReadOnlyList<string> RelevantNpmLines(string path)
    {
        try
        {
            return File.ReadLines(path)
                .Where(line => line.Contains("command", StringComparison.OrdinalIgnoreCase)
                               || line.Contains("install", StringComparison.OrdinalIgnoreCase)
                               || line.Contains("update", StringComparison.OrdinalIgnoreCase)
                               || line.Contains("postinstall", StringComparison.OrdinalIgnoreCase)
                               || line.Contains("@anthropic-ai", StringComparison.OrdinalIgnoreCase)
                               || line.Contains("@openai", StringComparison.OrdinalIgnoreCase))
                .TakeLast(8)
                .Select(line => CredentialRedactor.Redact(line.Trim()))
                .Select(line => line.Length <= 500 ? line : line[..500])
                .ToList();
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "LocalCliSelfHealService: npm log unreadable");
            return [];
        }
    }

    private static IReadOnlyList<string> MissingShims(NpmCliInstallInspection inspection)
    {
        var missing = new List<string>();
        if (!inspection.CommandShimPresent && inspection.CommandShimPath is not null)
            missing.Add(Path.GetFileName(inspection.CommandShimPath));
        if (!inspection.ShellShimPresent && inspection.ShellShimPath is not null)
            missing.Add(Path.GetFileName(inspection.ShellShimPath));
        if (!inspection.PowerShellShimPresent && inspection.PowerShellShimPath is not null)
            missing.Add(Path.GetFileName(inspection.PowerShellShimPath));
        return missing;
    }
}

public sealed record LocalCliProbeResult(
    bool Available,
    string? Version,
    string Path,
    bool RepairAttempted,
    string? Error);

internal static class CliRepairStatuses
{
    internal const string Attempting = "attempting";
    internal const string Repaired = "repaired";
    internal const string Failed = "failed";
}

internal sealed record CliRepairEvent
{
    [JsonPropertyName("attemptId")] public string AttemptId { get; init; } = string.Empty;
    [JsonPropertyName("attemptedAt")] public DateTime AttemptedAt { get; init; }
    [JsonPropertyName("completedAt")] public DateTime? CompletedAt { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;
    [JsonPropertyName("cliType")] public string CliType { get; init; } = string.Empty;
    [JsonPropertyName("packageName")] public string PackageName { get; init; } = string.Empty;
    [JsonPropertyName("packagePath")] public string? PackagePath { get; init; }
    [JsonPropertyName("versionBefore")] public string? VersionBefore { get; init; }
    [JsonPropertyName("versionAfter")] public string? VersionAfter { get; init; }
    [JsonPropertyName("packageModifiedAtBefore")] public DateTime? PackageModifiedAtBefore { get; init; }
    [JsonPropertyName("packageModifiedAtAfter")] public DateTime? PackageModifiedAtAfter { get; init; }
    [JsonPropertyName("missingShims")] public IReadOnlyList<string> MissingShims { get; init; } = [];
    [JsonPropertyName("npmActivityBefore")] public IReadOnlyList<NpmActivityEvidence> NpmActivityBefore { get; init; } = [];
    [JsonPropertyName("npmActivityAfter")] public IReadOnlyList<NpmActivityEvidence> NpmActivityAfter { get; init; } = [];
    [JsonPropertyName("npmExitCode")] public int? NpmExitCode { get; init; }
    [JsonPropertyName("npmStandardOutput")] public string? NpmStandardOutput { get; init; }
    [JsonPropertyName("npmStandardError")] public string? NpmStandardError { get; init; }
    [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;
    [JsonPropertyName("error")] public string? Error { get; init; }
}

internal sealed record NpmActivityEvidence
{
    [JsonPropertyName("fileName")] public string FileName { get; init; } = string.Empty;
    [JsonPropertyName("modifiedAt")] public DateTime ModifiedAt { get; init; }
    [JsonPropertyName("sizeBytes")] public long SizeBytes { get; init; }
    [JsonPropertyName("relevantLines")] public IReadOnlyList<string> RelevantLines { get; init; } = [];
}
