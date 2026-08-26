using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using AgentStudio.Diagnostics;
using AgentStudio.Persistence;
using Microsoft.Extensions.Options;

namespace AgentStudio.Cli;

/// <summary>
/// Detects and repairs the Windows npm failure shape where a supported CLI's
/// package still exists but its Windows-launchable global shims have disappeared.
/// True uninstalls and executable failures with an intact shim remain operator
/// decisions. Repair attempts are durable, auditable, and limited to one per
/// CLI per hour across backend restarts.
/// </summary>
public sealed class LocalCliRepairService
{
    internal static readonly TimeSpan RepairCooldown = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions JournalJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IConfiguration _configuration;
    private readonly ILogger<LocalCliRepairService> _logger;
    private readonly IJsonlAppender _appender;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string?> _lastObservedVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LocalCliRepairStatus> _latest = new(StringComparer.OrdinalIgnoreCase);

    public LocalCliRepairService(
        IConfiguration configuration,
        ILogger<LocalCliRepairService> logger,
        IOptions<BackendFileLoggerOptions> logOptions,
        IJsonlAppender? appender = null,
        TimeProvider? timeProvider = null)
    {
        _configuration = configuration;
        _logger = logger;
        _appender = appender ?? new JsonlAppender();
        _time = timeProvider ?? TimeProvider.System;
        JournalPath = Path.Combine(Path.GetFullPath(logOptions.Value.LogDirectory), "cli-repairs.jsonl");
        RestoreJournalState();
    }

    public string JournalPath { get; }

    public IReadOnlyList<LocalCliRepairStatus> Snapshot()
        => _latest.Values.OrderBy(status => status.CliType, StringComparer.OrdinalIgnoreCase).ToArray();

    public async Task<IReadOnlyList<LocalCliRepairStatus>> ProbeConfiguredAsync(
        CliRouter router,
        CancellationToken ct)
    {
        foreach (var cliType in new[] { CliTypes.Claude, CliTypes.Codex })
        {
            await EnsureAvailableAsync(router.Get(cliType), ct).ConfigureAwait(false);
        }
        return Snapshot();
    }

    /// <summary>
    /// Probes the exact configured adapter. An available probe only refreshes
    /// the last-known version. An unavailable probe reaches npm only for the
    /// missing-shim/package-present shape.
    /// </summary>
    public async Task<(bool Ok, string? Error)> EnsureAvailableAsync(
        ICliExecutionService cli,
        CancellationToken ct)
        => await EnsureAvailableAsync(cli.CliType, () => cli.TestCliPath(), ct).ConfigureAwait(false);

    internal async Task<(bool Ok, string? Error)> EnsureAvailableAsync(
        string cliType,
        Func<(bool Available, string? Version, string Path)> probe,
        CancellationToken ct)
    {
        var initial = probe();
        if (initial.Available)
        {
            ObserveAvailable(cliType, initial.Version);
            return (true, null);
        }

        if (!OperatingSystem.IsWindows())
            return (false, $"--version probe failed at '{initial.Path}'");

        var descriptor = DescriptorFor(cliType);
        if (descriptor is null)
            return (false, $"--version probe failed at '{initial.Path}'");

        var npmBin = ResolveNpmBin();
        var inspection = InspectGlobalInstall(descriptor, npmBin);
        if (inspection.Kind != NpmCliInstallKind.MissingShimWithPackage)
        {
            var state = inspection.Kind == NpmCliInstallKind.PackageMissing
                ? "uninstalled"
                : "shim-present-but-unusable";
            _latest[descriptor.CliType] = new LocalCliRepairStatus(
                descriptor.CliType,
                state,
                null,
                null,
                inspection.PackageVersion,
                null,
                inspection.Detail);
            _logger.LogInformation(
                "Local CLI probe classified {Cli} as {State}; automatic npm repair is not eligible. {Detail}",
                descriptor.CliType, state, inspection.Detail);
            return (false, inspection.Detail);
        }

        var gate = _gates.GetOrAdd(descriptor.CliType, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var reprobe = probe();
            if (reprobe.Available)
            {
                ObserveAvailable(descriptor.CliType, reprobe.Version);
                return (true, null);
            }

            var now = _time.GetUtcNow();
            if (_lastAttempts.TryGetValue(descriptor.CliType, out var lastAttempt)
                && !CanAttempt(lastAttempt, now))
            {
                var next = lastAttempt + RepairCooldown;
                var detail = $"missing npm shim repair is rate-limited until {next:O}";
                // Keep a failed attempt acute for the whole cooldown. Replacing
                // it with a neutral cooldown receipt would hide the only state
                // that should alarm the operator.
                if (_latest.TryGetValue(descriptor.CliType, out var latest)
                    && latest.State == "failed")
                {
                    return (false, latest.Detail);
                }
                _latest[descriptor.CliType] = new LocalCliRepairStatus(
                    descriptor.CliType,
                    "cooldown",
                    null,
                    lastAttempt,
                    inspection.PackageVersion,
                    null,
                    detail);
                return (false, detail);
            }

            _lastAttempts[descriptor.CliType] = now;
            return await RepairAsync(probe, descriptor, inspection, npmBin, now, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal static bool CanAttempt(DateTimeOffset lastAttempt, DateTimeOffset now)
        => now - lastAttempt >= RepairCooldown;

    internal static NpmCliInstallInspection InspectGlobalInstall(string cliType, string npmBin)
        => InspectGlobalInstall(
            DescriptorFor(cliType) ?? throw new ArgumentOutOfRangeException(nameof(cliType)),
            npmBin);

    private static NpmCliInstallInspection InspectGlobalInstall(NpmCliDescriptor descriptor, string npmBin)
    {
        var packageDirectory = Path.Combine(
            npmBin,
            "node_modules",
            descriptor.PackageSegments[0],
            descriptor.PackageSegments[1]);
        var packagePresent = Directory.Exists(packageDirectory);
        var shimPaths = new[] { "", ".cmd", ".ps1", ".exe" }
            .Select(extension => Path.Combine(npmBin, descriptor.Command + extension))
            .ToArray();
        var presentShims = shimPaths.Where(File.Exists).ToArray();
        // Process.Start resolves a bare command through PATHEXT on Windows.
        // A stranded PowerShell shim is therefore evidence of an interrupted
        // npm link step, not an executable command for the backend. The
        // extensionless shell shim is useful to Git Bash but likewise cannot
        // make a failed Windows Process.Start probe healthy on its own.
        var presentLaunchShims = new[] { ".cmd", ".exe" }
            .Select(extension => Path.Combine(npmBin, descriptor.Command + extension))
            .Where(File.Exists)
            .ToArray();
        var packageVersion = ReadPackageVersion(Path.Combine(packageDirectory, "package.json"));

        if (!packagePresent)
        {
            return new NpmCliInstallInspection(
                descriptor.CliType,
                NpmCliInstallKind.PackageMissing,
                packageDirectory,
                packageVersion,
                presentShims,
                $"npm package '{descriptor.PackageName}' is not installed under '{packageDirectory}'");
        }

        if (presentLaunchShims.Length == 0)
        {
            return new NpmCliInstallInspection(
                descriptor.CliType,
                NpmCliInstallKind.MissingShimWithPackage,
                packageDirectory,
                packageVersion,
                presentShims,
                presentShims.Length == 0
                    ? $"npm package '{descriptor.PackageName}' is present but command shims are missing from '{npmBin}'"
                    : $"npm package '{descriptor.PackageName}' is present but no Windows-launchable command shim exists in '{npmBin}'; stranded shims: {string.Join(", ", presentShims.Select(Path.GetFileName))}");
        }

        return new NpmCliInstallInspection(
            descriptor.CliType,
            NpmCliInstallKind.ShimPresent,
            packageDirectory,
            packageVersion,
            presentShims,
            $"npm package and {presentShims.Length} command shim(s) are present; the failure is not a missing-shim repair case");
    }

    private async Task<(bool Ok, string? Error)> RepairAsync(
        Func<(bool Available, string? Version, string Path)> probe,
        NpmCliDescriptor descriptor,
        NpmCliInstallInspection before,
        string npmBin,
        DateTimeOffset attemptedAt,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var previousObservedVersion = _lastObservedVersions.GetValueOrDefault(descriptor.CliType);
        var activityBefore = CaptureActivity(descriptor, before, npmBin, attemptedAt);
        await AppendJournalAsync(new LocalCliRepairJournalRecord(
            attemptedAt,
            descriptor.CliType,
            descriptor.PackageName,
            "missing-shim-with-package-present",
            "attempting",
            previousObservedVersion,
            before.PackageVersion,
            null,
            null,
            -1,
            0,
            null,
            "",
            "",
            activityBefore)).ConfigureAwait(false);
        var result = await RunNpmInstallAsync(descriptor, ct).ConfigureAwait(false);
        var afterInspection = InspectGlobalInstall(descriptor, npmBin);
        var afterProbe = probe();
        if (afterProbe.Available) ObserveAvailable(descriptor.CliType, afterProbe.Version);

        var repaired = result.ExitCode == 0 && afterProbe.Available;
        var error = repaired
            ? null
            : result.Error
              ?? $"npm exited {result.ExitCode}; --version remained unavailable at '{afterProbe.Path}'";
        var activityAfter = CaptureActivity(descriptor, afterInspection, npmBin, _time.GetUtcNow());
        var record = new LocalCliRepairJournalRecord(
            attemptedAt,
            descriptor.CliType,
            descriptor.PackageName,
            "missing-shim-with-package-present",
            repaired ? "repaired" : "failed",
            previousObservedVersion,
            before.PackageVersion,
            afterInspection.PackageVersion,
            afterProbe.Version,
            result.ExitCode,
            stopwatch.ElapsedMilliseconds,
            error,
            TrimOutput(result.StandardOutput),
            TrimOutput(result.StandardError),
            [.. activityBefore, .. activityAfter]);

        await AppendJournalAsync(record).ConfigureAwait(false);

        var status = new LocalCliRepairStatus(
            descriptor.CliType,
            repaired ? "repaired" : "failed",
            repaired ? attemptedAt : null,
            attemptedAt,
            afterInspection.PackageVersion,
            afterProbe.Version,
            repaired
                ? $"CLI repaired at {attemptedAt.ToLocalTime():g}"
                : $"CLI repair failed at {attemptedAt.ToLocalTime():g}: {error}");
        _latest[descriptor.CliType] = status;

        if (repaired)
        {
            _logger.LogInformation(
                "Local CLI repaired cli={Cli} at={RepairedAt} previousVersion={PreviousVersion} packageBefore={PackageBefore} packageAfter={PackageAfter} cliAfter={CliAfter} journal={JournalPath}",
                descriptor.CliType,
                attemptedAt,
                previousObservedVersion ?? "unknown",
                before.PackageVersion ?? "unknown",
                afterInspection.PackageVersion ?? "unknown",
                afterProbe.Version ?? "unknown",
                JournalPath);
            return (true, null);
        }

        _logger.LogError(
            "Local CLI repair failed cli={Cli} at={AttemptedAt} error={Error} journal={JournalPath}",
            descriptor.CliType, attemptedAt, error, JournalPath);
        return (false, error);
    }

    private async Task<NpmInstallResult> RunNpmInstallAsync(
        NpmCliDescriptor descriptor,
        CancellationToken ct)
    {
        var npm = GenericCliExecutionService.ResolveExecutable(
            _configuration["NpmCli:Path"] ?? "npm");
        var commandProcessor = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        var psi = new ProcessStartInfo
        {
            FileName = commandProcessor,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("/d");
        psi.ArgumentList.Add("/s");
        psi.ArgumentList.Add("/c");
        // cmd.exe /s /c requires the executable quotes to sit inside one
        // additional outer quote pair when npm.cmd lives below a spaced path.
        psi.ArgumentList.Add(BuildWindowsNpmCommand(npm, descriptor.PackageName));

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return new NpmInstallResult(-1, "", "", "Process.Start returned null");

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) { AgentStudio.Diagnostics.SilentCatch.Note(ex, "LocalCliRepairService: best-effort npm timeout cleanup"); }
                var reason = ct.IsCancellationRequested
                    ? "npm install was canceled after the triggering operation ended"
                    : "npm install timed out after five minutes";
                return new NpmInstallResult(
                    -1,
                    await stdoutTask.ConfigureAwait(false),
                    await stderrTask.ConfigureAwait(false),
                    reason);
            }

            return new NpmInstallResult(
                process.ExitCode,
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false),
                process.ExitCode == 0 ? null : $"npm install exited {process.ExitCode}");
        }
        catch (Exception ex)
        {
            return new NpmInstallResult(-1, "", "", $"npm install could not start: {ex.Message}");
        }
    }

    private async Task AppendJournalAsync(LocalCliRepairJournalRecord record)
    {
        try
        {
            // A repair is a material host mutation. Persist the pre-mutation
            // fence and terminal receipt even when the HTTP probe or launch
            // request that noticed the breakage disconnects while npm runs.
            await _appender.AppendAsync(
                JournalPath,
                record,
                JournalJson,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to append local CLI repair journal {JournalPath}", JournalPath);
        }
    }

    private void ObserveAvailable(string cliType, string? version)
    {
        if (!string.IsNullOrWhiteSpace(version)) _lastObservedVersions[cliType] = version;
        if (_latest.TryGetValue(cliType, out var current) && current.State == "repaired") return;
        _latest.TryRemove(cliType, out _);
    }

    private void RestoreJournalState()
    {
        if (!File.Exists(JournalPath)) return;
        try
        {
            foreach (var line in File.ReadLines(JournalPath).TakeLast(200))
            {
                LocalCliRepairJournalRecord? record;
                try { record = JsonSerializer.Deserialize<LocalCliRepairJournalRecord>(line, JournalJson); }
                catch (JsonException) { continue; }
                if (record is null || string.IsNullOrWhiteSpace(record.CliType)) continue;
                _lastAttempts[record.CliType] = record.OccurredAt;
                if (!string.IsNullOrWhiteSpace(record.CliVersionAfter))
                    _lastObservedVersions[record.CliType] = record.CliVersionAfter;
                var state = record.Outcome is "repaired" or "failed"
                    ? record.Outcome
                    : "cooldown";
                _latest[record.CliType] = new LocalCliRepairStatus(
                    record.CliType,
                    state,
                    record.Outcome == "repaired" ? record.OccurredAt : null,
                    record.OccurredAt,
                    record.PackageVersionAfter,
                    record.CliVersionAfter,
                    record.Outcome switch
                    {
                        "repaired" => $"CLI repaired at {record.OccurredAt.ToLocalTime():g}",
                        "failed" => $"CLI repair failed at {record.OccurredAt.ToLocalTime():g}: {record.Error}",
                        _ => $"CLI repair began at {record.OccurredAt.ToLocalTime():g}; another attempt is rate-limited",
                    });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not restore local CLI repair state from {JournalPath}", JournalPath);
        }
    }

    private static IReadOnlyList<NpmActivityEvidence> CaptureActivity(
        NpmCliDescriptor descriptor,
        NpmCliInstallInspection inspection,
        string npmBin,
        DateTimeOffset observedAt)
    {
        var evidence = new List<NpmActivityEvidence>
        {
            new("package", observedAt, $"{descriptor.PackageName} version={inspection.PackageVersion ?? "unknown"} path={inspection.PackageDirectory} modified={SafeLastWrite(inspection.PackageDirectory):O}"),
            new("shims", observedAt, inspection.PresentShims.Count == 0
                ? $"No {descriptor.Command} command shims present in {npmBin}"
                : $"Present shims: {string.Join(", ", inspection.PresentShims.Select(Path.GetFileName))}"),
        };

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var npmLogs = string.IsNullOrWhiteSpace(localAppData)
            ? ""
            : Path.Combine(localAppData, "npm-cache", "_logs");
        if (!Directory.Exists(npmLogs)) return evidence;

        try
        {
            var recent = Directory.GetFiles(npmLogs, "*-debug-*.log")
                .Select(path => new FileInfo(path))
                .Where(file => file.LastWriteTimeUtc >= observedAt.UtcDateTime.AddHours(-24)
                    && file.LastWriteTimeUtc <= observedAt.UtcDateTime.AddMinutes(5))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(6);
            foreach (var file in recent)
            {
                var matches = File.ReadLines(file.FullName)
                    .Where(line => line.Contains(descriptor.PackageName, StringComparison.OrdinalIgnoreCase)
                        || line.Contains("postinstall", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("auto-update", StringComparison.OrdinalIgnoreCase))
                    .TakeLast(8)
                    .Select(ScrubLocalPaths)
                    .ToArray();
                evidence.Add(new NpmActivityEvidence(
                    "npm-log",
                    file.LastWriteTimeUtc,
                    matches.Length == 0
                        ? $"{file.Name}: no package/update lines in bounded scan"
                        : $"{file.Name}: {string.Join(" | ", matches)}"));
            }
        }
        catch (Exception ex)
        {
            evidence.Add(new NpmActivityEvidence("npm-log-scan", observedAt, $"scan failed: {ex.GetType().Name}: {ex.Message}"));
        }
        return evidence;
    }

    private static string ResolveNpmBin()
    {
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        return string.IsNullOrWhiteSpace(appData) ? "" : Path.Combine(appData, "npm");
    }

    internal static string BuildWindowsNpmCommand(string npmPath, string packageName)
        => $"\"\"{npmPath}\" install --global {packageName}\"";

    private static NpmCliDescriptor? DescriptorFor(string cliType)
        => CliTypes.Normalize(cliType) switch
        {
            CliTypes.Claude => new(CliTypes.Claude, "claude", "@anthropic-ai/claude-code", ["@anthropic-ai", "claude-code"]),
            CliTypes.Codex => new(CliTypes.Codex, "codex", "@openai/codex", ["@openai", "codex"]),
            _ => null,
        };

    private static string? ReadPackageVersion(string packageJson)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(packageJson));
            return json.RootElement.TryGetProperty("version", out var version) ? version.GetString() : null;
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "LocalCliRepairService: package version unavailable");
            return null;
        }
    }

    private static DateTimeOffset SafeLastWrite(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "LocalCliRepairService: last-write timestamp unavailable");
            return DateTimeOffset.MinValue;
        }
    }

    private static string TrimOutput(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var scrubbed = ScrubLocalPaths(value.Replace('\r', ' ').Replace('\n', ' ').Trim());
        return scrubbed.Length <= 4_000 ? scrubbed : scrubbed[^4_000..];
    }

    private static string ScrubLocalPaths(string value)
    {
        foreach (var variable in new[] { "USERPROFILE", "APPDATA", "LOCALAPPDATA" })
        {
            var path = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(path))
                value = value.Replace(path, $"%{variable}%", StringComparison.OrdinalIgnoreCase);
        }
        return value;
    }

    private sealed record NpmCliDescriptor(
        string CliType,
        string Command,
        string PackageName,
        string[] PackageSegments);

    private sealed record NpmInstallResult(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        string? Error);
}

public enum NpmCliInstallKind
{
    PackageMissing,
    MissingShimWithPackage,
    ShimPresent,
}

public sealed record NpmCliInstallInspection(
    string CliType,
    NpmCliInstallKind Kind,
    string PackageDirectory,
    string? PackageVersion,
    IReadOnlyList<string> PresentShims,
    string Detail);

public sealed record LocalCliRepairStatus(
    string CliType,
    string State,
    DateTimeOffset? RepairedAt,
    DateTimeOffset? AttemptedAt,
    string? PackageVersion,
    string? CliVersion,
    string Detail);

public sealed record NpmActivityEvidence(
    string Source,
    DateTimeOffset ObservedAt,
    string Summary);

public sealed record LocalCliRepairJournalRecord(
    DateTimeOffset OccurredAt,
    string CliType,
    string PackageName,
    string Detection,
    string Outcome,
    string? PreviousObservedVersion,
    string? PackageVersionBefore,
    string? PackageVersionAfter,
    string? CliVersionAfter,
    int ExitCode,
    long DurationMs,
    string? Error,
    string StandardOutput,
    string StandardError,
    IReadOnlyList<NpmActivityEvidence> Activity);
